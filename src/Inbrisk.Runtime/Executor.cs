using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// The single action pipeline: resolve target → guards (secure desktop,
/// integrity) → safety policy → strongest backend strategy → verify →
/// telemetry. Fallback transitions are recorded in Attempts.
/// </summary>
public sealed class Executor
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
        ComputerControlActivityService? activity = null)
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
    }

    public ActionResult Perform(ActionIntent intent, ActionContext? ctx = null)
    {
        // every action — MCP tool, run step, agent step, CLI — marks the
        // computer "actively used" for the screen-control indicator
        using var _activityLease = _activity?.BeginActivity();
        // bump even before guards resolve: a partially-failed action may
        // still have mutated the desktop, and stale snapshots must never
        // serve a post-action verification
        Interlocked.Increment(ref _mutationVersion);
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
                if (element.Hwnd is { } h) window = _windows.GetWindow(h);
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
                if (element.Hwnd is { } h) window = _windows.GetWindow(h);
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
            else if (intent.Kind is ActionKind.KeyPress or ActionKind.Hotkey or ActionKind.TypeText)
            {
                window = _windows.GetForegroundWindow();
                if (window != null) targetDesc = $"foreground window '{window.Title}'";
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
            SafetyClass cls;
            using (PerfTrace.Stage("policy"))
                cls = _policy.Classify(intent, element, window);
            if (cls == SafetyClass.Deny)
                return Finish(false, null, "policy", attempts, VerifyResult.NotRequested,
                    sw, ErrorCode.PolicyDenied, "action denied by safety policy");
            if (cls == SafetyClass.Confirm)
            {
                // a run-scoped confirmer wins over global AutoConfirm — an
                // explicit per-run policy is always more specific
                var confirmer = ctx?.Confirmer ?? _policy.Confirmer;
                if (confirmer == null)
                {
                    if (!_policy.AutoConfirm)
                        return Finish(false, null, "policy", attempts, VerifyResult.NotRequested,
                            sw, ErrorCode.ConfirmationRequired,
                            "action requires confirmation (AutoConfirm=false)");
                }
                else if (confirmer(intent, element, window))
                    attempts.Add(new Attempt(BackendId.Win32, "confirm", true,
                        "approved by confirmer", TimeSpan.Zero));
                else
                    return Finish(false, null, "policy", attempts, VerifyResult.NotRequested,
                        sw, ErrorCode.ConfirmationRequired,
                        "action denied by run confirmer");
            }

            ct.ThrowIfCancellationRequested();

            // --- act ---
            ActionResult result;
            using (PerfTrace.Stage("act"))
                result = Act(intent, element, point, window, attempts, ct);

            // --- verify ---
            var verify = VerifyResult.NotRequested;
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
            else if (result.Success && element != null)
                using (PerfTrace.Stage("verify.post"))
                    (verify, evidence) = PostVerify(intent, element);

            var final = result with { Verification = verify, Attempts = attempts,
                Evidence = evidence ?? result.Evidence };
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

    private ActionResult Act(ActionIntent intent, UiElement? element,
        (int X, int Y)? point, WindowInfo? window, List<Attempt> attempts,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        switch (intent.Kind)
        {
            // ---- element semantic actions: backend first, coordinate fallback ----
            case ActionKind.Click or ActionKind.Invoke or ActionKind.SetValue
                or ActionKind.Toggle or ActionKind.Select or ActionKind.Expand
                or ActionKind.Collapse or ActionKind.FocusElement or ActionKind.ScrollIntoView
                when element != null:
            {
                if (element.Props.TryGetValue("enabled", out var isEn) && isEn is false or 0 && intent.Kind != ActionKind.FocusElement)
                {
                    return new ActionResult(false, null, "guard", attempts,
                        VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Disabled,
                        $"element {element.Id} ('{element.Name}') is disabled; cannot perform {intent.Kind}");
                }
                var backend = _backends.First(b => b.Id == element.Handle.Backend);
                var native = backend.PerformNative(element, intent, ct);
                if (native != null)
                {
                    attempts.AddRange(native.Attempts);
                    if (native.Success) return native;
                    // disabled/stale → try re-resolve once, else fall through to click
                    if (native.Error == ErrorCode.Stale || native.Error == ErrorCode.Disabled)
                        return native;
                }
                if (intent.Kind != ActionKind.Click && intent.Kind != ActionKind.Invoke)
                    return new ActionResult(false, null, "none", attempts,
                        VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Unsupported,
                        $"no backend can {intent.Kind} on {element.Id}");
                if (element.Props.TryGetValue("offscreen", out var isOff) && isOff is true or 1)
                {
                    return new ActionResult(false, null, "none", attempts,
                        VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Unsupported,
                        $"element {element.Id} is offscreen and cannot be clicked via coordinates");
                }
                // coordinate fallback: foreground the owning window first
                if (window != null) GuardFocus(window, attempts);
                var c = element.Center;
                attempts.Add(Do(() => _input.Click(c.X, c.Y), BackendId.Win32, "SendInput.click"));
                return Ok(BackendId.Win32, "SendInput.click", attempts, sw);
            }

            case ActionKind.Click or ActionKind.RightClick or ActionKind.DoubleClick
                or ActionKind.MiddleClick when point is { } p:
            {
                var button = intent.Kind == ActionKind.RightClick ? MouseButton.Right
                    : intent.Kind == ActionKind.MiddleClick ? MouseButton.Middle
                    : MouseButton.Left;
                var count = intent.Kind == ActionKind.DoubleClick ? 2 : 1;
                attempts.Add(Do(() => _input.Click(p.X, p.Y, button, count),
                    BackendId.Win32, $"SendInput.{intent.Kind.ToString().ToLower()}"));
                return Ok(BackendId.Win32, $"SendInput.{intent.Kind.ToString().ToLower()}", attempts, sw);
            }

            case ActionKind.Drag when element != null || point != null:
            {
                var from = element?.Center ?? point!.Value;
                var to = ParsePoint(intent.Args, "to") ?? throw new InbriskException(
                    ErrorCode.Unsupported, "drag requires args.to={x,y}");
                attempts.Add(Do(() => _input.Drag(from.X, from.Y, to.X, to.Y, ct: ct),
                    BackendId.Win32, "SendInput.drag"));
                return Ok(BackendId.Win32, "SendInput.drag", attempts, sw);
            }

            case ActionKind.Scroll:
            {
                var p = point ?? element?.Center
                    ?? throw new InbriskException(ErrorCode.Unsupported, "scroll needs a point or element");
                var delta = Convert.ToInt32(intent.Args?.TryGetValue("delta", out var d) == true ? d : -120);
                attempts.Add(Do(() => _input.Scroll(p.X, p.Y, delta), BackendId.Win32, "SendInput.scroll"));
                return Ok(BackendId.Win32, "SendInput.scroll", attempts, sw);
            }

            case ActionKind.MouseMove:
            {
                var p = point ?? element?.Center
                    ?? throw new InbriskException(ErrorCode.Unsupported, "hover needs a point or element");
                // hovering an occluded point would hit the occluder — bring
                // the owning window forward first
                if (window != null) GuardFocus(window, attempts);
                attempts.Add(Do(() => _input.MoveMouse(p.X, p.Y), BackendId.Win32, "SendInput.move"));
                return Ok(BackendId.Win32, "SendInput.move", attempts, sw);
            }

            case ActionKind.TypeText:
            {
                if (window != null) GuardFocus(window, attempts);
                if (element != null) FocusElement(element, attempts, ct);
                else EnsureHoverInWindow(window, attempts);
                var text = intent.Args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "";
                attempts.Add(Do(() => _input.TypeText(text), BackendId.Win32, "SendInput.type"));
                return Ok(BackendId.Win32, "SendInput.type", attempts, sw);
            }

            case ActionKind.KeyPress:
            {
                if (window != null) GuardFocus(window, attempts);
                if (element != null) FocusElement(element, attempts);
                else EnsureHoverInWindow(window, attempts);
                var key = ParseKey(intent.Args);
                var count = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("count", out var c) == true ? c : 1), 1, 200);
                attempts.Add(Do(() =>
                {
                    for (var i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        _input.KeyPress(key);
                        if (i + 1 < count) ct.WaitHandle.WaitOne(12); // avoid queue overrun
                    }
                }, BackendId.Win32, "SendInput.key"));
                return Ok(BackendId.Win32, "SendInput.key", attempts, sw);
            }

            case ActionKind.Hotkey:
            {
                if (window != null) GuardFocus(window, attempts);
                if (element != null) FocusElement(element, attempts);
                else EnsureHoverInWindow(window, attempts);
                var mods = ParseKeys(intent.Args, "modifiers");
                var key = ParseKey(intent.Args);
                attempts.Add(Do(() => _input.Hotkey(mods, key), BackendId.Win32, "SendInput.hotkey"));
                return Ok(BackendId.Win32, "SendInput.hotkey", attempts, sw);
            }

            case ActionKind.FocusWindow when window != null:
            {
                var h = window.Hwnd;
                attempts.Add(Do(() =>
                {
                    if (!_windows.FocusWindow(h))
                        throw new InbriskException(ErrorCode.Internal, "SetForegroundWindow failed");
                }, BackendId.Win32, "SetForegroundWindow"));
                return Ok(BackendId.Win32, "SetForegroundWindow", attempts, sw);
            }

            default:
                return new ActionResult(false, null, "none", attempts,
                    VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Unsupported,
                    $"unsupported intent {intent.Kind} for target");
        }
    }

    /// <summary>
    /// Default post-action state check for element-targeted actions that
    /// carry no explicit VerifySpec. A native "SUCCESS" only means the call
    /// was dispatched — providers (CEF/LegacyIAccessible in particular)
    /// routinely report success without the state ever changing. Re-reads
    /// the target element (bounded: ~3 polls over ~500ms) and compares the
    /// props the action was supposed to mutate. Verified carries evidence;
    /// an unchanged required postcondition is a FAILED action, not a quiet
    /// success — Click/Invoke stay honest Unverified when nothing readable
    /// changed.
    /// </summary>
    private (VerifyResult, VerifyEvidence?) PostVerify(ActionIntent intent, UiElement element)
    {
        // Click and Invoke have no required property-mutation postcondition;
        // verification is delegated to AutoVerifier (window/event/hierarchy checks)
        // to avoid redundant ReResolve COM roundtrips.
        if (intent.Kind is ActionKind.Click or ActionKind.Invoke)
            return (VerifyResult.Unverified, null);

        if (intent.Kind is not (ActionKind.Toggle or ActionKind.SetValue or ActionKind.Select))
            return (VerifyResult.NotRequested, null);
        var backend = _backends.FirstOrDefault(b => b.Id == element.Handle.Backend);
        if (backend == null) return (VerifyResult.Unverified, null);

        UiElement? lastFresh = element;
        var maxAttempts = 3;
        for (var i = 0; i < maxAttempts; i++)
        {
            UiElement? fresh;
            try { fresh = backend.ReResolve(element.Handle); }
            catch { return (VerifyResult.Unverified, null); }
            lastFresh = fresh ?? lastFresh;
            if (fresh == null)
            {
                return (VerifyResult.Unverified,
                    new VerifyEvidence("ElementNotReResolved",
                        Detail: "target element was no longer found after action — cannot independently verify state"));
            }
            switch (intent.Kind)
            {
                case ActionKind.Toggle:
                    if (Prop(fresh, "toggleState") is { } after &&
                        after != Prop(element, "toggleState"))
                        return (VerifyResult.Verified, new VerifyEvidence(
                            "PropertyChanged", Prop(element, "toggleState"),
                            after, "toggleState"));
                    break;
                case ActionKind.SetValue:
                    var want = intent.Args?.TryGetValue("text", out var t) == true
                        ? t?.ToString() : null;
                    if (want != null && Prop(fresh, "value") == want)
                        return (VerifyResult.Verified,
                            new VerifyEvidence("ValueReadback", want, want));
                    break;
                case ActionKind.Select:
                    if (fresh.Props.GetValueOrDefault("selected") is true)
                        return (VerifyResult.Verified,
                            new VerifyEvidence("PropertyChanged", "false", "true", "selected"));
                    break;
            }
            if (i < maxAttempts - 1)
                Thread.Sleep(60);
        }
        // element still readable and the required postcondition never met
        if (intent.Kind is ActionKind.Toggle)
            return (VerifyResult.Failed, new VerifyEvidence("PropertyChanged",
                "≠ " + Prop(element, "toggleState"), Prop(lastFresh!, "toggleState"),
                "toggleState unchanged after Toggle"));
        if (intent.Kind is ActionKind.Select)
            return (VerifyResult.Failed, new VerifyEvidence("PropertyChanged",
                "true", Prop(lastFresh!, "selected"), "selected still false after Select"));
        if (intent.Kind is ActionKind.SetValue)
            return (VerifyResult.Failed, new VerifyEvidence("ValueReadback",
                intent.Args?.TryGetValue("text", out var tv) == true ? tv?.ToString() : null,
                Prop(lastFresh!, "value"), "value unchanged after SetValue"));
        return (VerifyResult.Unverified, null);
    }

    private static string? Prop(UiElement e, string name) =>
        e.Props.GetValueOrDefault(name)?.ToString();

    private void FocusElement(UiElement element, List<Attempt> attempts, CancellationToken ct = default)
    {
        var backend = _backends.First(b => b.Id == element.Handle.Backend);
        var r = backend.PerformNative(element,
            new ActionIntent(ActionKind.FocusElement, TargetRef.Element(element.Id)), ct);
        if (r != null) attempts.AddRange(r.Attempts);
    }

    private void GuardFocus(WindowInfo window, List<Attempt> attempts)
    {
        var fg = _windows.GetForegroundWindow();
        if (fg?.Hwnd == window.Hwnd) return;
        var sw = Stopwatch.StartNew();
        var ok = _windows.FocusWindow(window.Hwnd);
        attempts.Add(new Attempt(BackendId.Win32, "foreground-guard", ok,
            ok ? null : "SetForegroundWindow did not raise the target window",
            sw.Elapsed));
        // A failed foreground switch must abort the action: proceeding would
        // deliver coordinates/keystrokes to whatever window happens to be on
        // top — the classic wrong-target incident.
        if (!ok)
            throw new InbriskException(ErrorCode.Internal,
                $"foreground-guard failed for '{window.Title}' — refusing " +
                "to send input to an occluded target");
    }

    private static Attempt Do(Action act, BackendId backend, string method)
    {
        var sw = Stopwatch.StartNew();
        try { act(); return new Attempt(backend, method, true, null, sw.Elapsed); }
        catch (Exception e) { return new Attempt(backend, method, false, e.Message, sw.Elapsed); }
    }

    private static ActionResult Ok(BackendId backend, string method,
        List<Attempt> attempts, Stopwatch sw)
    {
        // The last attempt is authoritative: earlier attempts may record a
        // failed backend fallback (e.g. UIA Invoke declined → coordinate
        // click) that the final attempt recovered from. Reporting a stale
        // mid-fallback failure as the action result would mask a success.
        var last = attempts[^1];
        return new ActionResult(last.Success, backend, method, attempts,
            VerifyResult.NotRequested, sw.Elapsed,
            last.Success ? ErrorCode.None : ErrorCode.Internal, last.Error);
    }

    private ActionResult Finish(bool success, BackendId? backend, string method,
        List<Attempt> attempts, VerifyResult verify, Stopwatch sw,
        ErrorCode error, string? message)
    {
        var r = new ActionResult(success, backend, method, attempts, verify,
            sw.Elapsed, error, message);
        return r;
    }

    private void Emit(ActionIntent intent, string targetDesc, string? elementId,
        ActionResult r, Stopwatch sw, ActionContext? ctx)
    {
        _telemetry?.Emit(new ActionTelemetry(
            Interlocked.Increment(ref _seq), DateTimeOffset.Now, intent.Kind,
            targetDesc, elementId, r.BackendUsed, r.Method, r.Attempts,
            r.Verification, r.Success, r.Error, r.ErrorMessage, sw.Elapsed,
            ctx?.RunId, ctx?.StepId, ctx?.ActionId));
    }

    private static (int X, int Y)? ParsePoint(IReadOnlyDictionary<string, object?>? args, string key)
    {
        if (args?.TryGetValue(key, out var v) != true || v == null) return null;
        if (v is ValueTuple<int, int> p) return (p.Item1, p.Item2);
        if (v is int[] a && a.Length == 2) return (a[0], a[1]);
        return null;
    }

    private static KeyCode ParseKey(IReadOnlyDictionary<string, object?>? args) =>
        args?.TryGetValue("key", out var k) == true && Enum.TryParse<KeyCode>(k?.ToString(), true, out var kk)
            ? kk : throw new InbriskException(ErrorCode.Unsupported, "args.key required");

    private static List<KeyCode> ParseKeys(IReadOnlyDictionary<string, object?>? args, string key)
    {
        var list = new List<KeyCode>();
        if (args?.TryGetValue(key, out var v) == true && v is IEnumerable<object?> items)
            foreach (var i in items)
                if (Enum.TryParse<KeyCode>(i?.ToString(), true, out var kk)) list.Add(kk);
        return list;
    }

    private void EnsureHoverInWindow(WindowInfo? window, List<Attempt> attempts)
    {
        if (window == null) return;
        try
        {
            var curPos = _input.CursorPosition();
            var wb = window.Bounds;
            if (wb.Width > 100 && wb.Height > 100 &&
                (curPos.X < wb.X || curPos.X > wb.X + wb.Width ||
                 curPos.Y < wb.Y + 50 || curPos.Y > wb.Y + wb.Height))
            {
                var targetX = wb.X + wb.Width / 2;
                var targetY = wb.Y + Math.Max(60, wb.Height / 2);
                attempts.Add(Do(() => _input.MoveMouse(targetX, targetY), BackendId.Win32, "SendInput.hoverToFocus"));
            }
        }
        catch { }
    }
}
