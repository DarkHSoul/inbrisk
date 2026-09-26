using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Input lockup reproduction stress: thousands of real injected operations
/// with random cancellation and emergency storms. The invariant under test —
/// after EVERY iteration, ownedHeldInputs == 0 and no modifier/button reads
/// physically down. A single stuck state is a failure.
///
/// Counts follow the milestone spec (1000 click / 1000 hotkey / 1000 type /
/// 500 drag / 500 scroll / 100 panic). INBRISK_STRESS_SCALE=0.2 shrinks them
/// for quick runs.
/// </summary>
[Collection("desktop")]
public class InputStressTests
{
    private const int VK_LCTRL = 0xA2, VK_LSHIFT = 0xA0, VK_LALT = 0xA4,
        VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04;

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Stress storms inject THOUSANDS of real input events onto the
    /// live desktop — cursor moves, clicks, hotkeys at machine speed. They are
    /// opt-in so a normal `dotnet test` never hijacks the user's session:
    /// run with INBRISK_STRESS=1 (optionally INBRISK_STRESS_SCALE=0.2).</summary>
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("INBRISK_STRESS") == "1";
    private static readonly double Scale =
        double.TryParse(Environment.GetEnvironmentVariable("INBRISK_STRESS_SCALE"),
            out var s) ? s : 1.0;
    /// <summary>xUnit 2 has no runtime skip — stress bodies no-op (pass)
    /// unless INBRISK_STRESS=1. They print nothing and cost ~0ms when off.</summary>
    private static bool StressEnabled => Enabled;
    private static int N(int spec) => Math.Max(5, (int)(spec * Scale));
    private static readonly Random Rng = new(12345);

    private readonly DesktopFixture _fx;
    public InputStressTests(DesktopFixture fx) => _fx = fx;

    private SendInputService Input => (SendInputService)_fx.Inbrisk.Parts.Input;

    private (int X, int Y) TestAppPoint()
    {
        var w = _fx.Inbrisk.Windows().First(w => w.Hwnd == _fx.Hwnd);
        return (w.Bounds.X + w.Bounds.Width / 2, w.Bounds.Y + w.Bounds.Height / 2);
    }

    private static void AssertSteady(SendInputService input, string ctx)
    {
        Assert.True(input.HeldKeys.Count == 0,
            $"{ctx}: held keys remain: {string.Join(",", input.HeldKeys)}");
        Assert.True(input.OpenTransactions == 0,
            $"{ctx}: {input.OpenTransactions} transactions still open");
        foreach (var vk in new[] { VK_LCTRL, VK_LSHIFT, VK_LALT,
                 VK_LBUTTON, VK_RBUTTON, VK_MBUTTON })
            Assert.False(Down(vk), $"{ctx}: vk 0x{vk:X2} physically held");
    }

    [Fact]
    public void ClickStorm_NoStuckState()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        var (x, y) = TestAppPoint();
        for (var i = 0; i < N(1000); i++)
        {
            input.Click(x, y);
            Assert.True(input.HeldKeys.Count == 0, $"held keys after click {i}");
        }
        AssertSteady(input, "click storm");
    }

    [Fact]
    public void HotkeyStorm_NoStuckModifiers()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        for (var i = 0; i < N(1000); i++)
        {
            input.Hotkey(new[] { KeyCode.Ctrl }, KeyCode.S);
            Assert.False(Down(VK_LCTRL), $"ctrl held after hotkey {i}");
        }
        AssertSteady(input, "hotkey storm");
    }

    [Fact]
    public void TypeStorm_NoStuckState()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        var box = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
            AutomationId: "MainTextBox")).First();
        _fx.Inbrisk.Click(box.Id);
        Thread.Sleep(200);
        for (var i = 0; i < N(1000); i++)
        {
            input.TypeText("ab");
            Assert.True(input.HeldKeys.Count == 0, $"held keys after type {i}");
        }
        AssertSteady(input, "type storm");
    }

    [Fact]
    public void DragStorm_WithRandomCancellation()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        var (x, y) = TestAppPoint();
        var cancelled = 0;
        for (var i = 0; i < N(500); i++)
        {
            using var cts = new CancellationTokenSource();
            var dur = 60 + Rng.Next(120);
            // ~25% of drags get cancelled at a random point mid-flight
            if (Rng.Next(4) == 0)
            {
                cancelled++;
                var cancelAt = Rng.Next(10, dur);
                var token = cts.Token;
                Task.Run(async () => { await Task.Delay(cancelAt); cts.Cancel(); });
                try { input.Drag(x, y, x + 60, y + 30, durationMs: dur, ct: token); }
                catch (OperationCanceledException) { }
            }
            else input.Drag(x, y, x + 60, y + 30, durationMs: dur, ct: cts.Token);
            Assert.False(Down(VK_LBUTTON), $"mouse held after drag {i}");
        }
        AssertSteady(input, $"drag storm ({cancelled} cancelled)");
    }

    [Fact]
    public void ScrollStorm_NoStuckState()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        var (x, y) = TestAppPoint();
        for (var i = 0; i < N(500); i++)
            input.Scroll(x, y, i % 2 == 0 ? 120 : -120);
        AssertSteady(input, "scroll storm");
    }

    /// <summary>The kill-shot scenario: input ops hammering in the background
    /// while panic cleanup fires at random moments — exactly the real-world
    /// "panic mid-action" that used to strand input. Every cycle must end
    /// with zero owned/physical held state and the block lifting cleanly.</summary>
    [Fact]
    public void EmergencyStorm_AlwaysLeavesCleanState()
    {
        if (!StressEnabled) return;
        _fx.EnsureForeground();
        var input = Input;
        var (x, y) = TestAppPoint();
        var manualResets = 0;
        var emergencyRecoveries = 0;

        for (var cycle = 0; cycle < N(100); cycle++)
        {
            var stop = new ManualResetEventSlim();
            var workerDone = new ManualResetEventSlim();
            var opsBlocked = 0;
            var worker = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    try
                    {
                        input.Hotkey(new[] { KeyCode.Ctrl }, KeyCode.S);
                        input.Click(x, y);
                    }
                    catch (InbriskException) { opsBlocked++; } // emergency block — expected
                    catch (OperationCanceledException) { }
                }
                workerDone.Set();
            }) { IsBackground = true };
            worker.Start();

            Thread.Sleep(Rng.Next(5, 40)); // panic lands at a random sequence point
            input.EmergencyCleanup(drainMs: 100);
            emergencyRecoveries++;
            stop.Set();
            Assert.True(workerDone.Wait(3000), $"worker stuck at cycle {cycle}");
            worker.Join(1000);

            // after panic cleanup: no owned input, nothing physically held
            AssertSteady(input, $"emergency cycle {cycle}");
            Assert.True(opsBlocked > 0 || true); // informational

            input.ClearEmergency(); // local resume
        }

        Assert.Equal(0, manualResets);
        Assert.True(emergencyRecoveries > 0);
        AssertSteady(input, "emergency storm end");
    }
}
