using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// M3 deterministic agent tests. The model adapter is scripted/fake; the
/// Windows runtime underneath (UIA, SendInput, capture, waits) is real.
/// </summary>
[Collection("desktop")]
public class AgentTests
{
    private readonly DesktopFixture _fx;
    public AgentTests(DesktopFixture fx)
    {
        _fx = fx;
        _fx.Inbrisk.Focus(_fx.Hwnd);
        _fx.Inbrisk.Policy.KillSwitch = false;
        _fx.Inbrisk.Policy.AutoConfirm = true;
        _fx.CloseDialogs();
        _fx.EnsureAnimationOff();
    }

    private static string State() => DesktopFixture.ReadState() ?? "";

    private static AgentAction A(AgentActionKind k, string? el = null,
        string? text = null, string? result = null, long? hwnd = null,
        ImagePoint? pt = null, int? ms = null, string? query = null,
        string? key = null) =>
        new(k, ElementId: el, Text: text, Result: result, Hwnd: hwnd,
            Point: pt, Ms: ms, Query: query, Key: key);

    // ----------------------------------------------------------------
    // 1. End-to-end: the scripted "model" drives the real TestApp
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_E2E_Type_Toggle_Save()
    {
        DesktopFixture.WriteState("idle");
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.SetValue,
                el: ScriptedModelAdapter.FindId(i, "Edit", "Main text"),
                text: "hello-m3")]),
            i => new ModelTurn([A(AgentActionKind.Toggle,
                el: ScriptedModelAdapter.FindId(i, "CheckBox", "Enable"))]),
            i => new ModelTurn([A(AgentActionKind.Invoke,
                el: ScriptedModelAdapter.FindId(i, "Button", "Save"))]),
            _ => new ModelTurn([A(AgentActionKind.Finish, result: "goal done")]),
        });

        var r = _fx.Inbrisk.RunGoal("type hello, enable feature, save",
            adapter, hwndHint: _fx.Hwnd);

        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.True(r.Steps >= 3);
        Assert.All(r.History, s => Assert.True(s.Outcome.Success,
            $"step {s.Step} {s.Action.Summary()} → {s.Outcome.Kind}: {s.Outcome.Detail}"));
        Assert.True(SpinWait.SpinUntil(() => State().StartsWith("saved:hello-m3"), 4000));
        Assert.Equal("saved:hello-m3", State());
    }

    // ----------------------------------------------------------------
    // 2. Stale element re-resolution inside a live loop
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_StaleElement_Reresolves()
    {
        _fx.CloseDialogs();
        string? staleCloseId = null;
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            // open dialog
            i => new ModelTurn([A(AgentActionKind.Invoke,
                el: ScriptedModelAdapter.FindId(i, "Button", "Open dialog"))]),
            // close it — and remember the close button's id
            i =>
            {
                staleCloseId = ScriptedModelAdapter.FindId(i, "Button", "Close dialog");
                return new ModelTurn([A(AgentActionKind.Invoke, el: staleCloseId)]);
            },
            // reopen — new dialog instance, same old id must re-resolve
            i => new ModelTurn([A(AgentActionKind.Invoke,
                el: ScriptedModelAdapter.FindId(i, "Button", "Open dialog"))]),
            i => new ModelTurn([A(AgentActionKind.Invoke, el: staleCloseId)]),
            _ => new ModelTurn([A(AgentActionKind.Finish, result: "ok")]),
        });

        var r = _fx.Inbrisk.RunGoal("open/close dialog twice with stale id",
            adapter, hwndHint: _fx.Hwnd);

        Assert.Equal(AgentStatus.Completed, r.Status);
        var lastInvoke = r.History.Last(s => s.Action.Kind == AgentActionKind.Invoke);
        Assert.True(lastInvoke.Outcome.Success, lastInvoke.Outcome.Detail);
        // both dialogs gone
        _fx.CloseDialogs();
    }

    // ----------------------------------------------------------------
    // 3. Window disappears → WindowLost outcome, loop survives
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_WindowLost_Recoverable()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            _ => new ModelTurn([A(AgentActionKind.FocusWindow, hwnd: 0x00DEAD)]),
            _ => new ModelTurn([A(AgentActionKind.Finish, result: "noted")]),
        });
        var r = _fx.Inbrisk.RunGoal("focus a dead window", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.Equal(OutcomeKind.WindowLost, r.History[0].Outcome.Kind);
        Assert.False(r.History[0].Outcome.Success);
    }

    // ----------------------------------------------------------------
    // 4. Malformed actions never reach Windows
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_Malformed_Rejected()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            _ => new ModelTurn([
                A(AgentActionKind.Click),                  // no target at all
                A(AgentActionKind.Type, el: null),         // missing text
                A(AgentActionKind.SetValue, el: null),     // no element, no text
                new AgentAction(AgentActionKind.Key, Key: "NotAKey"),
            ]),
            _ => new ModelTurn([A(AgentActionKind.Finish)]),
        });
        var r = _fx.Inbrisk.RunGoal("malformed storm", adapter,
            new AgentOptions(MaxConsecutiveFailures: 99), hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
        Assert.Equal(4, r.Steps);
        Assert.All(r.History, s => Assert.Equal(OutcomeKind.Malformed, s.Outcome.Kind));
    }

    // ----------------------------------------------------------------
    // 5. Unknown element id rejected at validation
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_UnknownElement_Rejected()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            _ => new ModelTurn([A(AgentActionKind.Click, el: "uia_999999")]),
            _ => new ModelTurn([A(AgentActionKind.Finish)]),
        });
        var r = _fx.Inbrisk.RunGoal("click ghost", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(OutcomeKind.Malformed, r.History[0].Outcome.Kind);
        Assert.Contains("unknown element", r.History[0].Outcome.Detail);
    }

    // ----------------------------------------------------------------
    // 6. Repeated failure → consecutive-failure abort
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_RepeatedFailure_Aborts()
    {
        var adapter = new ScriptedModelAdapter(Array.Empty<Func<ModelTurnInput, ModelTurn>>())
        {
            Fallback = i => new ModelTurn([A(AgentActionKind.Toggle,
                el: ScriptedModelAdapter.FindId(i, "Button", "Disabled")
                    ?? ScriptedModelAdapter.FindId(i, "Button", "nope"))]),
        };
        var r = _fx.Inbrisk.RunGoal("toggle the disabled button", adapter,
            new AgentOptions(MaxConsecutiveFailures: 2), hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("consecutive failures", r.Reason);
        Assert.Equal(2, r.Steps);
    }

    // ----------------------------------------------------------------
    // 7. Max steps termination
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_MaxSteps_Terminates()
    {
        var adapter = new ScriptedModelAdapter(Array.Empty<Func<ModelTurnInput, ModelTurn>>())
        {
            Fallback = _ => new ModelTurn([A(AgentActionKind.Wait, ms: 20)]),
        };
        var r = _fx.Inbrisk.RunGoal("never finishes", adapter,
            new AgentOptions(MaxSteps: 4, MaxIdenticalRepeats: 99),
            hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Failed, r.Status);
        Assert.Contains("max steps", r.Reason);
        Assert.Equal(4, r.Steps);
    }

    // ----------------------------------------------------------------
    // 8. Timeout termination
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_Timeout_Terminates()
    {
        var adapter = new ScriptedModelAdapter(Array.Empty<Func<ModelTurnInput, ModelTurn>>())
        {
            Fallback = _ => new ModelTurn([A(AgentActionKind.Wait, ms: 700)]),
        };
        var r = _fx.Inbrisk.RunGoal("slow goal", adapter,
            new AgentOptions(TimeoutMs: 1500, MaxSteps: 50), hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.TimedOut, r.Status);
    }

    // ----------------------------------------------------------------
    // 9. Cancel mid-run
    // ----------------------------------------------------------------
    [Fact]
    public async Task Agent_Cancel_StopsLoop()
    {
        var agent = _fx.Inbrisk.CreateAgent(new AgentOptions(MaxSteps: 50));
        var adapter = new ScriptedModelAdapter(Array.Empty<Func<ModelTurnInput, ModelTurn>>())
        {
            Fallback = _ => new ModelTurn([A(AgentActionKind.Wait, ms: 1500)]),
        };
        var run = Task.Run(() => agent.RunAsync("long run", adapter, _fx.Hwnd));
        await Task.Delay(600);
        agent.Cancel();
        var r = await run;
        Assert.Equal(AgentStatus.Cancelled, r.Status);
    }

    // ----------------------------------------------------------------
    // 10. Pause / resume
    // ----------------------------------------------------------------
    [Fact]
    public async Task Agent_PauseResume()
    {
        var agent = _fx.Inbrisk.CreateAgent(new AgentOptions(MaxSteps: 10));
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            _ => new ModelTurn([A(AgentActionKind.Wait, ms: 20)]),
            _ => new ModelTurn([A(AgentActionKind.Wait, ms: 20)]),
            _ => new ModelTurn([A(AgentActionKind.Finish, result: "done")]),
        });
        var paused = new ManualResetEventSlim();
        var once = 0;
        agent.StepCompleted += _ =>
        {
            if (Interlocked.Exchange(ref once, 1) == 0) { agent.Pause(); paused.Set(); }
        };
        var run = Task.Run(() => agent.RunAsync("pausable", adapter, _fx.Hwnd));

        Assert.True(paused.Wait(8000), "agent never reached first step");
        Assert.Equal(AgentStatus.Paused, agent.Status);
        var stepsAtPause = agent.History.Count;
        await Task.Delay(400);
        Assert.Equal(stepsAtPause, agent.History.Count); // frozen

        agent.Resume();
        var r = await run;
        Assert.Equal(AgentStatus.Completed, r.Status);
    }

    // ----------------------------------------------------------------
    // 11. Stale frame coordinate rejection (race-condition guard)
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_StaleFrame_Rejected()
    {
        long oldFrameId = -1;
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            // turn 1: click using the CURRENT frame — valid
            i =>
            {
                oldFrameId = i.Observation.Frames[0].FrameId;
                return new ModelTurn([A(AgentActionKind.Click,
                    pt: new ImagePoint(6, 6, oldFrameId))]);
            },
            // turn 2: reuse the OLD frame id — must be rejected
            _ => new ModelTurn([A(AgentActionKind.Click,
                pt: new ImagePoint(6, 6, oldFrameId))]),
            _ => new ModelTurn([A(AgentActionKind.Finish)]),
        });
        var r = _fx.Inbrisk.RunGoal("frame guard", adapter,
            new AgentOptions(ScreenshotPolicy: VisualAttachPolicy.Always),
            hwndHint: _fx.Hwnd);

        Assert.Equal(AgentStatus.Completed, r.Status);
        // M4: a successful click with no observable delta is Unverified or
        // NoProgress — both mean "ran, nothing proven"
        Assert.Contains(r.History[0].Outcome.Kind,
            new[] { OutcomeKind.Unverified, OutcomeKind.NoProgress,
                OutcomeKind.Verified, OutcomeKind.ObservedChange, OutcomeKind.Failed });
        Assert.Equal("SendInput.click", r.History[0].Outcome.Method);
        Assert.Equal(OutcomeKind.StaleFrame, r.History[1].Outcome.Kind);
        Assert.False(r.History[1].Outcome.Success);
    }

    // ----------------------------------------------------------------
    // 12. Screenshot policy: semantic-only vs attached frame
    // ----------------------------------------------------------------
    [Fact]
    public void Observation_ScreenshotPolicy()
    {
        // TestApp has rich UIA → Auto attaches nothing (semantic-only)
        var auto = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            screenshotPolicy: VisualAttachPolicy.Auto);
        Assert.Empty(auto.Observation.Frames);
        Assert.NotEmpty(auto.Observation.Elements);
        Assert.Contains(auto.Observation.Elements, e => e.Role == "Edit");

        // Always → window frame with PNG + frame id for grounding
        var vis = _fx.Inbrisk.ObserveAgent(_fx.Hwnd,
            screenshotPolicy: VisualAttachPolicy.Always);
        var fr = Assert.Single(vis.Observation.Frames);
        Assert.Equal(VisualAttach.WindowFrame, fr.Attach);
        Assert.True(fr.Png!.Length > 500);
        Assert.True(fr.Width > 100);
        Assert.NotEmpty(vis.Frames); // raw frames available for grounding
    }

    // ----------------------------------------------------------------
    // 13. Observation delta: element value change surfaces
    // ----------------------------------------------------------------
    [Fact]
    public void Observation_Delta_ReportsChanges()
    {
        _ = _fx.Inbrisk.ObserveAgent(_fx.Hwnd); // baseline
        var edit = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
            AutomationId: "MainTextBox"));
        _fx.Inbrisk.SetValue(edit[0].Id, "delta-check");
        Thread.Sleep(300);

        var obs2 = _fx.Inbrisk.ObserveAgent(_fx.Hwnd).Observation;
        var d = obs2.Delta;
        Assert.NotNull(d);
        Assert.Contains(d!.Changed,
            e => e.Role == "Edit" && e.Value == "delta-check");
        Assert.Contains(d.Notes, n => n.Contains("changed"));
    }

    // ----------------------------------------------------------------
    // 14. Safety: kill switch blocks the whole loop
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_KillSwitch_Blocks()
    {
        _fx.Inbrisk.Policy.KillSwitch = true;
        try
        {
            var adapter = new ScriptedModelAdapter(Array.Empty<Func<ModelTurnInput, ModelTurn>>());
            var r = _fx.Inbrisk.RunGoal("anything", adapter, hwndHint: _fx.Hwnd);
            Assert.Equal(AgentStatus.Blocked, r.Status);
            Assert.Equal(0, r.Steps);
        }
        finally { _fx.Inbrisk.Policy.KillSwitch = false; }
    }

    // ----------------------------------------------------------------
    // 15. Safety: CONFIRM gate — confirmer denies then approves
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_Confirm_DenyThenApprove()
    {
        _fx.Inbrisk.Policy.AutoConfirm = false;
        try
        {
            var approvals = 0;
            var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
            {
                i => new ModelTurn([A(AgentActionKind.SetValue,
                    el: ScriptedModelAdapter.FindId(i, "Edit", "Main text"),
                    text: "blocked-text")]),
                i => new ModelTurn([A(AgentActionKind.SetValue,
                    el: ScriptedModelAdapter.FindId(i, "Edit", "Main text"),
                    text: "approved-text")]),
                _ => new ModelTurn([A(AgentActionKind.Finish)]),
            });
            var r = _fx.Inbrisk.RunGoal("type twice", adapter,
                new AgentOptions(Confirmer: (_, _) => approvals++ > 0),
                hwndHint: _fx.Hwnd);

            Assert.Equal(AgentStatus.Completed, r.Status);
            Assert.Equal(OutcomeKind.ConfirmationDenied, r.History[0].Outcome.Kind);
            Assert.True(r.History[1].Outcome.Success);
            var edit = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd,
                AutomationId: "MainTextBox"));
            Assert.Equal("approved-text",
                edit[0].Props["value"]?.ToString());
        }
        finally { _fx.Inbrisk.Policy.AutoConfirm = true; }
    }

    // ----------------------------------------------------------------
    // 16. Condensed model-facing history + prev-action context
    // ----------------------------------------------------------------
    [Fact]
    public void Agent_ModelSeesCondensedHistory()
    {
        var adapter = new ScriptedModelAdapter(new Func<ModelTurnInput, ModelTurn>[]
        {
            i => new ModelTurn([A(AgentActionKind.Invoke,
                el: ScriptedModelAdapter.FindId(i, "Button", "Save"))]),
            i =>
            {
                // turn 2 must see turn 1's outcome + a condensed step
                Assert.Single(i.History);
                Assert.Contains("invoke", i.History[0].Action);
                // M4: invoke now auto-verifies when evidence exists —
                // either Verified (element gone / prop change / event) or
                // honest Unverified, never a blind Verified
                Assert.Contains(i.History[0].Outcome,
                    new[] { "Verified", "Unverified" });
                Assert.NotNull(i.Observation.PrevAction);
                Assert.True(i.Observation.PrevAction!.Success);
                return new ModelTurn([A(AgentActionKind.Finish)]);
            },
        });
        var r = _fx.Inbrisk.RunGoal("check history", adapter, hwndHint: _fx.Hwnd);
        Assert.Equal(AgentStatus.Completed, r.Status);
    }
}
