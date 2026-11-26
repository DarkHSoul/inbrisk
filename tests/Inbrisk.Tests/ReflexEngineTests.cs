using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class ReflexEngineTests
{
    private sealed class FakeWindowService : IWindowService
    {
        public WindowInfo? WindowToReturn { get; set; }
        public IReadOnlyList<WindowInfo> ListWindows() => WindowToReturn != null ? new[] { WindowToReturn } : Array.Empty<WindowInfo>();
        public WindowInfo? GetWindow(long hwnd) => WindowToReturn?.Hwnd == hwnd ? WindowToReturn : null;
        public WindowInfo? GetForegroundWindow() => WindowToReturn;
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public bool IsWindowAlive(long hwnd) => WindowToReturn?.Hwnd == hwnd;
        public bool IsWindowEnabled(long hwnd) => true;
        public WindowInfo? GetModalPopup(long ownerHwnd) => null;
        public WindowInfo? GetTopLevelWindow(long hwnd) => WindowToReturn;
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => Array.Empty<WindowInfo>();
    }

    private sealed class FakeSilentInputService : ISilentInputService
    {
        public int ClickCalls { get; private set; }
        public int CloseCalls { get; private set; }

        public bool IsSupported(ActionKind kind, out string reason)
        {
            reason = "ok";
            return true;
        }

        public SilentInputResult TryClick(SilentTargetRef target)
        {
            ClickCalls++;
            return new SilentInputResult(true, SilentInputStatus.Ok, "bm_click", true, "ok");
        }

        public SilentInputResult TrySetText(SilentTargetRef target, string text) =>
            new(false, SilentInputStatus.Unsupported, "none", false, "unsupported");

        public SilentInputResult TrySetCheck(SilentTargetRef target, bool isChecked) =>
            new(false, SilentInputStatus.Unsupported, "none", false, "unsupported");

        public SilentInputResult TryClose(long hwnd)
        {
            CloseCalls++;
            return new SilentInputResult(true, SilentInputStatus.Ok, "wm_close", true, "ok");
        }
    }

    private sealed class FakeModalSource : IModalInterruptSource
    {
        public event Action<ModalInterrupt>? ModalDetected;
        public int SubscriberCount => ModalDetected?.GetInvocationList().Length ?? 0;
        public void Raise(ModalInterrupt m) => ModalDetected?.Invoke(m);
        public void Dispose() { }
    }

    private sealed class PolicyAdapter : IReflexPolicy
    {
        public bool IsDangerous(ModalInfo modal) => ReflexPolicy.IsDangerous(modal);
        public ReflexDecision Decide(ModalInfo modal, AutoDismissMode mode) => ReflexPolicy.Decide(modal, mode);
    }

    [Fact]
    public void Lifecycle_ArmReArmDisarmDispose_TransitionsStateCleanly()
    {
        var source = new FakeModalSource();
        var engine = new ReflexEngine(new FakeWindowService(), new FakeSilentInputService(), new PolicyAdapter(), source);

        Assert.Equal(1, source.SubscriberCount);
        Assert.False(engine.Armed);
        Assert.Equal(0, engine.ArmedHwnd);
        Assert.Null(engine.Mode);
        Assert.Null(engine.Abort);
        Assert.False(engine.Paused);
        Assert.True(engine.PauseGate.IsSet);

        engine.Arm(mainHwnd: 1000, mainPid: 42, mode: AutoDismissMode.Save);
        Assert.True(engine.Armed);
        Assert.Equal(1000, engine.ArmedHwnd);
        Assert.Equal(AutoDismissMode.Save, engine.Mode);

        // Re-arming updates target and mode in-place
        engine.Arm(mainHwnd: 2000, mainPid: 42, mode: AutoDismissMode.CloseOnly);
        Assert.Equal(2000, engine.ArmedHwnd);
        Assert.Equal(AutoDismissMode.CloseOnly, engine.Mode);

        engine.Disarm();
        Assert.False(engine.Armed);
        Assert.Equal(0, engine.ArmedHwnd);
        Assert.Null(engine.Mode);
        Assert.True(engine.PauseGate.IsSet);

        engine.Dispose();
        Assert.Equal(0, source.SubscriberCount);
        Assert.Throws<ObjectDisposedException>(() => engine.Arm(1000, 42, AutoDismissMode.Off));
    }

    [Fact]
    public void OnInterrupt_WhenDisarmedOrZeroHwnd_IsIgnoredWithoutLoggingOrInput()
    {
        var silent = new FakeSilentInputService();
        using var engine = new ReflexEngine(new FakeWindowService(), silent, new PolicyAdapter());

        // Disarmed -> ignored
        engine.OnInterrupt(new ModalInterrupt(Hwnd: 999999, Pid: 42, OwnerHwnd: 1000, Title: "Modal", ClassName: "#32770", Detection: "test"));
        Thread.Sleep(50);
        Assert.Empty(engine.Log);

        // Armed but hwnd == 0 -> ignored
        engine.Arm(1000, 42, AutoDismissMode.Save);
        engine.OnInterrupt(new ModalInterrupt(Hwnd: 0, Pid: 42, OwnerHwnd: 1000, Title: "Zero", ClassName: "#32770", Detection: "test"));
        Thread.Sleep(50);
        Assert.Empty(engine.Log);
        Assert.Equal(0, silent.ClickCalls);
        Assert.Equal(0, silent.CloseCalls);
    }

    [Fact]
    public void OnInterrupt_VanishedWindow_LogsIgnoreAndNeverAbortsOrClicks()
    {
        var silent = new FakeSilentInputService();
        var source = new FakeModalSource();
        using var engine = new ReflexEngine(new FakeWindowService(), silent, new PolicyAdapter(), source);

        engine.Arm(mainHwnd: 1000, mainPid: 42, mode: AutoDismissMode.Discard);

        // Non-existent Win32 HWND fails Native.IsWindow -> Verify.Gone -> Ignore
        source.Raise(new ModalInterrupt(
            Hwnd: 0x00DEAD01, Pid: 42, OwnerHwnd: 1000,
            Title: "Gone Dialog", ClassName: "#32770", Detection: "winevent"));

        Assert.True(SpinWait.SpinUntil(() => engine.Log.Count >= 1, 2000),
            "Expected ReflexEngine worker to process and log vanished modal");

        var entry = engine.Log[0];
        Assert.Equal(0x00DEAD01, entry.Hwnd);
        Assert.Equal(ReflexAction.Ignore, entry.Action);
        Assert.True(entry.Success);
        Assert.Contains("gone", entry.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(engine.Abort);
        Assert.Equal(0, silent.ClickCalls);
        Assert.Equal(0, silent.CloseCalls);
    }
}
