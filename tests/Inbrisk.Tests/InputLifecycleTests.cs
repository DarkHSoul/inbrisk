using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Input-ownership lifecycle: every DOWN must be paired, on every exit path.
/// These tests inject REAL input — they run in the desktop collection and
/// only use harmless keys (LCtrl / F-keys are never typed into apps) plus
/// fixture-scoped TestApp interactions.
/// </summary>
[Collection("desktop")]
public class InputLifecycleTests
{
    private const int VK_LCTRL = 0xA2, VK_LSHIFT = 0xA0, VK_LBUTTON = 0x01;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private readonly DesktopFixture _fx;
    public InputLifecycleTests(DesktopFixture fx) => _fx = fx;

    private SendInputService Input =>
        (SendInputService)_fx.Inbrisk.Parts.Input;

    private static void AssertNoHeld(SendInputService input)
    {
        Assert.Empty(input.HeldKeys);
        Assert.False(Down(VK_LCTRL), "LCTRL physically still down");
        Assert.False(Down(VK_LSHIFT), "LSHIFT physically still down");
        Assert.False(Down(VK_LBUTTON), "LBUTTON physically still down");
    }

    [Fact]
    public void Transaction_ReleasesOwnedKey_OnDispose()
    {
        var input = Input;
        var txn = input.BeginTransaction(InputTxnKind.Key);
        input.TxnSendKey(txn, VK_LCTRL, down: true);
        Assert.True(Down(VK_LCTRL), "injected ctrl-down did not land");
        Assert.Contains((ushort)VK_LCTRL, input.HeldKeys);
        txn.Dispose();
        Assert.True(SpinWait.SpinUntil(() => !Down(VK_LCTRL), 2000),
            "owned ctrl not released on txn dispose");
        AssertNoHeld(input);
    }

    [Fact]
    public void Transaction_ReleasesOwnedKey_OnException()
    {
        var input = Input;
        var ex = Record.Exception(Run);
        Assert.IsType<InvalidOperationException>(ex);
        void Run()
        {
            using var txn = input.BeginTransaction(InputTxnKind.Key);
            input.TxnSendKey(txn, VK_LCTRL, down: true);
            input.TxnSendKey(txn, VK_LSHIFT, down: true);
            throw new InvalidOperationException("boom mid-sequence");
        }
        Assert.True(SpinWait.SpinUntil(() => !Down(VK_LCTRL) && !Down(VK_LSHIFT), 2000),
            "owned modifiers survived an exception");
        AssertNoHeld(input);
    }

    [Fact]
    public void Click_LeavesNoOwnedInput()
    {
        _fx.EnsureForeground();
        var input = Input;
        var w = _fx.Inbrisk.Windows().First(w => w.Hwnd == _fx.Hwnd);
        input.Click(w.Bounds.X + w.Bounds.Width / 2,
                    w.Bounds.Y + w.Bounds.Height / 2);
        AssertNoHeld(input);
        Assert.Equal(0, input.OpenTransactions);
    }

    [Fact]
    public void Hotkey_LeavesModifiersUp()
    {
        _fx.EnsureForeground();
        var input = Input;
        input.Hotkey(new[] { KeyCode.Ctrl }, KeyCode.S);
        AssertNoHeld(input);
        Assert.Equal(0, input.OpenTransactions);
    }

    [Fact]
    public void Drag_CancelMidway_ReleasesButton()
    {
        var input = Input;
        var w = _fx.Inbrisk.Windows().First(w => w.Hwnd == _fx.Hwnd);
        var cx = w.Bounds.X + w.Bounds.Width / 2;
        var cy = w.Bounds.Y + w.Bounds.Height / 2;
        using var cts = new CancellationTokenSource();
        var ex = Record.Exception(() =>
        {
            var t = Task.Run(() =>
            {
                Thread.Sleep(60);
                cts.Cancel();
            });
            input.Drag(cx, cy, cx + 120, cy + 60, durationMs: 2000, ct: cts.Token);
            t.Wait();
        });
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.True(SpinWait.SpinUntil(() => !Down(VK_LBUTTON), 2000),
            "mouse button still held after cancelled drag");
        AssertNoHeld(input);
    }

    [Fact]
    public void Hotkey_SkipsExternallyHeldModifier()
    {
        var input = Input;
        var foreign = new SendInputService(); // stands in for the user
        foreign.KeyDown(KeyCode.Ctrl); // "physical" hold — not owned by `input`
        try
        {
            Assert.True(Down(VK_LCTRL), "foreign ctrl-down did not land");
            input.Hotkey(new[] { KeyCode.Ctrl }, KeyCode.S);
            // the hotkey must NOT release a modifier it does not own
            Assert.True(Down(VK_LCTRL),
                "hotkey released a modifier held externally — physical interference");
        }
        finally { foreign.ReleaseAll(); }
        AssertNoHeld(input);
    }

    [Fact]
    public void EmergencyCleanup_BlocksNewDowns_AndReleasesOwned()
    {
        var input = Input;
        input.KeyDown(KeyCode.Ctrl);            // bare hold, pre-panic
        input.MouseDown(MouseButton.Left);
        try
        {
            input.EmergencyCleanup(drainMs: 50);
            Assert.Empty(input.HeldKeys);
            Assert.False(Down(VK_LCTRL));
            Assert.False(Down(VK_LBUTTON));
            // new injected input is refused while the block stands
            Assert.Throws<InbriskException>(() => input.KeyDown(KeyCode.Ctrl));
            Assert.Throws<InbriskException>(() => input.MouseDown());
        }
        finally { input.ClearEmergency(); }
        input.KeyPress(KeyCode.Shift); // proves the block lifted cleanly
        AssertNoHeld(input);
    }

    [Fact]
    public void Watchdog_ForceReleasesOverBudgetTransaction()
    {
        var input = Input;
        var killsBefore = Interlocked.Read(ref InputTelemetry.WatchdogKills);
        var txn = input.BeginTransaction(InputTxnKind.Key, budgetMs: 80);
        input.TxnSendKey(txn, VK_LCTRL, down: true);
        Assert.True(Down(VK_LCTRL));
        Assert.True(SpinWait.SpinUntil(() => !Down(VK_LCTRL) &&
            input.OpenTransactions == 0, 4000),
            "watchdog did not force-release the over-budget transaction");
        Assert.True(txn.Abandoned);
        Assert.True(Interlocked.Read(ref InputTelemetry.WatchdogKills) > killsBefore);
        AssertNoHeld(input);
    }

    [Fact]
    public void DiagnoseInput_ReportsOwnedAndPhysical()
    {
        var input = Input;
        input.KeyDown(KeyCode.Ctrl);
        try
        {
            var d = input.DiagnoseInput();
            var owned = (string[])d["ownedKeys"]!;
            Assert.Contains("0xA2", owned);
            var phys = (Dictionary<string, object?>)d["physicalStateDown"]!;
            Assert.True(phys.ContainsKey("0xA2"), "physical ctrl state not reported");
        }
        finally { input.ReleaseAll(); }
        var after = Input.DiagnoseInput();
        Assert.Empty((string[])after["ownedKeys"]!);
    }

    [Fact]
    public void TypeText_LeavesNoOwnedInput()
    {
        _fx.EnsureForeground();
        var input = Input;
        var box = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
            AutomationId: "MainTextBox")).First();
        _fx.Inbrisk.Type(box.Id, "lifecycle-test\n");
        var save = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
            AutomationId: "SaveButton")).First();
        _fx.Inbrisk.Invoke(save.Id);
        Assert.True(SpinWait.SpinUntil(() =>
            (DesktopFixture.ReadState() ?? "").Contains("lifecycle"), 3000),
            "typed text did not land");
        AssertNoHeld(input);
        Assert.Equal(0, input.OpenTransactions);
    }
}
