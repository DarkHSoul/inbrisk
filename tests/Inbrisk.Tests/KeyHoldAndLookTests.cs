using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// `key` with ms holds the key down (games read held state per tick) and
/// `look` sends relative mouse motion. Pins the executor behavior, the
/// bounds that keep a plan from holding input for long, the step schema,
/// and that both stay behind the same consent gate as keystrokes.
/// </summary>
public sealed class KeyHoldAndLookTests
{
    private const long Hwnd = 0x4242;
    private const int Pid = 777;

    private static WindowInfo Win() => new(Hwnd, Pid, "Game", "game.exe",
        new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);

    // ---------- stubs (same shape as PolicyOwnershipAndCapabilitiesTests) ----------

    private sealed class StubWindows : IWindowService
    {
        private readonly WindowInfo _win;
        public StubWindows(WindowInfo win) => _win = win;
        public IReadOnlyList<WindowInfo> ListWindows() => new[] { _win };
        public WindowInfo? GetWindow(long hwnd) => hwnd == _win.Hwnd ? _win : null;
        public WindowInfo? GetForegroundWindow() => _win;
        public WindowInfo? GetTopLevelWindow(long hwnd) => GetWindow(hwnd);
        public IReadOnlyList<MonitorInfo> GetMonitors() => Array.Empty<MonitorInfo>();
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => Array.Empty<WindowInfo>();
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private sealed class StubIntegrity : IIntegrityService
    {
        public bool IsSecureDesktopActive() => false;
        public bool CanControlWindow(long hwnd) => true;
        public IntegrityRelation CheckTarget(long hwnd) => IntegrityRelation.Reachable;
    }

    private sealed class StubCapture : ICaptureService
    {
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => Array.Empty<byte>();
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private sealed class NullBackend : IElementBackend
    {
        public BackendId Id => BackendId.Uia;
        public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) => Array.Empty<UiElement>();
        public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => Array.Empty<UiElement>();
        public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) => null;
        public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => null;
        public bool IsAlive(UiElement element) => true;
    }

    private sealed class RecordingInput : IInputService
    {
        public List<string> Log { get; } = new();
        public void MoveMouse(int x, int y) { }
        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) { }
        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default) { }
        public void Scroll(int x, int y, int wheelDelta) { }
        public void KeyPress(KeyCode key) => Log.Add($"tap:{key}");
        public void KeyDown(KeyCode key) => Log.Add($"down:{key}");
        public void KeyUp(KeyCode key) => Log.Add($"up:{key}");
        public void MoveMouseRelative(int dx, int dy) => Log.Add($"look:{dx},{dy}");
        public void MouseDown(MouseButton button) => Log.Add($"mdown:{button}");
        public void MouseUp(MouseButton button) => Log.Add($"mup:{button}");
        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key) { }
        public void TypeText(string text) { }
        public (int X, int Y) CursorPosition() => (0, 0);
        public void ReleaseAll() { }
    }

    private static (Executor Exec, RecordingInput Input) Build(bool autoConfirm = true)
    {
        var input = new RecordingInput();
        var backend = new NullBackend();
        var reg = new ElementRegistry(new[] { backend });
        var policy = new SafetyPolicy
        {
            Provenance = new ProcessProvenanceService(),
            AutoConfirm = autoConfirm,
        };
        return (new Executor(new StubWindows(Win()), new StubIntegrity(), input,
            new StubCapture(), new[] { backend }, reg, policy), input);
    }

    private static ActionIntent Key(Dictionary<string, object?> args) =>
        new(ActionKind.KeyPress, TargetRef.Window(Hwnd), args);

    private static ActionIntent Look(int dx, int dy) =>
        new(ActionKind.MouseLook, TargetRef.Window(Hwnd),
            new Dictionary<string, object?> { ["dx"] = dx, ["dy"] = dy });

    // ---------- executor ----------

    [Fact]
    public void Key_without_hold_is_still_an_instant_tap()
    {
        var (exec, input) = Build();
        var r = exec.Perform(Key(new() { ["key"] = "W" }));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.key", r.Method);
        Assert.Equal(new[] { "tap:W" }, input.Log);
    }

    [Fact]
    public void Key_with_holdMs_presses_waits_and_releases()
    {
        var (exec, input) = Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = exec.Perform(Key(new() { ["key"] = "W", ["holdMs"] = 250 }));
        sw.Stop();
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.keyhold", r.Method);
        Assert.Equal(new[] { "down:W", "up:W" }, input.Log);
        Assert.True(sw.ElapsedMilliseconds >= 230, $"held only {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Key_hold_can_press_several_keys_together_and_releases_in_reverse()
    {
        var (exec, input) = Build();
        var r = exec.Perform(Key(new()
        {
            ["key"] = "Space",
            ["modifiers"] = new[] { "W" },
            ["holdMs"] = 120,
        }));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(new[] { "down:W", "down:Space", "up:Space", "up:W" }, input.Log);
    }

    [Fact]
    public void Key_hold_always_releases_when_cancelled()
    {
        var (exec, input) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var r = exec.Perform(Key(new() { ["key"] = "W", ["holdMs"] = 5000 }), new ActionContext(cts.Token));
        Assert.False(r.Success);
        Assert.Equal("down:W", input.Log[0]);
        Assert.Equal("up:W", input.Log[^1]); // never left held
    }

    [Fact]
    public void Look_sends_relative_motion_and_clamps_huge_values()
    {
        var (exec, input) = Build();
        var r = exec.Perform(Look(300, -120));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.look", r.Method);
        Assert.Equal(new[] { "look:300,-120" }, input.Log);

        input.Log.Clear();
        exec.Perform(Look(900_000, -900_000));
        Assert.Equal(new[] { "look:4000,-4000" }, input.Log);
    }

    private static ActionIntent Mouse(string button, int holdMs) =>
        new(ActionKind.MouseHold, TargetRef.Window(Hwnd),
            new Dictionary<string, object?> { ["button"] = button, ["holdMs"] = holdMs });

    [Fact]
    public void Mouse_hold_presses_waits_and_releases_without_moving_the_pointer()
    {
        var (exec, input) = Build();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = exec.Perform(Mouse("left", 250));
        sw.Stop();
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.mousehold", r.Method);
        Assert.Equal(new[] { "mdown:Left", "mup:Left" }, input.Log);
        Assert.True(sw.ElapsedMilliseconds >= 230);
    }

    [Fact]
    public void Mouse_hold_always_releases_when_cancelled_and_honours_the_button()
    {
        var (exec, input) = Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        var r = exec.Perform(Mouse("right", 5000), new ActionContext(cts.Token));
        Assert.False(r.Success);
        Assert.Equal("mdown:Right", input.Log[0]);
        Assert.Equal("mup:Right", input.Log[^1]);
    }

    [Fact]
    public void Mouse_hold_can_hold_keys_together_and_releases_everything()
    {
        var (exec, input) = Build();
        var r = exec.Perform(new ActionIntent(ActionKind.MouseHold, TargetRef.Window(Hwnd),
            new Dictionary<string, object?>
            {
                ["button"] = "right", ["holdMs"] = 120,
                ["key"] = "Space", ["modifiers"] = new[] { "W" },
            }));
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal(new[] { "down:W", "down:Space", "mdown:Right", "mup:Right", "up:Space", "up:W" },
            input.Log);
    }

    [Fact]
    public void Mouse_step_accepts_keys_and_still_requires_ms()
    {
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Ms = 3000, Button = "right", Keys = "space" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Keys = "space" }));
    }

    [Fact]
    public void Mouse_hold_is_behind_the_confirm_gate_and_grantable()
    {
        var (exec, input) = Build(autoConfirm: false);
        Assert.Equal(ErrorCode.ConfirmationRequired, exec.Perform(Mouse("left", 100)).Error);
        Assert.Empty(input.Log);

        var ch = new ConfirmChannel("test");
        Assert.False(ch.Confirmer(Mouse("left", 100), null, Win()));
        Assert.True(Assert.Single(ch.Pending).CanAllowWindow);
    }

    [Fact]
    public void Mouse_step_requires_a_bounded_ms_and_a_known_button()
    {
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Ms = 3000 }));
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Ms = 3000, Button = "right" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Ms = 20_000 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "mouse", Ms = 500, Button = "side" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Key = "w", Button = "left" })); // button only on mouse
    }

    [Fact]
    public void Look_and_hold_stay_behind_the_confirm_gate()
    {
        var (exec, input) = Build(autoConfirm: false);
        Assert.Equal(ErrorCode.ConfirmationRequired, exec.Perform(Look(10, 0)).Error);
        Assert.Equal(ErrorCode.ConfirmationRequired,
            exec.Perform(Key(new() { ["key"] = "W", ["holdMs"] = 100 })).Error);
        Assert.Empty(input.Log);
    }

    [Fact]
    public void Backends_without_relative_motion_report_it_instead_of_faking_success()
    {
        IInputService bare = new BareInput();
        Assert.Throws<NotSupportedException>(() => bare.MoveMouseRelative(1, 1));
    }

    private sealed class BareInput : IInputService
    {
        public void MoveMouse(int x, int y) { }
        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) { }
        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default) { }
        public void Scroll(int x, int y, int wheelDelta) { }
        public void KeyPress(KeyCode key) { }
        public void KeyDown(KeyCode key) { }
        public void KeyUp(KeyCode key) { }
        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key) { }
        public void TypeText(string text) { }
        public (int X, int Y) CursorPosition() => (0, 0);
        public void ReleaseAll() { }
    }

    // ---------- step schema ----------

    [Fact]
    public void Key_step_accepts_ms_and_bounds_it()
    {
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Key = "w", Ms = 800 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Key = "w", Ms = 20_000 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "hotkey", Key = "w", Ms = 800 })); // ms only on key
    }

    [Fact]
    public void Key_step_keys_combo_requires_ms_and_excludes_key()
    {
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Keys = "w+space", Ms = 900 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Keys = "w+space" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Key = "w", Keys = "w+space", Ms = 900 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key" }));
    }

    [Fact]
    public void Look_step_requires_a_delta_and_bounds_it()
    {
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "look", Dx = 200 }));
        Assert.Null(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "look", Dx = 200, Dy = -50 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "look" }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "look", Dx = 50_000 }));
        Assert.NotNull(InbriskTools.ValidateStep(
            new InbriskTools.RunStep { Action = "key", Key = "w", Dx = 5 })); // dx only on look
    }

    // ---------- consent: one window grant covers hold and look ----------

    [Fact]
    public void A_window_grant_covers_look_like_keystrokes()
    {
        var ch = new ConfirmChannel("test");
        var intent = Look(5, 5);
        var win = Win();
        Assert.False(ch.Confirmer(intent, null, win));
        var pending = Assert.Single(ch.Pending);
        Assert.True(pending.CanAllowWindow);
    }
}
