using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public class AutoVerifierTests
{
    private sealed class DummyWindowService : IWindowService
    {
        public bool IsClosed { get; set; } = false;
        public IReadOnlyList<WindowInfo> ListWindows() => Array.Empty<WindowInfo>();
        public WindowInfo? GetWindow(long hwnd) => IsClosed ? null : new(hwnd, 1, "App", "app.exe", new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        public WindowInfo? GetForegroundWindow() => GetWindow(100);
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

    private static UiElement MakeElement(string id, string? value = null, long hwnd = 100)
    {
        var props = new Dictionary<string, object?>
        {
            ["value"] = value,
            ["enabled"] = true
        };
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, hwnd, "app", Role.Edit, id, id, Array.Empty<AncestryStep>(), new RectPx(0, 0, 100, 30)));
        return new UiElement(id, BackendId.Uia, Role.Edit, id, new RectPx(0, 0, 100, 30),
            new[] { "type", "click" }, props, handle, 1, hwnd);
    }

    [Fact]
    public void VerifyType_EmptyString_ReturnsNull_NeverFalseVerified()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, new[] { backend }, winSvc, events);

        var el = MakeElement("input_1", "existing value");
        backend.OnReResolve = _ => el;

        var pre = verifier.Snapshot(el);

        var action = new AgentAction(AgentActionKind.Type, ElementId: "input_1", Text: "");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeType", Array.Empty<Attempt>(), VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));
        var outcome = verifier.Verify(action, pre, actionResult, null, CancellationToken.None);

        // Crucial fix: empty text typing must never claim Verified("ValueReadback")
        Assert.Null(outcome);
    }

    [Fact]
    public void VerifyActed_WhenElementGone_OnlyVerifiesIfWindowClosed()
    {
        var backend = new MockBackend();
        var winSvc = new DummyWindowService();
        var reg = new ElementRegistry(new[] { backend });
        var events = new RecentEventBuffer();

        var verifier = new AutoVerifier(reg, new[] { backend }, winSvc, events);

        var el = MakeElement("btn_ok", hwnd: 200);
        // Element cannot be re-resolved after click
        backend.OnReResolve = _ => null;

        var pre = verifier.Snapshot(el);
        var action = new AgentAction(AgentActionKind.Click, ElementId: "btn_ok");
        var actionResult = new ActionResult(true, BackendId.Uia, "NativeClick", Array.Empty<Attempt>(), VerifyResult.Unverified, TimeSpan.FromMilliseconds(5));

        // Case 1: Window is still open
        winSvc.IsClosed = false;
        var outcomeOpen = verifier.Verify(action, pre, actionResult, null, CancellationToken.None);
        // Must NOT falsely verify ElementGone!
        Assert.Null(outcomeOpen);

        // Case 2: Window closed as a result of the click (e.g. OK/Cancel/Close button)
        winSvc.IsClosed = true;
        var outcomeClosed = verifier.Verify(action, pre, actionResult, null, CancellationToken.None);
        Assert.NotNull(outcomeClosed);
        Assert.Equal(OutcomeKind.Verified, outcomeClosed!.Kind);
        Assert.Equal("WindowClosed", outcomeClosed.Evidence?.Method);
    }
}
