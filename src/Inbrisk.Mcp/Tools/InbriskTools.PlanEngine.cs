using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

public sealed partial class InbriskTools
{
    private async Task<CallToolResult> RunPlanCore(RunStep[] steps,
        string? runId, Stopwatch sw, CancellationToken ct,
        string? detail = null, bool? enableReflex = null,
        string? autoDismissModals = null)
    {
        if (steps is not { Length: > 0 })
            return Error(OutcomeKind.Malformed, "steps required", sw);

        // ---- reflex (modal interception) params — validated before the
        // run exists. enableReflex defaults true; autoDismissModals
        // defaults to closeOnly — cancel a save prompt rather than
        // committing or discarding the user's work.
        var reflexOn = enableReflex ?? true;
        var reflexDismiss = ReflexDismissMode.CloseOnly;
        if (autoDismissModals != null)
        {
            switch (autoDismissModals.Trim().ToLowerInvariant())
            {
                case "off": reflexDismiss = ReflexDismissMode.Off; break;
                case "save": reflexDismiss = ReflexDismissMode.Save; break;
                case "discard": reflexDismiss = ReflexDismissMode.Discard; break;
                case "closeonly" or "close_only" or "close-only":
                    reflexDismiss = ReflexDismissMode.CloseOnly; break;
                default:
                    return Error(OutcomeKind.Malformed,
                        "autoDismissModals must be off|save|discard|closeOnly, " +
                        $"got '{autoDismissModals}'", sw);
            }
        }
        var reflexModeName = reflexDismiss switch
        {
            ReflexDismissMode.Off => "off",
            ReflexDismissMode.Save => "save",
            ReflexDismissMode.Discard => "discard",
            _ => "closeOnly",
        };
        var reflexEng = reflexOn ? _s.Reflex : null;

        using var trace = PerfTrace.Begin("computer_run",
            runId ?? "run.pending");
        trace.Set("stepCount", steps.Length);

        var state = runId != null && _s.Runs.TryGetValue(runId, out var existing)
            ? existing
            : null;
        var explicitHwnd = steps.Select(s => ParseHwnd(s.Hwnd) ?? ParseHwnd(s.Target?.Hwnd)).FirstOrDefault(h => h != null);
        if (state == null)
        {
            state = new RunState
            {
                RunId = !string.IsNullOrWhiteSpace(runId) ? runId
                    : "r_" + Guid.NewGuid().ToString("N")[..8],
                ScopeHwnd = explicitHwnd ?? _s.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd,
            };
            using (PerfTrace.Stage("plan.baseline"))
            {
                // Read-only plans (find/assert/wait…) never mutate the UI —
                // defer the baseline entirely: Delta() can then prove "no
                // relevant events" and skip both snapshots. Mutating plans
                // keep the pre-run snapshot for an honest element-level diff.
                var readOnly = steps.All(s => ReadOnlyStepActions.Contains(
                    s.Action ?? ""));
                state.BaselineDeferred = readOnly;
                state.RunStartedAt = DateTimeOffset.Now;
                state.BaselineHwnd = state.ScopeHwnd;
                state.BaselineAt = DateTimeOffset.Now;
                if (!readOnly)
                    state.Baseline = Snapshot(state.ScopeHwnd);
                else
                    PerfTrace.Count("plan.baselineDeferred");
            }
            // reflex modals are reported per-run: slice the session-wide
            // engine log from this offset on every leg (incl. resumes).
            state.ReflexLogBaseline = _s.Reflex?.Log?.Count ?? 0;
            _s.Runs[state.RunId] = state;
        }
        else if (explicitHwnd != null)
        {
            state.ScopeHwnd = explicitHwnd;
        }
        state.Slim = Slim(detail);
        state.ReflexEnabled = reflexEng != null;
        state.ReflexMode = reflexModeName;
        state.ReflexAborted = false; // per-leg flag — reset on resume
        trace.Id = state.RunId;

        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return RunReport(state, "EmergencyStopped", sw, [],
                pauseStep: -1, pauseStatus: "EmergencyStopped",
                pauseError: StoppedDetail);

        var scope = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        var octx = new ObsContext(
            _s.LastObservation?.ObservationId ?? 0, scope, _s.Frames,
            () => _s.MonitorFor(scope ?? 0), MaxFrameAgeMs: 15_000,
            _s.ValidObsIds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);
        using var cancelReg = linked.Token.CanBeCanceled
            ? linked.Token.Register(() => _s.Rt.PurgePendingWork())
            : default;

        var report = new List<Dictionary<string, object?>>();
        var internalActions = 0;
        var executed = 0;
        var skipped = 0;
        var pre = state.LastElementId != null
            ? ReadElementState(state.LastElementId) : null;
        var fgBefore = _s.Rt.ForegroundWindow()?.Hwnd;
        var eventGenBefore = _s.Rt.EventBuffer.CurrentGeneration;

        // outer lease: the run reads as one continuous Active session —
        // waits between steps don't flicker the indicator. Disposed when
        // this call returns (pause/checkpoint → grace → ConnectedIdle).
        using var runLease = _s.Rt.Activity.BeginActivity();
        state.PlannedSteps = steps;

        IDisposable? planHighlightLease = null;
        long currentHighlightHwnd = 0;

        void UpdateHighlight(long targetHwnd)
        {
            if (targetHwnd == currentHighlightHwnd) return;
            planHighlightLease?.Dispose();
            planHighlightLease = null;
            currentHighlightHwnd = targetHwnd;
            if (targetHwnd > 0 && _s?.Rt?.TargetHighlight != null)
            {
                planHighlightLease = _s.Rt.TargetHighlight.BeginWindowActivity(targetHwnd, _s.SessionId);
            }
        }

        // Reflex engine: armed lazily once a real target window is known —
        // the run's scope hwnd when present, else the first step's resolved
        // window. Multi-window runs re-arm per step when the step's target
        // window changes. A faulting engine is best-effort: once broken it
        // stays off for the rest of the leg instead of retrying per step.
        long reflexArmedHwnd = 0;
        var reflexBroken = false;
        void EnsureReflexArmed(long hwnd)
        {
            if (reflexEng == null || reflexBroken || hwnd <= 0 ||
                hwnd == reflexArmedHwnd) return;
            try
            {
                var pid = _s?.Rt?.Window(hwnd)?.Pid ?? 0;
                reflexEng.Arm(hwnd, pid, reflexDismiss);
                reflexArmedHwnd = hwnd;
            }
            catch (Exception ex)
            {
                reflexBroken = true;
                Debug.WriteLine($"reflex arm hwnd=0x{hwnd:X} failed, engine disabled for this leg: {ex.Message}");
            }
        }
        EnsureReflexArmed(scope ?? 0);

        try
        {
        for (var i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            var stepTargetHwnd = ParseHwnd(s.Hwnd) ?? ParseHwnd(s.Target?.Hwnd ?? (s.Target?.Window != null && (s.Target.Window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || long.TryParse(s.Target.Window, out _)) ? s.Target.Window : null))
                ?? (s.ElementId != null ? _s?.Rt?.Parts.Registry.Get(s.ElementId)?.Hwnd : null)
                ?? state.ScopeHwnd ?? _s?.ScopeHwnd ?? _s?.Rt?.ForegroundWindow()?.Hwnd ?? 0;
            if (stepTargetHwnd > 0) UpdateHighlight(stepTargetHwnd);
            EnsureReflexArmed(stepTargetHwnd);
            var action = (s.Action ?? "").ToLowerInvariant();

            // 1. Safe-point pause requested (e.g. via computer_pause_run)
            if (state.SafePointPauseRequested)
            {
                state.PausedStepIndex = i;
                state.IsPausedForHuman = true;
                state.PauseStatus = OutcomeKind.PausedForHuman.ToString();
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = DescribeStep(s);
                _s.Rt.PostHumanTakeover(state.PauseReason ?? "Güvenli noktada duraklatıldı", nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "PausedForHuman",
                    pauseError: state.PauseReason ?? "Paused at safe point for human takeover",
                    includeDelta: true, includeAvailable: true);
            }

            // 2. Explicit human takeover step action
            if (action is "human" or "human_takeover" or "pause_for_human")
            {
                state.PausedStepIndex = i;
                state.IsPausedForHuman = true;
                state.PauseStatus = OutcomeKind.PausedForHuman.ToString();
                state.PauseReason = s.Reason ?? s.Note ?? "İnsan müdahalesi gerekiyor";
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = (i + 1 < steps.Length) ? DescribeStep(steps[i + 1]) : null;
                _s.Rt.PostHumanTakeover(state.PauseReason, nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "PausedForHuman",
                    pauseError: state.PauseReason,
                    includeDelta: true, includeAvailable: true);
            }

            using var stepSpan = PerfTrace.Stage($"step.{action}");
            if (ValidateStep(s) is { } verr)
            {
                report.Add(StepEntry(i, s, "Malformed", detail: verr));
                // tolerated malformed step (batch continueOnError / failFast:false)
                if (s.ContinueOnError == true)
                {
                    report[^1]["continuedOnError"] = true;
                    continue;
                }
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Malformed", pauseError: verr);
            }
            if (linked.Token.IsCancellationRequested)
            {
                _s.Rt.PurgePendingWork();
                if (epoch.Value.IsCancellationRequested)
                    return RunReport(state, "EmergencyStopped", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: "EmergencyStopped",
                        pauseError: StoppedDetail);

                return RunReport(state, "Cancelled", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Cancelled",
                    pauseError: "execution cancelled");
            }

            // ---- reflex gate: a modal between steps aborts or parks the
            // run. Runs AFTER the cancellation check above so panic can
            // never be masked by a dialog interception.
            if (reflexEng != null && reflexArmedHwnd != 0)
            {
                var abort = reflexEng.Abort;
                if (abort == null && reflexEng.Paused)
                {
                    // a modal is parked for human handling — bounded wait
                    // on the pause gate (~30s); linked.Token keeps panic /
                    // request-cancel able to interrupt the wait.
                    using var gateCts = CancellationTokenSource
                        .CreateLinkedTokenSource(linked.Token);
                    gateCts.CancelAfter(TimeSpan.FromSeconds(30));
                    var gateWaitCancelled = false;
                    try
                    {
                        await reflexEng.WaitPausedAsync(gateCts.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { gateWaitCancelled = true; }
                    catch { /* gate faults degrade to the abort path below */ }
                    // panic/cancel during the wait outranks any modal state
                    if (_s.Control.State == ComputerControlState.EmergencyStopped
                        || epoch.Value.IsCancellationRequested)
                    {
                        _s.Rt.PurgePendingWork();
                        return RunReport(state, "EmergencyStopped", sw, report,
                            internalActions, executed, skipped,
                            pauseStep: i, pauseStatus: "EmergencyStopped",
                            pauseError: StoppedDetail);
                    }
                    if (linked.Token.IsCancellationRequested)
                        return RunReport(state, "Cancelled", sw, report,
                            internalActions, executed, skipped,
                            pauseStep: i, pauseStatus: "Cancelled",
                            pauseError: "execution cancelled");
                    abort = reflexEng.Abort;
                    if (abort == null && reflexEng.Paused && gateWaitCancelled)
                    {
                        // pause gate actually timed out unresolved → abort
                        var last = reflexEng.Log?.Count > 0
                            ? reflexEng.Log[^1] : null;
                        abort = new ReflexAbort(last?.Title,
                            last?.Disposition,
                            "modal pause gate timed out after 30s without " +
                            "resolution — treating as abort",
                            last?.ButtonsSeen);
                    }
                }
                if (abort != null)
                {
                    // EmergencyStopped outranks a modal abort — never let
                    // the engine swallow panic.
                    if (_s.Control.State == ComputerControlState.EmergencyStopped
                        || epoch.Value.IsCancellationRequested)
                    {
                        _s.Rt.PurgePendingWork();
                        return RunReport(state, "EmergencyStopped", sw, report,
                            internalActions, executed, skipped,
                            pauseStep: i, pauseStatus: "EmergencyStopped",
                            pauseError: StoppedDetail);
                    }
                    state.ReflexAborted = true;
                    state.PausedStepIndex = i;
                    state.PausedAt = DateTimeOffset.UtcNow;
                    state.PauseStatus = OutcomeKind.InterruptedByDialog.ToString();
                    state.PauseReason = abort.Reason;
                    var fgA = _s.Rt.ForegroundWindow();
                    state.PrePauseForegroundHwnd = fgA?.Hwnd;
                    state.PrePauseForegroundTitle = fgA?.Title;
                    return ReflexAbortResult(state, steps, i, abort, report,
                        internalActions, executed, skipped, sw);
                }
            }

            if (state.Bindings.Count > 0)
            {
                s = s with
                {
                    Text = SubstituteVariables(s.Text, state),
                    Value = SubstituteVariables(s.Value, state),
                };
            }

            // ---- declarative conditions → skip, never fail ----
            string? skip; string? condKind, condDetail;
            using (PerfTrace.Stage("step.condition"))
                skip = EvalConditions(s, state, out condKind, out condDetail);
            if (condKind != null)
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: condKind, pauseError: condDetail);
            if (skip != null)
            {
                skipped++;
                report.Add(StepEntry(i, s, "Skipped", detail: skip));
                continue;
            }

            // ---- checkpoint: hand control back with fresh context ----
            if (action == "checkpoint")
            {
                return RunReport(state, "Checkpoint", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Checkpoint",
                    pauseError: s.Note ?? "checkpoint reached",
                    includeDelta: true);
            }

            // ---- scan / for_each: local iteration, filtering, and collection over items ----
            if (action is "scan" or "for_each")
            {
                var scanRes = await ExecuteScanStep(s, state, octx, linked.Token);
                internalActions += scanRes.InternalActions;
                if (!scanRes.Outcome.Success)
                {
                    report.Add(StepEntry(i, s, scanRes.Outcome.Kind.ToString(), detail: scanRes.Outcome.Detail, ms: (int)scanRes.DurationMs));
                    if (s.ContinueOnError == true)
                    {
                        report[^1]["continuedOnError"] = true;
                        continue;
                    }
                    return RunReport(state, "Paused", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: scanRes.Outcome.Kind.ToString(),
                        pauseError: scanRes.Outcome.Detail,
                        includeDelta: true, includeAvailable: true,
                        pauseDiagnosis: scanRes.Outcome.Diagnosis);
                }
                executed++;
                state.TotalExecuted++;
                report.Add(StepEntry(i, s, "Ok", detail: scanRes.Outcome.Detail, ms: (int)scanRes.DurationMs, actions: scanRes.SubStepsCount > 0 ? scanRes.SubStepsCount : null));
                if (scanRes.PostId != null) { state.LastElementId = scanRes.PostId; _s.NoteElementRef(scanRes.PostId); }
                continue;
            }

            // ---- application adapters: specialist direct execution (media, testapp, CLI) ----
            if (action is "adapter" or "media" or "play" or "pause" or "play_pause" or "toggle_playback"
                or "next" or "previous" or "stop" or "volume_up" or "volume_down" or "mute" || s.Adapter != null)
            {
                var adapterStepSw = Stopwatch.StartNew();
                var proc = s.Target?.Process ?? _s?.Rt?.ForegroundWindow()?.ProcessName;
                var targetHwnd = s.Target?.Hwnd != null ? ParseHwnd(s.Target.Hwnd) : _s?.Rt?.ForegroundWindow()?.Hwnd;

                var adapterAction = action == "adapter" ? (s.Query ?? s.Text ?? "execute") : action;
                var effectiveArgs = s.Args != null ? new Dictionary<string, object?>(s.Args) : new Dictionary<string, object?>();
                if (s.Text != null && !effectiveArgs.ContainsKey("text")) effectiveArgs["text"] = s.Text;
                if (s.Value != null && !effectiveArgs.ContainsKey("value")) effectiveArgs["value"] = s.Value;

                TargetRef? stepTargetRef = null;
                if (targetHwnd.HasValue) stepTargetRef = TargetRef.Window(targetHwnd.Value);
                else if (!string.IsNullOrEmpty(s.Target?.ElementId)) stepTargetRef = TargetRef.Element(s.Target.ElementId);

                // Dangerous-action gate: an "adapter" plan step with a
                // script-exec action (evaluate/exec/eval/execute) runs
                // caller-supplied code — same class as browser_evaluate.
                // Denied steps flow into the normal adapter-failure path
                // (report Failed, honour continueOnError) and every
                // attempt is audit-logged.
                var stepIsScriptExec = ScriptExecActions.Contains(adapterAction);
                var stepScriptCode = stepIsScriptExec ? ExtractScriptArg(effectiveArgs) : null;
                bool handled; Inbrisk.Core.AdapterResult? adapterRes;
                if (stepIsScriptExec && !ScriptExecAllowed())
                {
                    AuditScriptExec("computer_run", adapterAction, s.Adapter,
                        ok: false, reason: "denied — allowScriptExecution off",
                        stepScriptCode);
                    handled = true;
                    adapterRes = new Inbrisk.Core.AdapterResult(false,
                        "SafetyPolicy.ScriptExec",
                        ScriptExecDeniedDetail($"run step '{adapterAction}'"),
                        Error: ErrorCode.ConfirmationRequired);
                }
                else
                {
                    (handled, adapterRes) = await (_s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry()).TryExecuteAsync(
                        action: adapterAction,
                        target: stepTargetRef,
                        args: effectiveArgs,
                        processName: proc,
                        hwnd: targetHwnd,
                        preferredAdapterId: s.Adapter,
                        ct: linked.Token);

                    if (stepIsScriptExec)
                        AuditScriptExec("computer_run", adapterAction, s.Adapter,
                            ok: handled && adapterRes?.Success == true,
                            reason: handled
                                ? "allowed — executed per result"
                                : "allowed — no adapter handled",
                            stepScriptCode);
                }

                if (handled && adapterRes != null)
                {
                    internalActions++;
                    if (!adapterRes.Success)
                    {
                        report.Add(StepEntry(i, s, "Failed", detail: adapterRes.Detail ?? "adapter action failed", ms: (int)adapterStepSw.ElapsedMilliseconds));
                        if (s.ContinueOnError == true)
                        {
                            report[^1]["continuedOnError"] = true;
                            continue;
                        }
                        return RunReport(state, "Paused", sw, report,
                            internalActions, executed, skipped,
                            pauseStep: i, pauseStatus: "Failed",
                            pauseError: adapterRes.Detail,
                            includeDelta: true);
                    }

                    executed++;
                    state.TotalExecuted++;
                    if (s.As != null && adapterRes.Data != null)
                    {
                        foreach (var kv in adapterRes.Data)
                            state.Bindings[$"{s.As}.{kv.Key}"] = kv.Value?.ToString() ?? "";
                    }
                    report.Add(StepEntry(i, s, "Ok", method: adapterRes.Method, detail: adapterRes.Detail, ms: (int)adapterStepSw.ElapsedMilliseconds));
                    continue;
                }
            }

            // ---- execute with bounded retry; target re-resolves per try ----
            var tries = Math.Clamp((s.Retry ?? 0) + 1, 1, 20);
            var retryWait = Math.Clamp(s.RetryInterval ?? 400, 50, 5000);
            StepOutcome? outcome = null;
            string? errKind = null, errDetail = null;
            TargetDiagnosis? stepDiag = null;
            var summaries = new List<string>();
            var retryReasons = new List<string>();
            string? postId = null;
            var stepSw = Stopwatch.StartNew();
            var isPseudo = action is "find" or "assert" or "launch";
            List<AgentAction>? built = null;

            var targetName = s.Target?.Process ?? s.Target?.Window ?? s.Target?.Name ?? (s.Target?.ElementId != null ? "element" : null);
            if (action == "launch")
            {
                var app = s.Target?.Process ?? s.Text ?? "Uygulama";
                _s.Rt.PostActivity(ActivityGranularity.FormatAction(app, "launch"), app);
            }
            else if (!string.IsNullOrEmpty(targetName))
            {
                _s.Rt.PostActivity(ActivityGranularity.FormatAction(targetName, action), targetName);
            }

            for (var t = 0; t < tries; t++)
            {
                summaries.Clear();
                if (isPseudo)
                {
                    using (PerfTrace.Stage("step.pseudo"))
                        outcome = PseudoOutcome(s, state, linked.Token, out postId)!;
                    if (outcome.Success) break;
                    var (healed, healDetail) = await TrySelfHeal(s, state, outcome.Kind.ToString(), outcome.Detail, octx, linked.Token);
                    if (healed)
                    {
                        internalActions++;
                        summaries.Add(healDetail);
                        tries++;
                        continue;
                    }
                    // only transient states retry — element absent yet, stale
                    // mid-refresh, or an assert whose postcondition hasn't
                    // settled (async UI flip). Malformed/AmbiguousTarget/
                    // PolicyDenied/EmergencyStopped break immediately.
                    var transient = outcome.Kind is OutcomeKind.TargetNotFound
                        or OutcomeKind.TargetOffscreen
                        or OutcomeKind.TargetOnDifferentScreen
                        or OutcomeKind.Stale
                        || (action == "assert" && outcome.Kind is OutcomeKind.Failed
                            or OutcomeKind.Unverified);
                    if (transient && t + 1 < tries)
                    {
                        retryReasons.Add(outcome.Kind.ToString());
                        linked.Token.WaitHandle.WaitOne(retryWait); continue;
                    }
                    break;
                }
                using (PerfTrace.Stage("step.resolve"))
                    built = BuildStepActions(s, state,
                        out errKind, out errDetail, out postId, out stepDiag);
                // silent:true on the step opts every produced action into
                // WM-message delivery — the executor's safety gates are
                // identical; unsupported kinds fail NotSupported upstream.
                if (built != null && s.Silent == true)
                    built = built.Select(a => a with { Silent = true }).ToList();
                if (built == null)
                {
                    var (healed, healDetail) = await TrySelfHeal(s, state, errKind, errDetail, octx, linked.Token);
                    if (healed)
                    {
                        internalActions++;
                        summaries.Add(healDetail);
                        tries++;
                        continue;
                    }

                    outcome = new StepOutcome(
                        Enum.TryParse<OutcomeKind>(errKind, out var okk)
                            ? okk : OutcomeKind.Malformed,
                        false, "plan", errDetail, 0,
                        Diagnosis: stepDiag);
                    if (errKind is "TargetNotFound" or "TargetOffscreen" or "TargetOnDifferentScreen" or "AmbiguousTarget" &&
                        t + 1 < tries)
                    {
                        retryReasons.Add(errKind);
                        linked.Token.WaitHandle.WaitOne(retryWait); continue;
                    }
                    break;
                }
                outcome = null;
                // snapshot the target BEFORE its first mutation so the run's
                // changes{} reflects the whole plan, not just the last step
                using (PerfTrace.Stage("verify.targeted"))
                    pre ??= postId != null ? ReadElementState(postId) : null;
                var stepCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                if (s.Timeout is > 0) stepCts.CancelAfter(s.Timeout.Value);
                var actx = new ActionContext(stepCts.Token, null, _s.SessionId, null, null);

                IInputLease? physicalLease = null;
                var requiresPhysical = built.Any(a =>
                    a.Kind is AgentActionKind.Click or AgentActionKind.RightClick or AgentActionKind.DoubleClick or AgentActionKind.Drag or AgentActionKind.Scroll or AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey or AgentActionKind.Look or AgentActionKind.MouseHold or AgentActionKind.FocusWindow or AgentActionKind.FocusElement
                    || (a.Kind is AgentActionKind.Invoke && (
                        a.ElementId == null ||
                        _s?.Rt?.Parts.Registry.Get(a.ElementId) is not { } el ||
                        !el.IsActionSupported("invoke"))));
                if (requiresPhysical)
                {
                    try
                    {
                        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
                        physicalLease = await arbiter.AcquireAsync(
                            ownerId: state.RunId,
                            kind: LeaseKind.PhysicalInput,
                            description: $"Run '{state.RunId}' step {i}: {action}",
                            timeout: TimeSpan.FromSeconds(5),
                            ct: stepCts.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException tex)
                    {
                        stepCts.Dispose();
                        errKind = "ConcurrencyConflict";
                        errDetail = tex.Message;
                        outcome = new StepOutcome(OutcomeKind.ConcurrencyConflict, false, "Arbiter", tex.Message, (int)stepSw.ElapsedMilliseconds);
                        break;
                    }
                }

                try
                {
                    foreach (var a in built)
                    {
                        // the local resume chord is human-only, even inside a plan
                        if (a.Kind is AgentActionKind.Hotkey or AgentActionKind.Key &&
                            _s.Control.IsLocalResumeKey(a.Key,
                                a.Kind == AgentActionKind.Hotkey ? a.Modifiers : null))
                        {
                            errKind = "PolicyDenied";
                            errDetail = "local resume hotkey is reserved for the human";
                            outcome = new StepOutcome(OutcomeKind.PolicyDenied,
                                false, "plan", errDetail, 0);
                            break;
                        }
                        if (BadPoint(a, out var pointErr))
                        {
                            errKind = "StaleFrame"; errDetail = pointErr;
                            outcome = new StepOutcome(OutcomeKind.StaleFrame,
                                false, "plan", pointErr, 0);
                            break;
                        }
                        summaries.Add(a.Summary());
                        using (PerfTrace.Stage("step.execute"))
                            outcome = await Task.Run(
                                () => _s.Resolver.Execute(a, octx, actx), stepCts.Token);
                        internalActions++;
                        if (a.ElementId != null) postId = a.ElementId;
                        if (!outcome.Success) break;
                        if (stepCts.IsCancellationRequested &&
                            !linked.IsCancellationRequested)
                        {
                            outcome = new StepOutcome(OutcomeKind.Timeout, false,
                                "plan", $"step exceeded timeout {s.Timeout}ms", 0);
                            break;
                        }
                    }
                }
                finally
                {
                    physicalLease?.Dispose();
                    stepCts.Dispose();
                }

                if (outcome?.Success == true || errKind is "PolicyDenied" or "StaleFrame")
                    break;
                if (RecoveryPolicy.IsFatal(outcome?.Kind ?? OutcomeKind.Failed))
                    break;
                if (t + 1 < tries)
                {
                    retryReasons.Add(outcome?.Kind.ToString() ?? "?");
                    using (PerfTrace.Stage("step.retryWait"))
                        linked.Token.WaitHandle.WaitOne(retryWait);
                }
            }

            if (isPseudo || retryReasons.Count > 0)
                UiaPerf.Write(new
                {
                    kind = "run.step",
                    runId = state.RunId,
                    step = i,
                    action,
                    status = outcome?.Kind.ToString() ?? errKind,
                    ms = stepSw.ElapsedMilliseconds,
                    retryCount = retryReasons.Count,
                    retryReasons = retryReasons.Count > 0 ? retryReasons : null,
                });
            var ok = outcome?.Success == true;
            var okDetail = summaries.Count > 0
                ? string.Join(" → ", summaries) : outcome?.Detail;
            report.Add(StepEntry(i, s, ok ? (outcome?.Kind.ToString() ?? "Ok")
                    : (outcome?.Kind.ToString() ?? errKind ?? "Failed"),
                method: outcome?.Method, ms: (int)stepSw.ElapsedMilliseconds,
                detail: ok ? okDetail : outcome?.Detail ?? errDetail,
                actions: summaries.Count > 1 ? summaries.Count : null));
            if (outcome?.Delta is { } stepDelta && !stepDelta.IsEmpty)
                report[^1]["delta"] = DeltaPayload(stepDelta);
            if (s.Observe == true && ok)
            {
                try
                {
                    var scopeHwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
                    var obs = _s.Observe(scopeHwnd, ObservationBudget.Default, VisualAttachPolicy.Never);
                    var oObs = obs.Observation;
                    report[^1]["observation"] = new Dictionary<string, object?>
                    {
                        ["observationId"] = oObs.ObservationId,
                        ["activeWindow"] = oObs.ActiveWindow != null ? new
                        {
                            hwnd = $"0x{oObs.ActiveWindow.Hwnd:X}",
                            title = oObs.ActiveWindow.Title,
                            process = oObs.ActiveWindow.Process,
                        } : null,
                        ["elements"] = oObs.Elements.Take(30).Select(e => new
                        {
                            id = e.Id,
                            role = e.Role,
                            name = e.Name,
                            value = e.Value,
                            state = e.State,
                            actions = e.Actions,
                        }).ToList(),
                    };
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"computer_run post-step observation build failed: {ex.Message}");
                }
            }
            if (postId != null) { state.LastElementId = postId; _s.NoteElementRef(postId); }
            if (!ok)
            {
                state.PausedStepIndex = i;
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = DescribeStep(steps[i]);

                // a step cancelled by panic must not read as a resumable
                // pause — the control state is EmergencyStopped
                if (_s.Control.State == ComputerControlState.EmergencyStopped)
                    return RunReport(state, "EmergencyStopped", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: "EmergencyStopped",
                        pauseError: StoppedDetail);

                // tolerated failure (batch failFast:false or per-step
                // continueOnError) — the report entry already records the
                // failing status; keep executing instead of pausing and
                // signalling human takeover
                if (s.ContinueOnError == true)
                {
                    report[^1]["continuedOnError"] = true;
                    continue;
                }

                _s.Rt.PostHumanTakeover($"Adım durdu: {outcome?.Kind.ToString() ?? errKind ?? "Hata"}", nextStepDesc);
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: outcome?.Kind.ToString() ?? errKind ?? "Failed",
                    pauseError: outcome?.Detail ?? errDetail,
                    includeDelta: true, includeAvailable: true,
                    pauseDiagnosis: outcome?.Diagnosis ?? stepDiag);
            }
            executed++;
            state.TotalExecuted++;
            var fgNow = _s.Rt.ForegroundWindow();
            if (fgNow != null)
            {
                var app = fgNow.ProcessName ?? "";
                var scrBefore = _s.Memory.GetCurrentScreen(app);
                var scrAfter = DesktopStateMemory.InferScreenName(app, fgNow.Title, []);
                if (!string.Equals(scrBefore, scrAfter, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(scrBefore) && !string.IsNullOrEmpty(scrAfter))
                {
                    var actionSummary = $"{s.Action} '{(s.Target?.Name ?? s.Text ?? s.Key ?? s.Query ?? s.ElementId)}'";
                    _s.Memory.RecordTransition(app, scrBefore, scrAfter, actionSummary, s.Target?.Name, s.Target?.Role);
                }
            }
            // an unexpected modal must interrupt the plan — the next steps
            // were written for a UI that may no longer be on screen
            (long hwnd, string title, bool dialogLikely, List<string> elements)? newWin = null;
            using (PerfTrace.Stage("step.modalCheck"))
                if (action is not ("focus" or "focus_window"))
                    newWin = DetectNewWindow(fgBefore,
                        built?.LastOrDefault(b => b.Kind == AgentActionKind.FocusWindow)?.Hwnd,
                        eventGenBefore,
                        StepTargetPids(built, state.ScopeHwnd ?? fgBefore));
            if (newWin is { dialogLikely: true } nw0)
            {
                // Reflex grace window: when the engine is armed it may have
                // already intercepted this very modal and be mid-dismiss
                // (BM_CLICK → the window is dying but still enumerable).
                // Give it a short bounded window before pausing the plan:
                //   window dies     → reflex dismissed it → keep running
                //   abort latched   → structured InterruptedByDialog abort
                //   still pending   → legacy UnexpectedModalOpened pause
                if (reflexEng != null && reflexArmedHwnd != 0)
                {
                    var grace = Stopwatch.StartNew();
                    while (grace.ElapsedMilliseconds < 1500)
                    {
                        if (reflexEng.Abort is { } graceAbort)
                        {
                            state.ReflexAborted = true;
                            state.PausedStepIndex = i;
                            state.PausedAt = DateTimeOffset.UtcNow;
                            state.PauseStatus =
                                OutcomeKind.InterruptedByDialog.ToString();
                            state.PauseReason = graceAbort.Reason;
                            return ReflexAbortResult(state, steps, i, graceAbort,
                                report, internalActions, executed, skipped, sw);
                        }
                        if (_s.Rt.Window(nw0.hwnd) == null)
                        {
                            newWin = null; // reflex dismissed it — no pause
                            break;
                        }
                        if (!reflexEng.Paused) break; // engine drained, window persists
                        await Task.Delay(50, linked.Token).ConfigureAwait(false);
                    }
                }
            }
            if (newWin is { dialogLikely: true } nw)
            {
                state.PausedStepIndex = i;
                state.PausedAt = DateTimeOffset.UtcNow;
                state.PrePauseForegroundHwnd = nw.hwnd;
                state.PrePauseForegroundTitle = nw.title;
                var nextStepDesc = (i + 1 < steps.Length) ? DescribeStep(steps[i + 1]) : null;
                _s.Rt.PostHumanTakeover("Beklenmeyen pencere açıldı", nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "UnexpectedModalOpened",
                    pauseError: $"new window 0x{nw.hwnd:X} \"{nw.title}\" " +
                        "appeared — inspect its elements before resuming",
                    includeDelta: true, includeAvailable: true);
            }
            fgBefore = _s.Rt.ForegroundWindow()?.Hwnd;
            eventGenBefore = _s.Rt.EventBuffer.CurrentGeneration;
        }

        // a modal surfaced by the final step still aborts the run — the
        // plan must not report Completed over an open dialog. Emergency
        // check first: panic outranks a reflex abort.
        if (_s.Control.State == ComputerControlState.EmergencyStopped)
            return RunReport(state, "EmergencyStopped", sw, report,
                internalActions, executed, skipped,
                pauseStep: steps.Length, pauseStatus: "EmergencyStopped",
                pauseError: StoppedDetail);
        if (reflexEng != null && reflexArmedHwnd != 0 &&
            reflexEng.Abort is { } postAbort)
        {
            state.ReflexAborted = true;
            state.PausedStepIndex = steps.Length;
            state.PausedAt = DateTimeOffset.UtcNow;
            state.PauseStatus = OutcomeKind.InterruptedByDialog.ToString();
            state.PauseReason = postAbort.Reason;
            return ReflexAbortResult(state, steps, steps.Length, postAbort,
                report, internalActions, executed, skipped, sw);
        }

        UiElement? post;
        using (PerfTrace.Stage("verify.targeted"))
            post = state.LastElementId != null
                ? ReadElementState(state.LastElementId) : null;
        _s.Rt.PostSuccess("✓ Tamamlandı");
        return RunReport(state, "Completed", sw, report,
            internalActions, executed, skipped, pre: pre, post: post,
            includeDelta: true);
        }
        finally
        {
            try { if (reflexArmedHwnd != 0) reflexEng?.Disarm(); }
            catch (Exception ex) { Debug.WriteLine($"reflex disarm hwnd=0x{reflexArmedHwnd:X} failed: {ex.Message}"); }
            planHighlightLease?.Dispose();
        }
    }

    // ------------------------------------------------------------ plan steps

    private static string DescribeStep(RunStep? s)
    {
        if (s == null) return "Bilinmeyen adım";
        var act = (s.Action ?? "step").ToLowerInvariant();
        var targetName = s.Target?.Name ?? s.Target?.Role ?? s.ElementId ?? s.Query;
        if (act is "click" or "rightclick" or "doubleclick" or "invoke" or "toggle" or "select")
            return $"{act} \"{targetName ?? "öğe"}\"";
        if (act is "type" or "set_value" or "setvalue")
            return $"{act} \"{s.Text ?? s.Value ?? ""}\"";
        if (act is "key" or "hotkey")
            return $"{act} {s.Keys ?? s.Key ?? ""}";
        if (act is "find")
            return $"bul \"{targetName ?? s.As ?? "öğe"}\"";
        if (act is "wait" or "wait_for" or "wait_for_change" or "wait_for_stable")
            return $"bekle {s.Query ?? (s.Ms != null ? $"{s.Ms}ms" : "")}".TrimEnd();
        if (act is "adapter" or "media" or "play" or "pause")
            return $"medya: {act}";
        if (act is "launch")
            return $"başlat \"{s.App ?? s.Executable ?? s.Path ?? s.Uri ?? ""}\"";
        if (!string.IsNullOrEmpty(s.Reason ?? s.Note))
            return $"{act}: {s.Reason ?? s.Note}";
        return act;
    }

    /// <summary>Strict per-action validation — an unknown field, a field
    /// meaningless for the step's action, or a step with no effective
    /// operation is Malformed. A misspelled field must never produce a
    /// Verified-looking step.</summary>
    public static string? ValidateStep(RunStep s)
    {
        if (s.Extra is { Count: > 0 } extra)
            return $"unknown step field(s): {string.Join(", ", extra.Keys)} — " +
                "see computer_capabilities for the step schema";
        var a = (s.Action ?? "").ToLowerInvariant();
        if (a.Length == 0) return "step is missing 'action'";

        var set = new List<string>();
        if (s.Target != null) set.Add("target");
        if (s.ElementId != null) set.Add("elementId");
        if (s.As != null) set.Add("as");
        if (s.Text != null) set.Add("text");
        if (s.Value != null) set.Add("value");
        if (s.Mode != null) set.Add("mode");
        if (s.Position != null) set.Add("position");
        if (s.Submit) set.Add("submit");
        if (s.Key != null) set.Add("key");
        if (s.Keys != null) set.Add("keys");
        if (s.Modifiers != null) set.Add("modifiers");
        if (s.Count != null) set.Add("count");
        if (s.Delta != null) set.Add("delta");
        if (s.Ms != null) set.Add("ms");
        if (s.Query != null) set.Add("query");
        if (s.Hwnd != null) set.Add("hwnd");
        if (s.X != null) set.Add("x");
        if (s.Y != null) set.Add("y");
        if (s.FrameId != null) set.Add("frameId");
        if (s.ObservationId != null) set.Add("observationId");
        if (s.ToX != null) set.Add("toX");
        if (s.ToY != null) set.Add("toY");
        if (s.ToFrameId != null) set.Add("toFrameId");
        if (s.ToObservationId != null) set.Add("toObservationId");
        if (s.Note != null) set.Add("note");
        if (s.Contains != null) set.Add("contains");
        if (s.NotContains != null) set.Add("notContains");
        if (s.Exact != null) set.Add("exact");
        if (s.State != null) set.Add("state");
        if (s.Enabled != null) set.Add("enabled");
        if (s.App != null) set.Add("app");
        if (s.Search != null) set.Add("search");
        if (s.Executable != null) set.Add("executable");
        if (s.Path != null) set.Add("path");
        if (s.Aumid != null) set.Add("aumid");
        if (s.Uri != null) set.Add("uri");
        if (s.Arguments is { Length: > 0 }) set.Add("arguments");
        if (s.NewInstance == true) set.Add("newInstance");
        if (s.WaitFor != null) set.Add("waitFor");
        if (s.DebugPort != null) set.Add("debugPort");
        if (s.Where != null) set.Add("where");
        if (s.Steps is { Length: > 0 }) set.Add("steps");
        if (s.MaxItems != null) set.Add("maxItems");
        if (s.MaxPages != null) set.Add("maxPages");
        if (s.StopOn is { Length: > 0 }) set.Add("stopOn");
        if (s.Collect is { Length: > 0 }) set.Add("collect");
        if (s.ExpectedState != null) set.Add("expectedState");
        if (s.ExpectedValue != null) set.Add("expectedValue");
        if (s.Gone) set.Add("gone");
        if (s.MinCount != null) set.Add("minCount");
        if (s.StopOnDialog != null) set.Add("stopOnDialog");
        if (s.Adapter != null) set.Add("adapter");
        if (s.Args != null) set.Add("args");
        if (s.Reason != null) set.Add("reason");
        if (s.Dx != null) set.Add("dx");
        if (s.Dy != null) set.Add("dy");
        if (s.Button != null) set.Add("button");

        string[]? allowed = a switch
        {
            "find" => ["target", "elementId", "as", "select", "index", "orderby"],
            "assert" => ["elementId", "target", "contains", "notContains",
                "exact", "state", "enabled", "value", "note",
                "select", "index", "orderby"],
            "checkpoint" => ["note", "reason"],
            "human" or "human_takeover" or "pause_for_human" => ["note", "reason"],
            "adapter" or "media" or "play" or "pause" or "play_pause" or "toggle_playback"
                or "next" or "previous" or "stop" or "volume_up" or "volume_down" or "mute" =>
                ["target", "adapter", "args", "query", "text", "value", "as", "elementId", "select", "index", "orderby", "timeout", "note", "retry", "retryInterval"],
            "scan" or "for_each" => ["target", "elementId", "as", "where",
                "steps", "maxItems", "maxPages", "stopOn", "collect", "timeout", "note", "select", "index", "orderby"],
            "focus" or "focus_window" => ["hwnd", "elementId", "target",
                "select", "index", "orderby"],
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" or "invoke" or "toggle" or "select"
                or "hover" => ["elementId", "target", "x", "y",
                    "frameId", "observationId", "select", "index", "orderby"],
            "set_value" or "setvalue" => ["elementId", "target", "text", "value",
                "select", "index", "orderby"],
            "type" => ["elementId", "target", "text", "value", "mode",
                "position", "submit", "select", "index", "orderby"],
            "key" => ["elementId", "target", "key", "keys", "count", "ms",
                "select", "index", "orderby"],
            "look" => ["elementId", "target", "dx", "dy",
                "select", "index", "orderby"],
            "mouse" => ["elementId", "target", "button", "keys", "ms",
                "select", "index", "orderby"],
            "hotkey" => ["elementId", "target", "key", "keys", "modifiers",
                "select", "index", "orderby"],
            "scroll" => ["elementId", "target", "delta", "x", "y",
                "frameId", "observationId", "select", "index", "orderby"],
            "scroll_into_view" => ["elementId", "target", "select", "index", "orderby"],
            "drag" => ["elementId", "target", "x", "y", "frameId",
                "observationId", "toX", "toY", "toFrameId", "toObservationId",
                "select", "index", "orderby"],
            "wait" => ["ms"],
            "wait_for" => ["query", "ms", "target", "elementId", "expectedState", "expectedValue", "gone", "minCount", "stopOnDialog"],
            "wait_for_gone" => ["query", "ms", "target", "elementId", "stopOnDialog"],
            "wait_for_change" or "wait_for_stable" => ["ms"],
            "launch" => ["target", "text", "value", "as", "ms", "app",
                "search", "executable", "path", "aumid", "uri", "arguments",
                "newInstance", "waitFor", "debugPort", "adapter"],
            _ => null,
        };
        if (allowed == null)
            return $"unknown plan action '{s.Action}' — supported: scan, for_each, find, " +
                "assert, checkpoint, human, human_takeover, pause_for_human, adapter, media, play, pause, next, previous, volume_up, volume_down, mute, " +
                "focus, focus_window, click, rightclick, doubleclick, invoke, toggle, select, hover, set_value, type, " +
                "key, hotkey, look, mouse, scroll, scroll_into_view, drag, wait, wait_for, wait_for_gone, wait_for_change, " +
                "wait_for_stable, launch";
        var bad = set.Where(f => !allowed.Contains(f,
            StringComparer.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0)
            return $"field(s) [{string.Join(", ", bad)}] have no effect on " +
                $"action '{a}' — see computer_capabilities for valid fields";

        if (a is "scan" or "for_each")
        {
            if (s.Steps is not { Length: > 0 } && s.Collect is not { Length: > 0 })
                return $"{a} step requires at least 'steps' to execute or 'collect' to extract properties";
            if (s.Steps is { Length: > 0 } subSteps)
            {
                for (var j = 0; j < subSteps.Length; j++)
                {
                    if (ValidateStep(subSteps[j]) is { } subErr)
                        return $"sub-step {a}[{j}]: {subErr}";
                }
            }
        }

        var hasTarget = s.ElementId != null || s.Target != null;
        var hasPoint = (s.X != null && s.Y != null) || (s.Target?.X != null && s.Target?.Y != null);
        if (s.Select?.ToLowerInvariant() is { } sel &&
            sel is not ("first" or "last" or "nth"))
            return "select must be first|last|nth";
        if (s.OrderBy?.ToLowerInvariant() is { } ord &&
            ord is not ("visual" or "tree" or "score"))
            return "orderBy must be visual|tree|score";
        if (s.Index != null && s.Select?.ToLowerInvariant() != "nth")
            return "index only applies with select:\"nth\"";
        return a switch
        {
            "find" when !hasTarget => "find requires target or elementId",
            "assert" when !hasTarget => "assert requires elementId or target",
            "focus" when !hasTarget && s.Hwnd == null =>
                "focus requires elementId, target, or hwnd",
            "focus_window" when s.Hwnd == null && s.Target == null =>
                "focus_window requires hwnd or a window/process target",
            "invoke" or "toggle" or "select" or "hover" or "scroll_into_view" when !hasTarget =>
                $"{a} requires elementId or target",
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" when !hasTarget && !hasPoint =>
                $"{a} requires elementId, target, or x+y",
            "set_value" or "setvalue" when !hasTarget =>
                "set_value requires elementId or target",
            "set_value" or "setvalue" when s.Text == null && s.Value == null =>
                "set_value requires text or value",
            "type" when s.Text == null && s.Value == null =>
                "type requires text",
            "key" when s.Key == null && s.Keys == null => "key requires a key name",
            "key" when s.Key != null && s.Keys != null => "use either key or keys, not both",
            "key" when s.Keys != null && s.Ms is null or <= 0 =>
                "key with keys (e.g. \"w+space\" held together) requires ms",
            "key" when s.Ms is < 0 or > 10_000 => "key hold ms must be 0..10000",
            "mouse" when s.Ms is null or <= 0 or > 10_000 =>
                "mouse requires ms 1..10000 (how long to hold the button)",
            "mouse" when s.Button != null &&
                s.Button.ToLowerInvariant() is not ("left" or "right" or "middle") =>
                "mouse button must be left|right|middle",
            "look" when s.Dx == null && s.Dy == null => "look requires dx and/or dy",
            "look" when Math.Abs(s.Dx ?? 0) > 4000 || Math.Abs(s.Dy ?? 0) > 4000 =>
                "look dx/dy must be within ±4000",
            "hotkey" when s.Key == null && s.Keys == null =>
                "hotkey requires key+modifiers, or keys shorthand like \"ctrl+s\"",
            "scroll" when s.Delta == null => "scroll requires delta",
            "drag" when s.ToX == null || s.ToY == null =>
                "drag requires a toX+toY destination",
            "wait" when s.Ms == null => "wait requires ms",
            "wait_for" when s.Query == null && s.Target == null && s.ElementId == null && s.ExpectedState == null && s.ExpectedValue == null && !s.Gone =>
                "wait_for requires query, target, or condition",
            "wait_for_gone" when s.Query == null && s.Target == null && s.ElementId == null =>
                "wait_for_gone requires query, target, or elementId",
            "launch" when s.App == null && s.Search == null &&
                s.Executable == null &&
                s.Path == null && s.Aumid == null && s.Uri == null &&
                s.Target?.Process == null && s.Text == null &&
                s.Value == null =>
                "launch requires app (\"Spotify\") — or executable/path/" +
                "aumid/uri; text/target.process accepted as the app name",
            _ => null,
        };
    }

    /// <summary>$name → bound elementId; anything else passes through.</summary>
    private static string? BoundId(string? id, RunState state)
        => id != null && id.StartsWith('$')
            ? state.Bindings.GetValueOrDefault(id[1..])
            : id;

    /// <summary>Substitutes $var, {{var}}, $var.name, $var.value, etc. using run bindings.</summary>
    private static string? SubstituteVariables(string? input, RunState state)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (state.Bindings.Count == 0) return input;

        if (input.StartsWith('$') && !input.Contains(' ') && state.Bindings.TryGetValue(input[1..], out var direct))
            return direct;

        var result = input;
        foreach (var (k, v) in state.Bindings.OrderByDescending(kv => kv.Key.Length))
        {
            if (result.Contains("{{" + k + "}}", StringComparison.OrdinalIgnoreCase))
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result, @"\{\{" + System.Text.RegularExpressions.Regex.Escape(k) + @"\}\}", v ?? "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            if (result.Contains("$" + k, StringComparison.OrdinalIgnoreCase))
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result, @"\$" + System.Text.RegularExpressions.Regex.Escape(k) + @"(?![a-zA-Z0-9_\.])", v ?? "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }
        return result;
    }

    private UiElement? ResolveRunTarget(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out int matchCount,
        out TargetDiagnosis? diagnosis)
    {
        errKind = null; errDetail = null; matchCount = 0; diagnosis = null;
        // an unbound $ref must fail loudly — never degrade to "untargeted"
        // (a click with no target) or silently drop a within constraint
        string? Unbound(string? raw) =>
            raw?.StartsWith('$') == true && BoundId(raw, state) == null ? raw : null;
        if (Unbound(s.ElementId) is { } ub)
        { errKind = "Malformed"; errDetail = $"unbound $ref '{ub}'"; return null; }
        var id = BoundId(s.ElementId, state);
        var target = s.Target;
        if (target != null)
        {
            if (Unbound(target.ElementId) is { } ube)
            { errKind = "Malformed"; errDetail = $"unbound $ref '{ube}'"; return null; }
            if (Unbound(target.Within) is { } ubw)
            { errKind = "Malformed"; errDetail = $"unbound $ref '{ubw}' in within"; return null; }
            target = target with
            {
                Name = SubstituteVariables(target.Name, state),
                NameContains = SubstituteVariables(target.NameContains, state),
                ElementId = BoundId(target.ElementId, state),
                Within = BoundId(target.Within, state),
                Value = SubstituteVariables(target.Value, state),
                ValueContains = SubstituteVariables(target.ValueContains, state),
            };
        }
        var purpose = (s.Action ?? "").ToLowerInvariant() switch
        {
            "set_value" or "setvalue" or "type" => "edit",
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" or "invoke" or "hover" => "invoke",
            "toggle" => "toggle",
            "select" => "select",
            _ => (string?)null,
        };
        var sel = s.Select != null
            ? new Selection(s.Select.ToLowerInvariant(), s.Index,
                s.OrderBy?.ToLowerInvariant() ?? "score")
            : null;
        var el = ResolveTargetElement(id, target, out var err, out matchCount,
            purpose, sel: sel);
        if (err != null)
        {
            (errKind, errDetail, diagnosis) = ErrPartsWithDiag(err);
            return null;
        }
        if (el == null && id == null && target == null)
            return null; // untargeted step (key/wait/hotkey) is legal
        return el;
    }

    private UiElement? ResolveRunTarget(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out int matchCount)
        => ResolveRunTarget(s, state, out errKind, out errDetail, out matchCount, out _);

    /// <summary>Plan-level steps that produce no executor action: find binds
    /// a name, assert validates state. Returns null when the step is a real
    /// action; otherwise a synthetic outcome.</summary>
    private StepOutcome? PseudoOutcome(RunStep s, RunState state,
        CancellationToken ct, out string? postId)
    {
        postId = null;
        var action = (s.Action ?? "").ToLowerInvariant();
        if (action is not ("find" or "assert" or "launch")) return null;
        if (action == "launch") return LaunchOutcome(s, state, ct);

        UiElement? el; string? ek, ed; int mc; TargetDiagnosis? diag;
        using (PerfTrace.Stage("step.resolve"))
            el = ResolveRunTarget(s, state, out ek, out ed, out mc, out diag);
        if (el == null)
            return new StepOutcome(
                Enum.TryParse<OutcomeKind>(ek, out var ekk)
                    ? ekk : OutcomeKind.Malformed,
                false, "plan", ed ?? $"no element for {action}", 0, Diagnosis: diag);
        postId = el.Id;
        if (action == "find")
        {
            if (!string.IsNullOrWhiteSpace(s.As))
                state.Bindings[s.As.TrimStart('$')] = el.Id;
            return new StepOutcome(OutcomeKind.Verified, true, "UIA.find",
                $"{el.Id} {el.Role} \"{Trunc(el.Name, 60)}\"" +
                (s.As != null ? $" → ${s.As.TrimStart('$')}" : "") +
                (mc > 0 ? $" ({mc} match{(mc > 1 ? "es" : "")})" : ""), 0);
        }
        // assert
        UiElement? fresh;
        using (PerfTrace.Stage("verify.targeted"))
            fresh = ReadElementState(el.Id) ?? el;
        var failures = new List<string>();
        if (s.Contains == null && s.NotContains == null && s.Exact == null &&
            s.State == null && s.Enabled == null && s.Value == null)
            return new StepOutcome(OutcomeKind.Malformed, false, "assert",
                "assert needs contains|notContains|exact|state|enabled|value", 0);
        if (s.Contains != null &&
            !(Prop(fresh, "value")?.ToString()?.Contains(s.Contains,
                StringComparison.OrdinalIgnoreCase) == true) &&
            !(fresh.Name?.Contains(s.Contains,
                StringComparison.OrdinalIgnoreCase) == true))
            failures.Add($"neither value nor name contains \"{s.Contains}\"");
        if (s.NotContains != null &&
            (Prop(fresh, "value")?.ToString()?.Contains(s.NotContains,
                StringComparison.OrdinalIgnoreCase) == true ||
             fresh.Name?.Contains(s.NotContains,
                StringComparison.OrdinalIgnoreCase) == true))
            failures.Add($"value/name unexpectedly contains \"{s.NotContains}\"");
        if (s.Exact != null &&
            Prop(fresh, "value")?.ToString() != s.Exact && fresh.Name != s.Exact)
            failures.Add($"value/name != \"{s.Exact}\"");
        if (s.State != null &&
            Prop(fresh, "state")?.ToString()?.Contains(s.State,
                StringComparison.OrdinalIgnoreCase) != true)
            failures.Add($"state is \"{Prop(fresh, "state")}\", expected \"{s.State}\"");
        if (s.Enabled is { } want &&
            (Prop(fresh, "enabled") as bool?) != want)
            failures.Add($"enabled={Prop(fresh, "enabled")}, expected {want}");
        if (s.Value != null &&
            Prop(fresh, "value")?.ToString() != s.Value)
            failures.Add($"value=\"{Trunc(Prop(fresh, "value")?.ToString(), 60)}\", expected \"{Trunc(s.Value, 60)}\"");
        return failures.Count == 0
            ? new StepOutcome(OutcomeKind.Verified, true, "assert",
                $"assertions held on {el.Id}", 0)
            : new StepOutcome(OutcomeKind.Failed, false, "assert",
                string.Join("; ", failures), 0);
    }

    /// <summary>Declarative per-step conditions. Returns a skip reason, or
    /// null to execute. Condition evaluation errors (unknown role etc.)
    /// surface via out params and pause the run — never silently skip.</summary>
    private string? EvalConditions(RunStep s, RunState state,
        out string? errKind, out string? errDetail)
    {
        errKind = null; errDetail = null;
        string? Eval(TargetSpec? spec, string? elId, out UiElement? found,
            bool existenceOnly = false)
        {
            found = null;
            // an unbound $ref in a condition means "the element does not
            // exist" → skip semantics, never a condition error or pause
            if (elId?.StartsWith('$') == true && BoundId(elId, state) == null &&
                spec == null)
                return null;
            if (spec?.ElementId?.StartsWith('$') == true &&
                BoundId(spec.ElementId, state) == null)
                return null;
            if (spec?.Within?.StartsWith('$') == true &&
                BoundId(spec.Within, state) == null)
                return null;
            var id = BoundId(elId ?? spec?.ElementId, state);
            if (spec == null && id == null) return "no target";
            var t = spec == null ? null : spec with
            {
                ElementId = BoundId(spec.ElementId, state),
                Within = BoundId(spec.Within, state),
            };
            // existence-only fast path is safe iff every filter is pushed
            // into the native UIA condition — post-filters (negations,
            // relations, window TITLE) still need full enumeration, and
            // prop-reading conditions (ifEnabled/ifValue) need the same
            // deterministic pick as before → caller opts in per condition
            var firstOnly = existenceOnly && t != null && PureNativeSpec(t);
            var el = ResolveTargetElement(id, t, out var err, out _,
                anyMatch: true, firstOnly: firstOnly);
            if (err != null)
            {
                var (k, d) = ErrParts(err);
                return k == "TargetNotFound" ? null : d ?? "condition error";
            }
            found = el;
            return null;
        }
        if (s.IfExists != null || s.IfExistsId != null)
        {
            var e = Eval(s.IfExists, s.IfExistsId, out var el,
                existenceOnly: true);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifExists target absent";
        }
        if (s.IfNotExists != null || s.IfNotExistsId != null)
        {
            var e = Eval(s.IfNotExists, s.IfNotExistsId, out var el,
                existenceOnly: true);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el != null) return $"skipped: ifNotExists matched {el.Id}";
        }
        if (s.IfEnabled != null || s.IfEnabledId != null)
        {
            var e = Eval(s.IfEnabled, s.IfEnabledId, out var el);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifEnabled target absent";
            if (Prop(el, "enabled") is not true)
                return $"skipped: {el.Id} not enabled";
        }
        if (s.IfValue != null)
        {
            var c = s.IfValue;
            var e = Eval(c.Target, c.ElementId, out var el);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifValue target absent";
            var val = Prop(el, "value")?.ToString() ?? "";
            var name = el.Name ?? "";
            if (c.Contains != null &&
                !val.Contains(c.Contains, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(c.Contains, StringComparison.OrdinalIgnoreCase))
                return $"skipped: {el.Id} value lacks \"{c.Contains}\"";
            if (c.Exact != null && val != c.Exact && name != c.Exact)
                return $"skipped: {el.Id} value != \"{c.Exact}\"";
        }
        return null;
    }

    /// <summary>launch: resolve + start an app through IAppService
    /// (deterministic Windows registration pipeline — never a shell) and
    /// bind its top-level hwnd for later within:/wait_for scoping.
    /// Idempotent by default: a running instance is reused unless
    /// newInstance.</summary>
    private StepOutcome LaunchOutcome(RunStep s, RunState state,
        CancellationToken ct)
    {
        var spec = StepLaunchSpec(s, out var err, out var bridgeInfo);
        if (err != null)
            return new StepOutcome(OutcomeKind.Malformed, false, "launch",
                err, 0);
        LaunchResult r;
        try
        {
            using (PerfTrace.Stage("step.launch"))
                r = _s.Rt.Launch(spec!, ct);
        }
        catch (OperationCanceledException)
        {
            return new StepOutcome(OutcomeKind.Cancelled, false, "launch",
                "cancelled", 0);
        }
        var via = r.Method is { } m ? $"launch.{m}" : "launch";
        if (!r.Success)
        {
            var kind = r.Error switch
            {
                "AmbiguousApplication" => OutcomeKind.AmbiguousApplication,
                "PolicyDenied" => OutcomeKind.PolicyDenied,
                "EmergencyStopped" => OutcomeKind.EmergencyStopped,
                "Timeout" => OutcomeKind.Timeout,
                "Cancelled" => OutcomeKind.Cancelled,
                "TargetNotFound" => OutcomeKind.TargetNotFound,
                "Malformed" => OutcomeKind.Malformed,
                _ => OutcomeKind.Failed,
            };
            var cand = r.Candidates is { Count: > 0 } cs
                ? $" — candidates: {string.Join(", ",
                    cs.Select(c => $"{c.Name} ({c.Method})"))}"
                : "";
            return new StepOutcome(kind, false, via,
                (r.ErrorDetail ?? r.Error ?? "launch failed") + cand, 0);
        }
        if (!string.IsNullOrWhiteSpace(s.As) && r.Hwnd is { } h)
            state.Bindings[s.As.TrimStart('$')] = h.ToString();
        return new StepOutcome(OutcomeKind.Verified, true, via,
            $"{r.ResolvedName} {r.LaunchState} pid={r.Pid}" +
            (r.Hwnd is { } hh
                ? $" hwnd=0x{hh:X} \"{Trunc(r.WindowTitle, 40)}\""
                : "") +
            (s.As != null && r.Hwnd != null
                ? $" → ${s.As.TrimStart('$')}" : "") +
            (bridgeInfo != null && r.LaunchState != "AlreadyRunning"
                ? $" adapterBridge={bridgeInfo["adapter"]}@127.0.0.1:{bridgeInfo["port"]}"
                : ""), 0);
    }

    private sealed record ScanResult(
        StepOutcome Outcome,
        long DurationMs,
        int SubStepsCount,
        int InternalActions,
        string? PostId);

    private async Task<ScanResult> ExecuteScanStep(
        RunStep s, RunState state, ObsContext octx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var asVar = !string.IsNullOrWhiteSpace(s.As) ? s.As.TrimStart('$') : "item";
        var maxItems = Math.Clamp(s.MaxItems ?? 20, 1, 200);
        var maxPages = Math.Clamp(s.MaxPages ?? 0, 0, 20);
        var stopOn = s.StopOn != null ? new HashSet<string>(s.StopOn, StringComparer.OrdinalIgnoreCase) : null;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var processedCount = 0;
        var subStepsExecuted = 0;
        var internalActions = 0;
        string? lastPostId = null;

        // 1. Resolve container or target root
        UiElement? container = null;
        if (s.Target != null || s.ElementId != null)
        {
            container = ResolveRunTarget(s, state, out var errKind, out var errDetail, out _, out var scanDiag);
            if (container == null)
            {
                return new ScanResult(
                    new StepOutcome(Enum.TryParse<OutcomeKind>(errKind, out var ok) ? ok : OutcomeKind.TargetNotFound,
                        false, "scan", errDetail ?? "scan target container not found", (int)sw.ElapsedMilliseconds, Diagnosis: scanDiag),
                    sw.ElapsedMilliseconds, 0, 0, null);
            }
        }

        var scopeHwnd = container?.Hwnd ?? state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;

        for (var page = 0; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            // Fetch candidate elements under container or window
            IReadOnlyList<UiElement> candidates;
            Inbrisk.Core.Role? roleFilter = null;
            if (!string.IsNullOrWhiteSpace(s.Where?.Role) && Enum.TryParse<Inbrisk.Core.Role>(s.Where.Role, true, out var r))
                roleFilter = r;
            else if (container?.Role == Inbrisk.Core.Role.List) roleFilter = Inbrisk.Core.Role.ListItem;
            else if (container?.Role == Inbrisk.Core.Role.Table) roleFilter = Inbrisk.Core.Role.DataItem;
            else if (container?.Role == Inbrisk.Core.Role.Tree) roleFilter = Inbrisk.Core.Role.TreeItem;

            if (container != null)
            {
                var spec = new FindSpec(
                    Hwnd: scopeHwnd,
                    ScopeElementId: container.Id,
                    Role: roleFilter,
                    IncludeOffscreen: true,
                    MaxResults: Math.Max(50, maxItems * 2));
                candidates = _s.Rt.Find(spec);
                if (candidates.Count == 0 && (container.Role is Inbrisk.Core.Role.ListItem or Inbrisk.Core.Role.DataItem or Inbrisk.Core.Role.Button or Inbrisk.Core.Role.Edit))
                {
                    candidates = [container];
                }
            }
            else
            {
                var spec = new FindSpec(
                    Hwnd: scopeHwnd,
                    Role: roleFilter,
                    IncludeOffscreen: true,
                    MaxResults: Math.Max(50, maxItems * 2));
                candidates = _s.Rt.Find(spec);
            }

            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var key = ObservationBuilder.StableKey(item, item.Handle?.Recipe?.AutomationId);
                if (!seenKeys.Add(key)) continue;

                // Match filter
                if (!MatchesItemFilter(item, s.Where)) continue;

                lastPostId = item.Id;

                // Collect properties
                if (s.Collect is { Length: > 0 })
                {
                    var rec = new Dictionary<string, object?>();
                    foreach (var col in s.Collect)
                    {
                        var c = col.ToLowerInvariant();
                        if (c == "id") rec["id"] = item.Id;
                        else if (c == "name") rec["name"] = item.Name;
                        else if (c == "role") rec["role"] = item.Role.ToString();
                        else if (c == "value") rec["value"] = Prop(item, "value");
                        else if (item.Props.TryGetValue(col, out var pv)) rec[col] = pv;
                    }
                    state.Collected.Add(rec);
                }

                // Execute sub-steps if provided
                if (s.Steps is { Length: > 0 } subSteps)
                {
                    state.Bindings[asVar] = item.Id;
                    state.Bindings[asVar + ".id"] = item.Id;
                    state.Bindings[asVar + ".name"] = item.Name ?? "";
                    state.Bindings[asVar + ".role"] = item.Role.ToString();
                    if (Prop(item, "value") is { } vVal)
                        state.Bindings[asVar + ".value"] = vVal.ToString() ?? "";
                    state.LastElementId = item.Id;

                    foreach (var sub in subSteps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var subAction = (sub.Action ?? "").ToLowerInvariant();

                        var effectiveSub = (sub.ElementId == null && sub.Target == null && sub.X == null &&
                            subAction is not ("wait" or "wait_for" or "wait_for_change" or "wait_for_stable" or "key" or "hotkey" or "look" or "mouse" or "launch"))
                            ? sub with { ElementId = $"${asVar}" }
                            : sub;

                        effectiveSub = effectiveSub with
                        {
                            ElementId = SubstituteVariables(effectiveSub.ElementId, state),
                            Text = SubstituteVariables(effectiveSub.Text, state),
                            Value = SubstituteVariables(effectiveSub.Value, state),
                            Target = effectiveSub.Target != null ? effectiveSub.Target with
                            {
                                Name = SubstituteVariables(effectiveSub.Target.Name, state),
                                NameContains = SubstituteVariables(effectiveSub.Target.NameContains, state),
                                ElementId = BoundId(effectiveSub.Target.ElementId, state),
                                Within = BoundId(effectiveSub.Target.Within, state),
                                Value = SubstituteVariables(effectiveSub.Target.Value, state),
                                ValueContains = SubstituteVariables(effectiveSub.Target.ValueContains, state),
                            } : null
                        };

                        var isSubPseudo = subAction is "find" or "assert" or "launch";

                        StepOutcome? subOutcome = null;
                        if (isSubPseudo)
                        {
                            subOutcome = PseudoOutcome(effectiveSub, state, ct, out var subPost);
                            if (subPost != null) { lastPostId = subPost; state.LastElementId = subPost; }
                        }
                        else
                        {
                            var built = BuildStepActions(effectiveSub, state, out var subErrKind, out var subErrDetail, out var subPost, out var subDiag);
                            if (built != null && effectiveSub.Silent == true)
                                built = built.Select(a => a with { Silent = true }).ToList();
                            if (built == null)
                            {
                                subOutcome = new StepOutcome(
                                    Enum.TryParse<OutcomeKind>(subErrKind, out var sok) ? sok : OutcomeKind.Malformed,
                                    false, "scan.substep", subErrDetail ?? "failed to resolve sub-step target", 0,
                                    Diagnosis: subDiag);
                            }
                            else
                            {
                                using var subStepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                if (effectiveSub.Timeout is > 0) subStepCts.CancelAfter(effectiveSub.Timeout.Value);
                                var actx = new ActionContext(subStepCts.Token, null, _s.SessionId, null, null);
                                foreach (var act in built)
                                {
                                    internalActions++;
                                    subOutcome = await Task.Run(() => _s.Resolver.Execute(act, octx, actx), subStepCts.Token);
                                    if (act.ElementId != null) { lastPostId = act.ElementId; state.LastElementId = act.ElementId; }
                                    if (!subOutcome.Success) break;
                                }
                            }
                        }

                        subStepsExecuted++;

                        if (subOutcome?.Success != true)
                        {
                            var errName = subOutcome?.Kind.ToString() ?? "Error";
                            bool shouldStop = stopOn == null
                                || stopOn.Contains("error")
                                || stopOn.Contains(errName)
                                || (errName == "AmbiguousTarget" && stopOn.Contains("ambiguous_target"))
                                || (errName == "UnexpectedModalOpened" && stopOn.Contains("unexpected_dialog"));

                            if (shouldStop)
                            {
                                return new ScanResult(
                                    subOutcome ?? new StepOutcome(OutcomeKind.Failed, false, "scan", "sub-step failed", (int)sw.ElapsedMilliseconds),
                                    sw.ElapsedMilliseconds, subStepsExecuted, internalActions, lastPostId);
                            }
                            break;
                        }
                    }
                }

                processedCount++;
                if (processedCount >= maxItems) break;
            }

            if (processedCount >= maxItems) break;

            if (page < maxPages && container != null)
            {
                var scrollAction = new AgentAction(AgentActionKind.Scroll, ElementId: container.Id, Delta: -120);
                var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                await Task.Run(() => _s.Resolver.Execute(scrollAction, octx, actx), ct);
                internalActions++;
                await Task.Delay(150, ct);
            }
        }

        return new ScanResult(
            new StepOutcome(OutcomeKind.Verified, true, "scan",
                $"scanned {processedCount} matching items, collected {state.Collected.Count} records", (int)sw.ElapsedMilliseconds),
            sw.ElapsedMilliseconds, subStepsExecuted, internalActions, lastPostId);
    }

    private async Task<(bool Healed, string Detail)> TrySelfHeal(
        RunStep s, RunState state, string? errKind, string? errDetail,
        ObsContext octx, CancellationToken ct)
    {
        // 1. AutoScroll: target offscreen
        var isOffscreen = errKind == "TargetOffscreen" || (errDetail?.Contains("OFFSCREEN", StringComparison.OrdinalIgnoreCase) == true);
        if (isOffscreen && s.AutoScroll != false)
        {
            var targetName = s.Target?.Name ?? s.Target?.NameContains;
            var targetRole = s.Target?.Role != null && Enum.TryParse<Inbrisk.Core.Role>(s.Target.Role, true, out var r) ? r : (Inbrisk.Core.Role?)null;
            var offMatches = _s.Rt.Find(new FindSpec(
                Role: targetRole,
                Name: targetName,
                AutomationId: s.Target?.AutomationId,
                IncludeOffscreen: true,
                MaxResults: 5));
            if (offMatches.Count > 0)
            {
                var offEl = offMatches[0];
                var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                // Attempt 1: native ScrollIntoView
                var scrollAction = new AgentAction(AgentActionKind.ScrollIntoView, ElementId: offEl.Id);
                var res = await Task.Run(() => _s.Resolver.Execute(scrollAction, octx, actx), ct);
                if (res.Success)
                {
                    await Task.Delay(150, ct);
                    return (true, $"[auto_scroll: native ScrollIntoView on '{offEl.Name ?? offEl.Id}']");
                }
                // Attempt 2: container/viewport wheel scroll
                var win = _s.Rt.ForegroundWindow();
                int cx = win != null ? win.Bounds.X + win.Bounds.Width / 2 : 500;
                int cy = win != null ? win.Bounds.Y + win.Bounds.Height / 2 : 500;
                int delta = (offEl.Bounds.Y < (win?.Bounds.Y ?? 0)) ? 240 : -240;
                var wheelAction = new AgentAction(AgentActionKind.Scroll, Point: new ImagePoint(cx, cy, 0, 0), Delta: delta);
                var wheelRes = await Task.Run(() => _s.Resolver.Execute(wheelAction, octx, actx), ct);
                if (wheelRes.Success)
                {
                    await Task.Delay(200, ct);
                    return (true, $"[auto_scroll: wheel scroll {delta} towards '{offEl.Name ?? targetName}']");
                }
            }
        }

        // 2. AutoNavigate: target on different screen
        var isDiffScreen = errKind == "TargetOnDifferentScreen" || (errDetail?.Contains("previously seen on screen", StringComparison.OrdinalIgnoreCase) == true);
        if (isDiffScreen && s.AutoNavigate == true)
        {
            var app = _s.Rt.ForegroundWindow()?.ProcessName ?? s.Target?.Process ?? "";
            var curScreen = _s.Memory.GetCurrentScreen(app);
            var targetName = s.Target?.Name ?? s.Target?.NameContains;
            var sug = _s.Memory.SuggestRecovery(targetName, s.Target?.Role, app, curScreen);
            if (sug != null)
            {
                var path = _s.Memory.FindNavigationPath(app, curScreen ?? "", sug.TargetScreen);
                if (path is { Count: > 0 })
                {
                    var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                    var navDetails = new List<string>();
                    bool allOk = true;
                    foreach (var step in path)
                    {
                        var navTarget = new TargetSpec(Name: step.TargetName, Role: step.TargetRole);
                        var navEl = ResolveTargetElement(null, navTarget, out var navErr, out _);
                        if (navEl == null) { allOk = false; break; }
                        var navAction = new AgentAction(AgentActionKind.Click, ElementId: navEl.Id);
                        var navRes = await Task.Run(() => _s.Resolver.Execute(navAction, octx, actx), ct);
                        if (!navRes.Success) { allOk = false; break; }
                        navDetails.Add($"{step.FromScreenId}→{step.ToScreenId} via '{step.ActionDescription}'");
                        await Task.Delay(250, ct);
                    }
                    if (allOk)
                    {
                        await Task.Delay(200, ct);
                        return (true, $"[auto_navigate: {string.Join(", ", navDetails)}]");
                    }
                }
            }
        }

        return (false, "");
    }

    private static bool MatchesItemFilter(UiElement el, ItemFilter? filter)
    {
        if (filter == null) return true;
        if (filter.StartsWith != null && !(el.Name?.StartsWith(filter.StartsWith, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.Contains != null &&
            !(el.Name?.Contains(filter.Contains, StringComparison.OrdinalIgnoreCase) == true ||
              Prop(el, "value")?.ToString()?.Contains(filter.Contains, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.NotContains != null &&
            (el.Name?.Contains(filter.NotContains, StringComparison.OrdinalIgnoreCase) == true ||
             Prop(el, "value")?.ToString()?.Contains(filter.NotContains, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.Exact != null &&
            !string.Equals(el.Name, filter.Exact, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Prop(el, "value")?.ToString(), filter.Exact, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.Role != null && !el.Role.ToString().Equals(filter.Role, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.State != null && !(Prop(el, "state")?.ToString()?.Contains(filter.State, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        return true;
    }

    /// <summary>RunStep → LaunchSpec. Friendly `app` is canonical; explicit
    /// executable/path/aumid/uri are the advanced overrides. `text` /
    /// `target.process` remain accepted as the app's name for backwards
    /// compatibility with earlier plans.</summary>
    private static LaunchSpec? StepLaunchSpec(RunStep s, out string? err,
        out Dictionary<string, object?>? bridgeInfo)
    {
        err = null;
        bridgeInfo = null;
        var app = s.App ?? s.Search ?? s.Target?.Process ?? s.Text ?? s.Value;
        var ids = new[]
        {
            app != null, s.Executable != null, s.Path != null,
            s.Aumid != null, s.Uri != null,
        }.Count(b => b);
        if (ids == 0)
        {
            err = "launch requires app (or executable/path/aumid/uri; " +
                  "text/target.process still accepted as the app name)";
            return null;
        }
        if (ids > 1)
        {
            err = "launch takes exactly one identifier: " +
                  "app|executable|path|aumid|uri";
            return null;
        }
        if (s.Arguments is { Length: > 0 } &&
            (s.Aumid != null || s.Uri != null))
        {
            err = "launch arguments only apply to executable targets";
            return null;
        }
        // adapter:"none" on a launch step opts out of bridge injection;
        // adapter:"blender" force-enables it even when the name doesn't
        // mention Blender.
        var mergedArgs = ApplyAdapterBridge(s.Adapter, bridge: null,
            app, s.Executable, s.Path, s.Aumid, s.Uri,
            s.Arguments, out bridgeInfo);
        return new LaunchSpec(app, s.Executable, s.Path, s.Aumid, s.Uri,
            mergedArgs, s.NewInstance ?? false, s.WaitFor ?? "window",
            s.Timeout ?? s.Ms ?? 25000, s.DebugPort);
    }

    /// <summary>Adapter bridge injection for launches: when the target is
    /// Blender (app/executable/path mentions "blender", or
    /// adapter:"blender" was requested explicitly) materialize the embedded
    /// bpy socket-bridge script via BlenderAdapter's bootstrap path and
    /// append <c>--python &lt;bridge-path&gt;</c> to the launch arguments so
    /// semantic control is live as soon as the window appears. Skipped when
    /// bridge:false, adapter:"none", an explicit non-blender adapter, the
    /// caller already supplied a --python arg, or the launch is aumid/uri
    /// (arguments don't apply there). A failed materialization never blocks
    /// the launch — the app simply starts without the bridge.</summary>
    private static IReadOnlyList<string>? ApplyAdapterBridge(
        string? adapter, bool? bridge,
        string? app, string? executable, string? path,
        string? aumid, string? uri,
        IReadOnlyList<string>? arguments,
        out Dictionary<string, object?>? bridgeInfo)
    {
        bridgeInfo = null;
        var adapterId = adapter?.Trim();
        if (bridge == false ||
            string.Equals(adapterId, "none", StringComparison.OrdinalIgnoreCase))
            return arguments;
        if (!string.IsNullOrEmpty(adapterId) &&
            !string.Equals(adapterId, "blender", StringComparison.OrdinalIgnoreCase))
            return arguments; // explicit non-blender adapter → nothing to inject
        if (aumid != null || uri != null)
            return arguments; // arguments never apply to aumid/uri launches
        if (arguments != null && arguments.Any(a =>
                a.Equals("--python", StringComparison.OrdinalIgnoreCase) ||
                a.StartsWith("--python=", StringComparison.OrdinalIgnoreCase)))
            return arguments; // caller already wired its own script

        var isBlender =
            string.Equals(adapterId, "blender", StringComparison.OrdinalIgnoreCase) ||
            MentionsBlender(app) || MentionsBlender(executable) ||
            MentionsBlender(path);
        if (!isBlender)
            return arguments;

        try
        {
            var res = new Inbrisk.Runtime.Adapters.BlenderAdapter()
                .ExecuteAsync("bootstrap").GetAwaiter().GetResult();
            var scriptPath = res.Data?["path"]?.ToString();
            if (!res.Success || string.IsNullOrEmpty(scriptPath))
                return arguments;
            var port = res.Data != null &&
                       res.Data.TryGetValue("port", out var p) && p is int pi
                ? pi : Inbrisk.Runtime.Adapters.BlenderAdapter.DefaultPort;
            var merged = arguments?.ToList() ?? new List<string>();
            merged.Add("--python");
            merged.Add(scriptPath);
            bridgeInfo = new Dictionary<string, object?>
            {
                ["adapter"] = "blender",
                ["script"] = scriptPath,
                ["port"] = port,
            };
            return merged;
        }
        catch
        {
            return arguments; // materialization failure must never block launch
        }

        static bool MentionsBlender(string? s) =>
            !string.IsNullOrEmpty(s) &&
            s.Contains("blender", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>wait_for scope: derive the window/process subtree to watch
    /// from the step's target — WITHOUT element scoring (scope is "which
    /// window", never a single element). Null scope → resolver falls back
    /// to the observed window, then UIA root.</summary>
    private (long? Hwnd, int? Pid, string? ScopeElementId, string? Err)
        WaitScope(TargetSpec? t, RunState state)
    {
        if (t == null) return (null, null, null, null);
        var wref = t.Within ?? t.ElementId;
        if (wref != null)
        {
            var bound = BoundId(wref, state);
            if (bound == null) return (null, null, null, $"unbound $ref '{wref}'");
            if (ParseHwnd(bound) is { } bh) return (bh, null, null, null);
            var el = _s.Rt.Parts.Registry.EnsureAlive(bound);
            if (el == null)
                return (null, null, null, $"within reference '{wref}' not found");
            // element binding → its subtree is the narrowest scope; the
            // owner hwnd stays as the fallback if the element goes stale
            return (el.Handle.Recipe.Hwnd ?? el.Hwnd, el.Pid,
                el.Handle.BackendRef, null);
        }
        if ((ParseHwnd(t.Hwnd) ?? ParseHwnd(t.Window)) is { } hw)
            return (hw, null, null, null);
        if (t.Window != null)
        {
            var w = _s.Rt.Windows().FirstOrDefault(w =>
                w.Title.Contains(t.Window, StringComparison.OrdinalIgnoreCase));
            return w != null ? (w.Hwnd, null, null, null)
                : (null, null, null, $"no window titled '{t.Window}'");
        }
        if (t.Process != null)
        {
            var w = FindBestWindowForProcess(t.Process);
            return w != null ? (null, w.Pid, null, null)
                : (null, null, null, $"no window for process '{t.Process}'");
        }
        return (null, null, null, null);
    }

    private List<AgentAction>? BuildStepActions(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out string? postId)
        => BuildStepActions(s, state, out errKind, out errDetail, out postId, out _);

    /// <summary>Resolve a plan step's target and expand it into canonical
    /// actions. Null + errKind set → step failed before executing.</summary>
    private List<AgentAction>? BuildStepActions(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out string? postId,
        out TargetDiagnosis? diagnosis)
    {
        errKind = null; errDetail = null; postId = null; diagnosis = null;
        var action = (s.Action ?? "").ToLowerInvariant();
        if (action is "find" or "assert" or "checkpoint" or "launch")
            return []; // handled by PseudoOutcome / main loop

        // wait_for watches a SCOPE (window/process subtree), not a scored
        // element — resolving its target through candidate ranking would
        // drown in the window's children and always report AmbiguousTarget
        if (action is "wait_for" or "wait_for_gone")
        {
            var (scopeHwnd, scopePid, scopeEl, scopeErr) = WaitScope(s.Target, state);
            if (scopeErr != null)
            { errKind = "TargetNotFound"; errDetail = scopeErr; return null; }
            var targetQuery = s.Query ?? s.Target?.Name ?? s.Target?.NameContains
                ?? MapQuery(s.Target?.Map);
            var targetEl = BoundId(s.ElementId, state) ?? scopeEl;
            return [new AgentAction(AgentActionKind.WaitFor, Query: targetQuery,
                ElementId: targetEl,
                Ms: s.Ms, Hwnd: scopeHwnd, Pid: scopePid,
                ScopeElementId: scopeEl,
                ExpectedState: s.ExpectedState,
                ExpectedValue: s.ExpectedValue,
                Gone: action == "wait_for_gone" || s.Gone,
                Count: s.MinCount,
                StopOnUnexpectedDialog: s.StopOnDialog ?? true)];
        }

        // focus/focus_window name a WINDOW, not an element — a window-shaped
        // target ({process|window} only, no role/name/element fields) must
        // resolve through the window list; element ranking would drown in
        // the window's many child elements and always report AmbiguousTarget
        var windowOnly = s.ElementId == null && s.Target?.ElementId == null &&
            !IsElementTarget(s.Target);
        if (windowOnly && (action is "focus_window" ||
            (action == "focus" && s.Hwnd == null)))
        {
            var wh = ParseHwnd(s.Hwnd) ?? ParseHwnd(s.Target?.Window) ??
                ParseHwnd(s.Target?.Hwnd) ??
                WindowHwndFor(s.Target, out errKind, out errDetail);
            if (wh == null)
            {
                if (action == "focus")
                { errKind = "Malformed"; errDetail = "focus requires elementId, target, or hwnd"; }
                else
                { errKind ??= "TargetNotFound"; errDetail ??= "no window matched the target"; }
                return null;
            }
            return [new AgentAction(AgentActionKind.FocusWindow, Hwnd: wh)];
        }

        var el = ResolveRunTarget(s, state, out errKind, out errDetail, out _, out diagnosis);
        postId = el?.Id;
        // a resolution error (TargetNotFound, AmbiguousTarget, Stale) must
        // surface as itself — never build a target-less action that fails
        // later with a meaningless "requires elementId" resolver error
        if (errKind != null) return null;
        AgentActionKind? Kind(string name) => name switch
        {
            "click" => AgentActionKind.Click,
            "rightclick" or "right_click" => AgentActionKind.RightClick,
            "doubleclick" or "double_click" => AgentActionKind.DoubleClick,
            "invoke" => AgentActionKind.Invoke,
            "toggle" => AgentActionKind.Toggle,
            "select" => AgentActionKind.Select,
            "hover" => AgentActionKind.Hover,
            "set_value" or "setvalue" => AgentActionKind.SetValue,
            "key" => AgentActionKind.Key,
            "hotkey" => AgentActionKind.Hotkey,
            "look" => AgentActionKind.Look,
            "mouse" => AgentActionKind.MouseHold,
            "scroll" => AgentActionKind.Scroll,
            "scroll_into_view" => AgentActionKind.ScrollIntoView,
            "drag" => AgentActionKind.Drag,
            "wait" => AgentActionKind.Wait,
            "wait_for" or "wait_for_gone" => AgentActionKind.WaitFor,
            "wait_for_change" => AgentActionKind.WaitForChange,
            "wait_for_stable" => AgentActionKind.WaitForStable,
            "focus_window" => AgentActionKind.FocusWindow,
            _ => null,
        };

        switch (action)
        {
            case "scroll_into_view":
            {
                CallToolResult? resErr = null;
                var offEl = el ?? ResolveTargetElement(s.ElementId, s.Target, out resErr, "invoke", includeOffscreen: true);
                if (resErr != null)
                {
                    (errKind, errDetail) = ErrParts(resErr);
                    return null;
                }
                if (offEl == null) return null;
                postId = offEl.Id;
                return [new AgentAction(AgentActionKind.ScrollIntoView, ElementId: offEl.Id)];
            }
            case "type":
            {
                var modeN = (s.Mode ?? "insert").ToLowerInvariant();
                var posN = (s.Position ?? (modeN == "append" ? "end" : "current"))
                    .ToLowerInvariant();
                if (modeN is not ("replace" or "append" or "insert") ||
                    posN is not ("current" or "start" or "end"))
                { errKind = "Malformed"; errDetail = "bad mode/position"; return null; }
                if (s.Text == null && s.Value == null)
                { errKind = "Malformed"; errDetail = "type requires text"; return null; }
                return BuildTypeSteps(el, s.Text ?? s.Value!, modeN, posN, s.Submit);
            }
            case "focus":
            {
                // element → UIA SetFocus; window-only target → foreground it
                if (el == null && s.Hwnd == null && s.Target?.Window == null)
                { errKind = "Malformed"; errDetail = "focus requires a target"; return null; }
                if (s.Hwnd != null || el == null || el.Role == Core.Role.Window)
                {
                    var h = ParseHwnd(s.Hwnd) ?? el?.Hwnd
                        ?? WindowHwndFor(s.Target, out errKind, out errDetail);
                    if (h == null)
                    { errKind ??= "TargetNotFound"; errDetail ??= "no window for focus"; return null; }
                    return [new AgentAction(AgentActionKind.FocusWindow, Hwnd: h)];
                }
                return [new AgentAction(AgentActionKind.FocusElement, ElementId: el.Id)];
            }
        }

        var kind = Kind(action);
        if (kind == null)
        { errKind = "Malformed"; errDetail = $"unknown plan action '{s.Action}'"; return null; }
        if (kind.Value == AgentActionKind.FocusWindow)
        {
            var h = ParseHwnd(s.Hwnd) ?? el?.Hwnd
                ?? WindowHwndFor(s.Target, out errKind, out errDetail);
            if (h == null)
            { errKind ??= "Malformed"; errDetail ??= "focus_window requires hwnd or window target"; return null; }
            return [new AgentAction(kind.Value, Hwnd: h)];
        }
        var key = s.Key; var mods = s.Modifiers;
        if (key == null && s.Keys is { } combo)
        {
            var parts = combo.Split(['+', ',', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            key = parts.LastOrDefault();
            mods = parts.Take(parts.Length - 1).ToArray();
        }
        var pointX = s.X ?? s.Target?.X;
        var pointY = s.Y ?? s.Target?.Y;
        return [new AgentAction(kind.Value,
            ElementId: el?.Id,
            Point: MakePoint(s.FrameId, s.ObservationId, pointX, pointY),
            To: s.ToX != null && s.ToY != null
                ? new ImagePoint(s.ToX.Value, s.ToY.Value,
                    s.ToFrameId ?? 0, s.ToObservationId ?? 0)
                : null,
            Text: s.Text ?? s.Value,
            Key: key, Modifiers: mods, Count: s.Count,
            Delta: s.Delta, Ms: s.Ms, Query: s.Query,
            Dx: s.Dx, Dy: s.Dy, Button: s.Button)];
    }

    /// <summary>Does the target describe an element (role/name/relationships)
    /// rather than just a window (process/title)?</summary>
    private static bool IsElementTarget(TargetSpec? t)
        => t != null && (t.Role != null || t.Name != null ||
            t.AutomationId != null || t.LabelledBy != null ||
            t.NearText != null || t.Within != null || t.Ancestor != null);

    /// <summary>First window matching a target's window/process filter —
    /// for focus steps that name a window rather than an element.</summary>
    private long? WindowHwndFor(TargetSpec? t, out string? errKind, out string? errDetail)
    {
        errKind = null; errDetail = null;
        if (t == null) return null;
        var hw = ParseHwnd(t.Window);
        if (hw != null) return hw;
        WindowInfo? w = null;
        if (t.Process != null)
            w = FindBestWindowForProcess(t.Process);
        if (w == null && t.Window != null)
            w = _s.Rt.Windows().FirstOrDefault(x => x.Title.Contains(t.Window, StringComparison.OrdinalIgnoreCase));
        return w?.Hwnd;
    }

    private bool BadPoint(AgentAction a, out string? error)
    {
        error = null;
        foreach (var p in new[] { a.Point, a.To }.OfType<ImagePoint>())
            if (p.ObservationId == 0 ||
                !_s.FrameObservations.TryGetValue(p.FrameId, out var obsId) ||
                obsId != p.ObservationId)
            {
                error = "frameId and observationId must identify the same session frame";
                return true;
            }
        return false;
    }

    // ------------------------------------------------------------ run state

    /// <summary>Scope-scoped element snapshot — the baseline a checkpoint or
    /// pause diffs against. Ids come from the shared registry so returned
    /// refs are immediately usable as targets.</summary>
    private List<UiElement> Snapshot(long? hwnd, CancellationToken ct = default)
    {
        try
        {
            using (PerfTrace.Stage("snapshot.uiaFind"))
                return hwnd != null
                    ? _s.Rt.Find(new FindSpec(Hwnd: hwnd), ct).ToList() : [];
        }
        catch { return []; }
    }

    private static string Describe(UiElement e)
    {
        var dis = e.Props.TryGetValue("enabled", out var en) && en is false ? " [DISABLED]" : "";
        return $"[{e.Id}] {e.Role}{dis} \"{Trunc(e.Name, 60)}\" " +
               $"bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})";
    }

    /// <summary>Step actions that cannot mutate the UI — a plan made only of
    /// these skips the pre-run baseline snapshot.</summary>
    private static readonly HashSet<string> ReadOnlyStepActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "find", "assert", "checkpoint", "wait", "wait_for",
            "wait_for_change", "wait_for_stable",
        };

    /// <summary>Events touching <paramref name="hwnd"/> since
    /// <paramref name="since"/>. Element-level events carry no hwnd (controls
    /// return 0) — those count as relevant for any scope.</summary>
    private List<ObservedEvent> RelevantEvents(long? hwnd, DateTimeOffset since)
        => _s.Rt.Parts.EventBuffer.Snapshot(200).Where(e =>
            e.At >= since && (hwnd == null || e.Hwnd is null or 0 ||
            e.Hwnd == hwnd)).ToList();

    /// <summary>Foreground-scoped semantic delta vs the run's last baseline;
    /// refreshes the baseline so consecutive pauses report only new change.
    /// Event-gated: no relevant events → no post snapshot; a deferred
    /// baseline + observed events → event-derived delta (bounded fallback —
    /// the real diff still runs whenever a baseline exists or events fired).</summary>
    private List<string> Delta(RunState state, bool actionsRan, int cap = 30)
    {
        using var _d = PerfTrace.Stage("report.delta");
        var hwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        var since = state.BaselineDeferred ? state.RunStartedAt : state.BaselineAt;
        var fresh = RelevantEvents(hwnd, since);

        if (state.BaselineDeferred)
        {
            if (fresh.Count == 0)
            {
                PerfTrace.Count("delta.eventGated");
                return [];
            }
            var evDelta = fresh.Take(cap)
                .Select(e => $"~ {e.Kind} {Trunc(e.Detail ?? "", 80)}").ToList();
            state.Baseline = Snapshot(hwnd);
            state.BaselineHwnd = hwnd;
            state.BaselineAt = DateTimeOffset.Now;
            state.BaselineDeferred = false;
            PerfTrace.Count("delta.eventDerived");
            return evDelta;
        }
        // baseline exists, window unchanged, nothing executed and nothing
        // observed → the tree is identical to the baseline; skip the
        // redundant post-run snapshot. Providers that under-report events
        // still get a real diff whenever an action ran (bounded fallback).
        if (state.BaselineHwnd == hwnd && fresh.Count == 0 && !actionsRan)
        {
            PerfTrace.Count("delta.eventGated");
            return [];
        }
        var cur = Snapshot(hwnd);
        var delta = new List<string>();
        if (state.BaselineHwnd != hwnd)
            delta.Add($"window: 0x{state.BaselineHwnd ?? 0:X} → 0x{hwnd ?? 0:X}");
        var oldByKey = state.BaselineHwnd == hwnd
            ? state.Baseline.GroupBy(e => ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId))
                            .ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<string, UiElement>();
        var curByKey = cur.GroupBy(e => ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId))
                          .ToDictionary(g => g.Key, g => g.First());
        foreach (var (k, e) in curByKey)
        {
            if (!oldByKey.TryGetValue(k, out var o))
            { delta.Add($"+ {Describe(e)}"); continue; }
            var ch = new List<string>();
            if (o.Name != e.Name)
                ch.Add($"name \"{Trunc(o.Name, 40)}\"→\"{Trunc(e.Name, 40)}\"");
            foreach (var p in new[] { "value", "state", "focused", "enabled" })
            {
                var b = Prop(o, p)?.ToString();
                var a = Prop(e, p)?.ToString();
                if (a != b) ch.Add($"{p} {Trunc(b, 40)}→{Trunc(a, 40)}");
            }
            if (ch.Count > 0) delta.Add($"~ {Describe(e)}: {string.Join("; ", ch)}");
        }
        foreach (var (k, e) in oldByKey)
            if (!curByKey.ContainsKey(k)) delta.Add($"- {Describe(e)}");
        state.Baseline = cur;
        state.BaselineHwnd = hwnd;
        state.BaselineAt = DateTimeOffset.Now;
        return delta.Take(cap).ToList();
    }

    private List<string> AvailableElements(RunState state, int cap = 15)
    {
        using var _a = PerfTrace.Stage("report.available");
        var hwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        // Delta() refreshes state.Baseline with a fresh snapshot of this hwnd —
        // reuse it instead of paying a second identical subtree enumeration.
        var els = state.BaselineHwnd == hwnd && state.Baseline.Count > 0
            ? state.Baseline
            : Snapshot(hwnd);
        var tags = DialogTags(els);
        return els.Where(e => e.Actions.Count > 0).Take(cap)
            .Select(e => Describe(e) +
                (e.Props.TryGetValue("labelledBy", out var lb) && lb != null
                    ? $" labelledBy=\"{lb}\"" : "") +
                (tags.TryGetValue(e.Id, out var tag) ? $" dialogRole={tag}" : ""))
            .ToList();
    }

    /// <summary>Canonical affordances for the common Windows file dialogs.
    /// Generic semantic normalization over label dictionaries — tags which
    /// control is the filename field, which button saves, etc., so a model
    /// doesn't need to guess from raw UIA names. Returns an empty map when
    /// the element set doesn't look like a file dialog.</summary>
    private static Dictionary<string, string> DialogTags(IReadOnlyList<UiElement> els)
    {
        static bool Named(UiElement e, params string[] pats) =>
            e.Name != null && pats.Any(p =>
                e.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
        var tags = new Dictionary<string, string>();
        var filename = els.Where(e =>
            e.Role is Core.Role.Edit or Core.Role.ComboBox &&
            Named(e, "dosya ad", "file name", "dateiname", "nom du fichier")).ToList();
        var accept = els.Where(e => e.Role == Core.Role.Button &&
            Named(e, "kaydet", "save", "aç", "open", "speichern", "enregistrer")).ToList();
        if (filename.Count == 0 || accept.Count == 0) return tags; // not a file dialog
        foreach (var f in filename) tags[f.Id] = "filename";
        foreach (var b in accept)
            tags[b.Id] = Named(b, "kaydet", "save", "speichern", "enregistrer")
                ? "saveButton" : "openButton";
        foreach (var c in els.Where(e => e.Role == Core.Role.ComboBox &&
            Named(e, "kayıt tür", "file type", "save as type",
                "files of type", "dateityp")))
            tags[c.Id] = "filetype";
        foreach (var c in els.Where(e => e.Role == Core.Role.Button &&
            Named(e, "iptal", "cancel", "abbrechen", "annuler")))
            tags[c.Id] = "cancelButton";
        foreach (var t in els.Where(e => e.Role == Core.Role.Tree &&
            Named(e, "gezinti", "navigation", "navigationsbereich")))
            tags[t.Id] = "location";
        foreach (var t in els.Where(e => e.Role == Core.Role.Toolbar &&
            Named(e, "adres", "address")))
            tags[t.Id] = "addressBar";
        foreach (var t in els.Where(e => e.Role == Core.Role.Edit &&
            Named(e, "arama", "search", "suche")))
            tags[t.Id] = "searchBox";
        return tags;
    }

    /// <summary>Cheap Win32 gate before paying a subtree snapshot for dialog
    /// tagging: file dialogs are the #32770 class; DirectUI task dialogs are
    /// small owned windows. Pure user32 calls — no UIA/COM cost.</summary>
    private static bool LooksLikeDialog(long hwnd)
    {
        var cls = WindowClass(hwnd);
        if (cls == "#32770") return true;
        if (cls is not ("DirectUIHWND" or "NativeHWNDHost")) return false;
        if (GetWindow((IntPtr)hwnd, 4 /*GW_OWNER*/) == IntPtr.Zero) return false;
        return GetWindowRect((IntPtr)hwnd, out var r) &&
            (long)(r.Right - r.Left) * (r.Bottom - r.Top) < 1_200_000;
    }

    /// <summary>Hard budget for the modal-check element snapshot. The
    /// check runs on the action's critical path — a wedged provider must
    /// degrade to "no elements" instead of stalling the step.</summary>
    private const int ModalCheckSnapshotBudgetMs = 2000;

    /// <summary>Did the foreground window change underneath us? A small
    /// button-bearing window that appeared right after an action is almost
    /// certainly a dialog — report it prominently instead of letting the
    /// plan walk past it. Returns null when nothing changed or the new
    /// window is exempt (an intended focus target).</summary>
    private (long hwnd, string title, bool dialogLikely, List<string> elements)?
        DetectNewWindow(long? beforeHwnd, long? exemptHwnd, long? baselineEventGen = null,
            IReadOnlyCollection<int>? targetPids = null)
    {
        var fg = _s.Rt.ForegroundWindow();
        if (fg == null || fg.Hwnd == beforeHwnd || fg.Hwnd == exemptHwnd)
            return null;

        // Event-gating: only take expensive element snapshot if a candidate window/dialog signal exists
        var candidateSignal = false;
        if (baselineEventGen.HasValue && _s.Rt.EventBuffer.CurrentGeneration > baselineEventGen.Value)
        {
            var events = _s.Rt.EventBuffer.Snapshot(50);
            candidateSignal = events.Any(e => e.Kind is EventKind.WindowOpened or EventKind.WindowShown or EventKind.ForegroundChanged);
        }
        else if (!baselineEventGen.HasValue)
        {
            candidateSignal = true;
        }

        var cls = WindowClass(fg.Hwnd);
        var isDialogCls = cls == "#32770";
        var isModal = _s.Rt.WindowService.GetActiveBlockingPopup(fg.Hwnd) != null ||
                      _s.Rt.WindowService.GetModalPopup(fg.Hwnd) != null;

        if (!candidateSignal && !isDialogCls && !isModal)
            return null;

        var ownerHwnd = GetWindow((IntPtr)fg.Hwnd, 4 /*GW_OWNER*/);
        var owner = ownerHwnd != IntPtr.Zero;
        var area = (long)fg.Bounds.Width * fg.Bounds.Height;
        // Every branch that can yield dialogLikely=true requires a
        // dialog-shaped window: #32770, a detected modal, or a small owned
        // popup. Anything else — a heavyweight window that merely took
        // foreground, e.g. Chrome after a click — is reported without a
        // UIA subtree walk (an unbounded Descendants find on a huge tree
        // costs seconds; it was the ~13s step.modalCheck regression).
        var dialogShaped = isDialogCls || isModal || (owner && area < 1_200_000);
        if (!dialogShaped)
            return (fg.Hwnd, fg.Title ?? "", false, []);

        // A modal that interrupts THIS action lives in the action
        // target's process — or is owned by one of its windows. A
        // dialog-shaped window from a foreign process still blocks the
        // desktop, so it is reported (cheaply) — but its element list is
        // never worth a cross-process tree walk.
        if (!OwnedByTargetProcess(fg.Hwnd, fg.Pid, ownerHwnd.ToInt64(), targetPids, beforeHwnd))
            return (fg.Hwnd, fg.Title ?? "", isDialogCls || isModal, []);

        using var snapshotCts = new CancellationTokenSource(ModalCheckSnapshotBudgetMs);
        var els = Snapshot(fg.Hwnd, snapshotCts.Token);
        var buttons = els.Count(e =>
            e.Role is Core.Role.Button or Core.Role.MenuItem);
        // #32770 is THE Windows dialog class — deterministic. Fallback:
        // small owned window with buttons (DirectUI/custom dialogs).
        var likely = isDialogCls || isModal ||
            (els.Count is > 0 and <= 80 && buttons >= 1 &&
             area < 1_200_000 && owner);
        return (fg.Hwnd, fg.Title ?? "", likely,
            els.Where(e => e.Actions.Count > 0).Take(12).Select(Describe).ToList());
    }

    /// <summary>Is <paramref name="hwnd"/> in — or owned by a window in —
    /// one of the action target's processes? A dialog for app X lives in
    /// X's process; a foreign window that merely took foreground (Chrome
    /// after a coordinate click) is never the target's modal.</summary>
    private bool OwnedByTargetProcess(long hwnd, int hwndPid, long ownerHwnd,
        IReadOnlyCollection<int>? targetPids, long? fallbackHwnd)
    {
        var pids = targetPids;
        if (pids == null || pids.Count == 0)
        {
            // No resolved targets — fall back to the pid of the window
            // that was foreground when the action ran.
            if (fallbackHwnd is > 0 &&
                _s.Rt.Window(fallbackHwnd.Value)?.Pid is { } fp && fp > 0)
                pids = new[] { fp };
            else
                return true; // nothing to compare — keep prior behavior
        }
        if (hwndPid > 0 && pids.Contains(hwndPid)) return true;
        // Cross-process but owned by a target window — a system dialog
        // raised on the target's behalf (consent/picker hosts).
        return ownerHwnd != 0 &&
            _s.Rt.Window(ownerHwnd)?.Pid is { } op && pids.Contains(op);
    }

    /// <summary>Pids of the windows a step's resolved actions target —
    /// scopes the post-action modal check to the target's own process.</summary>
    private HashSet<int>? StepTargetPids(IReadOnlyList<AgentAction>? actions, long? fallbackHwnd)
    {
        var pids = new HashSet<int>();
        if (actions != null)
            foreach (var a in actions)
            {
                if (a.Pid is > 0) pids.Add(a.Pid.Value);
                var h = a.Hwnd ?? (a.ElementId != null
                    ? _s.Rt.Parts.Registry.Get(a.ElementId)?.Hwnd : null);
                if (h is > 0 && _s.Rt.Window(h.Value)?.Pid is { } p && p > 0)
                    pids.Add(p);
            }
        if (pids.Count == 0 && fallbackHwnd is > 0 &&
            _s.Rt.Window(fallbackHwnd.Value)?.Pid is { } fp && fp > 0)
            pids.Add(fp);
        return pids.Count > 0 ? pids : null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT32 lpRect);

    private struct RECT32 { public int Left, Top, Right, Bottom; }

    private static string WindowClass(long hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName((IntPtr)hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static Dictionary<string, object?> StepEntry(int i, RunStep s,
        string status, string? method = null, int? ms = null,
        string? detail = null, int? actions = null)
    {
        var d = new Dictionary<string, object?>
        {
            ["step"] = i,
            ["action"] = s.Action,
            ["status"] = status,
        };
        if (ms.HasValue) d["ms"] = ms.Value;
        if (!string.IsNullOrEmpty(method)) d["method"] = method;
        if (!string.IsNullOrEmpty(detail)) d["detail"] = detail;
        if (actions.HasValue && actions.Value > 0) d["internalActions"] = actions.Value;
        return d;
    }

    /// <summary>reflex block shared by run/batch/do results — additive:
    /// {enabled, mode, modals:[{title,disposition,actionTaken,ms}],
    /// aborted}. Modals are sliced from the session engine's cumulative
    /// log at this run's baseline so resumed legs keep the full history.</summary>
    private Dictionary<string, object?> ReflexBlock(RunState state)
    {
        var modals = new List<Dictionary<string, object?>>();
        if (_s?.Reflex?.Log is { } log)
        {
            foreach (var m in log.Skip(Math.Max(0, state.ReflexLogBaseline)))
            {
                modals.Add(new Dictionary<string, object?>
                {
                    ["hwnd"] = m.Hwnd > 0 ? $"0x{m.Hwnd:X}" : null,
                    ["title"] = m.Title,
                    ["disposition"] = m.Disposition,
                    ["actionTaken"] = m.ActionTaken,
                    ["reason"] = m.Reason,
                    ["ms"] = m.Ms,
                });
            }
        }
        return new Dictionary<string, object?>
        {
            ["enabled"] = state.ReflexEnabled,
            ["mode"] = state.ReflexMode,
            ["modals"] = modals,
            ["aborted"] = state.ReflexAborted,
        };
    }

    /// <summary>Structured abort result when the reflex engine stops a run:
    /// {error:"InterruptedByDialog", modal:{title,disposition,reason,
    /// buttons}, runId, resume, resumeSteps[]}. state.PausedStepIndex
    /// points at the step that never ran, so computer_resume_run re-runs
    /// the remaining steps without re-submission.</summary>
    private CallToolResult ReflexAbortResult(RunState state, RunStep[] steps,
        int stepIndex, ReflexAbort abort,
        List<Dictionary<string, object?>> report,
        int internalActions, int executed, int skipped, Stopwatch sw)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = OutcomeKind.InterruptedByDialog.ToString(),
            ["status"] = OutcomeKind.InterruptedByDialog.ToString(),
            ["detail"] = abort.Reason,
            ["runId"] = state.RunId,
            ["modal"] = new Dictionary<string, object?>
            {
                ["hwnd"] = abort.ModalHwnd > 0 ? $"0x{abort.ModalHwnd:X}" : null,
                ["title"] = abort.Title,
                ["disposition"] = abort.Disposition,
                ["reason"] = abort.Reason,
                ["buttons"] = abort.ButtonsSeen,
            },
            ["steps"] = report,
            ["executed"] = executed,
            ["skipped"] = skipped,
            ["internalActions"] = internalActions,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["failedStep"] = stepIndex,
            ["remainingSteps"] = Math.Max(0, steps.Length - stepIndex),
            ["resume"] = $"computer_resume_run({{runId:\"{state.RunId}\"}})",
            ["resumeSteps"] = steps.Skip(Math.Min(stepIndex, steps.Length))
                .Select(DescribeStep).ToList(),
            ["reflex"] = ReflexBlock(state),
            ["provenance"] = Provenance("uia"),
        };
        UiaPerf.Write(new
        {
            kind = "run.reflexAbort",
            runId = state.RunId,
            step = stepIndex,
            modalTitle = abort.Title,
            disposition = abort.Disposition,
            ms = sw.ElapsedMilliseconds,
        });
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(payload, J) }],
        };
    }

    private CallToolResult RunReport(RunState state, string status,
        Stopwatch sw, List<Dictionary<string, object?>> steps,
        int internalActions = 0, int executed = 0, int skipped = 0,
        int pauseStep = -1, string? pauseStatus = null, string? pauseError = null,
        bool includeDelta = false, bool includeAvailable = false,
        UiElement? pre = null, UiElement? post = null,
        TargetDiagnosis? pauseDiagnosis = null)
    {
        string? curScreen = null;
        string app = "";
        try
        {
            var fg = _s?.Rt?.ForegroundWindow();
            app = fg?.ProcessName ?? "";
            curScreen = _s?.Memory?.GetCurrentScreen(app);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RunReport current-screen lookup failed: {ex.Message}");
        }

        var payload = new Dictionary<string, object?>
        {
            ["runId"] = state.RunId,
            ["status"] = status,
            ["screen"] = curScreen,
            ["executed"] = executed,
            ["steps"] = steps,
            ["durationMs"] = sw.ElapsedMilliseconds,
            // F03: steps/delta/post/availableElements embed screen-supplied
            // strings — data, never instructions
            ["provenance"] = Provenance("uia"),
        };
        if (!state.Slim || skipped > 0) payload["skipped"] = skipped;
        if (!state.Slim || internalActions > 0) payload["internalActions"] = internalActions;
        if (!state.Slim && status is "Completed")
        {
            payload["verificationHint"] = "plan execution completed successfully; follow-up computer_observe is NOT needed";
            if (executed >= 2)
                payload["recipeHint"] = "reusable flow? save it for replay: " +
                    $"computer_save_recipe{{name:\"<name>\", fromRunId:\"{state.RunId}\"}}";
        }
        // P2.9: per-run plan stats → find-perf.jsonl. One event per
        // computer_run/resume invocation; callsSaved estimates the
        // separate MCP tool calls this batched plan replaced (each
        // executed step + internal scan/adapter action ≈ one call).
        UiaPerf.Write(new
        {
            kind = "run.summary",
            runId = state.RunId,
            status,
            stepEntries = steps.Count,
            executed,
            skipped,
            internalActions,
            collectedRecords = state.Collected.Count,
            callsSaved = executed + internalActions,
            ms = sw.ElapsedMilliseconds,
        });
        if (state.Bindings.Count > 0) payload["bindings"] = state.Bindings;
        if (state.Collected.Count > 0) payload["collected"] = state.Collected;
        if (state.HumanChanges != null) payload["humanChanges"] = state.HumanChanges;
        if (_s?.Reflex != null) payload["reflex"] = ReflexBlock(state);
        if (state.IsPausedForHuman)
        {
            payload["isPausedForHuman"] = true;
            payload["humanTakeoverHint"] = "Human user has control of the desktop. Perform manual actions, then call computer_resume_run to continue.";
        }
        if (pauseStep >= 0)
        {
            var pause = new Dictionary<string, object?>
            {
                ["step"] = pauseStep,
                ["status"] = pauseStatus,
                ["error"] = pauseError,
                ["screen"] = curScreen,
            };
            if (pauseDiagnosis != null)
            {
                pause["diagnosis"] = pauseDiagnosis;
            }
            if (pauseStatus != null && Enum.TryParse<OutcomeKind>(pauseStatus, out var okind))
            {
                pause["recoveryHint"] = RecoveryPolicy.For(okind).ToString();
            }
            var sug = _s?.Memory?.SuggestRecovery(state.LastElementId, null, app, curScreen);
            if (sug != null)
            {
                pause["navigationSuggestion"] = new
                {
                    targetScreen = sug.TargetScreen,
                    hint = sug.NavigationHint,
                    path = sug.Path,
                };
            }
            if (includeDelta) pause["observationDelta"] =
                Delta(state, executed + internalActions > 0, state.Slim ? 12 : 30);
            if (includeAvailable) pause["availableElements"] =
                AvailableElements(state, state.Slim ? 8 : 15);
            payload["pause"] = pause;
            payload["resume"] = $"computer_resume_run({{runId:\"{state.RunId}\"}})";
        }
        else if (includeDelta && (!state.Slim || status is not "Completed"))
        {
            var deltaList = Delta(state, executed + internalActions > 0,
                state.Slim ? 12 : 30);
            if (!state.Slim || deltaList.Count > 0)
                payload["delta"] = deltaList;
        }
        if (post != null)
        {
            payload["post"] = new Dictionary<string, object?>
            {
                ["id"] = post.Id,
                ["role"] = post.Role.ToString(),
                ["name"] = post.Name,
                ["value"] = post.Props.TryGetValue("value", out var pv)
                    ? TruncEdges(pv?.ToString(), 300) : null,
                ["state"] = post.Props.TryGetValue("state", out var ps) ? ps : null,
                ["focused"] = post.Props.TryGetValue("focused", out var pf) ? pf : null,
                ["bounds"] = $"({post.Bounds.X},{post.Bounds.Y} {post.Bounds.Width}x{post.Bounds.Height})",
            };
            var changes = new Dictionary<string, object?>();
            if (!Equals(Prop(pre, "value"), Prop(post, "value")))
                changes["valueChanged"] = true;
            if (!Equals(Prop(pre, "state"), Prop(post, "state")))
                changes["stateChanged"] = true;
            if (changes.Count > 0) payload["changes"] = changes;
        }
        var failed = status is "Paused" or "EmergencyStopped";
        return new CallToolResult
        {
            IsError = failed,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, J) }],
        };
    }

    private static (string kind, string detail, TargetDiagnosis? diagnosis) ErrPartsWithDiag(CallToolResult err)
    {
        try
        {
            var text = (err.Content[0] as TextContentBlock)?.Text ?? "";
            using var doc = JsonDocument.Parse(text);
            var kind = doc.RootElement.TryGetProperty("error", out var k)
                ? k.GetString() ?? "Failed" : "Failed";
            var detail = doc.RootElement.TryGetProperty("detail", out var d)
                ? d.GetString() ?? "" : "";
            // AmbiguousTarget payloads carry the candidates the model needs
            // to refine — don't strip them when the error is repackaged
            if (doc.RootElement.TryGetProperty("candidates", out var cs) &&
                cs.ValueKind == JsonValueKind.Array)
            {
                var names = cs.EnumerateArray().Take(4).Select(c =>
                    $"{c.GetProperty("id").GetString()} " +
                    $"{c.GetProperty("role").GetString()} " +
                    $"\"{c.GetProperty("name").GetString()}\" " +
                    $"score={c.GetProperty("score").GetInt32()}");
                detail += " — candidates: " + string.Join("; ", names);
            }
            TargetDiagnosis? diag = null;
            if (doc.RootElement.TryGetProperty("diagnosis", out var diagEl))
            {
                try { diag = JsonSerializer.Deserialize<TargetDiagnosis>(diagEl.GetRawText(), J); }
                catch (Exception ex) { Debug.WriteLine($"target diagnosis payload parse failed: {ex.Message}"); }
            }
            return (kind, detail, diag);
        }
        catch { return ("Failed", "", null); }
    }

    private static (string kind, string detail) ErrParts(CallToolResult err)
    {
        var (k, d, _) = ErrPartsWithDiag(err);
        return (k, d);
    }
}
