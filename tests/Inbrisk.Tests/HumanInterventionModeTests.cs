using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Input;
using Xunit;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class HumanInterventionModeTests
{
    private sealed class RecordingInputService : IInputService
    {
        public int ReleaseAllCount { get; private set; }
        public int EmergencyCleanupCount { get; private set; }
        public int ClearEmergencyCount { get; private set; }

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
        public void ReleaseAll() => ReleaseAllCount++;
        public void EmergencyCleanup(int drainMs = 150) => EmergencyCleanupCount++;
        public void ClearEmergency() => ClearEmergencyCount++;
    }

    [Fact]
    public void OffMode_DoesNotStartMonitor_AndIgnoresAllPhysicalInput()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        var input = new RecordingInputService();
        using var reg = control.RegisterInput(input);

        control.ConfigureHumanIntervention(HumanInterventionMode.Off);

        Assert.False(monitor.IsRunning);
        Assert.Equal(HumanInterventionMode.Off, control.InterventionMode);

        monitor.SimulateKeyboard();
        monitor.SimulateMouseMove(200, 200, deltaPx: 50);
        monitor.SimulateMouseClick(200, 200);
        monitor.SimulateMouseWheel(200, 200);

        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.False(control.PausedByHumanIntervention);
        Assert.NotNull(control.ActionToken());
        Assert.Equal(0, input.ReleaseAllCount);
    }

    [Fact]
    public void PauseOnKeyboard_PausesOnKeyboard_ReleasesInput_AndIgnoresMouse()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        var input = new RecordingInputService();
        using var reg = control.RegisterInput(input);

        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnKeyboard);
        Assert.True(monitor.IsRunning);

        // Mouse input must not pause in PauseOnKeyboard mode
        monitor.SimulateMouseMove(300, 300, deltaPx: 40);
        monitor.SimulateMouseClick(300, 300);
        monitor.SimulateMouseWheel(300, 300);
        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.Equal(0, input.ReleaseAllCount);

        // Capture action token before keyboard intervention
        var tokenBefore = control.ActionToken();
        Assert.NotNull(tokenBefore);
        Assert.False(tokenBefore.Value.IsCancellationRequested);

        // Physical keyboard input pauses immediately
        monitor.SimulateKeyboard(vk: 0x41, flags: 0);

        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.True(control.PausedByHumanIntervention);
        Assert.Equal("HumanKeyboardIntervention", control.PauseReason);
        Assert.Equal(PhysicalInputKind.Keyboard, control.LastInterventionKind);
        Assert.True(tokenBefore.Value.IsCancellationRequested);
        Assert.Null(control.ActionToken());
        Assert.Equal(1, input.ReleaseAllCount);

        // Resuming from pause restores Active state
        Assert.True(control.ResumeFromPause("test_resume"));
        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.False(control.PausedByHumanIntervention);
        Assert.NotNull(control.ActionToken());
    }

    [Fact]
    public void PauseOnMouse_RespectsDeadzoneThreshold_ClicksAndWheelAlwaysCount_AndIgnoresKeyboard()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        var input = new RecordingInputService();
        using var reg = control.RegisterInput(input);

        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnMouse, mouseMoveThresholdPx: 12);
        Assert.True(monitor.IsRunning);
        Assert.Equal(12, monitor.MouseMoveThresholdPx);

        // Keyboard must not pause in PauseOnMouse mode
        monitor.SimulateKeyboard();
        Assert.Equal(ComputerControlState.Active, control.State);

        // Sub-threshold jitter (< 12px) must not pause
        monitor.SimulateMouseMove(105, 103, deltaPx: 6);
        monitor.SimulateMouseMove(110, 100, deltaPx: 11);
        Assert.Equal(ComputerControlState.Active, control.State);

        // Movement >= threshold pauses
        monitor.SimulateMouseMove(120, 100, deltaPx: 12);
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.True(control.PausedByHumanIntervention);
        Assert.Equal("HumanMouseIntervention", control.PauseReason);
        Assert.Equal(PhysicalInputKind.MouseMove, control.LastInterventionKind);
        Assert.Equal(1, input.ReleaseAllCount);

        // Resume and verify click pauses even with 0px delta
        Assert.True(control.ResumeFromPause());
        monitor.SimulateMouseClick(120, 100);
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.Equal(PhysicalInputKind.MouseButton, control.LastInterventionKind);

        // Resume and verify wheel pauses even with 0px delta
        Assert.True(control.ResumeFromPause());
        monitor.SimulateMouseWheel(120, 100);
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.Equal(PhysicalInputKind.MouseWheel, control.LastInterventionKind);
    }

    [Fact]
    public void PauseOnAnyInput_PausesOnBothKeyboardAndMouse()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnAnyInput, mouseMoveThresholdPx: 10);

        monitor.SimulateKeyboard();
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.Equal("HumanKeyboardIntervention", control.PauseReason);

        Assert.True(control.ResumeFromPause());
        monitor.SimulateMouseClick(50, 50);
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.Equal("HumanMouseIntervention", control.PauseReason);
    }

    [Fact]
    public void InjectedFlags_AreIgnoredByBothPhysicalInputMonitorAndEmergencyControl()
    {
        // 1. Verify PhysicalInputMonitor raw Win32 filter logic directly
        using var realMonitor = new PhysicalInputMonitor(mouseMoveThresholdPx: 12);
        realMonitor.ResetMouseAnchor(100, 100);

        Assert.True(PhysicalInputMonitor.IsKeyboardEventInjected(PhysicalInputMonitor.LLKHF_INJECTED));
        Assert.True(PhysicalInputMonitor.IsKeyboardEventInjected(PhysicalInputMonitor.LLKHF_LOWER_IL_INJECTED));
        Assert.False(PhysicalInputMonitor.IsKeyboardEventInjected(0));

        Assert.True(PhysicalInputMonitor.IsMouseEventInjected(PhysicalInputMonitor.LLMHF_INJECTED));
        Assert.True(PhysicalInputMonitor.IsMouseEventInjected(PhysicalInputMonitor.LLMHF_LOWER_IL_INJECTED));
        Assert.False(PhysicalInputMonitor.IsMouseEventInjected(0));

        Assert.Null(realMonitor.EvaluateKeyboardEvent(PhysicalInputMonitor.WM_KEYDOWN, 0x41, PhysicalInputMonitor.LLKHF_INJECTED));
        Assert.Null(realMonitor.EvaluateKeyboardEvent(PhysicalInputMonitor.WM_KEYDOWN, 0x41, PhysicalInputMonitor.LLKHF_LOWER_IL_INJECTED));
        Assert.NotNull(realMonitor.EvaluateKeyboardEvent(PhysicalInputMonitor.WM_KEYDOWN, 0x41, 0));

        Assert.Null(realMonitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 200, 200, PhysicalInputMonitor.LLMHF_INJECTED));
        Assert.Null(realMonitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_LBUTTONDOWN, 200, 200, PhysicalInputMonitor.LLMHF_INJECTED));
        Assert.Null(realMonitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEWHEEL, 200, 200, PhysicalInputMonitor.LLMHF_LOWER_IL_INJECTED));

        // 2. Verify EmergencyControl defense-in-depth ignores injected events in all modes
        var fake = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: fake);
        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnAnyInput);

        fake.SimulateKeyboard(vk: 0x41, flags: PhysicalInputMonitor.LLKHF_INJECTED);
        fake.SimulateKeyboard(vk: 0x41, flags: PhysicalInputMonitor.LLKHF_LOWER_IL_INJECTED);
        fake.SimulateMouseMove(300, 300, deltaPx: 200, flags: PhysicalInputMonitor.LLMHF_INJECTED);
        fake.SimulateMouseClick(300, 300, flags: PhysicalInputMonitor.LLMHF_INJECTED);
        fake.SimulateMouseWheel(300, 300, flags: PhysicalInputMonitor.LLMHF_LOWER_IL_INJECTED);

        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.False(control.PausedByHumanIntervention);
    }

    [Fact]
    public void PhysicalInputMonitor_MouseDeadzone_IgnoresMicroJitter_AccumulatesIntentionalMove_AndCountsClicksAtZero()
    {
        using var monitor = new PhysicalInputMonitor(mouseMoveThresholdPx: 12);
        monitor.ResetMouseAnchor(100, 100);

        // 1-2px sensor jitter around anchor should never fire
        Assert.Null(monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 102, 101, 0));
        Assert.Null(monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 99, 100, 0));
        Assert.Null(monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 101, 99, 0));

        // Deliberate movement accumulating to 12px from anchor (100,100 -> 106,100 -> 112,100) fires on reaching 12px
        Assert.Null(monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 106, 100, 0));
        var moveEv = monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 112, 100, 0);
        Assert.NotNull(moveEv);
        Assert.Equal(PhysicalInputKind.MouseMove, moveEv.Value.Kind);
        Assert.Equal(12, moveEv.Value.DeltaPx);

        // After firing, anchor moved to (112,100), so a 3px move does not immediately re-fire
        Assert.Null(monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEMOVE, 115, 100, 0));

        // Click and wheel always fire even with 0px movement from anchor
        var clickEv = monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_LBUTTONDOWN, 112, 100, 0);
        Assert.NotNull(clickEv);
        Assert.Equal(PhysicalInputKind.MouseButton, clickEv.Value.Kind);

        var wheelEv = monitor.EvaluateMouseEvent(PhysicalInputMonitor.WM_MOUSEWHEEL, 112, 100, 0);
        Assert.NotNull(wheelEv);
        Assert.Equal(PhysicalInputKind.MouseWheel, wheelEv.Value.Kind);
    }

    [Fact]
    public void GhostDesktopExemption_DoesNotPauseGhostExecution_ButInteractiveDesktopDoes()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnAnyInput);

        using var session = new McpSession(control, startEvents: false, initialDesktop: SessionDesktop.Ghost);
        var ghostRun = new RunState { RunId = "run_ghost_1" };
        session.Runs[ghostRun.RunId] = ghostRun;

        var notifications = new List<string>();
        session.BindNotifications((method, _, _) =>
        {
            notifications.Add(method);
            return Task.CompletedTask;
        });

        // Physical input on host desktop MUST NOT pause Ghost OS session
        monitor.SimulateKeyboard();
        monitor.SimulateMouseClick(200, 200);
        monitor.SimulateMouseMove(300, 300, deltaPx: 100);

        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.False(control.PausedByHumanIntervention);
        Assert.False(ghostRun.IsPausedForHuman);
        Assert.Empty(notifications);

        // Switching session to Interactive re-enables human intervention pause
        Assert.True(session.SetDesktop(SessionDesktop.Interactive));
        var interactiveRun = new RunState { RunId = "run_interactive_1" };
        session.Runs[interactiveRun.RunId] = interactiveRun;

        monitor.SimulateKeyboard();

        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.True(control.PausedByHumanIntervention);
        Assert.True(interactiveRun.IsPausedForHuman);
        Assert.True(interactiveRun.SafePointPauseRequested);
        Assert.Equal("HumanKeyboardIntervention", interactiveRun.PauseReason);
        Assert.Contains(McpSession.DesktopEventNotification, notifications);
    }

    [Fact]
    public void PanicAlwaysWinsOverPause_AndCannotBeDowngradedOrClearedByResumeFromPause()
    {
        var monitor = new FakePhysicalInputMonitor();
        using var control = EmergencyControl.ForTests(inputMonitor: monitor);
        var input = new RecordingInputService();
        using var reg = control.RegisterInput(input);
        control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnAnyInput);

        // 1. Human input transitions Active -> Paused
        monitor.SimulateKeyboard();
        Assert.Equal(ComputerControlState.Paused, control.State);
        Assert.True(control.PausedByHumanIntervention);

        // 2. Panic hotkey/UI MUST upgrade Paused -> EmergencyStopped
        control.TriggerLocalPanic("test panic hotkey");
        Assert.Equal(ComputerControlState.EmergencyStopped, control.State);
        Assert.False(control.PausedByHumanIntervention);
        Assert.Equal(1, input.EmergencyCleanupCount);

        // 3. ResumeFromPause MUST NOT clear EmergencyStopped
        Assert.False(control.ResumeFromPause("computer_resume_run"));
        Assert.Equal(ComputerControlState.EmergencyStopped, control.State);
        Assert.Null(control.ActionToken());

        // 4. Subsequent physical input MUST NOT downgrade EmergencyStopped to Paused
        monitor.SimulateKeyboard();
        monitor.SimulateMouseClick(50, 50);
        Assert.Equal(ComputerControlState.EmergencyStopped, control.State);
        Assert.False(control.PausedByHumanIntervention);

        // 5. Only local resume clears EmergencyStopped
        control.TriggerLocalResume("test local resume");
        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.NotNull(control.ActionToken());
    }

    [Fact]
    public void UserSettings_AndCapabilities_ExposeHumanInterventionModeAndPausedState()
    {
        var tmpPath = Path.Combine(Path.GetTempPath(), $"inbrisk-hi-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new UserSettings
            {
                HumanInterventionMode = HumanInterventionMode.PauseOnAnyInput,
                MouseMoveThresholdPx = 20
            };
            settings.Save(tmpPath);

            var loaded = UserSettings.Load(tmpPath);
            Assert.Equal(HumanInterventionMode.PauseOnAnyInput, loaded.HumanInterventionMode);
            Assert.Equal(20, loaded.MouseMoveThresholdPx);

            // Verify capabilities JSON reflects live control state
            var monitor = new FakePhysicalInputMonitor();
            using var control = EmergencyControl.ForTests(inputMonitor: monitor);
            control.ConfigureHumanIntervention(HumanInterventionMode.PauseOnKeyboard);

            using (var doc = JsonDocument.Parse(InbriskResources.CapabilitiesJson(control: control)))
            {
                Assert.Equal("PauseOnKeyboard", doc.RootElement.GetProperty("humanInterventionMode").GetString());
                Assert.False(doc.RootElement.GetProperty("pausedByHumanIntervention").GetBoolean());
            }

            monitor.SimulateKeyboard();

            using (var docSlim = JsonDocument.Parse(InbriskResources.CapabilitiesJsonSlim(control: control)))
            {
                Assert.Equal("Paused", docSlim.RootElement.GetProperty("controlState").GetString());
                Assert.Equal("PauseOnKeyboard", docSlim.RootElement.GetProperty("humanInterventionMode").GetString());
                Assert.True(docSlim.RootElement.GetProperty("pausedByHumanIntervention").GetBoolean());
            }
        }
        finally
        {
            try { File.Delete(tmpPath); } catch { }
        }
    }
}
