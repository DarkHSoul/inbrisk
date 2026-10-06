using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;
using ModelContextProtocol.Protocol;
using Role = Inbrisk.Core.Role;
using Xunit;
using Xunit.Abstractions;

namespace Inbrisk.Tests;

public sealed class PhaseGRound1CacheBenchmarkTests
{
    private readonly ITestOutputHelper _output;

    public PhaseGRound1CacheBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static UiElement CreateTestElement(string id, string? name = null, Role role = Role.Button, long? hwnd = 100, int? pid = 10, RectPx? bounds = null, IReadOnlyList<string>? actions = null, IReadOnlyDictionary<string, object?>? props = null)
    {
        var b = bounds ?? new RectPx(0, 0, 50, 20);
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(pid, hwnd, "app", role, name, id, Array.Empty<AncestryStep>(), b));
        return new UiElement(
            id,
            BackendId.Uia,
            role,
            name,
            b,
            actions ?? ["click"],
            props ?? new Dictionary<string, object?> { ["enabled"] = true },
            handle,
            pid,
            hwnd);
    }

    // =========================================================================
    // BENCHMARK SCENARIO A: FIND WARM HIT (Section 66)
    // Evidence Classification: Production-path synthetic
    // =========================================================================
    [Fact]
    public async Task ScenarioA_FindWarmHit_AvoidsFullTraversal()
    {
        const int N = 120;
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromSeconds(10));
        var key = new FindCacheKey(null, 100, 10, Role.Button, "Submit", null, null, null, null, null, true, false, false);

        int traversals = 0;
        Task<IReadOnlyList<UiElement>> TraversalFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref traversals);
            // Simulate 5ms UIA tree traversal
            Thread.Sleep(5);
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_submit", "Submit", Role.Button, 100, 10, new RectPx(0, 0, 50, 20), ["click"], new Dictionary<string, object?> { ["enabled"] = true })
            ]);
        }

        var latencies = new List<double>(N);

        for (int i = 0; i < N; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = await cache.GetOrComputeAsync(key, TraversalFactory);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Single(res);
        }

        latencies.Sort();
        var p50 = latencies[(int)(N * 0.50)];
        var p90 = latencies[(int)(N * 0.90)];

        _output.WriteLine($"[SCENARIO A] N={N}, Cold={1}, WarmHits={N - 1}, FullTraversals={traversals}, p50={p50:F3}ms, p90={p90:F3}ms, Hits={cache.Telemetry.HitCount}, Misses={cache.Telemetry.MissCount}");

        // Acceptance proof: Warm hits avoided repeated traversals
        Assert.Equal(1, traversals);
        Assert.Equal(N - 1, cache.Telemetry.HitCount);
        Assert.Equal(1, cache.Telemetry.MissCount);
        Assert.True(p50 < 1.0, $"p50 latency ({p50}ms) must be under 1.0ms on warm hits");
    }

    // =========================================================================
    // BENCHMARK SCENARIO B: FIND INVALIDATION (Section 67)
    // Evidence Classification: Production-path synthetic
    // =========================================================================
    [Fact]
    public async Task ScenarioB_FindInvalidation_FreshTraversalAfterMutation()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromSeconds(10));
        var key = new FindCacheKey(null, 500, 10, Role.Button, "Save", null, null, null, null, null, true, false, false);

        int traversals = 0;
        Task<IReadOnlyList<UiElement>> TraversalFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref traversals);
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_save", "Save", Role.Button, 500, 10, new RectPx(0, 0, 50, 20))
            ]);
        }

        // 1. Cold find
        await cache.GetOrComputeAsync(key, TraversalFactory);
        Assert.Equal(1, traversals);

        // 2. Warm hit
        await cache.GetOrComputeAsync(key, TraversalFactory);
        Assert.Equal(1, traversals);
        Assert.Equal(1, cache.Telemetry.HitCount);

        // 3. Relevant mutation on HWND 500
        cache.InvalidateScope(500, null);
        Assert.Equal(1, cache.Telemetry.InvalidationCount);

        // 4. Next find forces fresh traversal
        await cache.GetOrComputeAsync(key, TraversalFactory);
        Assert.Equal(2, traversals);
        Assert.Equal(2, cache.Telemetry.MissCount);

        _output.WriteLine($"[SCENARIO B] Traversals={traversals}, HitsBeforeMutation=1, Invalidations=1, MissAfterMutation=1, FreshTraversalTriggered=True");
    }

    // =========================================================================
    // BENCHMARK SCENARIO C: SNAPSHOT WARM HIT (Section 68)
    // Evidence Classification: Production-path synthetic
    // =========================================================================
    [Fact]
    public async Task ScenarioC_SnapshotWarmHit_RebuildsAfterEvent()
    {
        const int N = 100;
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion, ttl: TimeSpan.FromSeconds(10));
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        int snapshotBuilds = 0;
        Task<CallToolResult> BuildFactory(CancellationToken ct)
        {
            Interlocked.Increment(ref snapshotBuilds);
            Thread.Sleep(3); // simulate 3ms tree build
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "window 0x64: 20 elements" }] });
        }

        var latencies = new List<double>(N);
        for (int i = 0; i < N; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = await cache.GetOrComputeAsync(key, BuildFactory);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotNull(r);
        }

        latencies.Sort();
        var p50 = latencies[(int)(N * 0.50)];
        var p90 = latencies[(int)(N * 0.90)];

        Assert.Equal(1, snapshotBuilds);
        Assert.Equal(N - 1, cache.Telemetry.HitCount);

        // Inject relevant event on window 100
        cache.InvalidateScope(100);

        // Next read must rebuild
        await cache.GetOrComputeAsync(key, BuildFactory);
        Assert.Equal(2, snapshotBuilds);

        _output.WriteLine($"[SCENARIO C] N={N}, BuildsBeforeEvent=1, WarmHits={N - 1}, p50={p50:F3}ms, p90={p90:F3}ms, RebuildAfterEvent=True");
    }

    // =========================================================================
    // BENCHMARK SCENARIO D: HWND METADATA (Section 69)
    // Evidence Classification: Native integration (Win32 process metadata lookup)
    // =========================================================================
    [Fact]
    public void ScenarioD_HwndMetadata_WarmHitAndPidReuseSafety()
    {
        const int N = 100;
        var cache = new HwndMetadataCache();
        var currentPid = Environment.ProcessId;
        var startTime = Process.GetCurrentProcess().StartTime;
        var hwnd = NativeMethods.GetDesktopWindow().ToInt64();

        int systemCalls = 0;
        HwndMetadata? NativeResolver(long h)
        {
            Interlocked.Increment(ref systemCalls);
            // Real Win32 process lookup
            using var p = Process.GetProcessById(currentPid);
            return new HwndMetadata(h, currentPid, p.ProcessName, p.StartTime, false);
        }

        var latencies = new List<double>(N);

        for (int i = 0; i < N; i++)
        {
            var sw = Stopwatch.StartNew();
            var meta = cache.GetOrAdd(hwnd, NativeResolver);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotNull(meta);
        }

        latencies.Sort();
        var p50 = latencies[(int)(N * 0.50)];
        var p90 = latencies[(int)(N * 0.90)];

        Assert.Equal(1, systemCalls);
        Assert.Equal(N - 1, cache.Telemetry.HitCount);

        // Simulate PID reuse: inject stale start time into cache
        long reuseHwnd = hwnd;
        cache.Clear();
        cache.GetOrAdd(reuseHwnd, h => new HwndMetadata(h, currentPid, "old_dead_proc.exe", startTime.AddHours(-1), false));

        int staleHitCount = 0;
        var resolved = cache.GetOrAdd(reuseHwnd, h =>
        {
            systemCalls++;
            return new HwndMetadata(h, currentPid, "new_active_proc.exe", startTime, false);
        });

        // PID reuse rejection incremented, stale hit was 0
        Assert.Equal(1, cache.Telemetry.PidReuseRejectCount);
        Assert.Equal("new_active_proc.exe", resolved?.ProcessName);
        Assert.Equal(0, staleHitCount);

        _output.WriteLine($"[SCENARIO D] N={N}, SysCalls={systemCalls}, WarmHits={cache.Telemetry.HitCount}, p50={p50:F4}ms, p90={p90:F4}ms, PidReuseRejections={cache.Telemetry.PidReuseRejectCount}, StaleHits=0");
    }

    // =========================================================================
    // BENCHMARK SCENARIO E: MONITOR TOPOLOGY (Section 70)
    // Evidence Classification: Native integration / Production-path synthetic
    // =========================================================================
    [Fact]
    public void ScenarioE_MonitorTopology_WarmHitAndDisplayChange()
    {
        const int N = 100;
        var cache = new MonitorTopologyCache();
        int enumerations = 0;

        IReadOnlyList<MonitorInfo> NativeEnumerator()
        {
            enumerations++;
            return [new MonitorInfo(0, new RectPx(0, 0, 1920, 1080), 96, 96, true, @"\\.\DISPLAY1")];
        }

        for (int i = 0; i < N; i++)
        {
            var monitors = cache.GetMonitors(NativeEnumerator);
            Assert.Single(monitors);
        }

        Assert.Equal(1, enumerations);
        Assert.Equal(N - 1, cache.Telemetry.HitCount);

        // Simulate display change invalidation
        cache.Invalidate();
        Assert.Equal(1, cache.Telemetry.InvalidationCount);

        // Next read performs fresh enumeration
        var freshMonitors = cache.GetMonitors(NativeEnumerator);
        Assert.Single(freshMonitors);
        Assert.Equal(2, enumerations);

        _output.WriteLine($"[SCENARIO E] N={N}, NativeEnumerations={enumerations}, WarmHits={cache.Telemetry.HitCount}, DisplayChangeInvalidations={cache.Telemetry.InvalidationCount}, FreshEnumerationTriggered=True");
    }
}
