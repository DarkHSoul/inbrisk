using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public sealed class SelfHealingAndFeedbackTests
{
    private sealed class DummyWindowService : IWindowService
    {
        public IReadOnlyList<WindowInfo> ListWindows() => Array.Empty<WindowInfo>();
        public WindowInfo? GetWindow(long hwnd) => null;
        public WindowInfo? GetForegroundWindow() => new(100, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => Array.Empty<WindowInfo>();
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class MockBackend : IElementBackend
    {
        public BackendId Id => BackendId.Uia;
        public Func<ElementHandle, UiElement?> OnReResolve { get; set; } = _ => null;

        public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) => Array.Empty<UiElement>();
        public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => Array.Empty<UiElement>();
        public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) => null;
        public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => OnReResolve(handle);
        public bool IsAlive(UiElement element) => true;
    }

    private static UiElement MakeElement(string id, bool offscreen, RectPx bounds)
    {
        var props = new Dictionary<string, object?>
        {
            ["offscreen"] = offscreen,
            ["enabled"] = true
        };
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, 100, "app", Role.ListItem, id, id, Array.Empty<AncestryStep>(), bounds));
        return new UiElement(id, BackendId.Uia, Role.ListItem, id, bounds,
            new[] { "scroll_into_view" }, props, handle, 1, 100);
    }

    private static ActionResult MakeActionResult(bool success) =>
        new(success, BackendId.Uia, "ScrollItemPattern.ScrollIntoView",
            Array.Empty<Attempt>(), VerifyResult.Verified, TimeSpan.FromMilliseconds(10));

    [Fact]
    public void ScrollIntoView_Kind_And_RecoveryPolicy_Configured()
    {
        // 1. ActionKind and AgentActionKind have ScrollIntoView
        Assert.Equal(ActionKind.ScrollIntoView, Enum.Parse<ActionKind>("ScrollIntoView"));
        Assert.Equal(AgentActionKind.ScrollIntoView, Enum.Parse<AgentActionKind>("ScrollIntoView"));

        // 2. OutcomeKind.TargetOffscreen recovery hint is ReObserve, and it is non-fatal
        Assert.Equal(RecoveryHint.ReObserve, RecoveryPolicy.For(OutcomeKind.TargetOffscreen));
        Assert.False(RecoveryPolicy.IsFatal(OutcomeKind.TargetOffscreen));

        // 3. OutcomeKind.TargetOnDifferentScreen recovery hint is ReObserve, and it is non-fatal
        Assert.Equal(RecoveryHint.ReObserve, RecoveryPolicy.For(OutcomeKind.TargetOnDifferentScreen));
        Assert.False(RecoveryPolicy.IsFatal(OutcomeKind.TargetOnDifferentScreen));
    }

    [Fact]
    public void AutoVerifier_ScrollIntoView_VerifiesWhenElementComesOnScreen()
    {
        var mockBackend = new MockBackend();
        var registry = new ElementRegistry(new[] { mockBackend });
        var verifier = new AutoVerifier(registry, new[] { mockBackend },
            new DummyWindowService(), new RecentEventBuffer());

        var offscreenEl = MakeElement("item_42", offscreen: true, bounds: default);
        var onscreenEl = MakeElement("item_42", offscreen: false, bounds: new RectPx(10, 50, 200, 30));

        // Backend returns the newly visible element when re-read
        mockBackend.OnReResolve = h => onscreenEl;

        var pre = new AutoVerifier.PreState(offscreenEl, 100, DateTimeOffset.UtcNow);
        var action = new AgentAction(AgentActionKind.ScrollIntoView, ElementId: "item_42");
        var actResult = MakeActionResult(true);

        var outcome = verifier.Verify(action, pre, actResult, null, CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.Equal(OutcomeKind.Verified, outcome!.Kind);
        Assert.True(outcome.Success);
        Assert.Equal("ScrollIntoView", outcome.Evidence?.Method);
        Assert.Equal("onscreen", outcome.Evidence?.Expected);
        Assert.Contains("bounds:[10,50 200x30]", outcome.Evidence?.Actual);
    }

    [Fact]
    public void AutoVerifier_ScrollIntoView_FailsIfElementRemainsOffscreen()
    {
        var mockBackend = new MockBackend();
        var registry = new ElementRegistry(new[] { mockBackend });
        var verifier = new AutoVerifier(registry, new[] { mockBackend },
            new DummyWindowService(), new RecentEventBuffer());

        var offscreenEl = MakeElement("item_99", offscreen: true, bounds: default);

        // Backend still returns offscreen
        mockBackend.OnReResolve = h => offscreenEl;

        var pre = new AutoVerifier.PreState(offscreenEl, 100, DateTimeOffset.UtcNow);
        var action = new AgentAction(AgentActionKind.ScrollIntoView, ElementId: "item_99");
        var actResult = MakeActionResult(true);

        var outcome = verifier.Verify(action, pre, actResult, null, CancellationToken.None);

        // Does not produce a false-positive Verified
        Assert.Null(outcome);
    }

    [Fact]
    public void ValidateStep_Supports_ScrollIntoView()
    {
        var stepWithId = new InbriskTools.RunStep(Action: "scroll_into_view", ElementId: "uia_1_2");
        var stepWithTarget = new InbriskTools.RunStep(
            Action: "scroll_into_view",
            Target: new InbriskTools.TargetSpec(Role: "ListItem", Name: "TargetTrack"));

        var err1 = InbriskTools.ValidateStep(stepWithId);
        Assert.Null(err1);

        var err2 = InbriskTools.ValidateStep(stepWithTarget);
        Assert.Null(err2);

        var stepWithoutTarget = new InbriskTools.RunStep(Action: "scroll_into_view");
        var err3 = InbriskTools.ValidateStep(stepWithoutTarget);
        Assert.NotNull(err3);
        Assert.Contains("requires elementId or target", err3);
    }

    [Fact]
    public void RunStep_Serialization_RoundTrips_SelfHealing_And_Feedback_Properties()
    {
        var original = new InbriskTools.RunStep(
            Action: "click",
            ElementId: "btn_save",
            AutoScroll: true,
            AutoNavigate: true,
            Observe: true);

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<InbriskTools.RunStep>(json);

        Assert.NotNull(deserialized);
        Assert.Equal("click", deserialized!.Action);
        Assert.Equal("btn_save", deserialized.ElementId);
        Assert.True(deserialized.AutoScroll);
        Assert.True(deserialized.AutoNavigate);
        Assert.True(deserialized.Observe);
    }

    [Fact]
    public void DesktopStateMemory_FindNavigationPath_MultiHop_BFS()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-nav-{Guid.NewGuid():N}.json");
        try
        {
            var memory = new DesktopStateMemory(tempFile);

            // Transitions: A -> B -> C -> D
            memory.RecordTransition("spotify", "Home", "Library", "click 'Library'", "Library");
            memory.RecordTransition("spotify", "Library", "PlaylistDetail", "click 'Playlist'", "Playlist");
            memory.RecordTransition("spotify", "PlaylistDetail", "TrackMenu", "click 'More'", "More");

            // Direct route: Home -> TrackMenu should yield 3 transitions
            var path = memory.FindNavigationPath("spotify", "Home", "TrackMenu");
            Assert.NotNull(path);
            Assert.Equal(3, path.Count);
            Assert.Equal("Home", path[0].FromScreenId);
            Assert.Equal("Library", path[0].ToScreenId);
            Assert.Equal("PlaylistDetail", path[1].ToScreenId);
            Assert.Equal("TrackMenu", path[2].ToScreenId);

            // Same screen path is empty
            var selfPath = memory.FindNavigationPath("spotify", "Home", "Home");
            Assert.NotNull(selfPath);
            Assert.Empty(selfPath);

            // Unreachable screen returns null
            var unreachable = memory.FindNavigationPath("spotify", "Home", "NonExistentScreen");
            Assert.Null(unreachable);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void DesktopStateMemory_SuggestRecovery_Recommends_Previous_Screen()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mem-sug-{Guid.NewGuid():N}.json");
        try
        {
            var memory = new DesktopStateMemory(tempFile);

            // Screen 1: Settings with a Volume slider
            var settingsEls = new[]
            {
                MakeElement("tab_settings", offscreen: false, bounds: new RectPx(10, 10, 80, 20)),
                new UiElement("slider_vol", BackendId.Uia, Role.Slider, "VolumeSlider", new RectPx(20, 50, 100, 20),
                    ["set_value"], new Dictionary<string, object?> { ["value"] = "80%" },
                    new ElementHandle(BackendId.Uia, "slider_vol", new ReResolveRecipe(1, 100, "spotify", Role.Slider, "VolumeSlider", "slider_vol", Array.Empty<AncestryStep>(), new RectPx(20, 50, 100, 20))), 1, 100),
            };
            memory.RecordObservation(100, "spotify", "Spotify - Settings", settingsEls);

            // Screen 2: Home
            var homeEls = new[]
            {
                MakeElement("tab_home", offscreen: false, bounds: new RectPx(10, 10, 80, 20)),
                new UiElement("btn_play", BackendId.Uia, Role.Button, "PlayButton", new RectPx(50, 50, 60, 30),
                    ["invoke"], new Dictionary<string, object?>(),
                    new ElementHandle(BackendId.Uia, "btn_play", new ReResolveRecipe(1, 100, "spotify", Role.Button, "PlayButton", "btn_play", Array.Empty<AncestryStep>(), new RectPx(50, 50, 60, 30))), 1, 100),
            };
            memory.RecordObservation(100, "spotify", "Spotify - Home", homeEls);

            // Record navigation transition between them
            memory.RecordTransition("spotify", "Home", "Settings", "click 'Settings'", "Settings", "TabItem");

            // Suggest recovery for VolumeSlider from Home screen
            var suggestion = memory.SuggestRecovery("VolumeSlider", "Slider", "spotify", "Home");
            Assert.NotNull(suggestion);
            Assert.Equal("Settings", suggestion!.TargetScreen);
            Assert.Equal("Home", suggestion.CurrentScreen);
            Assert.Contains("Settings", suggestion.NavigationHint);
            Assert.Single(suggestion.Path);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}

