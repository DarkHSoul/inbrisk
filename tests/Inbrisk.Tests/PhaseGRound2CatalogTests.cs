using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Xunit;

namespace Inbrisk.Tests;

public sealed class PhaseGRound2CatalogTests : IDisposable
{
    private readonly string _tempDir;
    private DateTimeOffset _currentTime = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public PhaseGRound2CatalogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-g2-catalog-{Guid.NewGuid():N}");
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
    private void AdvanceTime(TimeSpan delta) => _currentTime = _currentTime.Add(delta);

    private static (IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint) MakeFakeCatalog(int count = 5)
    {
        var apps = new List<AppInfo>();
        var entries = new List<AppCatalogEntryDto>();
        for (int i = 1; i <= count; i++)
        {
            var name = $"App{i}";
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
        return (apps, entries, "fake-fp");
    }

    // -------------------------------------------------------------
    // Section 46: Catalog Correctness Tests
    // -------------------------------------------------------------

    [Fact]
    public void AppCatalog_FirstRun_ColdBuildsAndPersists()
    {
        int enumCount = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCount++;
                return MakeFakeCatalog(3);
            });

        Assert.Equal(0, svc.Telemetry.CatalogColdBuilds);
        var snapshot = svc.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot.Apps.Count);
        Assert.Equal(1, enumCount);
        Assert.Equal(1, svc.Telemetry.CatalogColdBuilds);
        Assert.True(File.Exists(svc.CatalogFilePath));

        // Content verification
        var content = File.ReadAllText(svc.CatalogFilePath);
        Assert.Contains("App1", content);
        Assert.Contains("FormatVersion", content);
    }

    [Fact]
    public void AppCatalog_ValidPersistentCache_WarmLoads()
    {
        // 1. Instance A writes catalog to disk
        using (var svcA = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () => MakeFakeCatalog(4)))
        {
            var s = svcA.GetSnapshot();
            Assert.Equal(4, s.Apps.Count);
        }

        // 2. Instance B starts with same temp dir, enumerator should not be called
        int enumCallsB = 0;
        using (var svcB = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCallsB++;
                return MakeFakeCatalog(10);
            }))
        {
            var snapshotB = svcB.GetSnapshot();
            Assert.NotNull(snapshotB);
            Assert.Equal(4, snapshotB.Apps.Count);
            Assert.Equal(0, enumCallsB); // Avoided cold enumeration!
            Assert.Equal(1, svcB.Telemetry.CatalogDiskLoads);
            Assert.Equal(1, svcB.Telemetry.CatalogWarmStarts);
            Assert.Equal(0, svcB.Telemetry.CatalogColdBuilds);
        }
    }

    [Fact]
    public void AppCatalog_WarmLoad_AvoidsFullEnumeration()
    {
        // Populate cache
        using (var svcA = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () => MakeFakeCatalog(2)))
        {
            _ = svcA.GetSnapshot();
        }

        bool enumeratorCalled = false;
        using (var svcB = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumeratorCalled = true;
                return MakeFakeCatalog(2);
            }))
        {
            var snap = svcB.GetSnapshot();
            Assert.NotNull(snap);
            Assert.False(enumeratorCalled);
        }
    }

    [Fact]
    public void AppCatalog_CorruptPersistentCache_RebuildsSafely()
    {
        // Write corrupt JSON
        var catalogFile = Path.Combine(_tempDir, "app-catalog.json");
        File.WriteAllText(catalogFile, "{ \"Corrupt\": true, \"FormatVersion\": \"invalid_type_break\" ... truncated");

        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(5);
            });

        var snap = svc.GetSnapshot();
        Assert.NotNull(snap);
        Assert.Equal(5, snap.Apps.Count);
        Assert.Equal(1, enumCalls);
        Assert.Equal(1, svc.Telemetry.CatalogDiskLoadFailures);
        Assert.Equal(1, svc.Telemetry.CatalogColdBuilds);

        // Repaired valid replacement persisted
        var repairedText = File.ReadAllText(catalogFile);
        Assert.Contains("App1", repairedText);
    }

    [Fact]
    public void AppCatalog_UnsupportedFormatVersion_Rebuilds()
    {
        // Write version 999
        var catalogFile = Path.Combine(_tempDir, "app-catalog.json");
        var dto = new AppCatalogDto
        {
            FormatVersion = 999,
            Generation = 1,
            CreatedAtUtc = GetClock(),
            Entries = new List<AppCatalogEntryDto>()
        };
        File.WriteAllText(catalogFile, JsonSerializer.Serialize(dto));

        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(3);
            });

        var snap = svc.GetSnapshot();
        Assert.NotNull(snap);
        Assert.Equal(3, snap.Apps.Count);
        Assert.Equal(1, svc.Telemetry.CatalogDiskLoadFailures);
        Assert.Equal(1, enumCalls);
    }

    [Fact]
    public void AppCatalog_Expired24hCache_Rebuilds()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        var snap1 = svc.GetSnapshot();
        Assert.Equal(1, snap1.Apps.Count);
        Assert.Equal(1, enumCalls);

        // Advance clock by 23 hours -> still warm hit
        AdvanceTime(TimeSpan.FromHours(23));
        var snap2 = svc.GetSnapshot();
        Assert.Same(snap1, snap2);
        Assert.Equal(1, enumCalls);

        // Advance clock by 2 more hours (total 25 hours >= 24h safety TTL) -> must rebuild!
        AdvanceTime(TimeSpan.FromHours(2));
        var snap3 = svc.GetSnapshot();
        Assert.NotSame(snap1, snap3);
        Assert.Equal(2, snap3.Apps.Count);
        Assert.Equal(2, enumCalls);
        Assert.True(svc.Telemetry.CatalogTtlInvalidations >= 1);
    }

    [Fact]
    public void AppCatalog_StartMenuCreate_Invalidates()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        var snap1 = svc.GetSnapshot();
        Assert.Equal(1, snap1.Apps.Count);

        // Simulate created shortcut event
        svc.TriggerWatcherEventForTest();
        Assert.True(svc.Telemetry.CatalogWatcherInvalidations >= 1);

        // Next snapshot request reflects dirty generation
        var snap2 = svc.GetSnapshot();
        Assert.True(snap2.Generation > snap1.Generation);
        Assert.Equal(2, enumCalls);
    }

    [Fact]
    public void AppCatalog_StartMenuDelete_Invalidates()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        _ = svc.GetSnapshot();
        svc.TriggerWatcherEventForTest();
        _ = svc.GetSnapshot();

        Assert.Equal(2, enumCalls);
    }

    [Fact]
    public void AppCatalog_StartMenuRename_Invalidates()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        _ = svc.GetSnapshot();
        svc.TriggerWatcherEventForTest();
        _ = svc.GetSnapshot();

        Assert.Equal(2, enumCalls);
    }

    [Fact]
    public async Task AppCatalog_WatcherEventStorm_CoalescesRefresh()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                Interlocked.Increment(ref enumCalls);
                return MakeFakeCatalog(3);
            });

        _ = svc.GetSnapshot();
        Assert.Equal(1, enumCalls);

        // Fire 100 rapid events
        for (int i = 0; i < 100; i++)
        {
            svc.TriggerWatcherEventForTest();
        }

        // Wait for 100ms debounce timer to fire
        await Task.Delay(250);

        // The storm should coalesce into very few refreshes (1 or 2, far less than 100)
        Assert.True(enumCalls <= 3, $"Expected <= 3 enumerations for 100 events, got {enumCalls}");
        Assert.Equal(100, svc.Telemetry.CatalogWatcherInvalidations);
    }

    [Fact]
    public void AppCatalog_WatcherOverflow_ForcesFullRefresh()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        var s1 = svc.GetSnapshot();
        Assert.Equal(1, enumCalls);

        // Trigger overflow
        svc.TriggerWatcherOverflowForTest();
        Assert.Equal(1, svc.Telemetry.CatalogWatcherOverflowInvalidations);

        var s2 = svc.GetSnapshot();
        Assert.True(s2.Generation > s1.Generation);
        Assert.Equal(2, enumCalls);
    }

    [Fact]
    public async Task AppCatalog_SourceChangesDuringRefresh_StaleBuildNotPublished()
    {
        var buildStarted = new TaskCompletionSource<bool>();
        var allowBuildFinish = new TaskCompletionSource<bool>();

        ApplicationCatalogService? svcRef = null;

        var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                buildStarted.TrySetResult(true);
                allowBuildFinish.Task.Wait();
                return MakeFakeCatalog(1);
            });
        svcRef = svc;

        // Start refresh in background
        var refreshTask = Task.Run(() => svcRef.RefreshAsync());
        await buildStarted.Task;

        // Source changes while refresh is in flight!
        svc.Invalidate("Source changed during build");

        // Allow initial build to finish
        allowBuildFinish.SetResult(true);
        await refreshTask;

        // Stale publication was rejected!
        Assert.True(svc.Telemetry.CatalogStalePublishRejects >= 1);
        svc.Dispose();
    }

    [Fact]
    public async Task AppCatalog_ConcurrentRefreshRequests_SingleFlight()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                Interlocked.Increment(ref enumCalls);
                Thread.Sleep(50); // Simulate enumeration work
                return MakeFakeCatalog(3);
            });

        // 20 concurrent requests
        var tasks = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => svc.GetSnapshot()))
            .ToArray();

        var snapshots = await Task.WhenAll(tasks);

        Assert.All(snapshots, s => Assert.NotNull(s));
        Assert.Equal(1, enumCalls); // Only 1 producer executed!
        Assert.True(svc.Telemetry.CatalogSingleFlightReuses >= 1);
    }

    [Fact]
    public void AppCatalog_RefreshFailure_DoesNotPublishPartialCatalog()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                if (enumCalls == 1) return MakeFakeCatalog(3);
                throw new InvalidOperationException("Simulated authoritative source transient failure");
            });

        var initial = svc.GetSnapshot();
        Assert.Equal(3, initial.Apps.Count);

        // Invalidate to trigger refresh
        svc.Invalidate("Test");

        // Refresh throws internally
        var retained = svc.GetSnapshot();
        Assert.NotNull(retained);
        Assert.Equal(3, retained.Apps.Count); // Old snapshot retained!
        Assert.Equal(1, svc.Telemetry.CatalogRefreshFailures);
    }

    [Fact]
    public void AppCatalog_AtomicWrite_CrashDoesNotExposePartialFile()
    {
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () => MakeFakeCatalog(3));

        var snap = svc.GetSnapshot();
        Assert.True(File.Exists(svc.CatalogFilePath));

        // Simulate an orphaned crash temp file
        var crashTemp = Path.Combine(_tempDir, $"app-catalog.json.tmp.{Guid.NewGuid():N}");
        File.WriteAllText(crashTemp, "PARTIAL TRUNCATED CRASH WRITE");

        // Verify catalog file itself is still complete and valid
        var validDto = JsonSerializer.Deserialize<AppCatalogDto>(File.ReadAllText(svc.CatalogFilePath));
        Assert.NotNull(validDto);
        Assert.Equal(3, validDto.Entries.Count);
    }

    [Fact]
    public async Task AppCatalog_ConcurrentReadersNeverSeePartialWrite()
    {
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () => MakeFakeCatalog(10));

        svc.GetSnapshot(); // initial write

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readerErrors = new ConcurrentBag<Exception>();

        var readerTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(svc.CatalogFilePath))
                    {
                        using var stream = new FileStream(svc.CatalogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        using var doc = JsonDocument.Parse(stream);
                        Assert.True(doc.RootElement.GetProperty("Entries").GetArrayLength() > 0);
                    }
                }
                catch (IOException)
                {
                    // Sharing collision during atomic MoveFile replacement is transient Win32 behavior
                }
                catch (Exception ex)
                {
                    readerErrors.Add(ex);
                }
            }
        });

        var writerTask = Task.Run(() =>
        {
            int i = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                i++;
                var snap = new ApplicationCatalogSnapshot(
                    new List<AppInfo> { new($"App{i}", LaunchMethod.StartMenu, $"app:\"App{i}\"", "installed") },
                    new List<AppCatalogEntryDto> { new() { Name = $"App{i}", Method = LaunchMethod.StartMenu } },
                    i, GetClock());
                svc.SaveCatalogToDisk(snap);
                Thread.Sleep(5);
            }
        });

        await Task.WhenAll(readerTask, writerTask);
        Assert.Empty(readerErrors);
    }

    [Fact]
    public void AppCatalog_Shutdown_DisposesWatchersAndRefreshWork()
    {
        var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () => MakeFakeCatalog(2),
            startMenuDirs: [_tempDir]);

        svc.GetSnapshot();
        svc.Dispose();

        // Calling Dispose twice should be idempotent
        svc.Dispose();
    }

    // -------------------------------------------------------------
    // Section 47 & 48: App Paths & Package Invalidation Tests
    // -------------------------------------------------------------

    [Fact]
    public void AppCatalog_AppPathsChange_Invalidates()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        var s1 = svc.GetSnapshot();
        Assert.Equal(1, enumCalls);

        // App Paths adapter invalidation
        svc.Invalidate("AppPaths changed");
        var s2 = svc.GetSnapshot();

        Assert.True(s2.Generation > s1.Generation);
        Assert.Equal(2, enumCalls);
    }

    [Fact]
    public void AppCatalog_PackageChange_Invalidates()
    {
        int enumCalls = 0;
        using var svc = new ApplicationCatalogService(
            storageDirectory: _tempDir,
            clock: GetClock,
            enumerator: () =>
            {
                enumCalls++;
                return MakeFakeCatalog(enumCalls);
            });

        var s1 = svc.GetSnapshot();
        Assert.Equal(1, enumCalls);

        // Package adapter invalidation
        svc.Invalidate("Package changed");
        var s2 = svc.GetSnapshot();

        Assert.True(s2.Generation > s1.Generation);
        Assert.Equal(2, enumCalls);
    }

    // -------------------------------------------------------------
    // Section 49: Launch Resolution Tests
    // -------------------------------------------------------------

    [Fact]
    public void LaunchResolution_RepeatedEquivalentName_Hits()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock, identityValidator: _ => true);

        int resolveCalls = 0;
        Func<(AppService.ResolvedApp?, bool, List<AppService.ResolvedApp>?)> resolver = () =>
        {
            resolveCalls++;
            return (new AppService.ResolvedApp(LaunchMethod.StartMenu, "C:\\App1.exe", "App1", ["App1"], 100), false, null);
        };

        // First call: Miss
        var r1 = cache.GetOrResolve("app1", false, false, resolver);
        Assert.NotNull(r1.Resolved);
        Assert.Equal(1, resolveCalls);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionMisses);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionInserts);

        // Second call with case difference ("APP1"): Warm Hit
        var r2 = cache.GetOrResolve("APP1", false, false, resolver);
        Assert.NotNull(r2.Resolved);
        Assert.Equal(1, resolveCalls); // Resolver was NOT called!
        Assert.Equal(1, cache.Telemetry.LaunchResolutionHits);
    }

    [Fact]
    public void LaunchResolution_DifferentApps_DoNotCollide()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock);

        var r1 = cache.GetOrResolve("notepad", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.Executable, "notepad.exe", "Notepad", ["notepad"], 100), false, null));

        var r2 = cache.GetOrResolve("calc", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.Aumid, "Microsoft.Calculator!App", "Calculator", ["calc"], 100), false, null));

        Assert.Equal("notepad.exe", r1.Resolved!.Identifier);
        Assert.Equal("Microsoft.Calculator!App", r2.Resolved!.Identifier);
        Assert.Equal(2, cache.EntryCount);
    }

    [Fact]
    public void LaunchResolution_KeyParameterDifferences_DoNotCollide()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock);

        var r1 = cache.GetOrResolve("app1", exactOnly: false, newInstance: false, () =>
            (new AppService.ResolvedApp(LaunchMethod.StartMenu, "app1.exe", "App1", ["app1"], 100), false, null));

        var r2 = cache.GetOrResolve("app1", exactOnly: true, newInstance: false, () =>
            (new AppService.ResolvedApp(LaunchMethod.StartMenu, "app1_exact.exe", "App1Exact", ["app1"], 100), false, null));

        var r3 = cache.GetOrResolve("app1", exactOnly: false, newInstance: true, () =>
            (new AppService.ResolvedApp(LaunchMethod.StartMenu, "app1_new.exe", "App1New", ["app1"], 100), false, null));

        Assert.Equal("app1.exe", r1.Resolved!.Identifier);
        Assert.Equal("app1_exact.exe", r2.Resolved!.Identifier);
        Assert.Equal("app1_new.exe", r3.Resolved!.Identifier);
        Assert.Equal(3, cache.EntryCount);
    }

    [Fact]
    public void LaunchResolution_AmbiguousInput_RemainsAmbiguous()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock);

        int resolveCalls = 0;
        var ambigList = new List<AppService.ResolvedApp>
        {
            new(LaunchMethod.StartMenu, "C:\\AppA.exe", "AppA", ["AppA"], 100),
            new(LaunchMethod.StartMenu, "C:\\AppB.exe", "AppB", ["AppB"], 100)
        };

        Func<(AppService.ResolvedApp?, bool, List<AppService.ResolvedApp>?)> resolver = () =>
        {
            resolveCalls++;
            return (null, true, ambigList);
        };

        var r1 = cache.GetOrResolve("app", false, false, resolver);
        Assert.True(r1.IsAmbiguous);
        Assert.Null(r1.Resolved);
        Assert.Equal(2, r1.AmbiguousCandidates!.Count);

        // Ambiguous input MUST NOT be cached as a winner!
        Assert.Equal(0, cache.EntryCount);

        // Second call remains ambiguous and does not synthesize a winner
        var r2 = cache.GetOrResolve("app", false, false, resolver);
        Assert.True(r2.IsAmbiguous);
        Assert.Null(r2.Resolved);
        Assert.Equal(2, resolveCalls);
    }

    [Fact]
    public void LaunchResolution_StaleIdentity_EvictsAndReResolves()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));

        bool targetExists = true;
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock,
            identityValidator: entry => targetExists);

        int resolveCalls = 0;
        Func<(AppService.ResolvedApp?, bool, List<AppService.ResolvedApp>?)> resolver = () =>
        {
            resolveCalls++;
            var ident = targetExists ? "C:\\App_Valid.exe" : "C:\\App_NewValid.exe";
            return (new AppService.ResolvedApp(LaunchMethod.StartMenu, ident, "App", ["App"], 100), false, null);
        };

        var r1 = cache.GetOrResolve("app", false, false, resolver);
        Assert.Equal("C:\\App_Valid.exe", r1.Resolved!.Identifier);
        Assert.Equal(1, resolveCalls);

        // Now target is deleted / uninstalled
        targetExists = false;

        // Next call detects stale identity, evicts, and re-resolves!
        var r2 = cache.GetOrResolve("app", false, false, resolver);
        Assert.Equal("C:\\App_NewValid.exe", r2.Resolved!.Identifier);
        Assert.Equal(2, resolveCalls);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionStaleIdentityEvictions);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionReResolutions);
    }

    [Fact]
    public void LaunchResolution_CatalogInvalidation_EvictsAffectedEntry()
    {
        using var catSvc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(catSvc, _tempDir, GetClock);

        _ = cache.GetOrResolve("app1", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.StartMenu, "app1.exe", "App1", ["app1"], 100), false, null));

        Assert.Equal(1, cache.EntryCount);

        // Invalidate catalog -> triggers CatalogChanged
        catSvc.Invalidate("Source changed");
        catSvc.GetSnapshot(); // bump generation

        // The old entry should be evicted
        Assert.Equal(1, cache.Telemetry.LaunchResolutionInvalidations);
        Assert.Equal(0, cache.EntryCount);
    }

    [Fact]
    public void LaunchResolution_PersistsAcrossServiceRestart()
    {
        // Service instance A populates resolution cache
        using (var catA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        using (var cacheA = new LaunchResolutionCache(catA, _tempDir, GetClock, identityValidator: _ => true))
        {
            _ = cacheA.GetOrResolve("mytool", false, false, () =>
                (new AppService.ResolvedApp(LaunchMethod.Executable, "mytool.exe", "MyTool", ["mytool"], 100), false, null));
            Assert.Equal(1, cacheA.EntryCount);
        }

        // Service instance B starts with same temp dir
        int resolverCallsB = 0;
        using (var catB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        using (var cacheB = new LaunchResolutionCache(catB, _tempDir, GetClock, identityValidator: _ => true))
        {
            var res = cacheB.GetOrResolve("mytool", false, false, () =>
            {
                resolverCallsB++;
                return (new AppService.ResolvedApp(LaunchMethod.Executable, "mytool.exe", "MyTool", ["mytool"], 100), false, null);
            });

            Assert.NotNull(res.Resolved);
            Assert.Equal("mytool.exe", res.Resolved.Identifier);
            Assert.Equal(0, resolverCallsB); // Hit from disk!
            Assert.Equal(1, cacheB.Telemetry.LaunchResolutionHits);
        }
    }

    [Fact]
    public void LaunchResolution_CorruptPersistentData_DoesNotBreakLaunch()
    {
        // Write corrupt JSON to launch-cache.json
        var cacheFile = Path.Combine(_tempDir, "launch-cache.json");
        File.WriteAllText(cacheFile, "{ invalid json truncated...");

        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock);

        // Does not throw, resolves normally
        var res = cache.GetOrResolve("notepad", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.Executable, "notepad.exe", "Notepad", ["notepad"], 100), false, null));

        Assert.NotNull(res.Resolved);
        Assert.Equal("notepad.exe", res.Resolved.Identifier);
    }

    [Fact]
    public void LaunchResolution_TransientFailure_IsNotPersisted()
    {
        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock);

        var res = cache.GetOrResolve("unknown_random_app", false, false, () =>
            (null, false, null));

        Assert.Null(res.Resolved);
        Assert.Equal(0, cache.EntryCount);

        // File should not have entries
        if (File.Exists(cache.CacheFilePath))
        {
            var dto = JsonSerializer.Deserialize<LaunchCacheDto>(File.ReadAllText(cache.CacheFilePath));
            Assert.Empty(dto!.Entries);
        }
    }

    [Fact]
    public async Task LaunchResolution_ConcurrentEquivalentMisses_SingleFlight()
    {
        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock, identityValidator: _ => true);

        int resolverCount = 0;
        var tasks = Enumerable.Range(0, 15).Select(_ => Task.Run(async () =>
        {
            return await cache.GetOrResolveAsync("same_app", false, false, async () =>
            {
                Interlocked.Increment(ref resolverCount);
                await Task.Delay(50);
                return (new AppService.ResolvedApp(LaunchMethod.Executable, "same.exe", "Same", ["same"], 100), false, null);
            });
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.NotNull(r.Resolved));
        Assert.Equal(1, resolverCount); // Only 1 resolver execution!
    }

    // -------------------------------------------------------------
    // Section 50: Persistence Across Service Lifetime
    // -------------------------------------------------------------

    [Fact]
    public void AppCatalog_PersistenceAcrossServiceLifetime_NoFullEnumeration()
    {
        // Service instance A performs cold build and persists
        using (var svcA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(5)))
        {
            var snapA = svcA.GetSnapshot();
            Assert.Equal(5, snapA.Apps.Count);
            Assert.Equal(1, svcA.Telemetry.CatalogColdBuilds);
        }

        // Service instance B starts in same directory
        int enumCallsB = 0;
        using (var svcB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            enumCallsB++;
            return MakeFakeCatalog(10);
        }))
        {
            var snapB = svcB.GetSnapshot();
            Assert.Equal(5, snapB.Apps.Count);
            Assert.Equal(0, enumCallsB);
            Assert.Equal(1, svcB.Telemetry.CatalogWarmStarts);
        }
    }

    // -------------------------------------------------------------
    // Repair Round 1 Tests
    // -------------------------------------------------------------

    [Fact]
    public void AppCatalog_WarmRestart_DoesNotResetPersistentAge()
    {
        var t0 = _currentTime;
        int enumCallsA = 0;
        using (var svcA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            enumCallsA++;
            return MakeFakeCatalog(3);
        }))
        {
            var snapA = svcA.GetSnapshot();
            Assert.Equal(3, snapA.Apps.Count);
            Assert.Equal(1, enumCallsA);
            Assert.Equal(t0, snapA.CreatedAtUtc);
        }

        // T0 + 23h: service restarts. Warm catalog load should be accepted because 23h < 24h
        AdvanceTime(TimeSpan.FromHours(23));
        int enumCallsB = 0;
        using (var svcB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            enumCallsB++;
            return MakeFakeCatalog(10);
        }))
        {
            var snapB = svcB.GetSnapshot();
            Assert.Equal(3, snapB.Apps.Count); // From warm cache
            Assert.Equal(0, enumCallsB); // No enumeration
            Assert.Equal(t0, snapB.CreatedAtUtc); // CreatedAtUtc is preserved from disk!
            Assert.Equal(1, svcB.Telemetry.CatalogWarmStarts);
        }

        // T0 + 25h: service restarts. Old snapshot has age 25h >= 24h, must be rejected!
        AdvanceTime(TimeSpan.FromHours(2));
        int enumCallsC = 0;
        using (var svcC = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            enumCallsC++;
            return MakeFakeCatalog(10);
        }))
        {
            var snapC = svcC.GetSnapshot();
            Assert.Equal(10, snapC.Apps.Count); // From authoritative fresh rebuild
            Assert.Equal(1, enumCallsC); // Authoritative enumeration occurred!
            Assert.True(svcC.Telemetry.CatalogTtlInvalidations >= 1);
            Assert.Equal(_currentTime, snapC.CreatedAtUtc);
        }
    }

    [Fact]
    public void LaunchResolution_ProcessRestart_GenerationABA_DoesNotReuseStaleEntry()
    {
        // Service A: Catalog Snapshot A (Generation 1, SnapshotId GuidA)
        // Resolves "calc" -> returns App1
        using (var catA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        using (var cacheA = new LaunchResolutionCache(catA, _tempDir, GetClock, identityValidator: _ => true))
        {
            var resA = cacheA.GetOrResolve("calc", false, false, () =>
                (new AppService.ResolvedApp(LaunchMethod.Executable, "calc_v1.exe", "Calc", ["calc"], 100), false, null));
            Assert.NotNull(resA.Resolved);
            Assert.Equal("calc_v1.exe", resA.Resolved.Identifier);
            Assert.Equal(1, cacheA.Telemetry.LaunchResolutionMisses);
        }

        // Authoritative source changes while process is down!
        // Service B starts fresh in same directory. Even if local dirty generation happens to start at 1,
        // it produces a new Snapshot with a different SnapshotId (or source changes).
        int reResolutionCount = 0;
        using (var catB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(5)))
        {
            // Force a refresh so catB has a different snapshot ID than catA
            catB.RefreshAsync().GetAwaiter().GetResult();

            using var cacheB = new LaunchResolutionCache(catB, _tempDir, GetClock, identityValidator: _ => true);
            // On lookup, cacheB detects that the persisted entry has SnapshotId != catB.CurrentSnapshotId
            var resB = cacheB.GetOrResolve("calc", false, false, () =>
            {
                reResolutionCount++;
                return (new AppService.ResolvedApp(LaunchMethod.Executable, "calc_v2.exe", "Calc", ["calc"], 100), false, null);
            });

            Assert.NotNull(resB.Resolved);
            Assert.Equal("calc_v2.exe", resB.Resolved.Identifier);
            Assert.Equal(1, reResolutionCount); // Fresh resolution was triggered, stale ABA entry was rejected!
            Assert.Equal(0, cacheB.Telemetry.LaunchResolutionHits);
        }
    }

    [Fact]
    public void LaunchResolution_StaleAumidPackage_EvictsAndReResolves()
    {
        var packageAumid = "Vendor.App_12345!App";
        var pkgEntry = new AppCatalogEntryDto
        {
            Name = "VendorApp",
            Method = LaunchMethod.Aumid,
            Launch = $"aumid:\"{packageAumid}\"",
            Identifier = packageAumid,
            Kind = "package",
            Score = 100
        };

        var catalogData = new List<AppCatalogEntryDto> { pkgEntry };
        var appsData = new List<AppInfo> { new("VendorApp", LaunchMethod.Aumid, pkgEntry.Launch, "package") };

        bool packageInstalled = true;
        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => (appsData, catalogData, "pkg-fp"));
        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock, packageValidator: _ => packageInstalled);

        // 1. Initial resolution succeeds
        var res1 = cache.GetOrResolve("vendorapp", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.Aumid, packageAumid, "VendorApp", ["vendorapp"], 100), false, null));
        Assert.NotNull(res1.Resolved);
        Assert.Equal(packageAumid, res1.Resolved.Identifier);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionMisses);

        // 2. Second lookup hits cache while package is installed
        var res2 = cache.GetOrResolve("vendorapp", false, false, () =>
            throw new InvalidOperationException("Should not resolve"));
        Assert.NotNull(res2.Resolved);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionHits);

        // 3. Package is uninstalled while catalog snapshot is still loaded
        packageInstalled = false;

        // 4. Stale AUMID lookup:
        // Self-healing detects missing package identity, evicts the entry, and triggers re-resolution!
        bool resolverCalled = false;
        var res3 = cache.GetOrResolve("vendorapp", false, false, () =>
        {
            resolverCalled = true;
            // Package uninstalled: resolver returns not found
            return (null, false, null);
        });

        Assert.True(resolverCalled);
        Assert.Null(res3.Resolved);
        Assert.True(cache.Telemetry.LaunchResolutionStaleIdentityEvictions >= 1);
        Assert.True(cache.Telemetry.LaunchResolutionReResolutions >= 1);
    }

    [Fact]
    public void LaunchResolution_KeyIsDeterministicAcrossServiceRestart()
    {
        var args = "--profile test --flag=1";
        using (var catA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        using (var cacheA = new LaunchResolutionCache(catA, _tempDir, GetClock, identityValidator: _ => true))
        {
            var resA = cacheA.GetOrResolve("testapp", false, false, () =>
                (new AppService.ResolvedApp(LaunchMethod.Executable, "test.exe", "Test", ["test"], 100), false, null),
                args);
            Assert.NotNull(resA.Resolved);
        }

        // Restart service B in same directory with warm catalog
        using (var catB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        using (var cacheB = new LaunchResolutionCache(catB, _tempDir, GetClock, identityValidator: _ => true))
        {
            // Same key with different whitespace / casing should resolve to exact same canonical key and hit cache!
            var resB = cacheB.GetOrResolve(" TESTAPP ", false, false, () =>
                throw new InvalidOperationException("Should hit warm cache!"),
                "  --profile test --flag=1  ");

            Assert.NotNull(resB.Resolved);
            Assert.Equal("test.exe", resB.Resolved.Identifier);
            Assert.Equal(1, cacheB.Telemetry.LaunchResolutionHits);
        }
    }

    [Fact]
    public void LaunchResolution_KeyCanonicalization_SeparatorsAndArgumentsCollisionsAvoided()
    {
        var key1 = new LaunchResolutionKey("app", false, false, "arg1|exact=True|new=True");
        var key2 = new LaunchResolutionKey("app", true, true, "arg1");

        // Despite crafted separator injection into arguments, distinct keys must not collide!
        Assert.NotEqual(key1, key2);
        Assert.NotEqual(key1.GetHashCode(), key2.GetHashCode());
        Assert.NotEqual(key1.ToString(), key2.ToString());
    }

    [Fact]
    public void AppCatalog_ConcurrentWriters_OlderSnapshotCannotOverwriteNewerSnapshot()
    {
        using var svc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));

        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddMinutes(5);

        // Snapshot B (newer, created at t1)
        var snapB = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("NewerApp", LaunchMethod.Executable, "newer.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "NewerApp", Method = LaunchMethod.Executable, Identifier = "newer.exe" } },
            generation: 2,
            createdAtUtc: t1,
            snapshotId: "snapshot-B");

        // Snapshot A (older, created at t0)
        var snapA = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("OlderApp", LaunchMethod.Executable, "older.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "OlderApp", Method = LaunchMethod.Executable, Identifier = "older.exe" } },
            generation: 1,
            createdAtUtc: t0,
            snapshotId: "snapshot-A");

        // Writer B commits newer snapshot first
        svc.SaveCatalogToDisk(snapB);

        // Verify disk has newer snapshot
        var fileTextB = File.ReadAllText(svc.CatalogFilePath);
        Assert.Contains("NewerApp", fileTextB);

        // Writer A (older build started earlier, finished later) attempts to commit
        svc.SaveCatalogToDisk(snapA);

        // Disk MUST STILL have newer snapshot B!
        var fileTextFinal = File.ReadAllText(svc.CatalogFilePath);
        Assert.Contains("NewerApp", fileTextFinal);
        Assert.DoesNotContain("OlderApp", fileTextFinal);
        Assert.True(svc.Telemetry.CatalogStalePublishRejects >= 1);
    }

    // -------------------------------------------------------------
    // Repair Round 2 Tests
    // -------------------------------------------------------------

    [Fact]
    public void LaunchResolution_AppPathsTargetChanged_OldExecutableStillExists_EvictsAndReResolves()
    {
        var oldExe = Path.Combine(_tempDir, "old_target.exe");
        var newExe = Path.Combine(_tempDir, "new_target.exe");
        File.WriteAllText(oldExe, "old");
        File.WriteAllText(newExe, "new");

        string currentRegistryTarget = oldExe;

        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));
        using var cache = new LaunchResolutionCache(
            cat,
            _tempDir,
            GetClock,
            appPathResolver: _ => currentRegistryTarget);

        // 1. Initial resolution resolves to old target and caches
        var res1 = cache.GetOrResolve("testapp", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.AppPath, oldExe, "testapp", ["testapp.exe"], 100), false, null));
        Assert.NotNull(res1.Resolved);
        Assert.Equal(oldExe, res1.Resolved.Identifier);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionMisses);

        // 2. Second lookup hits cache while registry target unchanged
        var res2 = cache.GetOrResolve("testapp", false, false, () =>
            throw new InvalidOperationException("Should hit cache"));
        Assert.NotNull(res2.Resolved);
        Assert.Equal(oldExe, res2.Resolved.Identifier);
        Assert.Equal(1, cache.Telemetry.LaunchResolutionHits);

        // 3. Registry target changes to newExe, but oldExe STILL EXISTS on disk!
        currentRegistryTarget = newExe;

        // 4. Third lookup: targeted App Path check detects registry changed, evicts stale entry, re-resolves!
        var res3 = cache.GetOrResolve("testapp", false, false, () =>
            (new AppService.ResolvedApp(LaunchMethod.AppPath, newExe, "testapp", ["testapp.exe"], 100), false, null));

        Assert.NotNull(res3.Resolved);
        Assert.Equal(newExe, res3.Resolved.Identifier);
        Assert.True(cache.Telemetry.LaunchResolutionStaleIdentityEvictions >= 1);
        Assert.True(cache.Telemetry.LaunchResolutionReResolutions >= 1);
        // Hits stayed at 1, did not return stale identity
        Assert.Equal(1, cache.Telemetry.LaunchResolutionHits);
    }

    [Fact]
    public void LaunchResolution_NewAppPathsEntry_MissRefreshDiscoversApp()
    {
        bool newAppInstalled = false;
        var newExe = Path.Combine(_tempDir, "brandnew.exe");
        File.WriteAllText(newExe, "bin");

        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            var apps = new List<AppInfo>();
            var entries = new List<AppCatalogEntryDto>();
            if (newAppInstalled)
            {
                apps.Add(new AppInfo("brandnew", LaunchMethod.AppPath, $"executable:\"{newExe}\"", "installed"));
                entries.Add(new AppCatalogEntryDto { Name = "brandnew", Method = LaunchMethod.AppPath, Identifier = newExe, Score = 100 });
            }
            return (apps, entries, "fp");
        });

        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock);

        // Initial warm snapshot has no brandnew app
        cat.GetSnapshot();
        Assert.Equal(1, cat.Telemetry.CatalogColdBuilds);

        // New application is installed in authoritative source!
        newAppInstalled = true;

        // Resolver returns null if not found in catalog
        var res = cache.GetOrResolve("brandnew", false, false, () =>
        {
            var snap = cat.GetSnapshot();
            var found = snap.Entries.FirstOrDefault(e => e.Name.Equals("brandnew", StringComparison.OrdinalIgnoreCase));
            if (found != null)
                return (new AppService.ResolvedApp(LaunchMethod.AppPath, found.Identifier, "brandnew", ["brandnew"], 100), false, null);
            return (null, false, null);
        });

        // Bounded one-shot refresh occurred and discovered the new app!
        Assert.NotNull(res.Resolved);
        Assert.Equal(newExe, res.Resolved.Identifier);
        Assert.True(cat.Telemetry.CatalogRefreshes >= 1);
    }

    [Fact]
    public void LaunchResolution_NewPackage_MissRefreshDiscoversApp()
    {
        bool packageInstalled = false;
        var aumid = "Contoso.Calculator_8wekyb3d8bbwe!App";

        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            var apps = new List<AppInfo>();
            var entries = new List<AppCatalogEntryDto>();
            if (packageInstalled)
            {
                apps.Add(new AppInfo("Calculator", LaunchMethod.Aumid, $"aumid:\"{aumid}\"", "package"));
                entries.Add(new AppCatalogEntryDto { Name = "Calculator", Method = LaunchMethod.Aumid, Identifier = aumid, Score = 100 });
            }
            return (apps, entries, "fp");
        });

        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock, packageValidator: _ => packageInstalled);

        cat.GetSnapshot(); // warm snapshot without package

        // Package is installed
        packageInstalled = true;

        var res = cache.GetOrResolve("calculator", false, false, () =>
        {
            var snap = cat.GetSnapshot();
            var found = snap.Entries.FirstOrDefault(e => e.Name.Equals("calculator", StringComparison.OrdinalIgnoreCase));
            if (found != null)
                return (new AppService.ResolvedApp(LaunchMethod.Aumid, found.Identifier, "Calculator", ["calculator"], 100), false, null);
            return (null, false, null);
        });

        Assert.NotNull(res.Resolved);
        Assert.Equal(aumid, res.Resolved.Identifier);
        Assert.True(cat.Telemetry.CatalogRefreshes >= 1);
    }

    [Fact]
    public void LaunchResolution_NewStartMenuEntry_MissRefreshDiscoversApp()
    {
        bool shortcutAdded = false;
        var lnkPath = Path.Combine(_tempDir, "Editor.lnk");
        File.WriteAllText(lnkPath, "shortcut");

        using var cat = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            var apps = new List<AppInfo>();
            var entries = new List<AppCatalogEntryDto>();
            if (shortcutAdded)
            {
                apps.Add(new AppInfo("Editor", LaunchMethod.StartMenu, $"app:\"Editor\"", "installed"));
                entries.Add(new AppCatalogEntryDto { Name = "Editor", Method = LaunchMethod.StartMenu, Identifier = lnkPath, Score = 100 });
            }
            return (apps, entries, "fp");
        });

        using var cache = new LaunchResolutionCache(cat, _tempDir, GetClock);

        cat.GetSnapshot();

        // Shortcut added
        shortcutAdded = true;

        var res = cache.GetOrResolve("editor", false, false, () =>
        {
            var snap = cat.GetSnapshot();
            var found = snap.Entries.FirstOrDefault(e => e.Name.Equals("editor", StringComparison.OrdinalIgnoreCase));
            if (found != null)
                return (new AppService.ResolvedApp(LaunchMethod.StartMenu, found.Identifier, "Editor", ["editor"], 100), false, null);
            return (null, false, null);
        });

        Assert.NotNull(res.Resolved);
        Assert.Equal(lnkPath, res.Resolved.Identifier);
        Assert.True(cat.Telemetry.CatalogRefreshes >= 1);
    }

    [Fact]
    public void LaunchResolution_PackageUninstalledWhileServiceStopped_WarmCatalogDoesNotReturnStaleAumid()
    {
        var aumid = "Vendor.App_12345!App";
        var pkgEntry = new AppCatalogEntryDto
        {
            Name = "VendorApp",
            Method = LaunchMethod.Aumid,
            Identifier = aumid,
            Score = 100
        };

        bool isPackageLive = true;

        // Service A creates catalog, resolves package, persists catalog and launch cache
        using (var catA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
            (new List<AppInfo> { new("VendorApp", LaunchMethod.Aumid, aumid, "package") }, new List<AppCatalogEntryDto> { pkgEntry }, "fp")))
        using (var cacheA = new LaunchResolutionCache(catA, _tempDir, GetClock, packageValidator: _ => isPackageLive))
        {
            var resA = cacheA.GetOrResolve("vendorapp", false, false, () =>
                (new AppService.ResolvedApp(LaunchMethod.Aumid, aumid, "VendorApp", ["vendorapp"], 100), false, null));
            Assert.NotNull(resA.Resolved);
        }

        // Service is stopped. While stopped, package is uninstalled!
        // Warm catalog still has VendorApp, but live package validator returns false!
        isPackageLive = false;

        // Service B starts, warm-loads catalog (which still has VendorApp)
        int reResolutionCalls = 0;
        using (var catB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
            (new List<AppInfo>(), new List<AppCatalogEntryDto>(), "fp")))
        using (var cacheB = new LaunchResolutionCache(catB, _tempDir, GetClock, packageValidator: _ => isPackageLive))
        {
            // Lookup MUST NOT return stale AUMID solely because warm catalog has it!
            var resB = cacheB.GetOrResolve("vendorapp", false, false, () =>
            {
                reResolutionCalls++;
                // Uninstalled, resolver returns null
                return (null, false, null);
            });

            Assert.Null(resB.Resolved);
            Assert.True(cacheB.Telemetry.LaunchResolutionStaleIdentityEvictions >= 1);
            Assert.Equal(0, cacheB.Telemetry.LaunchResolutionHits);
        }
    }

    [Fact]
    public void AppCatalog_ConcurrentWriters_EqualTimestampOlderSourceCannotOverwriteNewer()
    {
        using var svc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));

        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        // Snapshot B (newer source snapshot, Revision = 2)
        var snapB = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("NewerApp", LaunchMethod.Executable, "newer.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "NewerApp", Method = LaunchMethod.Executable, Identifier = "newer.exe" } },
            generation: 2,
            createdAtUtc: t0,
            snapshotId: "snapshot-B",
            revision: 2);

        // Snapshot A (older source snapshot, Revision = 1), SAME CreatedAtUtc!
        var snapA = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("OlderApp", LaunchMethod.Executable, "older.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "OlderApp", Method = LaunchMethod.Executable, Identifier = "older.exe" } },
            generation: 1,
            createdAtUtc: t0,
            snapshotId: "snapshot-A",
            revision: 1);

        // Writer B commits newer revision first
        svc.SaveCatalogToDisk(snapB);

        // Writer A attempts commit with equal CreatedAtUtc
        svc.SaveCatalogToDisk(snapA);

        // Disk MUST STILL have newer snapshot B because Revision 2 > Revision 1!
        var fileTextFinal = File.ReadAllText(svc.CatalogFilePath);
        Assert.Contains("NewerApp", fileTextFinal);
        Assert.DoesNotContain("OlderApp", fileTextFinal);
        Assert.True(svc.Telemetry.CatalogStalePublishRejects >= 1);
    }

    [Fact]
    public void AppCatalog_ConcurrentWriters_ClockRollbackCannotMakeStaleSnapshotWin()
    {
        using var svc = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2));

        var t0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var tEarlier = t0.AddHours(-1); // Clock was rolled back!

        // Snapshot B (newer, Revision = 2, earlier timestamp)
        var snapB = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("NewerApp", LaunchMethod.Executable, "newer.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "NewerApp", Method = LaunchMethod.Executable, Identifier = "newer.exe" } },
            generation: 2,
            createdAtUtc: tEarlier,
            snapshotId: "snapshot-B",
            revision: 2);

        // Snapshot A (older, Revision = 1, later timestamp!)
        var snapA = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("OlderApp", LaunchMethod.Executable, "older.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "OlderApp", Method = LaunchMethod.Executable, Identifier = "older.exe" } },
            generation: 1,
            createdAtUtc: t0,
            snapshotId: "snapshot-A",
            revision: 1);

        // Writer B commits newer snapshot
        svc.SaveCatalogToDisk(snapB);

        // Writer A attempts commit with later timestamp
        svc.SaveCatalogToDisk(snapA);

        // Revision ordering prevails: disk MUST retain snapshot B!
        var fileTextFinal = File.ReadAllText(svc.CatalogFilePath);
        Assert.Contains("NewerApp", fileTextFinal);
        Assert.DoesNotContain("OlderApp", fileTextFinal);
        Assert.True(svc.Telemetry.CatalogStalePublishRejects >= 1);
    }

    // -------------------------------------------------------------
    // Repair Round 3 Tests
    // -------------------------------------------------------------

    [Fact]
    public async Task AppCatalog_ProductionRefreshAlgorithm_LateOlderProcessCannotOverwriteNewerSource()
    {
        string currentSourceApp = "OldApp";
        var oldStartedTcs = new TaskCompletionSource<bool>();
        var allowOldToCompleteTcs = new TaskCompletionSource<bool>();

        // Service A captures source via its production enumerator
        using var svcA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            var capturedAtStart = currentSourceApp;
            oldStartedTcs.TrySetResult(true);
            // Simulate slow or delayed process A
            allowOldToCompleteTcs.Task.GetAwaiter().GetResult();
            return (new List<AppInfo> { new(capturedAtStart, LaunchMethod.Executable, $"{capturedAtStart}.exe", "installed") },
                    new List<AppCatalogEntryDto> { new() { Name = capturedAtStart, Method = LaunchMethod.Executable, Identifier = $"{capturedAtStart}.exe" } },
                    "fp");
        });

        // 1. Service A begins refresh and acquires cross-process lock
        var taskA = Task.Run(async () => await svcA.RefreshAsync());
        await oldStartedTcs.Task; // Service A is now inside lock and running enumerator

        // 2. Authoritative source becomes NEW while A is running
        currentSourceApp = "NewApp";

        // 3. Service B starts refresh
        using var svcB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            return (new List<AppInfo> { new(currentSourceApp, LaunchMethod.Executable, $"{currentSourceApp}.exe", "installed") },
                    new List<AppCatalogEntryDto> { new() { Name = currentSourceApp, Method = LaunchMethod.Executable, Identifier = $"{currentSourceApp}.exe" } },
                    "fp");
        });

        // Allow Service A to complete its refresh
        allowOldToCompleteTcs.TrySetResult(true);
        var snapA = await taskA;
        Assert.Equal("OldApp", snapA.Entries[0].Name);

        // Service B now starts refresh for the new source state, captures NEW, assigns next revision, and publishes
        var snapB = await svcB.RefreshAsync();
        Assert.Equal("NewApp", snapB.Entries[0].Name);
        Assert.True(snapB.Revision > snapA.Revision);

        // 4. Verify that the final catalog on disk is NEW
        using var svcVerify = new ApplicationCatalogService(_tempDir, GetClock);
        var finalSnap = svcVerify.GetSnapshot();
        Assert.Equal("NewApp", finalSnap.Entries[0].Name);
        Assert.DoesNotContain("OldApp", File.ReadAllText(svcVerify.CatalogFilePath));

        // 5. What if Service A attempts late publication of its older snapshot?
        svcA.SaveCatalogToDisk(snapA);

        // Still NEW on disk, stale publish rejected!
        Assert.Contains("NewApp", File.ReadAllText(svcVerify.CatalogFilePath));
        Assert.DoesNotContain("OldApp", File.ReadAllText(svcVerify.CatalogFilePath));
        Assert.True(svcA.Telemetry.CatalogStalePublishRejects >= 1);
    }

    [Fact]
    public async Task AppCatalog_RevisionAssignedUnderExclusiveRefreshLock()
    {
        // Pre-seed disk with revision = 7
        var startingSnap = new ApplicationCatalogSnapshot(
            new List<AppInfo> { new("BaseApp", LaunchMethod.Executable, "base.exe", "installed") },
            new List<AppCatalogEntryDto> { new() { Name = "BaseApp", Method = LaunchMethod.Executable, Identifier = "base.exe" } },
            generation: 1,
            createdAtUtc: GetClock(),
            snapshotId: "seed-id",
            revision: 7);

        using (var seedSvc = new ApplicationCatalogService(_tempDir, GetClock))
        {
            seedSvc.SaveCatalogToDisk(startingSnap);
            Assert.Equal(7, seedSvc.ReadDiskRevision());
        }

        // Instance A performs refresh
        long revA;
        using (var svcA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(1)))
        {
            var snapA = await svcA.RefreshAsync();
            revA = snapA.Revision;
            Assert.Equal(8, revA);
            Assert.Equal(8, svcA.ReadDiskRevision());
        }

        // Separate Instance B performs refresh
        long revB;
        using (var svcB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(2)))
        {
            var snapB = await svcB.RefreshAsync();
            revB = snapB.Revision;
            Assert.Equal(9, revB);
            Assert.Equal(9, svcB.ReadDiskRevision());
        }

        // Separate Instance C performs refresh
        long revC;
        using (var svcC = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () => MakeFakeCatalog(3)))
        {
            var snapC = await svcC.RefreshAsync();
            revC = snapC.Revision;
            Assert.Equal(10, revC);
            Assert.Equal(10, svcC.ReadDiskRevision());
        }

        Assert.Equal(8, revA);
        Assert.Equal(9, revB);
        Assert.Equal(10, revC);
        Assert.True(revA < revB && revB < revC);
    }

    [Fact]
    public async Task AppCatalog_TwoProcesses_SameCacheDirectory_AreMutuallyExclusiveDuringRefresh()
    {
        var inA = false;
        var inB = false;
        var overlapDetected = false;

        var aEnteredTcs = new TaskCompletionSource<bool>();
        var allowAToFinishTcs = new TaskCompletionSource<bool>();

        using var svcA = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            inA = true;
            if (inB) overlapDetected = true;
            aEnteredTcs.TrySetResult(true);
            allowAToFinishTcs.Task.GetAwaiter().GetResult();
            if (inB) overlapDetected = true;
            inA = false;
            return MakeFakeCatalog(1);
        });

        using var svcB = new ApplicationCatalogService(_tempDir, GetClock, enumerator: () =>
        {
            inB = true;
            if (inA) overlapDetected = true;
            Thread.Sleep(20);
            if (inA) overlapDetected = true;
            inB = false;
            return MakeFakeCatalog(2);
        });

        var taskA = Task.Run(async () => await svcA.RefreshAsync());
        await aEnteredTcs.Task;
        Assert.True(inA);

        // While A is in its critical section, B attempts refresh
        var taskB = Task.Run(async () => await svcB.RefreshAsync());

        // Give B time to attempt acquisition
        await Task.Delay(100);
        Assert.False(inB, "Service B must NOT enter critical section while Service A holds lock");

        // Release A
        allowAToFinishTcs.TrySetResult(true);
        await Task.WhenAll(taskA, taskB);

        Assert.False(overlapDetected, "Overlap between exclusive refresh sections was detected!");
        Assert.True(svcB.Telemetry.CatalogRefreshLockWaits >= 1 || svcB.Telemetry.CatalogCrossProcessReuseCount >= 1);
    }
}
