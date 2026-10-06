using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>A captured frame + its observation metadata — everything needed
/// to decide whether an image coordinate is still safe to act on.</summary>
public sealed record FrameRef(RawFrame Raw, ObsFrameRef Meta);

/// <summary>Per-step context the resolver validates against — the frames that
/// produced the current observation (FrameId → transform for grounding) and
/// the monitor factory for wait actions.</summary>
public sealed record ObsContext(
    long ObservationId,
    long? ActiveHwnd,
    IReadOnlyDictionary<long, FrameRef> Frames,
    Func<ChangeMonitor> MonitorFactory,
    /// <summary>Image points older than this are rejected even if the frame
    /// is still attached to the current observation.</summary>
    int MaxFrameAgeMs = 15_000,
    /// <summary>Optional set of observation ids a point may claim. Null →
    /// the point's ObservationId must equal ObservationId (agent-loop
    /// semantics). MCP sessions pass their minted-observation set.</summary>
    IReadOnlySet<long>? ValidObservationIds = null);

/// <summary>
/// Canonical action → validated execution. The chain:
///   schema → target exists → frame freshness/geometry → safety (in Executor)
///   → pre-state snapshot → Executor.Perform → auto-verification →
///   OutcomeKind classification + evidence.
/// Malformed actions never reach Windows.
/// </summary>
public sealed class ActionResolver
{
    private readonly ElementRegistry _registry;
    private readonly Executor _executor;
    private readonly WaitService _waits;
    private readonly IWindowService _windows;
    private readonly AutoVerifier _verifier;

    public ActionResolver(ElementRegistry registry, Executor executor,
        WaitService waits, IWindowService windows, AutoVerifier verifier)
    {
        _registry = registry;
        _executor = executor;
        _waits = waits;
        _windows = windows;
        _verifier = verifier;
    }

    public StepOutcome Execute(AgentAction a, ObsContext ctx, ActionContext actx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            switch (a.Kind)
            {
                // ---- waits: real runtime signals, not executor intents ----
                case AgentActionKind.Wait:
                    if (a.Ms is not > 0) return Malformed(sw, "wait requires ms>0");
                    if (actx.Ct.WaitHandle.WaitOne(Math.Min(a.Ms.Value, 30_000)))
                        return Done(sw, OutcomeKind.Cancelled, "wait", "cancelled", false);
                    return Done(sw, OutcomeKind.Verified, "wait", $"slept {a.Ms}ms");

                case AgentActionKind.WaitFor:
                {
                    if (string.IsNullOrWhiteSpace(a.Query) && string.IsNullOrWhiteSpace(a.ElementId) &&
                        string.IsNullOrWhiteSpace(a.ExpectedState) && string.IsNullOrWhiteSpace(a.ExpectedValue) && !a.Gone)
                        return Malformed(sw, "wait_for requires query or condition");

                    var scopeHwnd = a.Hwnd ??
                        (a.Pid == null && a.ScopeElementId == null &&
                         ctx.ActiveHwnd is > 0 ? ctx.ActiveHwnd : null);

                    var cond = new WaitCondition(
                        Name: a.Query,
                        ExpectedState: a.ExpectedState,
                        ExpectedValue: a.ExpectedValue,
                        Gone: a.Gone,
                        MinCount: a.Count,
                        ScopeElementId: a.ScopeElementId ?? a.ElementId,
                        Hwnd: scopeHwnd,
                        Pid: a.Pid,
                        StopOnUnexpectedDialog: a.StopOnUnexpectedDialog);

                    var r = _waits.ForCondition(cond, a.Ms ?? 5000, actx.Ct);
                    if (r.InterruptedByDialog)
                        return Done(sw, OutcomeKind.InterruptedByDialog, "wait_for", r.Reason, false);

                    return r.Success
                        ? Done(sw, OutcomeKind.Verified, "wait_for", r.Reason)
                        : Done(sw, OutcomeKind.Timeout, "wait_for", r.Reason, false);
                }

                case AgentActionKind.WaitForChange:
                case AgentActionKind.WaitForStable:
                {
                    if (ctx.ActiveHwnd == null)
                        return Malformed(sw, "no active window to watch");
                    var mon = ctx.MonitorFactory(); // orchestrator-owned — do not dispose
                    var r = a.Kind == AgentActionKind.WaitForChange
                        ? _waits.ForChange(mon, a.Ms ?? 5000, actx.Ct)
                        : _waits.ForStable(mon, a.Ms ?? 10000, actx.Ct);
                    return r.Success
                        ? Done(sw, OutcomeKind.Verified, a.Kind.ToString().ToLower(), r.Reason)
                        : Done(sw, OutcomeKind.Timeout, a.Kind.ToString().ToLower(),
                            r.Reason, false);
                }

                // ---- window targeting ----
                case AgentActionKind.FocusWindow:
                {
                    if (a.Hwnd is not { } h) return Malformed(sw, "focus_window requires hwnd");
                    if (_windows.GetWindow(h) == null)
                        return Done(sw, OutcomeKind.WindowLost, "guard",
                            $"window 0x{h:X} gone", false);
                    return Run(sw, a, new ActionIntent(ActionKind.FocusWindow,
                        TargetRef.Window(h)), actx);
                }

                // ---- everything else resolves to an executor intent ----
                default:
                    return RunValidated(sw, a, ctx, actx);
            }
        }
        catch (OperationCanceledException)
        {
            return Done(sw, OutcomeKind.Cancelled, "cancelled", "run cancelled", false);
        }
        catch (Exception e)
        {
            return Done(sw, OutcomeKind.Failed, "resolver", e.Message, false);
        }
    }

    private StepOutcome RunValidated(System.Diagnostics.Stopwatch sw,
        AgentAction a, ObsContext ctx, ActionContext actx)
    {
        var intent = ToIntent(a, ctx, out var err, out var kind);
        if (intent == null) return Done(sw, kind, "validation",
            err ?? "invalid action", false);
        return Run(sw, a, intent, actx);
    }

    /// <summary>pre-state → execute → auto-verify. Verified only with
    /// evidence; otherwise the executor's honest Unverified stands.</summary>
    private StepOutcome Run(System.Diagnostics.Stopwatch sw, AgentAction a,
        ActionIntent intent, ActionContext actx)
    {
        actx.Ct.ThrowIfCancellationRequested();
        UiElement? element;
        using (PerfTrace.Stage("resolve.element"))
            element = a.ElementId != null ? _registry.EnsureAlive(a.ElementId) : null;
        var window = element?.Hwnd is { } h ? _windows.GetTopLevelWindow(h) : null;
        AutoVerifier.PreState pre;
        using (PerfTrace.Stage("verify.pre"))
            pre = _verifier.Snapshot(element);

        ActionResult r;
        using (PerfTrace.Stage("executor"))
            r = _executor.Perform(intent, actx);
        var outcome = new StepOutcome(MapOutcome(r), r.Success, r.Method,
            r.ErrorMessage, (int)sw.ElapsedMilliseconds, r.Evidence,
            Delta: r.Delta);

        // executor success but no explicit VerifySpec → try auto-verification
        if (outcome.Kind == OutcomeKind.Unverified)
        {
            StepOutcome? auto;
            using (PerfTrace.Stage("verify.auto"))
                auto = _verifier.Verify(a, pre, r, window, actx.Ct);
            if (auto != null)
                outcome = auto with
                { Method = r.Method, DurationMs = (int)sw.ElapsedMilliseconds,
                    Delta = r.Delta };
        }
        return outcome;
    }

    /// <summary>Schema + target + frame-freshness validation → ActionIntent.
    /// Returns null when rejected; <paramref name="rejectKind"/> classifies why.</summary>
    private ActionIntent? ToIntent(AgentAction a, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        error = null;
        rejectKind = OutcomeKind.Malformed;
        TargetRef? target = a.Kind switch
        {
            AgentActionKind.Click or AgentActionKind.RightClick
                or AgentActionKind.DoubleClick or AgentActionKind.Invoke
                or AgentActionKind.Toggle or AgentActionKind.Select
                or AgentActionKind.SetValue or AgentActionKind.Scroll
                or AgentActionKind.Hover
                or AgentActionKind.ScrollIntoView
                or AgentActionKind.Drag => ResolveTarget(a, ctx, out error, out rejectKind),
            AgentActionKind.FocusElement
                => a.ElementId != null ? ResolveElement(a.ElementId, out error) : null,
            AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey
                => a.ElementId != null ? ResolveElement(a.ElementId, out error) : null,
            _ => null,
        };
        if (error != null) return null;

        var kind = a.Kind switch
        {
            AgentActionKind.Click => ActionKind.Click,
            AgentActionKind.RightClick => ActionKind.RightClick,
            AgentActionKind.DoubleClick => ActionKind.DoubleClick,
            AgentActionKind.Invoke => ActionKind.Invoke,
            AgentActionKind.Toggle => ActionKind.Toggle,
            AgentActionKind.Select => ActionKind.Select,
            AgentActionKind.ScrollIntoView => ActionKind.ScrollIntoView,
            AgentActionKind.SetValue => ActionKind.SetValue,
            AgentActionKind.Scroll => ActionKind.Scroll,
            AgentActionKind.Drag => ActionKind.Drag,
            AgentActionKind.Hover => ActionKind.MouseMove,
            AgentActionKind.FocusElement => ActionKind.FocusElement,
            AgentActionKind.Type => ActionKind.TypeText,
            AgentActionKind.Key => ActionKind.KeyPress,
            AgentActionKind.Hotkey => ActionKind.Hotkey,
            _ => (ActionKind?)null,
        };
        if (kind == null) { error = $"unhandled action kind {a.Kind}"; return null; }

        if (target == null && kind is not (ActionKind.TypeText or ActionKind.KeyPress
            or ActionKind.Hotkey))
        { error = $"{a.Kind} requires elementId or image point"; return null; }

        var args = new Dictionary<string, object?>();
        switch (kind.Value)
        {
            case ActionKind.SetValue or ActionKind.TypeText:
                if (a.Text == null) { error = $"{a.Kind} requires text"; return null; }
                args["text"] = a.Text; break;
            case ActionKind.KeyPress or ActionKind.Hotkey:
                if (!Enum.TryParse<KeyCode>(a.Key, true, out var key))
                { error = $"key '{a.Key}' unknown"; return null; }
                args["key"] = key.ToString();
                if (a.Modifiers != null) args["modifiers"] = a.Modifiers;
                if (a.Count is > 1) args["count"] = a.Count.Value;
                break;
            case ActionKind.Scroll:
                args["delta"] = a.Delta ?? -120; break;
            case ActionKind.Drag:
            {
                if (a.To == null) { error = "drag requires destination"; return null; }
                var to = ImageToDesktop(a.To, ctx, out error, out rejectKind);
                if (error != null) return null;
                args["to"] = new[] { to.X, to.Y };
                break;
            }
        }
        return new ActionIntent(kind.Value, target ?? TargetRef.At(0, 0)
            with { }, args);
    }

    /// <summary>Element id → TargetRef; unknown id is malformed, stale is
    /// handled inside the executor's EnsureAlive path.</summary>
    private TargetRef? ResolveElement(string id, out string? error)
    {
        if (_registry.Get(id) == null)
        { error = $"unknown element id '{id}' — observe first"; return null; }
        error = null;
        return TargetRef.Element(id);
    }

    private TargetRef? ResolveTarget(AgentAction a, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        rejectKind = OutcomeKind.Malformed;
        if (a.ElementId != null) return ResolveElement(a.ElementId, out error);
        if (a.Point != null)
        {
            var p = ImageToDesktop(a.Point, ctx, out error, out rejectKind);
            return error == null ? TargetRef.At(p.X, p.Y) : null;
        }
        error = $"{a.Kind} requires elementId or image point";
        return null;
    }

    /// <summary>The race-condition guard. An image point is only valid when:
    /// the frame is attached to THIS observation, it belongs to THIS
    /// observation id, it is not too old, and the captured window still
    /// exists with materially unchanged geometry.</summary>
    private (int X, int Y) ImageToDesktop(ImagePoint p, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        rejectKind = OutcomeKind.Malformed;
        if (p.FrameId == 0)
        {
            // Direct screen/window coordinate (e.g. from OCR bounds or explicit coordinates without a video frame)
            error = null;
            return (p.X, p.Y);
        }
        if (ctx.Frames.Count == 0)
        { error = "observation carried no frame — coordinates unusable"; return default; }
        if (!ctx.Frames.TryGetValue(p.FrameId, out var fref))
        {
            rejectKind = OutcomeKind.StaleFrame;
            error = $"frame {p.FrameId} is not part of the current observation; re-observe";
            return default;
        }
        if (p.ObservationId != 0)
        {
            var obsOk = ctx.ValidObservationIds != null
                ? ctx.ValidObservationIds.Contains(p.ObservationId)
                : p.ObservationId == ctx.ObservationId;
            if (!obsOk)
            {
                rejectKind = OutcomeKind.StaleFrame;
                error = $"point belongs to observation {p.ObservationId}, " +
                        $"which is not a current observation of this session";
                return default;
            }
        }
        if (fref.Meta.At != default &&
            (DateTimeOffset.Now - fref.Meta.At).TotalMilliseconds > ctx.MaxFrameAgeMs)
        {
            rejectKind = OutcomeKind.StaleFrame;
            error = $"frame {p.FrameId} is older than {ctx.MaxFrameAgeMs}ms; re-observe";
            return default;
        }
        if (fref.Meta.Hwnd is { } hw)
        {
            var win = _windows.GetWindow(hw);
            if (win == null)
            {
                rejectKind = OutcomeKind.WindowLost;
                error = $"frame's window 0x{hw:X} is gone";
                return default;
            }
            var d = win.Bounds;
            var s = fref.Meta.WindowBounds ?? fref.Meta.SourceRect;
            if (Math.Abs(d.X - s.X) > 4 || Math.Abs(d.Y - s.Y) > 4 ||
                Math.Abs(d.Width - s.Width) > 4 || Math.Abs(d.Height - s.Height) > 4)
            {
                rejectKind = OutcomeKind.WindowGeometryChanged;
                error = $"window moved/resized since frame {p.FrameId}: " +
                        $"{s.Width}x{s.Height}@{s.X},{s.Y} → {d.Width}x{d.Height}@{d.X},{d.Y}";
                return default;
            }
        }
        error = null;
        return fref.Raw.Transform.ImageToDesktop(p.X, p.Y);
    }

    private static StepOutcome Malformed(System.Diagnostics.Stopwatch sw, string why) =>
        new(OutcomeKind.Malformed, false, "validation", why, (int)sw.ElapsedMilliseconds);

    private static StepOutcome Done(System.Diagnostics.Stopwatch sw, OutcomeKind k,
        string method, string? detail, bool success = true) =>
        new(k, success, method, detail, (int)sw.ElapsedMilliseconds);

    public static OutcomeKind MapOutcome(ActionResult r) => r switch
    {
        { Success: true, Verification: VerifyResult.Verified } => OutcomeKind.Verified,
        { Success: true } => OutcomeKind.Unverified,
        { Error: ErrorCode.Stale or ErrorCode.StaleUnresolvable } => OutcomeKind.Stale,
        { Error: ErrorCode.NotFound } => OutcomeKind.TargetNotFound,
        { Error: ErrorCode.TargetElevated } => OutcomeKind.TargetElevated,
        { Error: ErrorCode.SecureDesktopActive } => OutcomeKind.SecureDesktop,
        { Error: ErrorCode.Timeout } => OutcomeKind.Timeout,
        { Error: ErrorCode.InputBlocked } => OutcomeKind.InputRejected,
        { Error: ErrorCode.CaptureFailed } => OutcomeKind.CaptureUnavailable,
        { Error: ErrorCode.PolicyDenied } => OutcomeKind.PolicyDenied,
        { Error: ErrorCode.ConfirmationRequired } => OutcomeKind.ConfirmationDenied,
        { Error: ErrorCode.Busy } => OutcomeKind.ConcurrencyConflict,
        _ => OutcomeKind.Failed,
    };
}

/// <summary>Outcome → loop steering. Recovery is decided here, not by the model.</summary>
public static class RecoveryPolicy
{
    public static RecoveryHint For(OutcomeKind k) => k switch
    {
        OutcomeKind.Verified or OutcomeKind.ObservedChange or OutcomeKind.Unverified => RecoveryHint.ReObserve,
        OutcomeKind.Stale => RecoveryHint.ReResolve,
        OutcomeKind.StaleFrame or OutcomeKind.WindowGeometryChanged
            or OutcomeKind.TargetNotFound => RecoveryHint.ReObserve,
        OutcomeKind.WindowLost => RecoveryHint.FindWindow,
        OutcomeKind.NoProgress => RecoveryHint.ReObserve,
        OutcomeKind.TargetElevated or OutcomeKind.SecureDesktop
            or OutcomeKind.PolicyDenied => RecoveryHint.Abort,
        OutcomeKind.InterruptedByDialog => RecoveryHint.ReObserve,
        OutcomeKind.TargetOnDifferentScreen => RecoveryHint.ReObserve,
        OutcomeKind.TargetOffscreen => RecoveryHint.ReObserve,
        _ => RecoveryHint.ReportToModel,
    };

    /// <summary>Outcomes that end the run regardless of retry budget.</summary>
    public static bool IsFatal(OutcomeKind k) =>
        For(k) == RecoveryHint.Abort;
}
