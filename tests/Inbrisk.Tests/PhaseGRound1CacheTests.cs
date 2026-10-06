using System;
using System.Collections.Concurrent;
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

namespace Inbrisk.Tests;

public sealed class PhaseGRound1CacheTests
{
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
    // SECTION 60: FIND CACHE TESTS (12 tests)
    // =========================================================================

    [Fact]
    public async Task FindCache_RepeatedEquivalentRequest_HitsWithinSession()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "Submit", null, null, null, null, null, true, false, false);

        int computationCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            computationCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_1", "Submit", Role.Button, 100, 10, new RectPx(0, 0, 50, 20), ["click"], new Dictionary<string, object?> { ["enabled"] = true })
            ]);
        }

        // First call: Miss -> computes
        var res1 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Single(res1);
        Assert.Equal(1, computationCount);
        Assert.Equal(1, cache.Telemetry.MissCount);
        Assert.Equal(0, cache.Telemetry.HitCount);

        // Second call: Hit -> reuses without factory execution
        var res2 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Single(res2);
        Assert.Equal(1, computationCount);
        Assert.Equal(1, cache.Telemetry.HitCount);
    }

    [Fact]
    public async Task FindCache_DifferentQuery_DoesNotCollide()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var keyA = new FindCacheKey(null, 100, 10, Role.Button, "Submit", null, null, null, null, null, true, false, false);
        var keyB = new FindCacheKey(null, 100, 10, Role.Button, "Cancel", null, null, null, null, null, true, false, false);

        int factoryACount = 0;
        int factoryBCount = 0;

        await cache.GetOrComputeAsync(keyA, ct =>
        {
            factoryACount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_submit", "Submit", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
            ]);
        });

        await cache.GetOrComputeAsync(keyB, ct =>
        {
            factoryBCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_cancel", "Cancel", Role.Button, 100, 10, new RectPx(60, 0, 50, 20))
            ]);
        });

        Assert.Equal(1, factoryACount);
        Assert.Equal(1, factoryBCount);
        Assert.Equal(2, cache.Telemetry.MissCount);
        Assert.Equal(0, cache.Telemetry.HitCount);
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public async Task FindCache_DifferentWindow_DoesNotCollide()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var keyWin1 = new FindCacheKey(null, 100, 10, Role.Button, "OK", null, null, null, null, null, true, false, false);
        var keyWin2 = new FindCacheKey(null, 200, 10, Role.Button, "OK", null, null, null, null, null, true, false, false);

        await cache.GetOrComputeAsync(keyWin1, ct => Task.FromResult<IReadOnlyList<UiElement>>([
            CreateTestElement("el_100", "OK", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
        ]));

        await cache.GetOrComputeAsync(keyWin2, ct => Task.FromResult<IReadOnlyList<UiElement>>([
            CreateTestElement("el_200", "OK", Role.Button, 200, 10, new RectPx(0, 0, 50, 20))
        ]));

        Assert.Equal(2, cache.EntryCount);
        Assert.Equal(2, cache.Telemetry.MissCount);
    }

    [Fact]
    public async Task FindCache_Ttl400ms_Expires()
    {
        long mutationVersion = 1;
        // Construct cache with a short TTL (100ms) to test expiration safely and quickly
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(100));
        var key = new FindCacheKey(null, 100, 10, Role.Button, "Save", null, null, null, null, null, true, false, false);

        int computationCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            computationCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_save", "Save", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
            ]);
        }

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, computationCount);

        // Immediate read: warm hit
        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, computationCount);

        // Wait for TTL to expire
        await Task.Delay(150);

        // Call after expiration: forces recompute
        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(2, computationCount);
        Assert.Equal(1, cache.Telemetry.ExpirationCount);
    }

    [Fact]
    public async Task FindCache_RelevantMutation_InvalidatesBeforeTtl()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(5000));
        var key = new FindCacheKey(null, 500, 10, Role.Button, "Submit", null, null, null, null, null, true, false, false);

        int computationCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            computationCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_sub", "Submit", Role.Button, 500, 10, new RectPx(0, 0, 50, 20))
            ]);
        }

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, computationCount);

        // Trigger scoped invalidation for HWND 500 before TTL
        cache.InvalidateScope(500, null);

        // Next call must recompute
        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(2, computationCount);
        Assert.True(cache.Telemetry.InvalidationCount >= 1);
    }

    [Fact]
    public async Task FindCache_UnrelatedWindowMutation_DoesNotInvalidate()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(5000));
        var keyA = new FindCacheKey(null, 100, 10, Role.Button, "BtnA", null, null, null, null, null, true, false, false);

        int computationCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            computationCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_a", "BtnA", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
            ]);
        }

        await cache.GetOrComputeAsync(keyA, Factory);
        Assert.Equal(1, computationCount);

        // Mutation on completely unrelated window 999
        cache.InvalidateScope(999, null);

        // KeyA must still be cached and hit
        await cache.GetOrComputeAsync(keyA, Factory);
        Assert.Equal(1, computationCount);
        Assert.Equal(1, cache.Telemetry.HitCount);
    }

    [Fact]
    public async Task FindCache_MutationDuringComputation_StaleResultNotPublished()
    {
        long mutationVersion = 100;
        var cache = new SessionFindCache(() => mutationVersion);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "AsyncSearch", null, null, null, null, null, true, false, false);

        var tcs = new TaskCompletionSource();

        var findTask = cache.GetOrComputeAsync(key, async ct =>
        {
            // Simulate slow traversal
            await tcs.Task;
            return [CreateTestElement("el_1", "AsyncSearch", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))];
        });

        // While computation is running, UI mutates and increments MutationVersion
        mutationVersion = 101;
        tcs.SetResult();

        var result = await findTask;
        Assert.Single(result);

        // Result returned to caller, but rejected from cache due to version drift
        Assert.Equal(1, cache.Telemetry.RejectedStalePublishCount);
        Assert.Equal(0, cache.EntryCount);

        // Next call must be a fresh miss
        int newFactoryCalls = 0;
        await cache.GetOrComputeAsync(key, ct =>
        {
            newFactoryCalls++;
            return Task.FromResult<IReadOnlyList<UiElement>>([]);
        });
        Assert.Equal(1, newFactoryCalls);
    }

    [Fact]
    public async Task FindCache_ConcurrentEquivalentMisses_SingleFlight()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "ConcurrentBtn", null, null, null, null, null, true, false, false);

        int factoryExecutions = 0;
        var enteredFactory = new TaskCompletionSource();
        var barrier = new TaskCompletionSource();

        async Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref factoryExecutions);
            enteredFactory.TrySetResult();
            await barrier.Task;
            return [CreateTestElement("el_shared", "ConcurrentBtn", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))];
        }

        // Launch 20 concurrent tasks for the exact same key
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => cache.GetOrComputeAsync(key, Factory)))
            .ToArray();

        await enteredFactory.Task;
        var sw = Stopwatch.StartNew();
        while (cache.Telemetry.SingleFlightReuseCount < 19 && sw.ElapsedMilliseconds < 3000)
        {
            await Task.Delay(2);
        }

        // Release factory
        barrier.SetResult();
        var results = await Task.WhenAll(tasks);

        // All 20 received results
        Assert.Equal(20, results.Length);
        foreach (var r in results)
        {
            Assert.Single(r);
            Assert.Equal("el_shared", r[0].Id);
        }

        // Factory was executed exactly once!
        Assert.Equal(1, factoryExecutions);
        Assert.Equal(19, cache.Telemetry.SingleFlightReuseCount);
    }

    [Fact]
    public async Task FindCache_CancelOneConsumer_DoesNotCancelOtherConsumer()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "SlowBtn", null, null, null, null, null, true, false, false);

        var barrier = new TaskCompletionSource();

        async Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            await barrier.Task;
            return [CreateTestElement("el_slow", "SlowBtn", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))];
        }

        using var ctsA = new CancellationTokenSource();
        using var ctsB = new CancellationTokenSource();

        var taskA = cache.GetOrComputeAsync(key, Factory, ctsA.Token);
        var taskB = cache.GetOrComputeAsync(key, Factory, ctsB.Token);

        // Cancel Consumer A
        ctsA.Cancel();

        // Consumer A must throw OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);

        // Release factory
        barrier.SetResult();

        // Consumer B must still succeed!
        var resB = await taskB;
        Assert.Single(resB);
        Assert.Equal("el_slow", resB[0].Id);
    }

    [Fact]
    public async Task FindCache_FailedComputation_NotCached()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "FailingBtn", null, null, null, null, null, true, false, false);

        int callCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            callCount++;
            if (callCount == 1)
                throw new InvalidOperationException("UIA infra failure");
            return Task.FromResult<IReadOnlyList<UiElement>>([
                CreateTestElement("el_ok", "FailingBtn", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
            ]);
        }

        // First call fails
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrComputeAsync(key, Factory));
        Assert.Equal(0, cache.EntryCount);

        // Second call retries factory rather than returning cached exception
        var res2 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Single(res2);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task FindCache_EmptySuccessfulResult_MayCacheUntilInvalidation()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(5000));
        var key = new FindCacheKey(null, 100, 10, Role.Button, "NotFound", null, null, null, null, null, true, false, false);

        int callCount = 0;
        Task<IReadOnlyList<UiElement>> Factory(CancellationToken ct)
        {
            callCount++;
            return Task.FromResult<IReadOnlyList<UiElement>>([]);
        }

        var res1 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Empty(res1);
        Assert.Equal(1, callCount);

        // Second call hits cache
        var res2 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Empty(res2);
        Assert.Equal(1, callCount);
        Assert.Equal(1, cache.Telemetry.HitCount);

        // Mutation invalidates
        cache.InvalidateScope(100, null);
        var res3 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Empty(res3);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task FindCache_SessionDispose_ClearsEntries()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new FindCacheKey(null, 100, 10, Role.Button, "Test", null, null, null, null, null, true, false, false);

        await session.FindCache.GetOrComputeAsync(key, ct => Task.FromResult<IReadOnlyList<UiElement>>([
            CreateTestElement("el_1", "Test", Role.Button, 100, 10, new RectPx(0, 0, 50, 20))
        ]));

        Assert.Equal(1, session.FindCache.EntryCount);

        session.Dispose();
        Assert.Equal(0, session.FindCache.EntryCount);
    }

    // =========================================================================
    // SECTION 61: SNAPSHOT CACHE TESTS (7 tests)
    // =========================================================================

    [Fact]
    public async Task SnapshotCache_RepeatedEquivalentInspect_Hits()
    {
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion);
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        int buildCount = 0;
        Task<CallToolResult> Factory(CancellationToken ct)
        {
            buildCount++;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "window 0x64: 5 elements" }] });
        }

        var r1 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount);
        Assert.Equal(1, cache.Telemetry.MissCount);

        var r2 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount);
        Assert.Equal(1, cache.Telemetry.HitCount);
    }

    [Fact]
    public async Task SnapshotCache_DifferentDetailMode_DoesNotCollide()
    {
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion);
        var keySlim = new SnapshotCacheKey(100, null, 40, "slim", false);
        var keyRelational = new SnapshotCacheKey(100, null, 40, "full", true);

        int buildCount = 0;
        Task<CallToolResult> Factory(CancellationToken ct)
        {
            buildCount++;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "snapshot" }] });
        }

        await cache.GetOrComputeAsync(keySlim, Factory);
        await cache.GetOrComputeAsync(keyRelational, Factory);

        Assert.Equal(2, buildCount);
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public async Task SnapshotCache_RelevantStructureChange_Invalidates()
    {
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(5000));
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        int buildCount = 0;
        Task<CallToolResult> Factory(CancellationToken ct)
        {
            buildCount++;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "snapshot" }] });
        }

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount);

        // Invalidate window 100
        cache.InvalidateScope(100);

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(2, buildCount);
        Assert.Equal(1, cache.Telemetry.InvalidationCount);
    }

    [Fact]
    public async Task SnapshotCache_WindowClosed_Invalidates()
    {
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion);
        var key = new SnapshotCacheKey(200, null, 40, "slim", false);

        await cache.GetOrComputeAsync(key, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "snap" }] }));
        Assert.Equal(1, cache.EntryCount);

        // WindowClosed for window 200
        cache.InvalidateScope(200);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task SnapshotCache_MutationDuringBuild_StaleSnapshotNotPublished()
    {
        long mutationVersion = 50;
        var cache = new SessionSnapshotCache(() => mutationVersion);
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        var tcs = new TaskCompletionSource();

        var buildTask = cache.GetOrComputeAsync(key, async ct =>
        {
            await tcs.Task;
            return new CallToolResult { Content = [new TextContentBlock { Text = "stale snapshot" }] };
        });

        // Increment mutation version during build
        mutationVersion = 51;
        tcs.SetResult();

        var result = await buildTask;
        Assert.NotNull(result);

        // Stale snapshot rejected from cache
        Assert.Equal(1, cache.Telemetry.RejectedStalePublishCount);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public async Task SnapshotCache_Expiration_ForcesFreshSnapshot()
    {
        long mutationVersion = 1;
        var cache = new SessionSnapshotCache(() => mutationVersion, ttl: TimeSpan.FromMilliseconds(100));
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        int buildCount = 0;
        Task<CallToolResult> Factory(CancellationToken ct)
        {
            buildCount++;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "snap" }] });
        }

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount);

        await Task.Delay(150);

        await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(2, buildCount);
        Assert.Equal(1, cache.Telemetry.ExpirationCount);
    }

    [Fact]
    public async Task SnapshotCache_SessionDispose_ClearsEntries()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new SnapshotCacheKey(100, null, 40, "slim", false);

        await session.SnapshotCache.GetOrComputeAsync(key, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "snap" }] }));
        Assert.Equal(1, session.SnapshotCache.EntryCount);

        session.Dispose();
        Assert.Equal(0, session.SnapshotCache.EntryCount);
    }

    // =========================================================================
    // SECTION 62: WINDOW SNAPSHOT TESTS (4 tests)
    // =========================================================================

    [Fact]
    public void WindowSnapshot_ReusedWithinSingleToolCall()
    {
        int enumerationCalls = 0;
        long mutationVersion = 1;

        IReadOnlyList<WindowInfo> Enumerator()
        {
            enumerationCalls++;
            return [new WindowInfo(100, 10, "Win1", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0)];
        }

        var snapshot = new RequestWindowSnapshot(Enumerator, () => mutationVersion);

        // Call 1
        var w1 = snapshot.GetWindows();
        Assert.Single(w1);
        Assert.Equal(1, enumerationCalls);

        // Call 2 in same tool turn
        var w2 = snapshot.GetWindows();
        Assert.Same(w1, w2);
        Assert.Equal(1, enumerationCalls);
    }

    [Fact]
    public void WindowSnapshot_NewToolCall_IsFresh()
    {
        int enumerationCalls = 0;
        long mutationVersion = 1;

        IReadOnlyList<WindowInfo> Enumerator()
        {
            enumerationCalls++;
            return [new WindowInfo(100, 10, "Win1", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0)];
        }

        // Tool call 1
        var call1Snapshot = new RequestWindowSnapshot(Enumerator, () => mutationVersion);
        call1Snapshot.GetWindows();
        Assert.Equal(1, enumerationCalls);

        // Tool call 2 creates a new RequestWindowSnapshot
        var call2Snapshot = new RequestWindowSnapshot(Enumerator, () => mutationVersion);
        call2Snapshot.GetWindows();
        Assert.Equal(2, enumerationCalls);
    }

    [Fact]
    public void WindowSnapshot_InRequestTopologyMutation_ForcesRefresh()
    {
        int enumerationCalls = 0;
        long mutationVersion = 1;

        IReadOnlyList<WindowInfo> Enumerator()
        {
            enumerationCalls++;
            return [new WindowInfo(100, 10, "Win1", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0)];
        }

        var snapshot = new RequestWindowSnapshot(Enumerator, () => mutationVersion);
        var w1 = snapshot.GetWindows();
        Assert.Equal(1, enumerationCalls);

        // Mutation occurs within the same tool call (e.g. launch or close mutates version)
        mutationVersion = 2;

        var w2 = snapshot.GetWindows();
        Assert.Equal(2, enumerationCalls);
    }

    [Fact]
    public void WindowSnapshot_DoesNotPersistInMcpSession()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);

        // McpSession does not possess a session-persistent WindowSnapshot field
        var prop = typeof(McpSession).GetProperty("WindowSnapshot");
        Assert.Null(prop);

        // InbriskTools possesses RequestWindowSnapshot scoped to the tool instance
        var tools = new InbriskTools(session);
        Assert.NotNull(tools.WindowSnapshot);
    }

    // =========================================================================
    // SECTION 63: HWND METADATA CACHE TESTS (7 tests)
    // =========================================================================

    [Fact]
    public void HwndMetadata_RepeatedLookup_HitsSessionCache()
    {
        var cache = new HwndMetadataCache();
        int resolverCalls = 0;
        var realHwnd = NativeMethods.GetDesktopWindow().ToInt64();
        var currentPid = Environment.ProcessId;
        var startTime = Process.GetCurrentProcess().StartTime;

        HwndMetadata? Resolver(long h)
        {
            resolverCalls++;
            return new HwndMetadata(h, currentPid, "notepad.exe", startTime, false);
        }

        // Query 1: Miss
        var m1 = cache.GetOrAdd(realHwnd, Resolver);
        Assert.NotNull(m1);
        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, cache.Telemetry.MissCount);

        // Query 2: Hit
        var m2 = cache.GetOrAdd(realHwnd, Resolver);
        Assert.NotNull(m2);
        Assert.Equal(1, resolverCalls);
        Assert.Equal(1, cache.Telemetry.HitCount);
        Assert.Equal("notepad.exe", m2.ProcessName);
    }

    [Fact]
    public void HwndMetadata_WindowDestroy_Invalidates()
    {
        var cache = new HwndMetadataCache();
        cache.GetOrAdd(500, h => new HwndMetadata(h, 1234, "app.exe", DateTimeOffset.UtcNow, false));
        Assert.Equal(1, cache.EntryCount);

        // Destroy event
        cache.InvalidateHwnd(500);
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(1, cache.Telemetry.InvalidationCount);
    }

    [Fact]
    public void HwndMetadata_WindowClose_Invalidates()
    {
        var cache = new HwndMetadataCache();
        cache.GetOrAdd(600, h => new HwndMetadata(h, 1234, "app.exe", DateTimeOffset.UtcNow, false));

        cache.InvalidateHwnd(600);
        Assert.False(cache.ContainsKey(600));
    }

    [Fact]
    public void HwndMetadata_ProcessExit_Invalidates()
    {
        var cache = new HwndMetadataCache();
        cache.GetOrAdd(501, h => new HwndMetadata(h, 4444, "calc.exe", DateTimeOffset.UtcNow, false));
        cache.GetOrAdd(502, h => new HwndMetadata(h, 4444, "calc.exe", DateTimeOffset.UtcNow, false));
        cache.GetOrAdd(503, h => new HwndMetadata(h, 5555, "other.exe", DateTimeOffset.UtcNow, false));

        Assert.Equal(3, cache.EntryCount);

        // Process 4444 exits
        cache.InvalidatePid(4444);

        Assert.Equal(1, cache.EntryCount);
        Assert.False(cache.ContainsKey(501));
        Assert.False(cache.ContainsKey(502));
        Assert.True(cache.ContainsKey(503));
    }

    [Fact]
    public void HwndMetadata_PidReuse_ProcessStartTimePreventsStaleHit()
    {
        var cache = new HwndMetadataCache();
        var currentPid = Environment.ProcessId;
        var actualStartTime = Process.GetCurrentProcess().StartTime;
        var realHwnd = NativeMethods.GetDesktopWindow().ToInt64();

        // Populate cache with an old start time whose difference is < 1 second (250ms)
        // With exact tick comparison, this is strictly rejected without an arbitrary tolerance window.
        var oldStartTime = actualStartTime.AddMilliseconds(-250);
        int resolverCalls = 0;

        HwndMetadata? Resolver(long h)
        {
            resolverCalls++;
            return new HwndMetadata(h, currentPid, "current_proc.exe", actualStartTime, false);
        }

        // Manually place the stale entry representing Process A
        cache.GetOrAdd(realHwnd, h => new HwndMetadata(h, currentPid, "old_process_A.exe", oldStartTime, false));

        // When queried again, cache detects that Process.GetProcessById(currentPid).StartTime != oldStartTime!
        // Stale hit is rejected and re-resolved
        var resolved = cache.GetOrAdd(realHwnd, Resolver);

        Assert.Equal(1, cache.Telemetry.PidReuseRejectCount);
        Assert.Equal(0, cache.Telemetry.HitCount);
        Assert.NotNull(resolved);
        Assert.Equal("current_proc.exe", resolved.ProcessName);
    }

    [Fact]
    public void HwndMetadata_SamePidDifferentCreationTimeWithinOneSecond_RejectsStale()
    {
        var cache = new HwndMetadataCache();
        var currentPid = Environment.ProcessId;
        var actualStartTime = Process.GetCurrentProcess().StartTime;
        var realHwnd = NativeMethods.GetDesktopWindow().ToInt64();

        // Sub-second generation difference (100ms)
        var oldStartTime = actualStartTime.AddMilliseconds(-100);
        int resolverCalls = 0;

        HwndMetadata? Resolver(long h)
        {
            resolverCalls++;
            return new HwndMetadata(h, currentPid, "current_proc.exe", actualStartTime, false);
        }

        cache.GetOrAdd(realHwnd, h => new HwndMetadata(h, currentPid, "generation_A.exe", oldStartTime, false));

        var resolved = cache.GetOrAdd(realHwnd, Resolver);

        Assert.Equal(1, cache.Telemetry.PidReuseRejectCount);
        Assert.Equal(0, cache.Telemetry.HitCount);
        Assert.NotNull(resolved);
        Assert.Equal("current_proc.exe", resolved.ProcessName);
        Assert.Equal(1, resolverCalls);
    }

    [Fact]
    public void HwndMetadata_InvalidHwnd_Evicts()
    {
        var cache = new HwndMetadataCache();
        // Use an HWND that is definitely not a real window (e.g. 0xDEADBEEF)
        long bogusHwnd = 0xDEADBEEF;

        cache.GetOrAdd(bogusHwnd, h => new HwndMetadata(h, Environment.ProcessId, "test.exe", DateTimeOffset.UtcNow, false));

        // Looking up bogusHwnd checks IsWindow -> evicts
        var hit = cache.TryGet(bogusHwnd, out _);
        Assert.False(hit);
        Assert.Equal(1, cache.Telemetry.EvictionCount);
    }

    [Fact]
    public void HwndMetadata_SessionDispose_Clears()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        session.HwndMetadata.GetOrAdd(NativeMethods.GetDesktopWindow().ToInt64(), h => new HwndMetadata(h, 10, "app.exe", DateTimeOffset.UtcNow, false));
        Assert.True(session.HwndMetadata.EntryCount >= 1);

        session.Dispose();
        Assert.Equal(0, session.HwndMetadata.EntryCount);
    }

    // =========================================================================
    // SECTION 64: MONITOR / VDM TESTS (6 tests)
    // =========================================================================

    [Fact]
    public void MonitorCache_RepeatedRead_ReusesTopology()
    {
        var cache = new MonitorTopologyCache();
        int enumCount = 0;

        IReadOnlyList<MonitorInfo> Factory()
        {
            enumCount++;
            return [new MonitorInfo(0, new RectPx(0, 0, 1920, 1080), 96, 96, true, @"\\.\DISPLAY1")];
        }

        var m1 = cache.GetMonitors(Factory);
        Assert.Single(m1);
        Assert.Equal(1, enumCount);

        var m2 = cache.GetMonitors(Factory);
        Assert.Same(m1, m2);
        Assert.Equal(1, enumCount);
        Assert.Equal(1, cache.Telemetry.HitCount);
    }

    [Fact]
    public void MonitorCache_DisplayChange_Invalidates()
    {
        var cache = new MonitorTopologyCache();
        int enumCount = 0;

        IReadOnlyList<MonitorInfo> Factory()
        {
            enumCount++;
            return [new MonitorInfo(0, new RectPx(0, 0, 1920, 1080), 96, 96, true, @"\\.\DISPLAY1")];
        }

        cache.GetMonitors(Factory);
        Assert.Equal(1, enumCount);

        // Display change event occurs (WM_DISPLAYCHANGE)
        cache.Invalidate();

        cache.GetMonitors(Factory);
        Assert.Equal(2, enumCount);
        Assert.Equal(1, cache.Telemetry.InvalidationCount);
    }

    [Fact]
    public void MonitorCache_DpiTopologyChange_Invalidates()
    {
        var cache = new MonitorTopologyCache();
        int enumCount = 0;

        IReadOnlyList<MonitorInfo> Factory()
        {
            enumCount++;
            return [new MonitorInfo(0, new RectPx(0, 0, 1920, 1080), 96, 96, true, @"\\.\DISPLAY1")];
        }

        cache.GetMonitors(Factory);
        cache.Invalidate();

        var fresh = cache.GetMonitors(Factory);
        Assert.NotNull(fresh);
        Assert.Equal(2, enumCount);
    }

    [Fact]
    public void VirtualDesktopManager_ReusedWithinLifetime()
    {
        var holder = new VirtualDesktopManagerHolder();
        int creationCalls = 0;

        IVirtualDesktopManager? Factory()
        {
            creationCalls++;
            return new FakeVirtualDesktopManager();
        }

        var vdm1 = holder.GetOrCreate(Factory);
        var vdm2 = holder.GetOrCreate(Factory);

        Assert.Same(vdm1, vdm2);
        Assert.Equal(1, creationCalls);
        Assert.Equal(1, holder.CreationCount);
    }

    [Fact]
    public void VirtualDesktopManager_Failure_RecreatesSafely()
    {
        var holder = new VirtualDesktopManagerHolder();
        int creationCalls = 0;

        IVirtualDesktopManager? Factory()
        {
            creationCalls++;
            return new FakeVirtualDesktopManager();
        }

        var vdm1 = holder.GetOrCreate(Factory);
        Assert.Equal(1, creationCalls);

        // Invalidate due to COM failure
        holder.Invalidate();

        var vdm2 = holder.GetOrCreate(Factory);
        Assert.Equal(2, creationCalls);
        Assert.NotSame(vdm1, vdm2);
    }

    [Fact]
    public void MonitorCache_SessionDispose_ReleasesResources()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        session.MonitorCache.GetMonitors(() => [new MonitorInfo(0, new RectPx(0, 0, 1920, 1080), 96, 96, true, @"\\.\DISPLAY1")]);
        session.VdmHolder.GetOrCreate(() => new FakeVirtualDesktopManager());

        Assert.True(session.MonitorCache.HasCachedMonitors);

        session.Dispose();
        Assert.False(session.MonitorCache.HasCachedMonitors);
    }

    // =========================================================================
    // SECTION 65: CACHE-RACE STRESS TEST (1 test)
    // =========================================================================

    [Fact]
    public async Task CacheRace_ConcurrentReadersAndInvalidations_NoStalePublishOrDeadlock()
    {
        long mutationVersion = 1;
        var cache = new SessionFindCache(() => Volatile.Read(ref mutationVersion), maxCapacity: 64);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exceptions = new List<Exception>();
        var readerTasks = new Task[32];

        for (int i = 0; i < 32; i++)
        {
            int readerId = i;
            readerTasks[i] = Task.Run(async () =>
            {
                var rand = new Random(readerId);
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        int targetWindow = rand.Next(1, 10);
                        var key = new FindCacheKey(null, targetWindow, 1, Role.Button, $"Btn_{rand.Next(1, 5)}", null, null, null, null, null, true, false, false);

                        var res = await cache.GetOrComputeAsync(key, async ct =>
                        {
                            await Task.Yield();
                            return [CreateTestElement($"el_{targetWindow}", key.Name, Role.Button, targetWindow, 1, new RectPx(0, 0, 10, 10))];
                        }, cts.Token);

                        Assert.NotNull(res);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        lock (exceptions) exceptions.Add(ex);
                        break;
                    }
                }
            });
        }

        var mutatorTask = Task.Run(async () =>
        {
            var rand = new Random(999);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Increment(ref mutationVersion);
                    cache.InvalidateScope(rand.Next(1, 10), null);
                    await Task.Delay(5);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    lock (exceptions) exceptions.Add(ex);
                    break;
                }
            }
        });

        await Task.WhenAll(readerTasks.Concat([mutatorTask]));

        Assert.Empty(exceptions);
        Assert.True(cache.EntryCount <= 64, $"EntryCount {cache.EntryCount} must respect maxCapacity 64");
    }

    // =========================================================================
    // SECTION 65B: REPAIR ROUND 1 CLOSURE TESTS
    // =========================================================================

    [Fact]
    public async Task FindCache_SessionDispose_InFlightProducerCannotPublish()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new FindCacheKey(null, 1, 100, Core.Role.Button, "test", null, null, null, null, null, null, false, false);

        var startedTcs = new TaskCompletionSource<bool>();
        bool producerCancellationObserved = false;

        var producerTask = Task.Run(async () =>
        {
            return await session.FindCache.GetOrComputeAsync(key, async ct =>
            {
                startedTcs.SetResult(true);
                try
                {
                    await Task.Delay(10000, ct);
                }
                catch (OperationCanceledException)
                {
                    producerCancellationObserved = true;
                    throw;
                }
                return (IReadOnlyList<UiElement>)[CreateTestElement("el1", "test", Role.Button, 100, 1, new RectPx(0, 0, 10, 10))];
            });
        });

        await startedTcs.Task;
        Assert.True(session.FindInFlightCount >= 1);

        // Dispose session while producer is in-flight
        session.Dispose();

        try { await producerTask; } catch { }

        // Must prove both cancellation was observed AND cache has 0 entries
        Assert.True(producerCancellationObserved);
        Assert.Equal(0, session.FindCacheEntryCount);
        Assert.Equal(0, session.FindInFlightCount);
    }

    [Fact]
    public async Task FindCache_SessionDispose_CancelsInFlightProducer()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new FindCacheKey(null, 2, 200, Core.Role.Button, "cancel-test", null, null, null, null, null, null, false, false);

        var startedTcs = new TaskCompletionSource<bool>();
        bool producerCanceled = false;

        var producerTask = Task.Run(async () =>
        {
            return await session.FindCache.GetOrComputeAsync(key, async ct =>
            {
                startedTcs.SetResult(true);
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    producerCanceled = true;
                    throw;
                }
                return (IReadOnlyList<UiElement>)[];
            });
        });

        await startedTcs.Task;
        Assert.True(session.FindInFlightCount >= 1);

        session.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await producerTask);
        Assert.True(producerCanceled);
        Assert.Equal(0, session.FindCacheEntryCount);
        Assert.Equal(0, session.FindInFlightCount);
        Assert.True(session.FindCache.Telemetry.ProducerDisposeCancellationCount >= 1);
    }

    [Fact]
    public async Task SnapshotCache_SessionDispose_InFlightBuildCannotPublish()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new SnapshotCacheKey(100, null, 20, "slim", false);

        var startedTcs = new TaskCompletionSource<bool>();
        bool buildCanceled = false;

        var producerTask = Task.Run(async () =>
        {
            return await session.SnapshotCache.GetOrComputeAsync(key, async ct =>
            {
                startedTcs.SetResult(true);
                try
                {
                    await Task.Delay(10000, ct);
                }
                catch (OperationCanceledException)
                {
                    buildCanceled = true;
                    throw;
                }
                return new ModelContextProtocol.Protocol.CallToolResult { Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "snapshot" }] };
            });
        });

        await startedTcs.Task;
        Assert.True(session.SnapshotInFlightCount >= 1);

        session.Dispose();

        try { await producerTask; } catch { }

        Assert.True(buildCanceled);
        Assert.Equal(0, session.SnapshotCacheEntryCount);
        Assert.Equal(0, session.SnapshotInFlightCount);
    }

    [Fact]
    public async Task SnapshotCache_SessionDispose_CancelsInFlightBuild()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new SnapshotCacheKey(200, null, 20, "slim", false);

        var startedTcs = new TaskCompletionSource<bool>();
        bool buildCanceled = false;

        var buildTask = Task.Run(async () =>
        {
            return await session.SnapshotCache.GetOrComputeAsync(key, async ct =>
            {
                startedTcs.SetResult(true);
                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (OperationCanceledException)
                {
                    buildCanceled = true;
                    throw;
                }
                return new ModelContextProtocol.Protocol.CallToolResult();
            });
        });

        await startedTcs.Task;
        Assert.True(session.SnapshotInFlightCount >= 1);

        session.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await buildTask);
        Assert.True(buildCanceled);
        Assert.Equal(0, session.SnapshotCacheEntryCount);
        Assert.Equal(0, session.SnapshotInFlightCount);
        Assert.True(session.SnapshotCache.Telemetry.ProducerDisposeCancellationCount >= 1);
    }

    [Fact]
    public void SessionDispose_AllCacheEntryAndInFlightCountsAreZero()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var findKey = new FindCacheKey(null, 1, 100, Core.Role.Button, "test", null, null, null, null, null, null, false, false);
        session.FindCache.GetOrCompute(findKey, () => []);

        var snapKey = new SnapshotCacheKey(100, null, 20, "slim", false);
        session.SnapshotCache.GetOrCompute(snapKey, () => new ModelContextProtocol.Protocol.CallToolResult());

        session.HwndMetadata.GetOrAdd(NativeMethods.GetDesktopWindow().ToInt64(), h => new HwndMetadata(h, 10, "app.exe", DateTimeOffset.UtcNow, false));

        Assert.True(session.FindCacheEntryCount >= 1);
        Assert.True(session.SnapshotCacheEntryCount >= 1);
        Assert.True(session.HwndMetadataEntryCount >= 1);

        session.Dispose();

        Assert.Equal(0, session.FindCacheEntryCount);
        Assert.Equal(0, session.SnapshotCacheEntryCount);
        Assert.Equal(0, session.HwndMetadataEntryCount);
        Assert.Equal(0, session.FindInFlightCount);
        Assert.Equal(0, session.SnapshotInFlightCount);
    }

    [Fact]
    public void FindCacheKey_ParameterDifferences_DoNotCollide()
    {
        var key1 = new FindCacheKey(null, 100, 10, Core.Role.Button, "Save", "btn_save", null, null, null, "Button", true, false, false, 20, "slim", null);
        var key2 = key1 with { Limit = 50 };
        var key3 = key1 with { Detail = "full" };
        var key4 = key1 with { NameNotContains = "Cancel" };
        var key5 = key1 with { Role = Core.Role.Edit };
        var key6 = key1 with { Enabled = false };

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key1, key4);
        Assert.NotEqual(key1, key5);
        Assert.NotEqual(key1, key6);
    }

    [Fact]
    public void SnapshotCacheKey_ParameterDifferences_DoNotCollide()
    {
        var key1 = new SnapshotCacheKey(100, "uia_1", 40, "slim", false);
        var key2 = key1 with { MaxElements = 80 };
        var key3 = key1 with { Detail = "full" };
        var key4 = key1 with { Relational = true };
        var key5 = key1 with { ElementId = "uia_2" };
        var key6 = key1 with { Hwnd = 200 };

        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1, key3);
        Assert.NotEqual(key1, key4);
        Assert.NotEqual(key1, key5);
        Assert.NotEqual(key1, key6);
    }

    [Fact]
    public async Task FindCache_WarmHit_ProducesCacheHitStageInPerfTrace()
    {
        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        var key = new FindCacheKey(null, 1, 100, Core.Role.Button, "test", null, null, null, null, null, null, false, false);
        int factoryCalls = 0;

        using (PerfTrace.Begin("test", "cold_find"))
        {
            var res1 = await session.FindCache.GetOrComputeAsync(key, async ct =>
            {
                factoryCalls++;
                await Task.Yield();
                return (IReadOnlyList<UiElement>)[CreateTestElement("el1", "test", Role.Button, 100, 1, new RectPx(0, 0, 10, 10))];
            });
        }

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, session.FindCache.Telemetry.MissCount);

        using (PerfTrace.Begin("test", "warm_find"))
        {
            var res2 = await session.FindCache.GetOrComputeAsync(key, async ct =>
            {
                factoryCalls++;
                await Task.Yield();
                return [];
            });
        }

        // Factory must not be called a second time — warm hit!
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, session.FindCache.Telemetry.HitCount);
    }

    [Fact]
    public void VirtualDesktopManager_ConcurrentQueries_SucceedWithoutApartmentAffinityError()
    {
        using var holder = new VirtualDesktopManagerHolder();
        var desktopHwnd = NativeMethods.GetDesktopWindow();

        Parallel.For(0, 20, _ =>
        {
            var vdm = holder.GetOrCreate(() => (IVirtualDesktopManager)new VirtualDesktopManagerCom());
            if (vdm != null)
            {
                var isOnCurrent = vdm.IsWindowOnCurrentVirtualDesktop(desktopHwnd);
                Assert.True(isOnCurrent == 0 || isOnCurrent == 1);
            }
        });

        Assert.Equal(1, holder.CreationCount);
    }

    [Fact]
    public void VirtualDesktopManager_AllComCallsExecuteOnOwnerMta()
    {
        using var holder = new VirtualDesktopManagerHolder();
        int creationThread = 0;
        int callThread = 0;
        ApartmentState callApartment = ApartmentState.Unknown;

        IVirtualDesktopManager? Factory()
        {
            creationThread = Thread.CurrentThread.ManagedThreadId;
            return new FakeVirtualDesktopManager();
        }

        var vdm = holder.GetOrCreate(Factory);
        Assert.NotNull(vdm);
        Assert.Equal(holder.MtaThreadId, creationThread);

        // Call from an external thread explicitly configured as STA
        int queryResult = -1;
        var thread = new Thread(() =>
        {
            queryResult = vdm.IsWindowOnCurrentVirtualDesktop(IntPtr.Zero);
            callThread = holder.LastExecutionThreadId;
            callApartment = holder.LastApartmentState;
        });
        thread.SetApartmentState(ApartmentState.STA); // Caller is STA!
        thread.Start();
        thread.Join();

        Assert.Equal(1, queryResult);
        Assert.Equal(holder.MtaThreadId, callThread);
        Assert.Equal(ApartmentState.MTA, callApartment);

        // Disposal happens on MTA thread
        holder.Dispose();
        Assert.Equal(holder.MtaThreadId, holder.DisposalThreadId);
    }

    [Fact]
    public void VirtualDesktopManager_ConcurrentCallers_ReuseSingleOwnedInstance()
    {
        using var holder = new VirtualDesktopManagerHolder();
        var desktopHwnd = NativeMethods.GetDesktopWindow();

        var results = new ConcurrentBag<int>();
        var threadIds = new ConcurrentBag<int>();

        Parallel.For(0, 20, _ =>
        {
            var vdm = holder.GetOrCreate(() => (IVirtualDesktopManager)new VirtualDesktopManagerCom());
            if (vdm != null)
            {
                var isOnCurrent = vdm.IsWindowOnCurrentVirtualDesktop(desktopHwnd);
                results.Add(isOnCurrent);
                threadIds.Add(holder.LastExecutionThreadId);
            }
        });

        Assert.Equal(20, results.Count);
        Assert.Equal(1, holder.CreationCount);
        // All calls were marshaled to execute on the single controlled MTA thread
        Assert.All(threadIds, tid => Assert.Equal(holder.MtaThreadId, tid));
    }

    [Fact]
    public async Task FindCache_DrainTimeout_LateProducerCannotPublish()
    {
        var cache = new SessionFindCache(() => 1);
        var key = new FindCacheKey(null, 1, 100, Core.Role.Button, "late-producer-test", null, null, null, null, null, null, false, false);

        var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var producerTask = cache.GetOrComputeAsync(key, async ct =>
        {
            startedTcs.SetResult(true);
            // Intentionally ignore cancellation and wait until explicitly released
            await finishTcs.Task;
            return (IReadOnlyList<UiElement>)[CreateTestElement("el_late", "Late Button", Role.Button, 100, 1, new RectPx(0, 0, 10, 10))];
        });

        await startedTcs.Task;
        Assert.Equal(1, cache.ActiveOwnedProducerCount);
        Assert.Equal(0, cache.DetachedProducerCount);
        Assert.Equal(1, cache.InFlightCount);

        // Bounded drain with short 50ms timeout
        cache.Dispose(drainTimeoutMs: 50);

        // 1. Drain timeout telemetry must be incremented
        Assert.Equal(1, cache.Telemetry.ProducerDisposeDrainTimeoutCount);
        // 2. Cache entries must be 0
        Assert.Equal(0, cache.EntryCount);
        // 3. Logical ownership released
        Assert.Equal(0, cache.ActiveOwnedProducerCount);
        // 4. Physical detached task tracked — telemetry is NOT falsely zero!
        Assert.Equal(1, cache.DetachedProducerCount);
        Assert.Equal(1, cache.InFlightCount);

        // Allow late producer to finish
        finishTcs.SetResult(true);
        var result = await producerTask;
        Assert.Single(result);

        // Yield to allow task ContinueWith to run
        await Task.Delay(50);

        // 5. Detached producer count and InFlightCount return to 0
        Assert.Equal(0, cache.DetachedProducerCount);
        Assert.Equal(0, cache.InFlightCount);
        // 6. Stale publication rejected — no entry appeared in cache
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(1, cache.Telemetry.RejectedStalePublishCount);
    }

    [Fact]
    public async Task SnapshotCache_DrainTimeout_LateProducerCannotPublish()
    {
        var cache = new SessionSnapshotCache(() => 1);
        var key = new SnapshotCacheKey(100, null, 20, "slim", false);

        var startedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var producerTask = cache.GetOrComputeAsync(key, async ct =>
        {
            startedTcs.SetResult(true);
            // Intentionally ignore cancellation and wait until explicitly released
            await finishTcs.Task;
            return new ModelContextProtocol.Protocol.CallToolResult
            {
                Content = [new ModelContextProtocol.Protocol.TextContentBlock { Text = "late-snapshot" }]
            };
        });

        await startedTcs.Task;
        Assert.Equal(1, cache.ActiveOwnedProducerCount);
        Assert.Equal(0, cache.DetachedProducerCount);
        Assert.Equal(1, cache.InFlightCount);

        // Bounded drain with short 50ms timeout
        cache.Dispose(drainTimeoutMs: 50);

        // 1. Drain timeout telemetry incremented
        Assert.Equal(1, cache.Telemetry.ProducerDisposeDrainTimeoutCount);
        // 2. Cache entry count is 0
        Assert.Equal(0, cache.EntryCount);
        // 3. Logical ownership released
        Assert.Equal(0, cache.ActiveOwnedProducerCount);
        // 4. Physical detached task tracked — telemetry is NOT falsely zero!
        Assert.Equal(1, cache.DetachedProducerCount);
        Assert.Equal(1, cache.InFlightCount);

        // Allow late snapshot builder to finish
        finishTcs.SetResult(true);
        var result = await producerTask;
        Assert.NotNull(result);

        // Yield to allow task ContinueWith to run
        await Task.Delay(50);

        // 5. Detached producer count and InFlightCount return to 0
        Assert.Equal(0, cache.DetachedProducerCount);
        Assert.Equal(0, cache.InFlightCount);
        // 6. Stale publication rejected — no entry appeared in cache
        Assert.Equal(0, cache.EntryCount);
        Assert.Equal(1, cache.Telemetry.RejectedStalePublishCount);
    }

    private sealed class FakeVirtualDesktopManager : IVirtualDesktopManager
    {
        public int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow) => 1;
        public Guid GetWindowDesktopId(IntPtr topLevelWindow) => Guid.Empty;
        public void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId) { }
    }
}
