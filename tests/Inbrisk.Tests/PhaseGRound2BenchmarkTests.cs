using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Xunit;
using Xunit.Abstractions;

namespace Inbrisk.Tests;

public sealed class PhaseGRound2BenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;
    private DateTimeOffset _currentTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public PhaseGRound2BenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-g2-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch { }
    }

    private DateTimeOffset GetClock() => _currentTime;

    private static (IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint) MakeSyntheticCatalog(int count = 50)
    {
        var apps = new List<AppInfo>(count);
        var entries = new List<AppCatalogEntryDto>(count);
        for (int i = 1; i <= count; i++)
        {
            var name = $"ProductivityApp_{i}";
            var launch = $"app:\"{name}\"";
            apps.Add(new AppInfo(name, LaunchMethod.StartMenu, launch, "installed"));
            entries.Add(new AppCatalogEntryDto
            {
                Name = name,
                Method = LaunchMethod.StartMenu,
                Launch = launch,
                Kind = "installed",
                Identifier = $"C:\\Program Files\\App{i}\\App{i}.exe",
                ExeHints = [name],
                Score = 100
            });
        }
        return (apps, entries, "fp-bench");
    }

    // -------------------------------------------------------------
    // Scenario A: Cold Build vs Warm Persisted Load Benchmark
    // -------------------------------------------------------------
    [Fact]
    public void Benchmark_ScenarioA_ColdVsWarmCatalog()
    {
        int enumCount = 0;
        int N = 5;

        // 1. Cold Build
        var swCold = Stopwatch.StartNew();
        using (var svcA = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                Interlocked.Increment(ref enumCount);
                Thread.Sleep(20); // Simulate authoritative source scan latency
                return MakeSyntheticCatalog(50);
            }))
        {
            var coldSnap = svcA.GetSnapshot();
            swCold.Stop();
            Assert.Equal(50, coldSnap.Apps.Count);
            Assert.Equal(1, svcA.Telemetry.CatalogColdBuilds);
        }

        // 2. Warm Loads across N restarts
        var warmTimes = new List<double>();
        for (int i = 0; i < N; i++)
        {
            var swWarm = Stopwatch.StartNew();
            using (var svcWarm = new ApplicationCatalogService(
                storageDirectory: _tempDir,
                clock: GetClock,
                enumerator: () =>
                {
                    Interlocked.Increment(ref enumCount);
                    return MakeSyntheticCatalog(50);
                }))
            {
                var warmSnap = svcWarm.GetSnapshot();
                swWarm.Stop();
                warmTimes.Add(swWarm.Elapsed.TotalMilliseconds);
                Assert.Equal(50, warmSnap.Apps.Count);
                Assert.Equal(1, svcWarm.Telemetry.CatalogWarmStarts);
                Assert.Equal(1, svcWarm.Telemetry.CatalogDiskLoads);
            }
        }

        // Ensure enumeration count stayed at 1 across all warm restarts!
        Assert.Equal(1, enumCount);

        double avgWarmMs = warmTimes.Average();
        _output.WriteLine($"[Scenario A: Cold vs Warm Catalog]");
        _output.WriteLine($"  Classification: Production-path synthetic");
        _output.WriteLine($"  N: {N}");
        _output.WriteLine($"  Cold Build Time: {swCold.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"  Warm Load Avg Time: {avgWarmMs:F2} ms (Speedup: {swCold.Elapsed.TotalMilliseconds / Math.Max(0.1, avgWarmMs):F1}x)");
        _output.WriteLine($"  Authoritative Enumerations: 1 (Avoided: {N})");
        _output.WriteLine($"  Entry Count: 50");
    }

    // -------------------------------------------------------------
    // Scenario B: Repeated Launch Resolution Benchmark (N >= 100)
    // -------------------------------------------------------------
    [Fact]
    public void Benchmark_ScenarioB_RepeatedResolution()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeSyntheticCatalog(20));
        catSvc.GetSnapshot();

        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock, identityValidator: _ => true);

        int fullComputations = 0;
        Func<(AppService.ResolvedApp?, bool, List<AppService.ResolvedApp>?)> resolver = () =>
        {
            Interlocked.Increment(ref fullComputations);
            return (new AppService.ResolvedApp(LaunchMethod.StartMenu, "C:\\App1.exe", "ProductivityApp_1", ["app1"], 100), false, null);
        };

        const int N = 200;
        var latencies = new List<double>(N);

        for (int i = 0; i < N; i++)
        {
            var sw = Stopwatch.StartNew();
            var res = cache.GetOrResolve("productivityapp_1", false, false, resolver);
            sw.Stop();
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            Assert.NotNull(res.Resolved);
        }

        latencies.Sort();
        double p50 = latencies[(int)(N * 0.50)];
        double p90 = latencies[(int)(N * 0.90)];
        double p99 = latencies[(int)(N * 0.99)];

        Assert.Equal(1, fullComputations); // Full computation happened exactly ONCE!
        Assert.Equal(N - 1, cache.Telemetry.LaunchResolutionHits);

        _output.WriteLine($"[Scenario B: Repeated Launch Resolution]");
        _output.WriteLine($"  Classification: Production-path synthetic");
        _output.WriteLine($"  Requests N: {N}");
        _output.WriteLine($"  Full Computations: {fullComputations}");
        _output.WriteLine($"  Cache Hits: {cache.Telemetry.LaunchResolutionHits}");
        _output.WriteLine($"  p50: {p50:F4} ms");
        _output.WriteLine($"  p90: {p90:F4} ms");
        _output.WriteLine($"  p99: {p99:F4} ms");
    }

    // -------------------------------------------------------------
    // Scenario C: Invalidation Benchmark (Stale Hit Count = 0)
    // -------------------------------------------------------------
    [Fact]
    public void Benchmark_ScenarioC_InvalidationSafety()
    {
        int enumCount = 0;
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            Interlocked.Increment(ref enumCount);
            return MakeSyntheticCatalog(10);
        });
        catSvc.GetSnapshot();

        int targetGen = 1;
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock, identityValidator: entry => entry.CatalogGeneration == targetGen);

        int resolveComputations = 0;
        Func<(AppService.ResolvedApp?, bool, List<AppService.ResolvedApp>?)> resolver = () =>
        {
            Interlocked.Increment(ref resolveComputations);
            return (new AppService.ResolvedApp(LaunchMethod.StartMenu, $"C:\\App_Gen{targetGen}.exe", $"AppGen{targetGen}", ["app"], 100), false, null);
        };

        // 1. Initial resolution (Miss)
        var r1 = cache.GetOrResolve("app", false, false, resolver);
        Assert.Equal("C:\\App_Gen1.exe", r1.Resolved!.Identifier);

        // 2. Warm hits before invalidation
        int hitsBefore = 0;
        for (int i = 0; i < 50; i++)
        {
            var r = cache.GetOrResolve("app", false, false, resolver);
            if (r.Resolved!.Identifier == "C:\\App_Gen1.exe")
                hitsBefore++;
        }
        Assert.Equal(50, hitsBefore);

        // 3. Trigger Invalidation!
        targetGen = 2;
        catSvc.Invalidate("Source update");
        catSvc.GetSnapshot(); // bump generation

        // 4. Next resolutions after invalidation
        int freshResolutions = 0;
        int staleHits = 0;
        for (int i = 0; i < 50; i++)
        {
            var r = cache.GetOrResolve("app", false, false, resolver);
            if (r.Resolved!.Identifier == "C:\\App_Gen2.exe")
                freshResolutions++;
            else
                staleHits++;
        }

        Assert.Equal(0, staleHits); // HARD CRITERION: Stale hit count must be exactly 0!
        Assert.Equal(50, freshResolutions);

        _output.WriteLine($"[Scenario C: Invalidation Benchmark]");
        _output.WriteLine($"  Classification: Production-path synthetic");
        _output.WriteLine($"  Hits Before Invalidation: {hitsBefore}");
        _output.WriteLine($"  Invalidations: 1");
        _output.WriteLine($"  Fresh Resolutions: {freshResolutions}");
        _output.WriteLine($"  Stale Hit Count: {staleHits} (Required: 0)");
    }

    // -------------------------------------------------------------
    // Scenario D: Concurrent Cold-Start Benchmark (Single-Flight Producer)
    // -------------------------------------------------------------
    [Fact]
    public async Task Benchmark_ScenarioD_ConcurrentColdStart()
    {
        int enumCount = 0;
        const int ConcurrentConsumers = 25;

        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            Interlocked.Increment(ref enumCount);
            Thread.Sleep(30); // Simulate enumeration work
            return MakeSyntheticCatalog(20);
        });

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, ConcurrentConsumers)
            .Select(_ => Task.Run(() => catSvc.GetSnapshot()))
            .ToArray();

        var results = await Task.WhenAll(tasks);
        sw.Stop();

        Assert.All(results, snap =>
        {
            Assert.NotNull(snap);
            Assert.Equal(20, snap.Apps.Count);
        });

        // Exactly ONE producer was executed!
        Assert.Equal(1, enumCount);
        Assert.Equal(ConcurrentConsumers - 1, catSvc.Telemetry.CatalogSingleFlightReuses);

        _output.WriteLine($"[Scenario D: Concurrent Cold Start]");
        _output.WriteLine($"  Classification: Production-path synthetic");
        _output.WriteLine($"  Concurrent Consumers: {ConcurrentConsumers}");
        _output.WriteLine($"  Total Time: {sw.Elapsed.TotalMilliseconds:F2} ms");
        _output.WriteLine($"  Authoritative Enumerations: {enumCount} (Expected: 1)");
        _output.WriteLine($"  Single-Flight Reuses: {catSvc.Telemetry.CatalogSingleFlightReuses}");
        _output.WriteLine($"  Completed Consumers: {results.Length}");
    }
}
