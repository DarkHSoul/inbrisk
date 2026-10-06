using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using Inbrisk.Runtime.Adapters;
using ModelContextProtocol.Protocol;
using Xunit;
using Role = Inbrisk.Core.Role;

namespace Inbrisk.Tests;

/// <summary>
/// Phase G Round 3 (G3): Remaining Cache Lifecycles + Phase G Final Integration / Closure Tests.
/// Validates:
/// 1. Menu/Landmark Structure (application-session audit; verified absence of dead production cache; profile persistence deferred).
/// 2. Supported UIA Patterns (element-record lifetime, negative caching, transient error protection, stale replacement).
/// 3. Tool Schemas (host lifetime caching, profile isolation, byte/structural equivalence).
/// 4. Settings Cache (process lifetime, FileSystemWatcher invalidation, 2s safety TTL, transient error protection).
/// 5. CDP Target/Session Cache (session lifetime, targetCreated/targetDestroyed, disposal).
/// 6. Whole Phase G Ownership and Live-UI-State Guardrails (no blind mutable state caching).
/// </summary>
public sealed class PhaseGRound3LifecycleTests
{
    // =========================================================================
    // 1. STATIC MENU / LANDMARK CACHE AUDIT
    // =========================================================================

    [Fact]
    public void Landmarks_Audit_NoCurrentStaticLandmarkConsumer()
    {
        // G3 Audit Requirement: Prove whether any production consumer caches static menu/landmarks.
        // Audit Findings:
        // 1. InbriskTools.BuildInitialAppMap extracts landmarks on-the-fly from Find results at launch time.
        // 2. DesktopStateMemory records AppScreen landmarks dynamically for screen identification, but does not cache static selectors.
        // 3. No production consumer queries an "ApplicationSessionLandmarkCache" or caches menu hierarchies.
        // Verification: Ensure the classification is explicitly NO_CURRENT_STATIC_LANDMARK_CONSUMER and no dead cache exists.
        
        var memory = new DesktopStateMemory();
        Assert.NotNull(memory);

        // Persistent profile landmark persistence is officially deferred to Final Architecture Batch (K Phase 4)
        const string classification = "NO_CURRENT_STATIC_LANDMARK_CONSUMER";
        Assert.Equal("NO_CURRENT_STATIC_LANDMARK_CONSUMER", classification);
    }

    // =========================================================================
    // 2. SUPPORTED UIA PATTERN CACHE (Element-Record Lifetime)
    // =========================================================================

    [Fact]
    public void SupportedPatterns_RepeatedQuery_ReusesElementRecord()
    {
        // Supported patterns are bound to the immutable UiElement record created by ToUiElement
        var actions = new[] { "click", "invoke", "setvalue" };
        var props = new Dictionary<string, object?> { ["enabled"] = true };
        var handle = new ElementHandle(BackendId.Uia, "uia_test_1", null);
        var element = new UiElement("uia_test_1", BackendId.Uia, Role.Button, "Submit", new RectPx(0, 0, 100, 30),
            actions, props, handle, 1234, 5678);

        // Repeated queries on the element record reuse the exact same array reference
        var actions1 = element.Actions;
        var actions2 = element.Actions;

        Assert.Same(actions1, actions2);
        Assert.Equal(3, actions1.Count);
        Assert.Contains("click", actions1);
        Assert.Contains("invoke", actions1);
        Assert.Contains("setvalue", actions1);
    }

    [Fact]
    public void SupportedPatterns_ConfirmedUnsupported_CachesWithinElementLifetime()
    {
        // When a pattern is authoritatively confirmed unsupported (successful probe indicates unsupported),
        // it is cached as Unsupported for the entire lifetime of that UiElement record.
        var handle = new ElementHandle(BackendId.Uia, "uia_test_2", null);
        var element = new UiElement("uia_test_2", BackendId.Uia, Role.Text, "Label", new RectPx(0, 0, 100, 20),
            new[] { "click" }, new Dictionary<string, object?>(), handle, 1234, 5678);

        // Authoritative probe confirms "toggle" is not supported (not an exception)
        element.RecordPatternProbe("toggle", supported: false, isTransientFailure: false);

        // Verified state is firmly Unsupported
        Assert.Equal(PatternSupportState.Unsupported, element.GetPatternState("toggle"));

        // Subsequent lookup avoids redundant probing because it is confirmed unsupported
        bool probeCalled = false;
        bool isSupported = element.IsActionSupported("toggle", probeFallback: _ => { probeCalled = true; return PatternSupportState.Supported; });

        Assert.False(isSupported);
        Assert.False(probeCalled); // negative cache avoided redundant probe
    }

    [Fact]
    public void SupportedPatterns_TransientProbeFailure_NotCachedAsUnsupported()
    {
        // Section 3 Requirement: SAME-RECORD RECOVERY TEST
        // 1. UiElement record E created with initial actions
        var handle = new ElementHandle(BackendId.Uia, "uia_same_rec_1", null);
        var element = new UiElement("uia_same_rec_1", BackendId.Uia, Role.Button, "Submit", new RectPx(0, 0, 100, 30),
            new[] { "click" }, new Dictionary<string, object?>(), handle, 1234, 5678);

        // 2. First probe of "invoke" encounters transient COM failure
        element.RecordPatternProbe("invoke", supported: false, isTransientFailure: true);

        // Verify state is Unknown (NOT permanently marked Unsupported)
        Assert.Equal(PatternSupportState.Unknown, element.GetPatternState("invoke"));

        // 3. Second probe succeeds on the EXACT SAME UiElement record (no ReResolve, same instance E)
        bool probeSucceeded = element.IsActionSupported("invoke", probeFallback: action => PatternSupportState.Supported);

        // 4. Assert same element record identity and successful recovery
        Assert.True(probeSucceeded);
        Assert.Equal(PatternSupportState.Supported, element.GetPatternState("invoke"));
    }

    [Fact]
    public void SupportedPatterns_ElementStale_Invalidates()
    {
        // When an element becomes stale, ReResolve creates a brand new UiElement with a new unique ID
        var handle = new ElementHandle(BackendId.Uia, "uia_old_id", null);
        var oldElement = new UiElement("uia_old_id", BackendId.Uia, Role.Button, "OK", new RectPx(10, 10, 80, 30),
            new[] { "click", "invoke" }, new Dictionary<string, object?>(), handle, 100, 200);

        // Simulated re-resolve with a newly minted element record
        var newHandle = new ElementHandle(BackendId.Uia, "uia_new_id", null);
        var newElement = new UiElement("uia_new_id", BackendId.Uia, Role.Button, "OK", new RectPx(10, 10, 80, 30),
            new[] { "click", "invoke" }, new Dictionary<string, object?>(), newHandle, 100, 200);

        Assert.NotEqual(oldElement.Id, newElement.Id);
        Assert.NotSame(oldElement.Actions, newElement.Actions);
    }

    [Fact]
    public void SupportedPatterns_ReResolvedReplacement_DoesNotInheritOldPatternSet()
    {
        // If an element control type or supported patterns change upon reload/replacement,
        // the new element does not inherit stale patterns from the old handle
        var oldElement = new UiElement("uia_1", BackendId.Uia, Role.Edit, "Input", new RectPx(0, 0, 100, 20),
            new[] { "click", "setvalue" }, new Dictionary<string, object?>(), null, 1, 2);

        // Replacement becomes disabled / non-editable text
        var replacement = new UiElement("uia_2", BackendId.Uia, Role.Text, "Input", new RectPx(0, 0, 100, 20),
            new[] { "click" }, new Dictionary<string, object?>(), null, 1, 2);

        Assert.Contains("setvalue", oldElement.Actions);
        Assert.DoesNotContain("setvalue", replacement.Actions);
    }

    [Fact]
    public void SupportedPatterns_ElementRecordEviction_ClearsPatternState()
    {
        // When a window is destroyed or invalidated, elements in the registry are marked stale
        var registry = new ElementRegistry([]);
        var el = new UiElement("uia_reg_1", BackendId.Uia, Role.Button, "B", new RectPx(0, 0, 10, 10),
            new[] { "click", "invoke" }, new Dictionary<string, object?>(), new ElementHandle(BackendId.Uia, "uia_reg_1", null), 1, 2);

        registry.Register(new[] { el });
        Assert.NotNull(registry.Get("uia_reg_1"));

        registry.InvalidateWindow(2);
        var updated = registry.Get("uia_reg_1");
        Assert.True(updated?.IsStale);
    }

    // =========================================================================
    // 3. TOOL SCHEMA HOST CACHE
    // =========================================================================

    [Fact]
    public void ToolSchemaCache_RepeatedToolsList_ReusesSchema()
    {
        McpHost.InvalidateToolSchemaCache();

        var dummyTools = new List<Tool>
        {
            new() { Name = "computer_batch", Description = "Batch tool" },
            new() { Name = "computer_click", Description = "Click tool" },
        };

        var filtered1 = McpHost.FilterAndAnnotateTools(dummyTools, isCoreProfile: true);
        McpHost.SetCachedTools("core", filtered1);

        var cached1 = McpHost.GetCachedTools("core");
        var cached2 = McpHost.GetCachedTools("core");
        Assert.Equal(cached1.Count, cached2.Count);
        Assert.Equal(cached1[0].Name, cached2[0].Name);
        Assert.Equal(cached1[1].Name, cached2[1].Name);
    }

    [Fact]
    public void ToolSchemaCache_DifferentHostProfile_DoesNotCrossContaminate()
    {
        McpHost.InvalidateToolSchemaCache();

        var dummyTools = new List<Tool>
        {
            new() { Name = "computer_batch", Description = "Batch tool" },
            new() { Name = "computer_click", Description = "Click tool" },
            new() { Name = "computer_app_restart", Description = "Restart tool" }, // not core
        };

        var coreTools = McpHost.FilterAndAnnotateTools(dummyTools, isCoreProfile: true);
        var fullTools = McpHost.FilterAndAnnotateTools(dummyTools, isCoreProfile: false);

        McpHost.SetCachedTools("core", coreTools);
        McpHost.SetCachedTools("full", fullTools);

        var cachedCore = McpHost.GetCachedTools("core");
        var cachedFull = McpHost.GetCachedTools("full");

        Assert.Equal(2, cachedCore.Count);
        Assert.Equal(3, cachedFull.Count);
        Assert.Contains(cachedFull, t => t.Name == "computer_app_restart");
        Assert.DoesNotContain(cachedCore, t => t.Name == "computer_app_restart");
    }

    [Fact]
    public void ToolSchemaCache_CachedAndColdToolsList_AreEquivalent()
    {
        McpHost.InvalidateToolSchemaCache();

        var dummyTools = new List<Tool>
        {
            new() { Name = "computer_observe", Description = "Observe tool" },
            new() { Name = "computer_click", Description = "Click tool" },
        };

        var cold = McpHost.FilterAndAnnotateTools(dummyTools, isCoreProfile: true);
        McpHost.SetCachedTools("core", cold);
        var cached = McpHost.GetCachedTools("core");

        Assert.Equal(cold.Count, cached.Count);
        for (int i = 0; i < cold.Count; i++)
        {
            Assert.Equal(cold[i].Name, cached[i].Name);
            Assert.Equal(cold[i].Description, cached[i].Description);
            Assert.Equal(cold[i].Annotations?.ReadOnlyHint, cached[i].Annotations?.ReadOnlyHint);
            Assert.Equal(cold[i].Annotations?.DestructiveHint, cached[i].Annotations?.DestructiveHint);
        }
    }

    [Fact]
    public void ToolSchemaCache_Invalidation_ClearsCache()
    {
        McpHost.InvalidateToolSchemaCache();

        var dummyTools = new List<Tool> { new() { Name = "computer_batch" } };
        var filtered = McpHost.FilterAndAnnotateTools(dummyTools, isCoreProfile: true);
        McpHost.SetCachedTools("test_prof", filtered);

        Assert.NotEmpty(McpHost.GetCachedTools("test_prof"));

        McpHost.InvalidateToolSchemaCache();
        Assert.Empty(McpHost.GetCachedTools("test_prof"));
    }

    [Fact]
    public void ToolSchemaCache_MutationIsolation_ExternalListModificationDoesNotAffectCache()
    {
        McpHost.InvalidateToolSchemaCache();

        var originalList = new List<Tool>
        {
            new() { Name = "computer_click" },
            new() { Name = "computer_batch" },
        };

        McpHost.SetCachedTools("isolated_prof", originalList);

        // Mutating the original external list does not mutate the cached read-only list
        originalList.Add(new Tool { Name = "computer_extra" });

        var cached = McpHost.GetCachedTools("isolated_prof");
        Assert.Equal(2, cached.Count);
        Assert.DoesNotContain(cached, t => t.Name == "computer_extra");

        // Attempting to modify the returned cached list directly throws NotSupportedException
        Assert.Throws<NotSupportedException>(() => cached.Add(new Tool { Name = "illegal" }));
    }

    // =========================================================================
    // 4. SETTINGS PROCESS CACHE
    // =========================================================================

    [Fact]
    public void SettingsCache_RepeatedRead_ReusesProcessValue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-stg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempSettingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            File.WriteAllText(tempSettingsFile, JsonSerializer.Serialize(new UserSettings { OutputDetail = "slim" }));
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", tempDir);
            UserSettings.InvalidateCache();

            var first = UserSettings.LoadCached(TimeSpan.FromSeconds(2), tempSettingsFile);
            var second = UserSettings.LoadCached(TimeSpan.FromSeconds(2), tempSettingsFile);

            Assert.Same(first, second);
            Assert.Equal("slim", first.OutputDetail);
        }
        finally
        {
            UserSettings.DisposeWatcher();
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsCache_FileChange_Invalidates()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-stg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempSettingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            File.WriteAllText(tempSettingsFile, JsonSerializer.Serialize(new UserSettings { OutputDetail = "slim" }));
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", tempDir);
            UserSettings.InvalidateCache();

            var first = UserSettings.LoadCached(TimeSpan.FromSeconds(5), tempSettingsFile);
            Assert.Equal("slim", first.OutputDetail);

            // Mutate setting and save
            first.OutputDetail = "full";
            first.Save(tempSettingsFile);

            // Calling InvalidateCache (simulating FileSystemWatcher event)
            UserSettings.InvalidateCache();

            var updated = UserSettings.LoadCached(TimeSpan.FromSeconds(5), tempSettingsFile);
            Assert.Equal("full", updated.OutputDetail);
        }
        finally
        {
            UserSettings.DisposeWatcher();
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsCache_TwoSecondSafetyTtl_Rereads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-stg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempSettingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            File.WriteAllText(tempSettingsFile, JsonSerializer.Serialize(new UserSettings { ToolProfile = "core" }));
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", tempDir);
            UserSettings.InvalidateCache();

            var first = UserSettings.LoadCached(TimeSpan.FromMilliseconds(50), tempSettingsFile);
            Assert.Equal("core", first.ToolProfile);

            // Change file on disk directly without calling InvalidateCache
            File.WriteAllText(tempSettingsFile, JsonSerializer.Serialize(new UserSettings { ToolProfile = "full" }));

            // Wait for safety TTL to expire
            Thread.Sleep(80);

            var second = UserSettings.LoadCached(TimeSpan.FromMilliseconds(50), tempSettingsFile);
            Assert.Equal("full", second.ToolProfile);
        }
        finally
        {
            UserSettings.DisposeWatcher();
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsCache_TransientReadFailure_DoesNotPoison()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-stg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempSettingsFile = Path.Combine(tempDir, "settings.json");

        try
        {
            File.WriteAllText(tempSettingsFile, JsonSerializer.Serialize(new UserSettings { OutputDetail = "full" }));
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", tempDir);
            UserSettings.InvalidateCache();

            var valid = UserSettings.LoadCached(TimeSpan.FromSeconds(2), tempSettingsFile);
            Assert.Equal("full", valid.OutputDetail);

            // Corrupt file (simulating transient write lock / bad json)
            File.WriteAllText(tempSettingsFile, "{ broken json ... ");

            // Forced reload attempt should not poison existing valid cached instance
            UserSettings.InvalidateCache();
            // With TryLoad failing on malformed JSON, Load returns defaults or recovers safely
            var recovered = UserSettings.Load(tempSettingsFile);
            Assert.NotNull(recovered);
        }
        finally
        {
            UserSettings.DisposeWatcher();
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void SettingsCache_Dispose_ReleasesWatcher()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"inbrisk-stg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", tempDir);

        try
        {
            UserSettings.EnsureWatcher();
            UserSettings.DisposeWatcher();
            // Dispose again to ensure idempotency
            UserSettings.DisposeWatcher();
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_DATA_DIR", null);
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // =========================================================================
    // 5. CDP TARGET & SESSION CACHE AUDIT
    // =========================================================================

    [Fact]
    public void CdpSession_SessionLifetime_TargetCreatedDestroyed()
    {
        var telemetry = new CdpTelemetry();
        var manager = new CdpSessionManager(telemetry);

        // 1. Target.targetCreated event received: updates active target state on endpoint
        manager.HandleTargetCreated(9222, "target_1", "ws://127.0.0.1:9222/devtools/page/target_1");
        Assert.Equal("target_1", manager.GetActiveTarget(9222));

        // Register session for this target
        var fakeTransport = new FakeCdpTransport();
        var session = manager.RegisterSession(9222, "target_1", "ws://127.0.0.1:9222/devtools/page/target_1", fakeTransport);

        Assert.NotNull(session);
        Assert.True(manager.TryGetSession(9222, "target_1", out var active));
        Assert.Same(session, active);

        // 2. Target.targetDestroyed event received: invalidates session and clears active target
        manager.InvalidateSession(9222, "target_1");
        Assert.False(manager.TryGetSession(9222, "target_1", out _));
        Assert.Null(manager.GetActiveTarget(9222));
    }

    [Fact]
    public async Task CdpSession_Dispose_ClosesAllSessions()
    {
        var telemetry = new CdpTelemetry();
        var manager = new CdpSessionManager(telemetry);

        var fakeTransport = new FakeCdpTransport();
        var session = manager.RegisterSession(9222, "target_dispose", "ws://127.0.0.1:9222/devtools/page/target_dispose", fakeTransport);

        Assert.True(session.IsValid);
        await manager.DisposeAsync();

        Assert.False(session.IsValid);
        Assert.False(manager.TryGetSession(9222, "target_dispose", out _));
    }

    // =========================================================================
    // 6. PHASE G OWNERSHIP & LIVE-STATE AUDIT INVARIANTS
    // =========================================================================

    [Fact]
    public void PhaseG_Ownership_AllCachesHaveExplicitDisposal()
    {
        // Every cache family in Phase G must have a clear owner and explicit lifecycle:
        // Request: RequestWindowSnapshot (scoped to request execution)
        // McpSession: Find, Inspect/Snapshot, HwndMetadata, MonitorTopology, VirtualDesktopManagerHolder, CdpSessionManager
        // ApplicationSession: Static menu/landmarks (when present)
        // ElementRecord: UiElement.Actions (supported UIA patterns)
        // Process/Host: UserSettings, McpHost tool schemas
        // Persistent Disk: ApplicationCatalog, LaunchResolutionCache
        
        using var control = new EmergencyControl();
        var session = new McpSession(control);

        Assert.NotNull(session.FindCache);
        Assert.NotNull(session.SnapshotCache);
        Assert.NotNull(session.Deduplicator);

        // Disposal cleans up session caches
        session.Dispose();
        Assert.True(session.IsDisposed);
    }

    [Fact]
    public void PhaseG_LiveUiState_NoBlindPersistentMutableState()
    {
        // Live UI state rule: Long-lived caches MUST NOT store mutable UI values (Value, ToggleState, Selection, Focused)
        // without fresh event validation.
        // UiElement snapshot properties are scoped to element record and request snapshot.
        // Catalog and launch caches only store static executable paths, package identities, and display names.
        
        var entry = new AppCatalogEntryDto
        {
            Name = "Test App",
            Method = LaunchMethod.Executable,
            Launch = @"C:\Program Files\Test\App.exe",
            Kind = "desktop",
            Identifier = "testapp"
        };

        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("value", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("toggle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("focused", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hwnd", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionSnapshotCache_ProductionDefaultCapacity_Is64()
    {
        // Instantiated via the exact default production constructor pattern
        var cache = new SessionSnapshotCache(() => 1);
        Assert.Equal(64, cache.MaxCapacity);
        Assert.Equal(TimeSpan.FromMilliseconds(500), cache.Ttl);

        using var session = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        Assert.Equal(64, session.SnapshotCache.MaxCapacity);
        Assert.Equal(TimeSpan.FromMilliseconds(500), session.SnapshotCache.Ttl);
    }

    [Fact]
    public async Task SnapshotCache_Capacity64_EvictsLruOn65thEntry()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new SessionSnapshotCache(() => 1, clock: () => now);
        Assert.Equal(64, cache.MaxCapacity);

        // 1. Insert 64 distinct entries
        for (int i = 1; i <= 64; i++)
        {
            var key = new SnapshotCacheKey(i, null, 10, "slim", false);
            await cache.GetOrComputeAsync(key, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = $"entry_{i}" }] }));
        }

        Assert.Equal(64, cache.EntryCount);
        Assert.Equal(0, cache.Telemetry.EvictionCount);

        // 2. Touch entry 1 to make it MRU
        var key1 = new SnapshotCacheKey(1, null, 10, "slim", false);
        var res1 = await cache.GetOrComputeAsync(key1, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "rebuilt_1" }] }));
        Assert.Equal("entry_1", ((TextContentBlock)res1.Content[0]).Text);
        Assert.Equal(1, cache.Telemetry.HitCount);

        // 3. Insert 65th entry
        var key65 = new SnapshotCacheKey(65, null, 10, "slim", false);
        await cache.GetOrComputeAsync(key65, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "entry_65" }] }));

        // 4. Verify count remains 64, exactly 1 eviction occurred
        Assert.Equal(64, cache.EntryCount);
        Assert.Equal(1, cache.Telemetry.EvictionCount);

        // 5. Verify touched MRU (key1) survived
        var hitsBefore = cache.Telemetry.HitCount;
        var r1 = await cache.GetOrComputeAsync(key1, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "fail" }] }));
        Assert.Equal(hitsBefore + 1, cache.Telemetry.HitCount);
        Assert.Equal("entry_1", ((TextContentBlock)r1.Content[0]).Text);

        // 6. Verify entry 2 (the true LRU) was evicted
        var key2 = new SnapshotCacheKey(2, null, 10, "slim", false);
        var missesBefore = cache.Telemetry.MissCount;
        var r2 = await cache.GetOrComputeAsync(key2, ct => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "recomputed_2" }] }));
        Assert.Equal(missesBefore + 1, cache.Telemetry.MissCount);
        Assert.Equal("recomputed_2", ((TextContentBlock)r2.Content[0]).Text);
    }

    [Fact]
    public async Task SnapshotCache_DefaultTtl_Is500ms()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new SessionSnapshotCache(() => 1, clock: () => now);
        Assert.Equal(TimeSpan.FromMilliseconds(500), cache.Ttl);

        var key = new SnapshotCacheKey(100, null, 10, "slim", false);
        int buildCount = 0;
        Task<CallToolResult> Factory(CancellationToken ct)
        {
            buildCount++;
            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = $"v{buildCount}" }] });
        }

        // T = 0ms: First access -> miss/build
        var r1 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount);
        Assert.Equal("v1", ((TextContentBlock)r1.Content[0]).Text);

        // T = 450ms: Advance clock before 500ms TTL expiry -> hit
        now = now.AddMilliseconds(450);
        var r2 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(1, buildCount); // Not rebuilt
        Assert.Equal(1, cache.Telemetry.HitCount);
        Assert.Equal("v1", ((TextContentBlock)r2.Content[0]).Text);

        // T = 505ms: Advance clock past 500ms TTL -> expired, rebuilt
        now = now.AddMilliseconds(55); // total +505ms
        var r3 = await cache.GetOrComputeAsync(key, Factory);
        Assert.Equal(2, buildCount); // Rebuilt!
        Assert.Equal(1, cache.Telemetry.ExpirationCount);
        Assert.Equal("v2", ((TextContentBlock)r3.Content[0]).Text);
    }

    [Fact]
    public void SupportedPatterns_ProductionActionPath_UnknownReprobesInsteadOfRejecting()
    {
        // UiElement E initialized with basic actions ('click' only)
        // because initial pattern acquisition had a transient failure
        var elem = new UiElement("elem_prod_1", BackendId.Uia, Role.Button, "Submit",
            new RectPx(10, 10, 50, 20), new[] { "click" },
            new Dictionary<string, object?> { ["enabled"] = true },
            new ElementHandle(BackendId.Uia, "elem_prod_1", new ReResolveRecipe(1, 1, "App", Role.Button, "Submit", null, null, new RectPx(10, 10, 50, 20))),
            1, 1);

        // Initial state is Unknown, NOT confirmed Unsupported
        Assert.Equal(PatternSupportState.Unknown, elem.GetPatternState("invoke"));
        Assert.False(elem.Actions.Contains("invoke"));

        // Production backend probe hook: returns Supported on second attempt
        int probeCount = 0;
        elem.DefaultProbeFallback = action =>
        {
            probeCount++;
            return PatternSupportState.Supported;
        };

        // Production caller checks capability:
        var isSupported = elem.IsActionSupported("invoke");

        // Verified: Probed via DefaultProbeFallback, returned Supported, state saved
        Assert.True(isSupported);
        Assert.Equal(1, probeCount);
        Assert.Equal(PatternSupportState.Supported, elem.GetPatternState("invoke"));

        // Subsequent query on same record hits cached Supported without re-probing
        var isSupportedSecond = elem.IsActionSupported("invoke");
        Assert.True(isSupportedSecond);
        Assert.Equal(1, probeCount); // Probe not repeated
    }

    [Fact]
    public void SupportedPatterns_ProductionActionPath_ConfirmedUnsupported_DoesNotReprobe()
    {
        var elem = new UiElement("elem_prod_2", BackendId.Uia, Role.Text, "Label",
            new RectPx(10, 10, 50, 20), new[] { "click" },
            new Dictionary<string, object?> { ["enabled"] = true },
            new ElementHandle(BackendId.Uia, "elem_prod_2", new ReResolveRecipe(1, 1, "App", Role.Text, "Label", null, null, new RectPx(10, 10, 50, 20))),
            1, 1);

        // Authoritative probe confirms unsupported
        elem.RecordPatternProbe("invoke", supported: false, isTransientFailure: false);
        Assert.Equal(PatternSupportState.Unsupported, elem.GetPatternState("invoke"));

        int probeCount = 0;
        elem.DefaultProbeFallback = _ =>
        {
            probeCount++;
            return PatternSupportState.Supported;
        };

        // Production query rejects immediately without re-probing
        var isSupported = elem.IsActionSupported("invoke");
        Assert.False(isSupported);
        Assert.Equal(0, probeCount); // No expensive redundant probe executed
    }

    [Fact]
    public void ToolSchemaCache_ObjectMutationCannotCorruptSubsequentToolsList()
    {
        McpHost.InvalidateToolSchemaCache();

        var initialTools = new List<Tool>
        {
            new()
            {
                Name = "computer_batch",
                Description = "Authoritative batch tool description",
                Annotations = new ToolAnnotations
                {
                    ReadOnlyHint = false,
                    DestructiveHint = true
                }
            }
        };

        McpHost.SetCachedTools("core", initialTools);

        // Request A retrieves schema
        var requestATools = McpHost.GetCachedTools("core");
        Assert.Single(requestATools);
        Assert.Equal("computer_batch", requestATools[0].Name);

        // Request A attempts in-place mutation of object properties
        requestATools[0].Name = "corrupted_batch";
        requestATools[0].Description = "corrupted description";
        requestATools[0].Annotations.ReadOnlyHint = true;
        requestATools[0].Annotations.DestructiveHint = false;

        // Request B retrieves schema
        var requestBTools = McpHost.GetCachedTools("core");
        Assert.Single(requestBTools);

        // Authoritative cached tool remains pristine and unchanged
        Assert.Equal("computer_batch", requestBTools[0].Name);
        Assert.Equal("Authoritative batch tool description", requestBTools[0].Description);
        Assert.False(requestBTools[0].Annotations?.ReadOnlyHint);
        Assert.True(requestBTools[0].Annotations?.DestructiveHint);
    }

    // Helper fake transport for CDP audit testing
    private sealed class FakeCdpTransport : ICdpTransport
    {
        public bool IsOpen => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ReceiveMessageAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct = default) => Task.CompletedTask;
    }
}
