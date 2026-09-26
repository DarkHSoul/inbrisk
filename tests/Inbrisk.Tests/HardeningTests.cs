using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// M4 hardening: auto-verification with evidence, prompt cancellation,
/// pause→re-observe, per-run isolation, context budgets, NoProgress, batch
/// guards, frame race guards, input cleanup, lifecycle transitions.
/// Model turns are scripted; everything below is the real Windows runtime.
/// </summary>
[Collection("desktop")]
public class HardeningTests
{
    private readonly DesktopFixture _fx;
    public HardeningTests(DesktopFixture fx)
    {
        _fx = fx;
        _fx.Inbrisk.Focus(_fx.Hwnd);
        _fx.CloseDialogs();
        _fx.EnsureAnimationOff();
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        long hWnd, long insAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);

    private static AgentAction A(AgentActionKind k, string? el = null,
        string? text = null, long? hwnd = null, ImagePoint? pt = null,
        int? ms = null) =>
        new(k, ElementId: el, Text: text, Hwnd: hwnd, Point: pt, Ms: ms);

    private static string? Id(ModelTurnInput i, string role, string name) =>
        ScriptedModelAdapter.FindId(i, role, name);

    // ------------------------------------------------------------------
    // 1-6. Auto-verification + evidence
    // ------------------------------------------------------------------

    [Fact]
    public void SetValue_AutoVerified_WithEvidence()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "auto-v")]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("set value", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        var step = r.History[0];
        Assert.True(step.Outcome.Kind == OutcomeKind.Verified,
            $"kind={step.Outcome.Kind} detail={step.Outcome.Detail} obsElems={adapter.Seen[0].Observation.Elements.Count}");
        Assert.NotNull(step.Outcome.Evidence);
        Assert.Equal("ValueReadback", step.Outcome.Evidence!.Method);
        Assert.Equal("auto-v", step.Outcome.Evidence.Expected);
        Assert.Equal("auto-v", step.Outcome.Evidence.Actual);
    }

    [Fact]
    public void Toggle_AutoVerified_BeforeAfter()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.Toggle, Id(i, "CheckBox", "Enable"))]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("toggle", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        var ev = r.History[0].Outcome.Evidence;
        Assert.NotNull(ev);
        Assert.Equal("PropertyChanged", ev!.Method);
        Assert.Equal("toggleState", ev.Detail);
        Assert.NotEqual(ev.Expected, ev.Actual); // before ≠ after — real transition
        Assert.StartsWith("ToggleState_", ev.Expected ?? "");
        Assert.StartsWith("ToggleState_", ev.Actual ?? "");
    }

    [Fact]
    public void Select_AutoVerified_SelectedTrue()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.Select, Id(i, "ListItem", "ItemTwo"))]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("select item", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        var step = r.History[0];
        Assert.True(step.Outcome.Kind == OutcomeKind.Verified,
            $"kind={step.Outcome.Kind} detail={step.Outcome.Detail} obsElems={adapter.Seen[0].Observation.Elements.Count}");
        Assert.Equal("PropertyChanged", step.Outcome.Evidence?.Method);
        Assert.Equal("true", step.Outcome.Evidence?.Actual);
    }

    [Fact]
    public void FocusWindow_AutoVerified_ForegroundHwnd()
    {
        // focus something else first so the result isn't vacuous
        var other = _fx.Inbrisk.Windows().First(w => w.Hwnd != _fx.Hwnd);
        _fx.Inbrisk.Focus(other.Hwnd);
        Thread.Sleep(200);

        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.FocusWindow, hwnd: _fx.Hwnd)]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("focus", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        var ev = r.History[0].Outcome.Evidence;
        Assert.Equal("ForegroundWindow", ev?.Method);
        Assert.Equal($"0x{_fx.Hwnd:X}", ev?.Actual);
    }

    [Fact]
    public void Click_NeverBlindVerified()
    {
        // click dead space inside the window (bottom-left margin): no
        // postcondition exists → outcome must NOT be a bare Verified
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.Click, pt: new ImagePoint(
                8, i.Observation.Frames[0].Height - 8,
                i.Observation.Frames[0].FrameId,
                i.Observation.ObservationId))]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("click dead area", adapter,
            opts: new AgentOptions(ScreenshotPolicy: VisualAttachPolicy.Always),
            hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        var o = r.History[0].Outcome;
        // honest contract: Verified only WITH evidence; a click on dead space
        // must land as Unverified or NoProgress — never a bare Verified
        if (o.Kind == OutcomeKind.Verified) Assert.NotNull(o.Evidence);
        else Assert.Contains(o.Kind, new[] { OutcomeKind.Unverified, OutcomeKind.NoProgress, OutcomeKind.ObservedChange });
    }

    [Fact]
    public void VerifiedOutcomes_AlwaysCarryEvidence()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([
                A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "ev"),
                A(AgentActionKind.Toggle, Id(i, "CheckBox", "Enable")),
            ]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("evidence sweep", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        foreach (var s in r.History.Where(h => h.Outcome.Kind == OutcomeKind.Verified))
            Assert.NotNull(s.Outcome.Evidence);
    }

    // ------------------------------------------------------------------
    // 7-8. Prompt cancellation
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cancel_LongWait_ReturnsFast()
    {
        var agent = _fx.Inbrisk.CreateAgent(new AgentOptions(TimeoutMs: 60_000));
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            ScriptedModelAdapter.Turn(A(AgentActionKind.Wait, ms: 30_000)),
        });
        var run = Task.Run(() => agent.RunAsync("wait", adapter, _fx.Hwnd));
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, 3000);
        await Task.Delay(400); // let the wait actually start

        var sw = Stopwatch.StartNew();
        agent.Cancel();
        var r = await run;
        sw.Stop();
        Assert.Equal(AgentStatus.Cancelled, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"cancel took {sw.Elapsed} — wait(30000) did not abort promptly");
    }

    [Fact]
    public async Task Cancel_WaitForStable_ReturnsFast()
    {
        var agent = _fx.Inbrisk.CreateAgent(new AgentOptions(TimeoutMs: 60_000));
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            ScriptedModelAdapter.Turn(A(AgentActionKind.WaitForStable, ms: 30_000)),
        });
        var run = Task.Run(() => agent.RunAsync("wait stable", adapter, _fx.Hwnd));
        SpinWait.SpinUntil(() => agent.Status == AgentStatus.Running, 3000);
        await Task.Delay(400);

        var sw = Stopwatch.StartNew();
        agent.Cancel();
        var r = await run;
        sw.Stop();
        Assert.Equal(AgentStatus.Cancelled, r.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"cancel took {sw.Elapsed}");
    }

    // ------------------------------------------------------------------
    // 9. Pause → re-observe
    // ------------------------------------------------------------------

    [Fact]
    public async Task Pause_Resume_ForcesReobservation()
    {
        var agent = _fx.Inbrisk.CreateAgent(new AgentOptions());
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "p")]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var pausedOnce = false;
        agent.StepCompleted += s =>
        {
            if (pausedOnce) return;
            pausedOnce = true;
            Assert.True(agent.Pause());
            Assert.Equal(AgentStatus.Paused, agent.Status);
            Task.Delay(300).ContinueWith(_ => agent.Resume());
        };
        var r = await agent.RunAsync("pause", adapter, _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.True(pausedOnce);
        // turn 2 must see an observation built AFTER resume — id strictly
        // beyond the post-step rebuild (gap ≥ 2 proves a fresh observe)
        Assert.True(adapter.Seen.Count >= 2);
        Assert.True(adapter.Seen[1].Observation.ObservationId >=
            adapter.Seen[0].Observation.ObservationId + 2,
            "resume did not force re-observation");
    }

    // ------------------------------------------------------------------
    // 10. Concurrent run isolation
    // ------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentRuns_ConfirmerIsolation()
    {
        var confirmCallsA = 0; var confirmCallsB = 0;
        var optsA = new AgentOptions(Confirmer: (a, t) => { confirmCallsA++; return true; });
        var optsB = new AgentOptions(Confirmer: (a, t) => { confirmCallsB++; return false; });
        var agentA = _fx.Inbrisk.CreateAgent(optsA);
        var agentB = _fx.Inbrisk.CreateAgent(optsB);

        Func<ModelTurnInput, ModelTurn> type = i => new ModelTurn(
            [A(AgentActionKind.Type, Id(i, "Edit", "text box"), "x")]);
        var adapterA = new ScriptedModelAdapter(new[] { type,
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)) });
        var adapterB = new ScriptedModelAdapter(new[] { type,
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)) });

        var ra = agentA.RunAsync("run A", adapterA, _fx.Hwnd);
        var rb = agentB.RunAsync("run B", adapterB, _fx.Hwnd);
        var res = await Task.WhenAll(ra, rb);

        Assert.NotEqual(res[0].RunId, res[1].RunId);
        Assert.Equal(1, confirmCallsA);
        Assert.Equal(1, confirmCallsB);
        // A's confirmer approved → its type action executed; B's denied it
        Assert.NotEqual(OutcomeKind.ConfirmationDenied, res[0].History[0].Outcome.Kind);
        Assert.Equal(OutcomeKind.ConfirmationDenied, res[1].History[0].Outcome.Kind);
        // histories did not cross
        Assert.All(res[0].History, s => Assert.Equal(res[0].RunId, s.RunId));
        Assert.All(res[1].History, s => Assert.Equal(res[1].RunId, s.RunId));
    }

    // ------------------------------------------------------------------
    // 11. Run/step/action ids in telemetry
    // ------------------------------------------------------------------

    [Fact]
    public void RunIds_InTelemetry_AndHistory()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "ids")]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("ids", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.False(string.IsNullOrEmpty(r.RunId));
        Assert.All(r.History, s => Assert.Equal(r.RunId, s.RunId));
        Assert.All(r.History, s => Assert.False(string.IsNullOrEmpty(s.ActionId)));

        // action telemetry rows carry the same RunId + ActionId
        var path = Path.Combine(Path.GetTempPath(), "inbrisk", "test-telemetry.jsonl");
        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var runLines = lines.Where(l => l.Contains($"\"RunId\":\"{r.RunId}\"")).ToList();
        Assert.NotEmpty(runLines);
        Assert.Contains(runLines, l => l.Contains("\"ActionId\":\"a1\""));
    }

    // ------------------------------------------------------------------
    // 12-13. Context budgets
    // ------------------------------------------------------------------

    [Fact]
    public void CharBudget_PrunesObservation()
    {
        var full = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            screenshotPolicy: VisualAttachPolicy.Never);
        var tight = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            budget: new ObservationBudget(MaxChars: 400),
            screenshotPolicy: VisualAttachPolicy.Never);
        Assert.True(tight.Observation.Elements.Count < full.Observation.Elements.Count);
        Assert.True(tight.Observation.Elements.Count <= 6,
            $"char budget let {tight.Observation.Elements.Count} elements through");
        Assert.True(tight.Observation.Stats.EstimatedCharacters > 0);
    }

    [Fact]
    public void ScreenshotBudget_BlocksOversizedAttachments()
    {
        var noBudget = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            screenshotPolicy: VisualAttachPolicy.Always);
        Assert.NotEmpty(noBudget.Observation.Frames);
        Assert.True(noBudget.Observation.Stats.ImageBytes > 0);

        var tight = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            budget: new ObservationBudget(MaxScreenshotBytes: 10),
            screenshotPolicy: VisualAttachPolicy.Always);
        Assert.Empty(tight.Observation.Frames);
        Assert.Equal(0, tight.Observation.Stats.ImageCount);
    }

    // ------------------------------------------------------------------
    // 14. History compaction
    // ------------------------------------------------------------------

    [Fact]
    public void History_CompactsToSummary()
    {
        var opts = new AgentOptions(MaxHistoryEntries: 2, MaxIdenticalRepeats: 20,
            MaxNoProgressSteps: 20);
        // six distinct small actions → condensed history rolls into summary
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "1")]),
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "2")]),
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "3")]),
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "4")]),
            i => new ModelTurn([A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "5")]),
            i =>
            {
                Assert.True(i.History.Count <= 2, $"model saw {i.History.Count} entries");
                Assert.NotNull(i.HistorySummary);
                Assert.Contains("steps", i.HistorySummary);
                return new ModelTurn([A(AgentActionKind.Finish)]);
            },
        });
        var r = _fx.Inbrisk.RunGoal("compact", adapter,
            opts: opts, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.True(r.History.Count >= 5); // debug history untouched
    }

    // ------------------------------------------------------------------
    // 15. NoProgress detection
    // ------------------------------------------------------------------

    [Fact]
    public void NoProgress_TerminatesLoop()
    {
        // setting the same value repeatedly changes nothing after step 1
        var opts = new AgentOptions(MaxIdenticalRepeats: 20, MaxNoProgressSteps: 3,
            MaxConsecutiveFailures: 10);
        var fake = new ScriptedModelAdapter(Enumerable.Empty<Func<ModelTurnInput, ModelTurn>>())
        {
            Fallback = i => new ModelTurn([A(AgentActionKind.SetValue,
                Id(i, "Edit", "text box"), "same")]),
        };
        var r = _fx.Inbrisk.RunGoal("loop", fake, opts: opts, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("no", r.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(r.History.Select(h => h.Outcome.Kind),
            k => k == OutcomeKind.NoProgress);
    }

    // ------------------------------------------------------------------
    // 16. Batch precondition break
    // ------------------------------------------------------------------

    [Fact]
    public void Batch_StaleFrame_CutsRemainingActions()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([
                // action 1 mutates state → rebuild → old frame ids die
                A(AgentActionKind.SetValue, Id(i, "Edit", "text box"), "batch"),
                // action 2 still references the pre-step observation's frame —
                // the batch precondition check must cut it before Windows
                A(AgentActionKind.Click, pt: new ImagePoint(10, 10,
                    i.Observation.Frames.Count > 0 ? i.Observation.Frames[0].FrameId : -1,
                    i.Observation.ObservationId)),
            ]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("batch", adapter,
            opts: new AgentOptions(ScreenshotPolicy: VisualAttachPolicy.Always),
            hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        // step 2 = the cut stale action; it never reached Windows
        Assert.Equal(2, r.History.Count);
        Assert.Equal(OutcomeKind.StaleFrame, r.History[1].Outcome.Kind);
        Assert.Contains("batch", r.History[1].Outcome.Method);
    }

    // ------------------------------------------------------------------
    // 17-18. Frame race guards
    // ------------------------------------------------------------------

    [Fact]
    public void StaleFrame_Age_Rejected()
    {
        // MaxFrameAgeMs=0 → every frame is instantly "too old"
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.Click, pt: new ImagePoint(10, 10,
                i.Observation.Frames[0].FrameId, i.Observation.ObservationId))]),
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        var r = _fx.Inbrisk.RunGoal("old frame", adapter,
            opts: new AgentOptions(ScreenshotPolicy: VisualAttachPolicy.Always,
                MaxFrameAgeMs: 0),
            hwndHint: _fx.Hwnd);
        Assert.Equal(OutcomeKind.StaleFrame, r.History[0].Outcome.Kind);
    }

    [Fact]
    public void WindowGeometryChanged_Rejected()
    {
        long frameId = 0; long obsId = 0; var moved = false;
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i =>
            {
                frameId = i.Observation.Frames[0].FrameId;
                obsId = i.Observation.ObservationId;
                // move the window AFTER the frame was captured
                var b = _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds;
                SetWindowPos(_fx.Hwnd, 0, b.X + 60, b.Y + 40, 0, 0,
                    0x0001 | 0x0040);
                moved = true;
                return new ModelTurn([A(AgentActionKind.Click,
                    pt: new ImagePoint(30, 30, frameId, obsId))]);
            },
            ScriptedModelAdapter.Turn(A(AgentActionKind.Finish)),
        });
        try
        {
            var r = _fx.Inbrisk.RunGoal("moved window", adapter,
                opts: new AgentOptions(ScreenshotPolicy: VisualAttachPolicy.Always),
                hwndHint: _fx.Hwnd);
            Assert.True(moved);
            Assert.Equal(OutcomeKind.WindowGeometryChanged, r.History[0].Outcome.Kind);
        }
        finally
        {
            var b = _fx.Inbrisk.Window(_fx.Hwnd)?.Bounds;
            if (b != null) // restore
                SetWindowPos(_fx.Hwnd, 0, 200, 120, 0, 0, 0x0001 | 0x0040);
        }
    }

    // ------------------------------------------------------------------
    // 19-20. Input cleanup guarantees
    // ------------------------------------------------------------------

    [Fact]
    public void ReleaseAll_ReleasesHeldKey()
    {
        var input = new SendInputService();
        const int VK_CONTROL = 0xA2;
        input.KeyDown(KeyCode.Ctrl);
        Thread.Sleep(50);
        Assert.True((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
            "ctrl was not physically down");
        input.ReleaseAll();
        Thread.Sleep(50);
        Assert.True((GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0,
            "ctrl still held after ReleaseAll");
        Assert.Empty(input.HeldKeys);
    }

    [Fact]
    public void ReleaseAll_ReleasesHeldMouseButton()
    {
        var input = new SendInputService();
        const int VK_LBUTTON = 0x01;
        input.MoveMouse(_fx.Inbrisk.Window(_fx.Hwnd)!.Bounds.X + 20,
            _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds.Y + 5); // title bar dead zone
        input.MouseDown();
        Thread.Sleep(50);
        Assert.True((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0,
            "mouse button was not physically down");
        input.ReleaseAll();
        Thread.Sleep(50);
        Assert.True((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0,
            "mouse button still held after ReleaseAll");
    }

    // ------------------------------------------------------------------
    // 19b. SweepAll — recovery for input held by a DEAD injector (untracked)
    // ------------------------------------------------------------------

    [Fact]
    public void SweepAll_ReleasesModifierHeldByAnotherInstance()
    {
        // Simulates a killed process's leftover: the key is physically held
        // but THIS instance never tracked it — ReleaseAll alone cannot help.
        var zombie = new SendInputService();
        var sweeper = new SendInputService();
        const int VK_CONTROL = 0xA2;
        try
        {
            zombie.KeyDown(KeyCode.Ctrl);
            Thread.Sleep(50);
            Assert.True((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0,
                "ctrl was not physically down");
            sweeper.SweepAll(); // must NOT need the zombie's tracking
            Thread.Sleep(50);
            Assert.True((GetAsyncKeyState(VK_CONTROL) & 0x8000) == 0,
                "ctrl still held after SweepAll");
        }
        finally { zombie.ReleaseAll(); sweeper.ReleaseAll(); }
    }

    [Fact]
    public void SweepAll_ReleasesMouseButtonHeldByAnotherInstance()
    {
        var zombie = new SendInputService();
        var sweeper = new SendInputService();
        const int VK_LBUTTON = 0x01;
        try
        {
            zombie.MoveMouse(_fx.Inbrisk.Window(_fx.Hwnd)!.Bounds.X + 20,
                _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds.Y + 5);
            zombie.MouseDown();
            Thread.Sleep(50);
            Assert.True((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0,
                "mouse button was not physically down");
            sweeper.SweepAll();
            Thread.Sleep(50);
            Assert.True((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0,
                "mouse button still held after SweepAll");
        }
        finally { zombie.ReleaseAll(); sweeper.ReleaseAll(); }
    }

    // ------------------------------------------------------------------
    // 19c. Input-blocking window detection + remediation (NVIDIA overlay class)
    // ------------------------------------------------------------------

    private const int ExTransparent = 0x20, ExLayered = 0x80000;

    private static readonly RectPx Monitor = new(0, 0, 1920, 1080);
    private static readonly RectPx[] Monitors = { Monitor, new(0, 1080, 1920, 1080) };

    [Fact]
    public void InputBlocker_LayeredOpaqueFullscreen_IsSuspect()
    {
        // the exact NVIDIA-overlay signature: layered but NOT click-through
        var ok = InputHealth.IsBlockerSuspect(Monitor, ExLayered | 0x08000000 | 0x80,
            Monitors, out var mon, out var cov);
        Assert.True(ok);
        Assert.Equal(0, mon);
        Assert.True(cov >= InputHealth.CoverageThreshold);
    }

    [Fact]
    public void InputBlocker_LayeredClickThrough_IsNotSuspect()
    {
        Assert.False(InputHealth.IsBlockerSuspect(Monitor,
            ExLayered | ExTransparent, Monitors, out _, out _));
    }

    [Fact]
    public void InputBlocker_OpaqueNonLayered_IsNotSuspect()
    {
        // a normal maximized window covers the monitor legitimately — it is
        // visible content, not an invisible input sink
        Assert.False(InputHealth.IsBlockerSuspect(Monitor, 0, Monitors, out _, out _));
    }

    [Fact]
    public void InputBlocker_SmallLayeredWindow_IsNotSuspect()
    {
        Assert.False(InputHealth.IsBlockerSuspect(new RectPx(100, 100, 300, 200),
            ExLayered, Monitors, out _, out _));
    }

    [Fact]
    public void MakeClickThrough_SetsTransparentFlag()
    {
        var hwnd = _fx.Hwnd;
        var original = InputHealth.GetExStyle(hwnd);
        try
        {
            Assert.True(InputHealth.MakeClickThrough(hwnd));
            Assert.True((InputHealth.GetExStyle(hwnd) & ExTransparent) != 0,
                "WS_EX_TRANSPARENT was not applied");
        }
        finally
        {
            SetWindowLongPtr(hwnd, -20, original); // restore
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(long hWnd, int nIndex, long dwNewLong);

    // ------------------------------------------------------------------
    // 21. Lifecycle transitions
    // ------------------------------------------------------------------

    [Fact]
    public async Task Lifecycle_InvalidTransitions_Rejected()
    {
        var agent = _fx.Inbrisk.CreateAgent();
        Assert.Equal(AgentStatus.Created, agent.Status);
        Assert.False(agent.Pause());   // Created → Paused illegal
        Assert.False(agent.Resume());  // Created → Running via Resume illegal
        Assert.False(agent.Cancel());  // Created → Cancelling illegal

        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            ScriptedModelAdapter.Turn(new AgentAction(AgentActionKind.Finish, Result: "done")),
        });
        var r = await agent.RunAsync("life", adapter, _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);

        Assert.False(agent.Resume());  // Completed is terminal
        Assert.False(agent.Pause());
        Assert.False(agent.Cancel());
        Assert.Equal(AgentStatus.Completed, agent.Status);
    }

    [Fact]
    public void Lifecycle_Transitions_AreObservedInTelemetry()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            ScriptedModelAdapter.Turn(new AgentAction(AgentActionKind.Finish, Result: "ok")),
        });
        var agent = _fx.Inbrisk.CreateAgent();
        var r = agent.Run("life", adapter, _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);

        var path = Path.Combine(Path.GetTempPath(), "inbrisk", "test-telemetry.jsonl");
        string[] lines;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
            lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, l => l.Contains("lifecycle")
            && l.Contains(r.RunId!) && l.Contains("Running"));
        Assert.Contains(lines, l => l.Contains("lifecycle")
            && l.Contains(r.RunId!) && l.Contains("Completed"));
    }
}
