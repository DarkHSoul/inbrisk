using System.Diagnostics;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Input;
using Xunit;

namespace Inbrisk.Tests;

public sealed class EmergencyControlSafetyTests
{
    [Fact]
    public void EmergencyControl_ProductionMode_CannotBeActivatedByTestEnvironmentVariable()
    {
        var originalEnv = Environment.GetEnvironmentVariable("INBRISK_TEST_ACTIVE");
        try
        {
            Environment.SetEnvironmentVariable("INBRISK_TEST_ACTIVE", "1");

            // Instantiating standard production control must use RealEmergencyHotkeyRegistrar
            // and must NOT be activated by any environment variable.
            using var prodControl = new EmergencyControl();

            // Initial production state MUST be fail-safe EmergencyStopped
            Assert.Equal(ComputerControlState.EmergencyStopped, prodControl.State);

            // Action token must be null because panic hotkeys have not been successfully bound by authority
            Assert.Null(prodControl.ActionToken());
        }
        finally
        {
            Environment.SetEnvironmentVariable("INBRISK_TEST_ACTIVE", originalEnv);
        }
    }

    [Fact]
    public void EmergencyControl_TestFake_DoesNotRegisterGlobalHotkey()
    {
        var fake = new FakeEmergencyHotkeyRegistrar();
        using var control = EmergencyControl.ForTests(fake);

        Assert.Equal(ComputerControlState.Active, control.State);
        Assert.NotNull(control.ActionToken());

        // Calling StartHotkeys on test fake sets parameters in memory without registering Win32 global hotkeys
        control.StartHotkeys("Ctrl+Alt+Pause", "Ctrl+Alt+Shift+Pause");
        Assert.True(fake.StartCalled);
        Assert.True(control.PanicAvailable);
        Assert.True(control.ResumeAvailable);
        Assert.Equal(ComputerControlState.Active, control.State);
    }

    [Fact]
    public void EmergencyControl_ProductionRegistrationFailure_PreservesFailSafeSemantics()
    {
        // When global hotkey registration fails (e.g. chord held by foreign process)
        var failingRegistrar = new FailingRegistrar();
        using var control = new EmergencyControl(failingRegistrar, testActive: false);

        control.StartHotkeys("Ctrl+Alt+F12", "Ctrl+Alt+Shift+F12");

        // Fail-safe requirement: state must remain EmergencyStopped, and actions must be denied
        Assert.False(control.PanicAvailable);
        Assert.Equal(ComputerControlState.EmergencyStopped, control.State);
        Assert.Null(control.ActionToken());
    }

    [Fact]
    public void EmergencyControl_TestInstances_RunInParallelWithoutGlobalHotkeyCollision()
    {
        // 50 test instances run in parallel using identical chords — zero OS collision
        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            using var c = EmergencyControl.ForTests();
            c.StartHotkeys("Ctrl+Alt+F12", "Ctrl+Alt+Shift+F12");
            Assert.Equal(ComputerControlState.Active, c.State);
            Assert.NotNull(c.ActionToken());
        })).ToArray();

        Task.WaitAll(tasks);
    }

    private sealed class FailingRegistrar : IEmergencyHotkeyRegistrar
    {
        public bool PanicAvailable => false;
        public bool ResumeAvailable => false;
        public HotkeyChord? Resume => null;

        public void Start(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null)
        {
            log?.Invoke("Simulated registration failure: hotkey already registered by foreign process");
        }

        public void TryReacquire(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null) { }

        public void Dispose() { }
    }
}
