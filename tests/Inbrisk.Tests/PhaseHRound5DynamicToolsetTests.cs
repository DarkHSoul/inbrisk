using Inbrisk.Mcp;
using Inbrisk.Mcp.DynamicToolset;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Phase H Round 5: Dynamic Toolset & listChanged Verification Tests.
/// Validates:
/// 1. Toolset_DefaultCoreIsDeterministic: Small stable core profile (~10-12 primitive tools).
/// 2. Toolset_BrowserContextActivatesBrowserSet: Contextual browser toolset activation on browser foreground.
/// 3. Toolset_ContextExitDeactivatesUnpinnedSet: Hysteresis and debounce on exit.
/// 4. Toolset_ExplicitPinPersistsAcrossFocusChanges: Explicit pinning persists across context changes.
/// 5. Toolset_NoEffectiveChange_NoListChanged: Zero notifications emitted on no-op recalculation.
/// 6. Toolset_RealChange_EmitsSingleListChanged: Exactly one list_changed emitted on actual visible set changes.
/// 7. Toolset_InFlightCallSurvivesDeactivation: In-flight tool call remains valid even if toolset deactivates midway.
/// 8. Toolset_SessionAActivationDoesNotLeakToSessionB: Strict logical session isolation.
/// 9. Toolset_SchemaCacheKeyIncludesVisibleSet: Cache key partitions by profile, active sets, generation, and compatibility.
/// 10. Toolset_AnnotationsRemainCorrect: Preserves ReadOnly, Idempotent, and Destructive hints across core & contextual tools.
/// 11. Toolset_CompatibilityModeRemainsUsable: Legacy compatibility mode provides deterministic usable toolset without flapping.
/// </summary>
public sealed class PhaseHRound5DynamicToolsetTests
{
    // =========================================================================
    // 1. Toolset_DefaultCoreIsDeterministic
    // =========================================================================
    [Fact]
    public void Toolset_DefaultCoreIsDeterministic()
    {
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);

        var visible = manager.GetVisibleToolNames();

        // Must include exactly the 16 small stable core desktop primitive tools
        Assert.Equal(16, visible.Count);
        Assert.Contains("computer_batch", visible);
        Assert.Contains("computer_do", visible);
        Assert.Contains("computer_run", visible);
        Assert.Contains("computer_launch", visible);
        Assert.Contains("computer_close_window", visible);
        Assert.Contains("computer_windows", visible);
        Assert.Contains("computer_observe", visible);
        Assert.Contains("computer_find", visible);
        Assert.Contains("computer_inspect", visible);
        Assert.Contains("computer_read", visible);
        Assert.Contains("computer_click", visible);
        Assert.Contains("computer_type", visible);
        Assert.Contains("computer_hotkey", visible);
        Assert.Contains("computer_screenshot", visible);
        Assert.Contains("computer_reset_input", visible);
        Assert.Contains("computer_capabilities", visible);

        // Optional browser tools must NOT be visible by default in core profile
        Assert.DoesNotContain("browser_browse", visible);
        Assert.DoesNotContain("browser_click", visible);
        Assert.DoesNotContain("browser_type", visible);
        Assert.DoesNotContain("browser_snapshot", visible);
        Assert.DoesNotContain("browser_tabs", visible);
        Assert.DoesNotContain("browser_content", visible);
        Assert.DoesNotContain("browser_screenshot", visible);

        // Zero overlap invariant between Core and Browser toolsets
        Assert.Empty(DynamicToolsetManager.DefaultCoreTools.Intersect(DynamicToolsetManager.BrowserToolset));

        // Repeated queries must be identical and deterministic
        var visible2 = manager.GetVisibleToolNames();
        Assert.True(visible.SetEquals(visible2));
    }

    // =========================================================================
    // 2. Toolset_BrowserContextActivatesBrowserSet
    // =========================================================================
    [Fact]
    public void Toolset_BrowserContextActivatesBrowserSet()
    {
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);

        var changed = manager.UpdateForegroundContext("chrome.exe", "Google Chrome - New Tab");
        Assert.True(changed);

        Assert.Contains("browser", manager.ActiveToolsets);

        var visible = manager.GetVisibleToolNames();
        Assert.Contains("browser_browse", visible);
        Assert.Contains("browser_click", visible);
        Assert.Contains("browser_type", visible);
        Assert.Contains("browser_snapshot", visible);
        Assert.Contains("browser_tabs", visible);
        Assert.Contains("browser_content", visible);
        Assert.Contains("browser_screenshot", visible);

        // Core tools must still remain visible, total tools = 16 + 7 = 23
        Assert.Equal(23, visible.Count);
        Assert.Contains("computer_click", visible);
        Assert.Contains("computer_observe", visible);
    }

    // =========================================================================
    // 3. Toolset_ContextExitDeactivatesUnpinnedSet
    // =========================================================================
    [Fact]
    public void Toolset_ContextExitDeactivatesUnpinnedSet()
    {
        var baseTime = DateTimeOffset.UtcNow;
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false)
        {
            DebounceDuration = TimeSpan.FromMilliseconds(300),
            TimeProvider = () => baseTime
        };

        // Activate browser context
        manager.UpdateForegroundContext("msedge.exe", "Microsoft Edge", baseTime);
        Assert.Contains("browser_browse", manager.GetVisibleToolNames());

        // Context switches to non-browser (notepad) at T+10ms
        var t1 = baseTime.AddMilliseconds(10);
        var changedImmediate = manager.UpdateForegroundContext("notepad.exe", "Untitled - Notepad", t1);

        // Immediate result: deactivation is debounced to prevent flapping; toolset still present
        Assert.False(changedImmediate);
        Assert.Contains("browser_browse", manager.GetVisibleToolNames());

        // Still within debounce window at T+200ms
        var t2 = baseTime.AddMilliseconds(200);
        var changedMid = manager.ProcessPendingDebounce(t2);
        Assert.False(changedMid);
        Assert.Contains("browser_browse", manager.GetVisibleToolNames());

        // Debounce expires at T+350ms
        var t3 = baseTime.AddMilliseconds(350);
        var changedExpired = manager.ProcessPendingDebounce(t3);
        Assert.True(changedExpired);

        var visibleAfterExit = manager.GetVisibleToolNames();
        Assert.DoesNotContain("browser_browse", visibleAfterExit);
        Assert.DoesNotContain("browser_click", visibleAfterExit);
        Assert.Contains("computer_click", visibleAfterExit);
    }

    // =========================================================================
    // 4. Toolset_ExplicitPinPersistsAcrossFocusChanges
    // =========================================================================
    [Fact]
    public void Toolset_ExplicitPinPersistsAcrossFocusChanges()
    {
        var baseTime = DateTimeOffset.UtcNow;
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false)
        {
            DebounceDuration = TimeSpan.FromMilliseconds(200),
            TimeProvider = () => baseTime
        };

        // Explicitly pin "browser" set
        var pinResult = manager.PinToolset("browser");
        Assert.True(pinResult);
        Assert.True(manager.IsPinned("browser"));
        Assert.Contains("browser_browse", manager.GetVisibleToolNames());

        // Foreground changes to non-browser app (calc)
        var t1 = baseTime.AddMilliseconds(50);
        manager.UpdateForegroundContext("calc.exe", "Calculator", t1);

        // Advance time well past debounce window
        var t2 = baseTime.AddSeconds(10);
        manager.ProcessPendingDebounce(t2, force: true);

        // Pinned set remains active and visible across focus changes
        var visiblePinned = manager.GetVisibleToolNames();
        Assert.Contains("browser_browse", visiblePinned);
        Assert.Contains("browser_click", visiblePinned);

        // Unpinning allows deactivation when outside browser context
        var unpinResult = manager.UnpinToolset("browser");
        Assert.True(unpinResult);
        Assert.False(manager.IsPinned("browser"));

        var visibleUnpinned = manager.GetVisibleToolNames();
        Assert.DoesNotContain("browser_browse", visibleUnpinned);
    }

    // =========================================================================
    // 5. Toolset_NoEffectiveChange_NoListChanged
    // =========================================================================
    [Fact]
    public void Toolset_NoEffectiveChange_NoListChanged()
    {
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);
        int notificationCount = 0;
        manager.ToolsListChanged += (_, _) => notificationCount++;

        // Switch between two non-browser applications (no effective visible toolset change)
        var c1 = manager.UpdateForegroundContext("notepad.exe", "Untitled - Notepad");
        Assert.False(c1);
        Assert.Equal(0, notificationCount);

        var c2 = manager.UpdateForegroundContext("calc.exe", "Calculator");
        Assert.False(c2);
        Assert.Equal(0, notificationCount);

        // Switch to browser: 1 real change occurs
        var c3 = manager.UpdateForegroundContext("chrome.exe", "Google Chrome");
        Assert.True(c3);
        Assert.Equal(1, notificationCount);

        // Switch between two different browser windows: effective visible toolset is still core + browser
        var c4 = manager.UpdateForegroundContext("msedge.exe", "Edge Window 2");
        Assert.False(c4);
        Assert.Equal(1, notificationCount);
    }

    // =========================================================================
    // 6. Toolset_RealChange_EmitsSingleListChanged
    // =========================================================================
    [Fact]
    public void Toolset_RealChange_EmitsSingleListChanged()
    {
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);
        int eventCount = 0;
        IReadOnlySet<string>? latestSet = null;

        manager.ToolsListChanged += (_, args) =>
        {
            eventCount++;
            latestSet = args.VisibleTools;
        };

        // Initial state has 0 emitted events
        Assert.Equal(0, eventCount);

        // Focus switches to browser -> triggers real change
        var changed = manager.UpdateForegroundContext("brave.exe", "Brave Browser");
        Assert.True(changed);
        Assert.Equal(1, eventCount);
        Assert.NotNull(latestSet);
        Assert.Contains("browser_snapshot", latestSet);

        // Duplicate update with same process does not re-emit
        var changedAgain = manager.UpdateForegroundContext("brave.exe", "Brave Browser - Tab 2");
        Assert.False(changedAgain);
        Assert.Equal(1, eventCount);
    }

    // =========================================================================
    // 7. Toolset_InFlightCallSurvivesDeactivation
    // =========================================================================
    [Fact]
    public void Toolset_InFlightCallSurvivesDeactivation()
    {
        var baseTime = DateTimeOffset.UtcNow;
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false)
        {
            DebounceDuration = TimeSpan.Zero, // Immediate deactivation for deterministic testing
            TimeProvider = () => baseTime
        };

        // In browser context: browser tools are active
        manager.UpdateForegroundContext("chrome.exe", "Chrome");
        Assert.True(manager.IsToolVisible("browser_click"));

        // Begin an in-flight tool call for browser_click
        using (var inFlightToken = manager.BeginToolCall("browser_click"))
        {
            Assert.True(inFlightToken.IsValid);
            Assert.Equal("browser_click", inFlightToken.ToolName);
            Assert.Equal(1, manager.ActiveInFlightCallsCount);

            // While call is in flight, user alt-tabs to notepad and browser set deactivates
            manager.UpdateForegroundContext("notepad.exe", "Notepad");

            // For new callers, browser_click is no longer visible or available
            Assert.False(manager.IsToolVisible("browser_click"));
            Assert.False(manager.IsToolAvailable("browser_click"));

            // BUT for the ongoing in-flight call, the token and execution remain fully valid and safe
            Assert.True(inFlightToken.IsValid);
            Assert.True(manager.IsToolAvailable("browser_click", inFlightToken));
        }

        // After token disposal, in-flight call count resets to 0
        Assert.Equal(0, manager.ActiveInFlightCallsCount);
    }

    // =========================================================================
    // 8. Toolset_SessionAActivationDoesNotLeakToSessionB
    // =========================================================================
    [Fact]
    public void Toolset_SessionAActivationDoesNotLeakToSessionB()
    {
        using var sessionA = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);
        using var sessionB = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: false);

        // Session A pins browser toolset
        sessionA.PinToolset("browser");
        Assert.True(sessionA.IsToolVisible("browser_browse"));
        Assert.Contains("browser", sessionA.ActiveToolsets);

        // Session B must remain in default core profile without browser tools
        Assert.False(sessionB.IsToolVisible("browser_browse"));
        Assert.DoesNotContain("browser", sessionB.ActiveToolsets);
        Assert.Contains("computer_click", sessionB.GetVisibleToolNames());
        Assert.DoesNotContain("browser_click", sessionB.GetVisibleToolNames());

        // McpSession integration test: verify session-scoped isolation
        using var mcpSessionA = new McpSession(EmergencyControl.ForTests(), startEvents: false);
        using var mcpSessionB = new McpSession(EmergencyControl.ForTests(), startEvents: false);

        mcpSessionA.DynamicToolset.PinToolset("browser");
        Assert.True(mcpSessionA.DynamicToolset.IsToolVisible("browser_browse"));
        Assert.False(mcpSessionB.DynamicToolset.IsToolVisible("browser_browse"));
    }

    // =========================================================================
    // 9. Toolset_SchemaCacheKeyIncludesVisibleSet
    // =========================================================================
    [Fact]
    public void Toolset_SchemaCacheKeyIncludesVisibleSet()
    {
        var keyCore = ToolsetCacheKey.Create("core", Array.Empty<string>(), schemaGeneration: 1, compatibilityMode: false);
        var keyBrowser = ToolsetCacheKey.Create("core", new[] { "browser" }, schemaGeneration: 1, compatibilityMode: false);
        var keyFull = ToolsetCacheKey.Create("full", Array.Empty<string>(), schemaGeneration: 1, compatibilityMode: false);
        var keyGen2 = ToolsetCacheKey.Create("core", Array.Empty<string>(), schemaGeneration: 2, compatibilityMode: false);
        var keyCompat = ToolsetCacheKey.Create("core", Array.Empty<string>(), schemaGeneration: 1, compatibilityMode: true);

        // All distinct configurations produce different cache keys
        Assert.NotEqual(keyCore, keyBrowser);
        Assert.NotEqual(keyCore, keyFull);
        Assert.NotEqual(keyCore, keyGen2);
        Assert.NotEqual(keyCore, keyCompat);

        // Canonical sorting guarantees identical keys regardless of input order
        var keyUnsorted1 = ToolsetCacheKey.Create("core", new[] { "browser", "custom_set" }, 1, false);
        var keyUnsorted2 = ToolsetCacheKey.Create("core", new[] { "custom_set", "browser" }, 1, false);
        Assert.Equal(keyUnsorted1, keyUnsorted2);

        // ToolsetSchemaCache behavior
        var cache = new ToolsetSchemaCache();
        var dummyToolsCore = new List<Tool> { new() { Name = "computer_click" } };
        var dummyToolsBrowser = new List<Tool> { new() { Name = "computer_click" }, new() { Name = "browser_click" } };

        cache.Set(keyCore, dummyToolsCore);
        cache.Set(keyBrowser, dummyToolsBrowser);

        Assert.True(cache.TryGet(keyCore, out var cachedCore));
        Assert.True(cache.TryGet(keyBrowser, out var cachedBrowser));
        Assert.Single(cachedCore);
        Assert.Equal(2, cachedBrowser.Count);

        // Invalidation increments generation and purges cached entries
        var genBefore = cache.CurrentGeneration;
        cache.Invalidate();
        Assert.True(cache.CurrentGeneration > genBefore);
        Assert.False(cache.TryGet(keyCore, out _));
    }

    // =========================================================================
    // 10. Toolset_AnnotationsRemainCorrect
    // =========================================================================
    [Fact]
    public void Toolset_AnnotationsRemainCorrect()
    {
        var rawTools = new List<Tool>
        {
            new() { Name = "computer_observe" },
            new() { Name = "computer_windows" },
            new() { Name = "computer_click" },
            new() { Name = "computer_batch" },
            new() { Name = "browser_snapshot" },
            new() { Name = "browser_tabs" },
            new() { Name = "browser_content" },
            new() { Name = "browser_screenshot" },
            new() { Name = "browser_click" },
            new() { Name = "browser_type" },
            new() { Name = "computer_toolset" },
        };

        var annotated = DynamicToolsetManager.ApplyAnnotations(rawTools);

        // ReadOnly & Idempotent tool verification
        var obs = annotated.First(t => t.Name == "computer_observe");
        Assert.True(obs.Annotations?.ReadOnlyHint);
        Assert.True(obs.Annotations?.IdempotentHint);
        Assert.Null(obs.Annotations?.DestructiveHint);

        var snap = annotated.First(t => t.Name == "browser_snapshot");
        Assert.True(snap.Annotations?.ReadOnlyHint);
        Assert.True(snap.Annotations?.IdempotentHint);
        Assert.Null(snap.Annotations?.DestructiveHint);

        var tabs = annotated.First(t => t.Name == "browser_tabs");
        Assert.True(tabs.Annotations?.ReadOnlyHint);
        Assert.True(tabs.Annotations?.IdempotentHint);

        var content = annotated.First(t => t.Name == "browser_content");
        Assert.True(content.Annotations?.ReadOnlyHint);
        Assert.True(content.Annotations?.IdempotentHint);

        var browserShot = annotated.First(t => t.Name == "browser_screenshot");
        Assert.True(browserShot.Annotations?.ReadOnlyHint);
        Assert.True(browserShot.Annotations?.IdempotentHint);

        // Destructive mutating tool verification
        var click = annotated.First(t => t.Name == "computer_click");
        Assert.True(click.Annotations?.DestructiveHint);
        Assert.Null(click.Annotations?.ReadOnlyHint);

        var batch = annotated.First(t => t.Name == "computer_batch");
        Assert.True(batch.Annotations?.DestructiveHint);

        var bClick = annotated.First(t => t.Name == "browser_click");
        Assert.True(bClick.Annotations?.DestructiveHint);
        Assert.Null(bClick.Annotations?.ReadOnlyHint);

        var bType = annotated.First(t => t.Name == "browser_type");
        Assert.True(bType.Annotations?.DestructiveHint);

        // Idempotent configuration tool
        var toolset = annotated.First(t => t.Name == "computer_toolset");
        Assert.True(toolset.Annotations?.IdempotentHint);
        Assert.Null(toolset.Annotations?.DestructiveHint);
    }

    // =========================================================================
    // 11. Toolset_CompatibilityModeRemainsUsable
    // =========================================================================
    [Fact]
    public void Toolset_CompatibilityModeRemainsUsable()
    {
        // Clients without dynamic list capabilities require a deterministic, stable profile
        using var manager = new DynamicToolsetManager(baseProfile: "core", compatibilityMode: true);

        var initialTools = manager.GetVisibleToolNames();

        // Must contain both core and contextual browser tools upfront
        Assert.Contains("computer_launch", initialTools);
        Assert.Contains("computer_click", initialTools);
        Assert.Contains("browser_browse", initialTools);
        Assert.Contains("browser_click", initialTools);
        Assert.Contains("browser_snapshot", initialTools);

        // Foreground switches away from browser to notepad
        manager.UpdateForegroundContext("notepad.exe", "Untitled - Notepad");
        manager.ProcessPendingDebounce(DateTimeOffset.UtcNow.AddMinutes(5), force: true);

        // In compatibility mode, tools are never dynamically retracted/flapped
        var toolsAfterSwitch = manager.GetVisibleToolNames();
        Assert.True(initialTools.SetEquals(toolsAfterSwitch));
        Assert.Contains("browser_browse", toolsAfterSwitch);
        Assert.Contains("computer_click", toolsAfterSwitch);
    }
}
