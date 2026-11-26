using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// The single action pipeline: resolve target → guards (secure desktop,
/// integrity) → safety policy → strongest backend strategy → verify →
/// telemetry. Fallback transitions are recorded in Attempts.
/// </summary>
public sealed partial class Executor
{
    private readonly IWindowService _windows;
    private readonly IIntegrityService _integrity;

    private readonly IInputService _input;
    private readonly ICaptureService _capture;

    private readonly IReadOnlyList<IElementBackend> _backends;
    private readonly ElementRegistry _registry;

    private readonly SafetyPolicy _policy;
    private readonly Verifier _verifier;

    private readonly ITelemetrySink? _telemetry;
    private readonly ComputerControlActivityService? _activity;

    private readonly ActionDeltaCollector _delta;
    /// <summary>Message-based ("silent") input backend — null on runtimes
    /// that have none; silent intents then fail NotSupported instead of
    /// falling back to SendInput.</summary>
    private readonly ISilentInputService? _silent;

    private int _seq;
    private long _mutationVersion;

    /// <summary>Monotonic count of performed actions. Semantic snapshot
    /// caches compare against it so a mutating action invalidates them
    /// deterministically — async UIA events can lose the race.</summary>
    public long MutationVersion => Volatile.Read(ref _mutationVersion);

    /// <summary>Desktop changed without a Perform (e.g. app launch).</summary>
    public void NoteMutation() => Interlocked.Increment(ref _mutationVersion);

    public Executor(
        IWindowService windows,
        IIntegrityService integrity,
        IInputService input,
        ICaptureService capture,
        IEnumerable<IElementBackend> backends,
        ElementRegistry registry,
        SafetyPolicy policy,
        ITelemetrySink? telemetry = null,
        ComputerControlActivityService? activity = null,
        IEventWaiter? eventWaiter = null,
        ISilentInputService? silent = null)
    {
        _windows = windows;
        _integrity = integrity;
        _input = input;
        _capture = capture;
        _backends = backends.ToList();
        _registry = registry;
        _policy = policy;
        _telemetry = telemetry;
        _activity = activity;
        _verifier = new Verifier(windows, capture);
        _delta = new ActionDeltaCollector(windows, eventWaiter, _backends);
        _silent = silent;
    }

    /// <summary>Public entry — wraps PerformCore with pre/post desktop
    /// delta collection so EVERY result (guard rejections included)
    /// carries what the action changed. Delta gathering lives here, not in
    /// the step engine, so MCP tools, run steps and agent steps all get it.</summary>
    public ActionResult Perform(ActionIntent intent, ActionContext? ctx = null)
    {
        // every action — MCP tool, run step, agent step, CLI — marks the
        // computer "actively used" for the screen-control indicator
        using var _activityLease = _activity?.BeginActivity();
        // bump even before guards resolve: a partially-failed action may
        // still have mutated the desktop, and stale snapshots must never
        // serve a post-action verification
        Interlocked.Increment(ref _mutationVersion);
        var deltaScope = _delta.Begin();
        var r = PerformCore(intent, ctx, deltaScope);
        if (r.Delta != null) return r; // act path already completed the scope
        // guard/policy rejections and mid-act exceptions land here — an
        // exception can still have partially mutated the desktop, so only
        // "guard"/"policy" methods (pre-act exits) skip the settle wait
        return r with { Delta = deltaScope.Complete(r, target: null,
            settle: r.Method is not ("guard" or "policy")) };
    }

    private ActionResult PerformCore(ActionIntent intent, ActionContext? ctx,
        ActionDeltaCollector.Scope deltaScope)
    {
        var sw = Stopwatch.StartNew();
        var attempts = new List<Attempt>();
        string? elementId = null;
        string targetDesc = "";
        var ct = ctx?.Ct ?? default;

        try
        {
            ct.ThrowIfCancellationRequested();
            // --- guard: a latched panic stop denies every mutating action in
            // every process — including one-shot CLI invocations that never
            // registered with the MCP-layer EmergencyControl. The marker is
            // cleared only by the local user's resume hotkey/tray, never by
            // automation.
            if (EmergencyGate.IsStopped)
                return Finish(false, null, "guard", attempts, VerifyResult.NotRequested,
                    sw, ErrorCode.Disabled,
                    "computer control stopped by the local emergency hotkey; only the local user can resume");
            // --- guard: secure desktop wins over everything ---
            bool secureDesktop;
            using (PerfTrace.Stage("guard.secureDesktop"))
                secureDesktop = _integrity.IsSecureDesktopActive();
            if (secureDesktop)
                return Finish(false, null, "guard", attempts, VerifyResult.NotRequested,
                    sw, ErrorCode.SecureDesktopActive,
                    "secure desktop active (UAC/lock/Ctrl+Alt+Del) — cannot observe or act");

            // --- resolve target ---
            UiElement? element = null;
            (int X, int Y)? point = null;
            WindowInfo? window = null;

            if (intent.Target.ElementId is { } id)
            {
                elementId = id;
                using (PerfTrace.Stage("resolve.element"))
                {
                    element = _registry.EnsureAlive(id)
                        ?? throw new InbriskException(ErrorCode.StaleUnresolvable,
                            $"element {id} is stale and could not be re-resolved");
                }
                point = element.Center;
                // element.Hwnd can be null for hwnd-less subtree elements (WPF
                // controls report zero native window handles); the element's
                // re-resolve recipe still records the OWNING top-level window,
                // which is what the policy/verify layers actually need.
                if ((element.Hwnd ?? element.Handle.Recipe.Hwnd) is { } h)
                    window = _windows.GetTopLevelWindow(h);
                targetDesc = $"{id} ({element.Role} '{element.Name}')";
            }
            else if (intent.Target.Selector is { } spec)
            {
                IReadOnlyList<UiElement> found;
                using (PerfTrace.Stage("resolve.find"))
                    found = Find(spec, ct);
                element = found.FirstOrDefault()
                    ?? throw new InbriskException(ErrorCode.NotFound, "no element matched selector");
                _registry.Register(found);
                elementId = element.Id;
                point = element.Center;
                // same owner-hwnd fallback as the elementId path above
                if ((element.Hwnd ?? element.Handle.Recipe.Hwnd) is { } h)
                    window = _windows.GetTopLevelWindow(h);
                targetDesc = $"{element.Id} ({element.Role} '{element.Name}')";
            }
            else if (intent.Target.Point is { } pt)
            {
                point = pt;
                targetDesc = $"({pt.X},{pt.Y})";
            }
            else if (intent.Target.Hwnd is { } wh)
            {
                window = _windows.GetWindow(wh)
                    ?? throw new InbriskException(ErrorCode.NotFound, $"window 0x{wh:X} not found");
                targetDesc = $"window '{window.Title}'";
            }
            else if (intent.Kind is ActionKind.KeyPress or ActionKind.Hotkey or ActionKind.TypeText
                or ActionKind.MouseLook or ActionKind.MouseHold)
            {
                window = _windows.GetForegroundWindow();
                if (window != null) targetDesc = $"foreground window '{window.Title}'";
            }
            // keyboard input without a resolved element (point target, or
            // no target at all) is delivered to whatever has focus —
            // the policy/danger checks must evaluate THAT window
            if (window == null && element == null && intent.Kind is ActionKind.KeyPress
                or ActionKind.Hotkey or ActionKind.TypeText or ActionKind.MouseLook
                or ActionKind.MouseHold)
            {
                window = _windows.GetForegroundWindow();
                if (window != null && targetDesc == "")
                    targetDesc = $"foreground window '{window.Title}'";
            }

            // --- guard: integrity level ---
            bool elevated;
            using (PerfTrace.Stage("guard.integrity"))
                elevated = window != null &&
                    _integrity.CheckTarget(window.Hwnd) == IntegrityRelation.TargetElevated;
            if (elevated)
                return Finish(false, null, "guard", attempts, VerifyResult.NotRequested,
                    sw, ErrorCode.TargetElevated,
                    $"target '{window!.Title}' runs elevated — injected input would be silently dropped (UIPI)");

            // --- policy ---
            PolicyDecision decision;
            using (PerfTrace.Stage("policy"))
                decision = _policy.Evaluate(intent, element, window);
            var cls = decision.Class;
            // the reason is recorded as an attempt row so the machine-readable
            // trail says WHY the gate fired, not just that it did
            if (cls != SafetyClass.Safe)
                attempts.Add(new Attempt(BackendId.Win32,
                    $"policy:{decision.Code}", false, decision.Detail, TimeSpan.Zero));
            if (cls == SafetyClass.Deny)
                return Finish(false, null, "policy", attempts, VerifyResult.NotRequested,
                    sw, ErrorCode.PolicyDenied,
                    $"action denied by safety policy [{decision.Code}]: {decision.Detail}");
            if (cls == SafetyClass.Confirm)
            {
                // F04: a client tool call can never confirm itself — an
                // approval flag inside the request is self-approval, not
                // consent. A consumed server-issued grant for this exact
                // action+target always satisfies the gate: only in-process
                // code can mint the approval it carries.
                var approvalId = intent.Args?.TryGetValue("approvalId", out var apv) == true
                    ? apv?.ToString() : null;
                if (approvalId != null &&
                    _policy.TryConsumeConfirmation(intent, element, window, approvalId))
                {
                    attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                        $"approved by server grant {approvalId}", TimeSpan.Zero));
                }
                else if (_policy.IsDangerous(intent, element, window))
                {
                    // F04: dangerous classes (shell/terminal input, destructive
                    // close chords) are never satisfiable by AutoConfirm — a
                    // boolean the model supplies inside its own tool call is
                    // self-approval, not consent. Only a consent hook installed
                    // by local code may approve: LocalConsent (the reserved
                    // trusted-UI hook) or the run/policy Confirmer, neither of
                    // which is reachable through the MCP JSON-RPC request path.
                    var consent = _policy.LocalConsent ?? ctx?.Confirmer ?? _policy.Confirmer;
                    bool localOk;
                    try { localOk = consent?.Invoke(intent, element, window) == true; }
                    catch { localOk = false; }
                    if (!localOk)
                        return DenyConfirm($"dangerous action requires a LOCAL " +
                            $"confirmation the client cannot supply " +
                            $"[{decision.Code}] {decision.Detail} — approve on " +
                            "this machine via the Inbrisk control UI, then retry " +
                            "with args.approvalId (AutoConfirm/tool-call flags " +
                            "are ignored for dangerous classes)");
                    attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                        "approved by local consent", TimeSpan.Zero));
                }
                else
                {
                    // precedence, most specific first:
                    // 1. a run-scoped confirmer — an explicit per-run policy
                    //    is always more specific than any global setting;
                    // 2. operator AutoConfirm (INBRISK_AUTOCONFIRM / CLI
                    //    --yes parity) — the machine's operator opted this
                    //    instance into unattended CONFIRM-class work before
                    //    the server started; never implied by tool args;
                    // 3. the wired confirm channel (session gate) — approves
                    //    only a pending a LOCAL surface granted, else fails
                    //    closed.
                    var confirmer = ctx?.Confirmer;
                    if (confirmer != null)
                    {
                        if (confirmer(intent, element, window))
                            attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                                "approved by confirmer", TimeSpan.Zero));
                        else
                            return DenyConfirm($"action denied by run confirmer " +
                                $"[{decision.Code}] {decision.Detail} — issued " +
                                "approvalId for local override");
                    }
                    else if (_policy.AutoConfirm)
                        attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                            "approved by operator AutoConfirm", TimeSpan.Zero));
                    else if (_policy.Confirmer != null)
                    {
                        if (_policy.Confirmer(intent, element, window))
                            attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                                "approved by confirmer", TimeSpan.Zero));
                        else
                            return DenyConfirm($"action denied by run confirmer " +
                                $"[{decision.Code}] {decision.Detail} — issued " +
                                "approvalId for local override");
                    }
                    else
                        return DenyConfirm("action requires confirmation — " +
                            $"[{decision.Code}] {decision.Detail} — approve via " +
                            "the local Inbrisk control surface, then retry " +
                            "with args.approvalId (AutoConfirm=false)");
                }
            }

            // A CONFIRM action that reached no approver still gets a
            // server-issued challenge so the local control surface can
            // decide it — the denial carries the id the retry must echo.
            ActionResult DenyConfirm(string why)
            {
                var ch = _policy.IssueConfirmation(intent, element, window);
                attempts.Add(new Attempt(BackendId.Win32, "confirm-challenge",
                    false, $"approvalId={ch.ApprovalId} " +
                    $"expires {ch.ExpiresAt:HH:mm:ss}Z", TimeSpan.Zero));
                return Finish(false, null, "policy", attempts,
                    VerifyResult.NotRequested, sw, ErrorCode.ConfirmationRequired,
                    $"{why} (approvalId={ch.ApprovalId})");
            }

            ct.ThrowIfCancellationRequested();

            // --- act ---
            ActionResult result;
            using (PerfTrace.Stage("act"))
                result = Act(intent, element, point, window, attempts, ct);

            // --- verify ---
            // a silent action can arrive pre-verified (WM_GETTEXT /
            // BM_GETCHECK read-backs) — keep that verdict; every other
            // result reports NotRequested here and verifies as before
            var verify = result.Verification;
            VerifyEvidence? evidence = null;
            if (result.Success && intent.Verify is { } vs)
            {
                using (PerfTrace.Stage("verify"))
                {
                    if (vs.Kind == VerifyKind.ElementGone)
                    {
                        var backend = element != null ? _backends.FirstOrDefault(b => b.Id == element.Handle.Backend) : null;
                        var fresh = backend != null && element != null ? backend.ReResolve(element.Handle) : null;
                        if (fresh == null)
                        {
                            verify = VerifyResult.Verified;
                            evidence = new VerifyEvidence("ElementGone", Detail: "element no longer present after action (expected)");
                        }
                        else
                        {
                            verify = VerifyResult.Failed;
                            evidence = new VerifyEvidence("ElementStillPresent", Detail: "element still present after action (expected gone)");
                        }
                    }
                    else
                    {
                        verify = _verifier.Verify(vs, element, window);
                    }
                }
            }
            else if (result.Success && element != null &&
                verify != VerifyResult.Verified)
                using (PerfTrace.Stage("verify.post"))
                    (verify, evidence) = PostVerify(intent, element);

            var final = result with { Verification = verify, Attempts = attempts,
                Evidence = evidence ?? result.Evidence,
                Delta = deltaScope.Complete(result, element,
                    settle: result.Success || result.Attempts.Any(a => a.Success)) };
            if (verify == VerifyResult.Failed)
                final = final with { Success = false, Error = ErrorCode.Internal,
                    ErrorMessage = (final.ErrorMessage is { } em ? em + " — " : "") +
                        (evidence?.Detail ?? "post-action state check failed") };
            Emit(intent, targetDesc, elementId, final, sw, ctx);
            return final;
        }
        catch (OperationCanceledException)
        {
            var r = Finish(false, null, "executor", attempts, VerifyResult.NotRequested,
                sw, ErrorCode.Internal, "cancelled");
            Emit(intent, targetDesc, elementId, r, sw, ctx);
            return r;
        }
        catch (InbriskException e)
        {
            var r = Finish(false, null, "executor", attempts, VerifyResult.NotRequested,
                sw, e.Code, e.Message);
            Emit(intent, targetDesc, elementId, r, sw, ctx);
            return r;
        }
        catch (Exception e)
        {
            var r = Finish(false, null, "executor", attempts, VerifyResult.NotRequested,
                sw, ErrorCode.Internal, e.Message);
            Emit(intent, targetDesc, elementId, r, sw, ctx);
            return r;
        }
    }

    public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default)
    {
        foreach (var backend in _backends)
        {
            ct.ThrowIfCancellationRequested();
            var found = backend.Find(spec, ct);
            if (found.Count > 0) return found;
        }
        return Array.Empty<UiElement>();
    }

    public async Task<IReadOnlyList<UiElement>> FindAsync(FindSpec spec, CancellationToken ct = default)
    {
        foreach (var backend in _backends)
        {
            ct.ThrowIfCancellationRequested();
            var found = await backend.FindAsync(spec, ct).ConfigureAwait(false);
            if (found.Count > 0) return found;
        }
        return Array.Empty<UiElement>();
    }
}
