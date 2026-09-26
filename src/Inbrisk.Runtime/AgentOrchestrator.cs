using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// The provider-independent agent loop:
///   goal → observe → adapter → validate → execute → verify → delta → adapter → …
/// Every run owns an AgentRunContext (ids, ct, pause, confirmer, budgets,
/// history) — nothing run-scoped is process-global, so concurrent runs are
/// isolated. Recovery, anti-loop guards, pause/resume/cancel and the kill
/// switch live here; the model only sees canonical observations/actions.
/// </summary>
public sealed class AgentOrchestrator : IAgentController
{
    private readonly ObservationBuilder _obs;
    private readonly ActionResolver _resolver;
    private readonly Func<long, ChangeMonitor> _monitorFactory;
    private readonly ElementRegistry _registry;
    private readonly IInputService _input;
    private readonly SafetyPolicy _policy;
    private readonly AgentOptions _opts;
    private readonly ITelemetrySink? _telemetry;
    private AgentRunContext? _run;

    public event Action<StepRecord>? StepCompleted;
    public IReadOnlyList<StepRecord> History => _run?.History ?? (IReadOnlyList<StepRecord>)[];
    public AgentStatus Status => _run?.Status ?? AgentStatus.Created;
    public string? RunId => _run?.RunId;

    /// <summary>The live run context — null until RunAsync starts.</summary>
    public AgentRunContext? RunContext => _run;

    public AgentOrchestrator(
        ObservationBuilder obs,
        ActionResolver resolver,
        Func<long, ChangeMonitor> monitorFactory,
        ElementRegistry registry,
        IInputService input,
        SafetyPolicy policy,
        AgentOptions? opts = null,
        ITelemetrySink? telemetry = null)
    {
        _obs = obs;
        _resolver = resolver;
        _monitorFactory = monitorFactory;
        _registry = registry;
        _input = input;
        _policy = policy;
        _opts = opts ?? new AgentOptions();
        _telemetry = telemetry;
    }

    // --------------------------- control surface ------------------------------

    /// <summary>Reject invalid transitions — false return = refused.</summary>
    public bool Pause()
    {
        var r = _run;
        if (r == null || !r.Transition(AgentStatus.Paused))
        { Lifecycle(r, AgentStatus.Paused, rejected: true); return false; }
        r.PauseGate.Reset();
        Lifecycle(r, AgentStatus.Paused);
        return true;
    }

    public bool Resume()
    {
        var r = _run;
        if (r == null || !r.Transition(AgentStatus.Running))
        { Lifecycle(r, AgentStatus.Running, rejected: true); return false; }
        r.NeedsReobserve = true; // never trust the pre-pause observation
        r.PauseGate.Set();
        Lifecycle(r, AgentStatus.Running);
        return true;
    }

    public bool Cancel()
    {
        var r = _run;
        if (r == null || !r.Transition(AgentStatus.Cancelling))
        { Lifecycle(r, AgentStatus.Cancelling, rejected: true); return false; }
        r.Cts.Cancel();
        r.PauseGate.Set();          // a paused run must still exit
        _input.ReleaseAll();        // never leave a held key/button behind
        Lifecycle(r, AgentStatus.Cancelling);
        return true;
    }

    // --------------------------- the loop -------------------------------------

    public async Task<AgentResult> RunAsync(string goal, IModelAdapter adapter,
        long? hwndHint = null)
    {
        var run = new AgentRunContext(_opts);
        if (!run.Transition(AgentStatus.Running))
            throw new InvalidOperationException("run context failed to start");
        _run = run;
        _obs.ResetDelta();
        Lifecycle(run, AgentStatus.Running);

        var ct = run.Cts.Token;
        var consecFail = 0;
        var stagnant = 0;
        string? lastKey = null;
        var repeats = 0;
        StepOutcome? prevOutcome = null;
        ChangeMonitor? monitor = null;
        var scopeHwnd = hwndHint;

        var (obs, frames) = SafeBuild(run, scopeHwnd, null, monitor);
        scopeHwnd ??= obs.ActiveWindow?.Hwnd;

        try
        {
            while (true)
            {
                var stop = CheckStop(run);
                if (stop != null) return FinishRun(run, stop.Value, null, Reason(stop.Value));
                if (run.History.Count >= _opts.MaxSteps)
                    return FinishRun(run, AgentStatus.Failed, null,
                        $"max steps ({_opts.MaxSteps}) reached");
                if (stagnant >= _opts.MaxNoProgressSteps)
                    return FinishRun(run, AgentStatus.Failed, null,
                        $"no progress in {stagnant} steps — terminating");

                if (run.NeedsReobserve) { (obs, frames) = SafeBuild(run, scopeHwnd, prevOutcome, monitor); run.NeedsReobserve = false; }

                ModelTurn turn;
                try
                {
                    turn = await adapter.DecideAsync(new ModelTurnInput(goal, obs,
                        run.Condensed.TakeLast(_opts.MaxHistoryEntries).ToList(),
                        _opts.MaxSteps - run.History.Count,
                        run.Deadline - run.Clock.Elapsed,
                        run.HistorySummary, run.RunId), ct);
                }
                catch (OperationCanceledException)
                { return FinishRun(run, AgentStatus.Cancelled, null, "cancelled"); }
                catch (Exception e)
                { return FinishRun(run, AgentStatus.Failed, null, $"adapter threw: {e.Message}"); }

                if (turn.Actions.Count == 0)
                {
                    consecFail++;
                    if (consecFail >= _opts.MaxConsecutiveFailures)
                        return FinishRun(run, AgentStatus.Failed, null, "model produced no actions");
                    (obs, frames) = SafeBuild(run, scopeHwnd, new StepOutcome(OutcomeKind.Failed,
                        false, "adapter", "empty turn", 0), monitor);
                    scopeHwnd = obs.ActiveWindow?.Hwnd ?? scopeHwnd;
                    continue;
                }

                var finished = false; string? finishResult = null;
                var batchBroken = false;
                for (var i = 0; i < turn.Actions.Count; i++)
                {
                    var action = turn.Actions[i];
                    var stop2 = CheckStop(run);
                    if (stop2 != null) return FinishRun(run, stop2.Value, null, Reason(stop2.Value));
                    if (run.NeedsReobserve)
                    { (obs, frames) = SafeBuild(run, scopeHwnd, prevOutcome, monitor); run.NeedsReobserve = false; }

                    if (action.Kind == AgentActionKind.Finish)
                    { finished = true; finishResult = action.Result; break; }

                    if (action.Kind == AgentActionKind.Observe)
                    {
                        (obs, frames) = SafeBuild(run, scopeHwnd, prevOutcome, monitor);
                        scopeHwnd = obs.ActiveWindow?.Hwnd ?? scopeHwnd;
                        continue;
                    }

                    // ---- batch precondition: after the first action every
                    // subsequent action is validated against the CURRENT
                    // observation, not the one the model saw
                    if (i > 0 && !BatchPreconditionOk(action, obs, frames, out var why))
                    {
                        var broken = new StepOutcome(
                            why.Contains("frame") ? OutcomeKind.StaleFrame : OutcomeKind.Stale,
                            false, "batch", $"batch cut at action {i}: {why}",
                            0);
                        RecordStep(run, obs, action, broken);
                        batchBroken = true;
                        break;
                    }

                    var stepNo = run.History.Count + 1;
                    var actionId = $"a{stepNo}";
                    run.CurrentAction = action;
                    // confirmer only when the run actually configured one —
                    // a null option means "fall back to global policy"
                    var actx = new ActionContext(ct,
                        run.Options.Confirmer != null ? run.Confirm : null,
                        run.RunId, stepNo, actionId);
                    var octx = new ObsContext(obs.ObservationId,
                        scopeHwnd ?? obs.ActiveWindow?.Hwnd, frames,
                        () => monitor ??= _monitorFactory(
                            (scopeHwnd ?? obs.ActiveWindow?.Hwnd) ?? 0),
                        _opts.MaxFrameAgeMs);
                    var outcome = _resolver.Execute(action, octx, actx);
                    run.CurrentAction = null;
                    run.NoteElementRef(action.ElementId);

                    // perceive-verify: every action is followed by a fresh
                    // observation carrying its delta — needed BEFORE the step
                    // is recorded so no-progress marking lands on the record
                    var actObs = obs;
                    (obs, frames) = SafeBuild(run, scopeHwnd, outcome, monitor);
                    scopeHwnd = obs.ActiveWindow?.Hwnd ?? scopeHwnd;

                    // ---- no-progress detection: a mutating action that
                    // produced zero meaningful delta is loop suspicion.
                    // Delta==null means "no baseline yet" — not evidence.
                    if (outcome.Success && IsMutating(action.Kind)
                        && obs.Delta is { IsEmpty: true })
                    {
                        stagnant++;
                        outcome = outcome with
                        {
                            Kind = OutcomeKind.NoProgress,
                            Detail = (outcome.Detail ?? "") +
                                " — produced no observable change",
                        };
                    }
                    else if (obs.Delta is { IsEmpty: false })
                    {
                        stagnant = 0;
                    }

                    RecordStep(run, actObs, action, outcome, obs);
                    prevOutcome = outcome;

                    consecFail = outcome.Success ? 0 : consecFail + 1;
                    if (consecFail >= _opts.MaxConsecutiveFailures)
                        return FinishRun(run, AgentStatus.Failed, null,
                            $"{consecFail} consecutive failures — last: {outcome.Kind} {outcome.Detail}");

                    if (outcome.Kind == OutcomeKind.Cancelled)
                        return FinishRun(run, AgentStatus.Cancelled, null, "cancelled");

                    if (stagnant >= _opts.MaxNoProgressSteps)
                        return FinishRun(run, AgentStatus.Failed, null,
                            $"no meaningful delta after {stagnant} steps");

                    var key = action.Summary() + "|" + outcome.Kind;
                    repeats = key == lastKey ? repeats + 1 : 0;
                    lastKey = key;
                    if (repeats >= _opts.MaxIdenticalRepeats)
                        return FinishRun(run, AgentStatus.Failed, null,
                            $"identical action repeated {repeats + 1}x without progress: {key}");

                    if (RecoveryPolicy.IsFatal(outcome.Kind))
                        return FinishRun(run, outcome.Kind is OutcomeKind.SecureDesktop
                            or OutcomeKind.TargetElevated or OutcomeKind.PolicyDenied
                            ? AgentStatus.Blocked : AgentStatus.Failed,
                            null, $"{outcome.Kind}: {outcome.Detail}");
                }

                if (finished)
                    return FinishRun(run, AgentStatus.Completed, finishResult, "model finished");
                if (batchBroken) { consecFail++; continue; }
            }
        }
        catch (OperationCanceledException)
        {
            return FinishRun(run, AgentStatus.Cancelled, null, "cancelled");
        }
        finally
        {
            _input.ReleaseAll();
            monitor?.Dispose();
            run.Dispose();
        }
    }

    public AgentResult Run(string goal, IModelAdapter adapter, long? hwndHint = null) =>
        RunAsync(goal, adapter, hwndHint).GetAwaiter().GetResult();

    // --------------------------- internals ------------------------------------

    /// <summary>Element ids must still resolve and image points must still
    /// reference a frame in the CURRENT observation before a batched action
    /// runs. False → cut the batch and return control to the model.</summary>
    private bool BatchPreconditionOk(AgentAction a, AgentObservation obs,
        IReadOnlyDictionary<long, FrameRef> frames, out string why)
    {
        why = "";
        if (a.ElementId != null && _registry.EnsureAlive(a.ElementId) == null)
        { why = $"element {a.ElementId} stale after earlier batch action"; return false; }
        if (a.Point != null && !frames.ContainsKey(a.Point.FrameId))
        { why = $"frame {a.Point.FrameId} stale after earlier batch action"; return false; }
        if (a.To != null && !frames.ContainsKey(a.To.FrameId))
        { why = $"drag-destination frame {a.To.FrameId} stale"; return false; }
        return true;
    }

    private static bool IsMutating(AgentActionKind k) => k is
        AgentActionKind.Click or AgentActionKind.RightClick or AgentActionKind.DoubleClick
        or AgentActionKind.Drag or AgentActionKind.Scroll or AgentActionKind.Hover
        or AgentActionKind.Invoke
        or AgentActionKind.SetValue or AgentActionKind.Toggle or AgentActionKind.Select
        or AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey
        or AgentActionKind.FocusWindow or AgentActionKind.FocusElement;

    private StepRecord RecordStep(AgentRunContext run, AgentObservation obs,
        AgentAction action, StepOutcome outcome, AgentObservation? postObs = null)
    {
        var deltaSource = postObs ?? obs;
        var step = new StepRecord(run.History.Count + 1, obs.ObservationId,
            action, outcome, null, outcome.Method,
            deltaSource.Delta is { } d ? DeltaSummary(d) : null,
            run.RunId, $"a{run.History.Count + 1}");
        run.History.Add(step);
        run.Condensed.Add(new CondensedStep(step.Step, action.Summary(),
            outcome.Kind.ToString(), outcome.Detail));
        CompactHistory(run);
        StepCompleted?.Invoke(step);
        return step;
    }

    /// <summary>Three levels: recent detailed steps → condensed (bounded) →
    /// deterministic run summary. Only the model-facing view is compacted;
    /// History (debug) keeps everything.</summary>
    private void CompactHistory(AgentRunContext run)
    {
        var overflow = run.Condensed.Count - _opts.MaxHistoryEntries;
        if (overflow <= 0) return;
        var old = run.Condensed.GetRange(0, overflow);
        run.Condensed.RemoveRange(0, overflow);
        var actions = old.GroupBy(c => c.Action.Split('(')[0])
            .Select(g => $"{g.Key}×{g.Count()}");
        var outcomes = old.GroupBy(c => c.Outcome)
            .Select(g => $"{g.Key}×{g.Count()}");
        var chunk = $"steps {old[0].Step}-{old[^1].Step}: " +
            $"{string.Join(",", actions)} [{string.Join(",", outcomes)}]";
        run.HistorySummary = run.HistorySummary == null
            ? chunk : run.HistorySummary + " | " + chunk;
    }

    private BuiltObservation Build(AgentRunContext run, long? scopeHwnd,
        StepOutcome? prevOutcome, ChangeMonitor? monitor)
        => _obs.Build(scopeHwnd, _opts.BudgetOrDefault, _opts.ScreenshotPolicy,
            monitor, prevOutcome, run.RecentElementRefs);

    /// <summary>Observation building hits real Windows APIs that can fail
    /// transiently (COM RPC during window transitions). Retry briefly; a
    /// persistent failure becomes a step outcome, not a run-ending crash.</summary>
    private BuiltObservation SafeBuild(AgentRunContext run, long? scopeHwnd,
        StepOutcome? prevOutcome, ChangeMonitor? monitor)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return Build(run, scopeHwnd, prevOutcome, monitor); }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
                    "observation", Target: $"build attempt {attempt} failed: {e.Message}"));
                if (attempt >= 2)
                    return new BuiltObservation(new AgentObservation(0, DateTimeOffset.Now,
                        null, [], [], [], [], [], "unavailable",
                        new StepOutcome(OutcomeKind.Failed, false, "observe",
                            $"observation build failed: {e.Message}", 0), null),
                        new Dictionary<long, FrameRef>());
                if (run.Cts.Token.WaitHandle.WaitOne(300))
                    throw new OperationCanceledException();
            }
        }
    }

    /// <summary>Cancel → Cancelling; timeout → TimedOut; kill → Blocked;
    /// paused → wait on the gate. Returns the terminal status or null.</summary>
    private AgentStatus? CheckStop(AgentRunContext run)
    {
        if (run.Cts.IsCancellationRequested)
        { run.Transition(AgentStatus.Cancelled); return AgentStatus.Cancelled; }
        if (run.Clock.ElapsedMilliseconds > _opts.TimeoutMs)
        { run.Transition(AgentStatus.TimedOut); return AgentStatus.TimedOut; }
        if (_policy.KillSwitch)
        { run.Transition(AgentStatus.Blocked); return AgentStatus.Blocked; }
        if (run.Status == AgentStatus.Paused)
        {
            try { run.PauseGate.Wait(run.Cts.Token); }
            catch (OperationCanceledException)
            { run.Transition(AgentStatus.Cancelled); return AgentStatus.Cancelled; }
        }
        return null;
    }

    private void Lifecycle(AgentRunContext? run, AgentStatus to, bool rejected = false)
    {
        _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now, "lifecycle",
            Target: $"{run?.RunId ?? "-"} → {to}{(rejected ? " REJECTED" : "")}"));
    }

    private AgentResult FinishRun(AgentRunContext run, AgentStatus status,
        string? result, string? reason)
    {
        run.Transition(status);
        Lifecycle(run, status);
        _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now, "agent",
            Backend: status.ToString(),
            DurationMs: run.History.Sum(h => (double)h.Outcome.DurationMs),
            ElementCount: run.History.Count, Target: run.RunId));
        return new AgentResult(status, run.History.Count, result, run.History,
            reason, run.RunId);
    }

    private static string Reason(AgentStatus s) => s switch
    {
        AgentStatus.Cancelled => "cancelled",
        AgentStatus.TimedOut => "timeout",
        AgentStatus.Blocked => "blocked (kill switch / guard)",
        _ => s.ToString(),
    };

    private static string DeltaSummary(ObsDelta d) =>
        string.Join("; ", d.Notes.Concat(
            d.Changed.Select(e => $"'{e.Name}'={e.State ?? e.Value ?? ""}"))).Trim();
}
