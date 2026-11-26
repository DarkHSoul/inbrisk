using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed partial class Executor
{
    private ActionResult Act(ActionIntent intent, UiElement? element,
        (int X, int Y)? point, WindowInfo? window, List<Attempt> attempts,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        // ---- silent (background WM-message) delivery ----
        // Reached only after EVERY PerformCore guard passed — emergency
        // stop, secure desktop, integrity/UIPI, safety policy and the
        // confirm/consent gate. Silent actions differ solely in the final
        // input mechanism: window messages to the target's own hwnd instead
        // of synthesized pointer/keyboard input. The foreground/focus guard
        // is skipped BY DESIGN (that is the point of silent input — no
        // focus theft, works on unfocused/occluded windows), while element
        // liveness, the disabled-element guard, hwnd↔window containment and
        // the protected-window check still apply inside ActSilent. A kind
        // with no honest message-only equivalent fails Unsupported — the
        // silent path NEVER falls back to SendInput.
        if (intent.Silent)
            return ActSilent(intent, element, window, attempts, sw, ct);

        switch (intent.Kind)
        {
            // ---- element semantic actions: backend first, coordinate fallback ----
            case ActionKind.Click or ActionKind.Invoke or ActionKind.SetValue
                or ActionKind.Toggle or ActionKind.Select or ActionKind.Expand
                or ActionKind.Collapse or ActionKind.FocusElement or ActionKind.ScrollIntoView
                when element != null:
            {
                if (GuardElementTargetable(intent, element, attempts, sw) is { } denied)
                    return denied;
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
                // volatile-backend staleness guard: OCR/Vision elements are
                // frozen snapshots — ReResolve returns null and IsAlive is
                // always true, so the recorded Center never refreshes. When
                // the owning window has moved since capture, the stale point
                // is outside the window's live bounds and the click would
                // land on whatever now occupies those desktop coordinates.
                if (window != null &&
                    element.Handle.Backend is BackendId.Ocr or BackendId.Vision &&
                    !window.Bounds.Contains(element.Center.X, element.Center.Y))
                    return new ActionResult(false, null, "guard", attempts,
                        VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Stale,
                        $"element {element.Id} point is outside the live bounds " +
                        $"of '{window.Title}' — the window moved since the " +
                        "OCR/vision capture; re-run computer_find");
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
                // no resolved window to guard-focus — the click itself
                // raises whatever sits under the point. Record where the
                // foreground actually landed so a wrong-window hit is
                // visible in the outcome instead of silently ignored.
                long? fgBefore = null;
                try { fgBefore = _windows.GetForegroundWindow()?.Hwnd; }
                catch { }
                var method = $"SendInput.{intent.Kind.ToString().ToLower()}";
                var click = Do(() => _input.Click(p.X, p.Y, button, count),
                    BackendId.Win32, method);
                attempts.Add(click);
                if (click.Success)
                    try
                    {
                        var fgAfter = _windows.GetForegroundWindow();
                        attempts.Add(new Attempt(BackendId.Win32,
                            "foreground-report", true,
                            fgAfter?.Hwnd != fgBefore
                                ? $"foreground moved to '{fgAfter?.Title ?? "?"}' " +
                                  $"0x{fgAfter?.Hwnd ?? 0:X}"
                                : $"foreground unchanged " +
                                  $"('{fgAfter?.Title ?? "none"}')",
                            TimeSpan.Zero));
                    }
                    catch { }
                return new ActionResult(click.Success, BackendId.Win32, method,
                    attempts, VerifyResult.NotRequested, sw.Elapsed,
                    click.Success ? ErrorCode.None : ErrorCode.Internal, click.Error);
            }

            case ActionKind.Drag when element != null || point != null:
            {
                var from = element?.Center ?? point!.Value;
                var to = ParsePoint(intent.Args, "to") ?? throw new InbriskException(
                    ErrorCode.Unsupported, "drag requires args.to={x,y}");
                if (window != null) GuardFocus(window, attempts);
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
                if (element != null &&
                    GuardKeyboardTargetable(intent, element, attempts, sw) is { } typeDenied)
                    return typeDenied;
                if (window != null) GuardFocus(window, attempts);
                if (element != null) FocusElement(element, attempts, ct);
                else EnsureHoverInWindow(window, attempts);
                var text = intent.Args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "";
                attempts.Add(Do(() => _input.TypeText(text), BackendId.Win32, "SendInput.type"));
                return Ok(BackendId.Win32, "SendInput.type", attempts, sw);
            }

            case ActionKind.KeyPress:
            {
                if (element != null &&
                    GuardKeyboardTargetable(intent, element, attempts, sw) is { } keyDenied)
                    return keyDenied;
                if (window != null) GuardFocus(window, attempts);
                if (element != null) FocusElement(element, attempts);
                else EnsureHoverInWindow(window, attempts);
                var key = ParseKey(intent.Args);
                var count = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("count", out var c) == true ? c : 1), 1, 200);
                // holdMs > 0: key DOWN, wait, key UP — games read held state
                // per tick, so an instant tap barely registers. Bounded so a
                // plan can never hold input for long: ≤10 s per hold, ≤30 s
                // total. The key is always released, even on cancel.
                var holdMs = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("holdMs", out var hm) == true ? hm : 0),
                    0, 10_000);
                if (holdMs > 0)
                {
                    count = Math.Min(count, Math.Max(1, 30_000 / holdMs));
                    // extra keys held together with the main one (keys:"w+space")
                    var together = ParseKeys(intent.Args, "modifiers");
                    attempts.Add(Do(() =>
                    {
                        for (var i = 0; i < count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var downed = new List<KeyCode>();
                            try
                            {
                                foreach (var k in together) { _input.KeyDown(k); downed.Add(k); }
                                _input.KeyDown(key); downed.Add(key);
                                ct.WaitHandle.WaitOne(holdMs);
                            }
                            finally
                            {
                                // release everything pressed, in reverse, even on cancel
                                for (var j = downed.Count - 1; j >= 0; j--)
                                    try { _input.KeyUp(downed[j]); } catch { }
                            }
                            ct.ThrowIfCancellationRequested();
                            if (i + 1 < count) ct.WaitHandle.WaitOne(30);
                        }
                    }, BackendId.Win32, "SendInput.keyhold"));
                    return Ok(BackendId.Win32, "SendInput.keyhold", attempts, sw);
                }
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

            case ActionKind.MouseHold:
            {
                // button DOWN, wait, button UP - the pointer never moves.
                // Bounded (<=10 s) and always released, even on cancel.
                if (window != null) GuardFocus(window, attempts);
                var hold = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("holdMs", out var mh) == true ? mh : 0),
                    1, 10_000);
                var btn = (intent.Args?.TryGetValue("button", out var mb) == true ? mb?.ToString() : null)
                    ?.ToLowerInvariant() switch
                {
                    "right" => MouseButton.Right,
                    "middle" => MouseButton.Middle,
                    _ => MouseButton.Left,
                };
                // keys held for the same duration (keys:"space", keys:"w+space")
                var heldKeys = ParseKeys(intent.Args, "modifiers");
                if (intent.Args?.TryGetValue("key", out var hk) == true &&
                    Enum.TryParse<KeyCode>(hk?.ToString(), true, out var hkk))
                    heldKeys.Add(hkk);
                attempts.Add(Do(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var downed = new List<KeyCode>();
                    var mouseDown = false;
                    try
                    {
                        foreach (var k2 in heldKeys) { _input.KeyDown(k2); downed.Add(k2); }
                        _input.MouseDown(btn); mouseDown = true;
                        ct.WaitHandle.WaitOne(hold);
                    }
                    finally
                    {
                        // release everything pressed, in reverse, even on cancel
                        if (mouseDown) try { _input.MouseUp(btn); } catch { }
                        for (var j = downed.Count - 1; j >= 0; j--)
                            try { _input.KeyUp(downed[j]); } catch { }
                    }
                    ct.ThrowIfCancellationRequested();
                }, BackendId.Win32, "SendInput.mousehold"));
                return Ok(BackendId.Win32, "SendInput.mousehold", attempts, sw);
            }

            case ActionKind.MouseLook:
            {
                // relative pointer motion into the targeted (or foreground)
                // window — the focus guard keeps it from landing elsewhere
                if (window != null) GuardFocus(window, attempts);
                var dx = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("dx", out var ldx) == true ? ldx : 0),
                    -4000, 4000);
                var dy = Math.Clamp(
                    Convert.ToInt32(intent.Args?.TryGetValue("dy", out var ldy) == true ? ldy : 0),
                    -4000, 4000);
                attempts.Add(Do(() => _input.MoveMouseRelative(dx, dy),
                    BackendId.Win32, "SendInput.look"));
                return Ok(BackendId.Win32, "SendInput.look", attempts, sw);
            }

            case ActionKind.Hotkey:
            {
                if (element != null &&
                    GuardKeyboardTargetable(intent, element, attempts, sw) is { } hotDenied)
                    return hotDenied;
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

            case ActionKind.CloseWindow when window != null:
            {
                var h = window.Hwnd;
                // parity with the silent path and the tool-layer close gate:
                // never close a protected host/terminal/critical-session
                // window. Lifecycle provenance (agent-owned-only closes)
                // stays at the tool layer, which executor callers do not
                // reach.
                if (_windows.IsWindowProtected(h, out var closeProtect))
                    return new ActionResult(false, null, "guard", attempts,
                        VerifyResult.NotRequested, sw.Elapsed,
                        ErrorCode.PolicyDenied,
                        $"refusing to close protected window: {closeProtect}");
                attempts.Add(Do(() =>
                {
                    if (!_windows.CloseWindow(h))
                        throw new InbriskException(ErrorCode.Internal,
                            "WM_CLOSE post failed");
                }, BackendId.Win32, "wm_close"));
                return Ok(BackendId.Win32, "wm_close", attempts, sw);
            }

            default:
                return new ActionResult(false, null, "none", attempts,
                    VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Unsupported,
                    $"unsupported intent {intent.Kind} for target");
        }
    }

    /// <summary>
    /// Silent (background WM-message) actuation. Every PerformCore guard
    /// already ran — this method differs from <see cref="Act"/> ONLY in the
    /// delivery mechanism. Deliberately skipped: the foreground/focus guard
    /// (message delivery needs no focus — that is the feature) and the
    /// offscreen gate (a message reaches occluded/minimized targets just
    /// fine). Deliberately kept: element liveness, the disabled-element
    /// guard, hwnd↔window containment (never message an hwnd outside the
    /// resolved window's tree), process agreement, and the protected-window
    /// check. Unsupported kinds and unresolvable targets fail with
    /// <see cref="ErrorCode.Unsupported"/> — NEVER a SendInput fallback.
    /// </summary>
    private ActionResult ActSilent(ActionIntent intent, UiElement? element,
        WindowInfo? window, List<Attempt> attempts, Stopwatch sw,
        CancellationToken ct)
    {
        ActionResult NotSupported(string why) => new(false, null, "guard",
            attempts, VerifyResult.NotRequested, sw.Elapsed,
            ErrorCode.Unsupported,
            $"silent execution not supported for {intent.Kind} ({why}) " +
            "— retry with silent:false");

        // ---- routing table ----
        //   Click/Invoke/Toggle → control click (BM_CLICK / WM_COMMAND)
        //   TypeText/SetValue   → control text set (WM_SETTEXT/EM_REPLACESEL)
        //   CloseWindow         → WM_CLOSE posted to the window
        // everything else (DoubleClick, RightClick, MiddleClick, Drag,
        // Scroll, MouseMove, KeyPress, Hotkey, FocusWindow, FocusElement,
        // Select/Expand/Collapse/ScrollIntoView, Clipboard*) has no honest
        // message-only equivalent → NotSupported.
        var kind = intent.Kind;
        if (kind is not (ActionKind.Click or ActionKind.Invoke
                or ActionKind.Toggle or ActionKind.TypeText
                or ActionKind.SetValue or ActionKind.CloseWindow))
        {
            string? why = null;
            if (_silent != null && !_silent.IsSupported(kind, out var w) &&
                w != null)
                why = w;
            return NotSupported(why ?? "no message-only path exists for this kind");
        }
        if (_silent == null)
            return NotSupported("this runtime has no silent-input backend wired");
        ct.ThrowIfCancellationRequested();

        if (kind == ActionKind.CloseWindow)
        {
            if (window == null)
                return NotSupported("requires a window (hwnd) target");
            var liveWin = _windows.GetWindow(window.Hwnd);
            if (liveWin == null)
                return new ActionResult(false, null, "guard", attempts,
                    VerifyResult.NotRequested, sw.Elapsed, ErrorCode.NotFound,
                    $"window 0x{window.Hwnd:X} is gone");
            if (_windows.IsWindowProtected(liveWin.Hwnd, out var protectWhy))
                return new ActionResult(false, null, "guard", attempts,
                    VerifyResult.NotRequested, sw.Elapsed,
                    ErrorCode.PolicyDenied,
                    $"refusing silent close of protected window: {protectWhy}");
            return SilentResultOf(intent, attempts, sw,
                () => _silent.TryClose(liveWin.Hwnd));
        }

        // ---- element verbs: Click/Invoke/Toggle/TypeText/SetValue ----
        if (element == null)
            return NotSupported("requires an element target — coordinate " +
                "and foreground-window delivery are SendInput semantics");

        // disabled-element guard — identical to the SendInput path
        if (GuardElementTargetable(intent, element, attempts, sw) is { } denied)
            return denied;

        // Containment: the hwnd we message must live inside the resolved
        // window's tree — never message an arbitrary hwnd. element.Hwnd
        // (the UIA NativeWindowHandle) may be a child control; the resolved
        // window is its GA_ROOT top-level. HWND-less elements fall back to
        // the backend's bounded child scan inside the resolved window —
        // still contained by construction.
        long? windowHwnd = window?.Hwnd;
        if (element.Hwnd is { } eh)
        {
            var top = _windows.GetTopLevelWindow(eh);
            if (top == null)
                return new ActionResult(false, null, "guard", attempts,
                    VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Stale,
                    $"element {element.Id} hwnd 0x{eh:X} is no longer a live window");
            if (window != null && top.Hwnd != window.Hwnd)
                return new ActionResult(false, null, "guard", attempts,
                    VerifyResult.NotRequested, sw.Elapsed,
                    ErrorCode.PolicyDenied,
                    $"element {element.Id} hwnd resolves to top-level " +
                    $"0x{top.Hwnd:X}, outside the resolved window " +
                    $"0x{window.Hwnd:X} — refusing to message it");
            windowHwnd ??= top.Hwnd;
        }
        windowHwnd ??= element.Handle.Recipe.Hwnd;
        if (windowHwnd == null)
            return NotSupported("element has no hwnd and no owning window " +
                "to scope a control scan — HWND-less elements cannot be " +
                "driven by window messages");
        var owner = _windows.GetWindow(windowHwnd.Value);
        if (owner == null)
            return new ActionResult(false, null, "guard", attempts,
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Stale,
                $"owning window 0x{windowHwnd:X} is gone");

        // process agreement: a resolved element whose recorded pid differs
        // from its window's pid is a stale/mismatched binding
        if (element.Pid is { } ep && owner.Pid != 0 && ep != owner.Pid)
            return new ActionResult(false, null, "guard", attempts,
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.PolicyDenied,
                $"element {element.Id} records pid {ep} but its window " +
                $"belongs to pid {owner.Pid} — refusing silent delivery");

        // protected-window check: silent messages to the host/terminal or a
        // critical-session window refuse exactly like the input path
        if (_windows.IsWindowProtected(windowHwnd.Value, out var prot))
            return new ActionResult(false, null, "guard", attempts,
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.PolicyDenied,
                $"refusing silent {kind} on protected window " +
                $"0x{windowHwnd:X}: {prot}");

        var st = new SilentTargetRef(
            ElementHwnd: element.Hwnd,
            WindowHwnd: windowHwnd,
            AutomationId: element.Handle.Recipe.AutomationId,
            Name: element.Name,
            Bounds: element.Bounds);

        return kind switch
        {
            ActionKind.TypeText or ActionKind.SetValue =>
                SilentResultOf(intent, attempts, sw,
                    () => _silent.TrySetText(st,
                        intent.Args?.TryGetValue("text", out var t) == true
                            ? t?.ToString() ?? "" : "")),
            // Click, Invoke, Toggle — BM_CLICK both clicks push buttons and
            // toggles checkable ones (BM_GETCHECK read-back verifies)
            _ => SilentResultOf(intent, attempts, sw, () => _silent.TryClick(st)),
        };
    }

    /// <summary>Run one silent op, record the attempt and the
    /// <c>silent.action</c> perf event, and map the result honestly:
    /// backend-reported Unsupported stays Unsupported (the target can't be
    /// driven by messages — retry with silent:false), Timeout stays
    /// Timeout, and a read-back-verified success carries evidence.</summary>
    private ActionResult SilentResultOf(ActionIntent intent,
        List<Attempt> attempts, Stopwatch sw, Func<SilentInputResult> op)
    {
        var asw = Stopwatch.StartNew();
        SilentInputResult r;
        try { r = op(); }
        catch (Exception e)
        {
            attempts.Add(new Attempt(BackendId.Win32, "silent", false,
                e.Message, asw.Elapsed));
            PerfLog.Write(new
            {
                kind = "silent.action",
                action = intent.Kind.ToString(),
                method = "exception",
                ms = asw.ElapsedMilliseconds,
                verified = false,
                ok = false,
            });
            return new ActionResult(false, BackendId.Win32, "silent",
                attempts, VerifyResult.NotRequested, sw.Elapsed,
                ErrorCode.Internal, e.Message);
        }
        attempts.Add(new Attempt(BackendId.Win32, r.Method, r.Ok, r.Detail,
            asw.Elapsed));
        PerfLog.Write(new
        {
            kind = "silent.action",
            action = intent.Kind.ToString(),
            method = r.Method,
            ms = asw.ElapsedMilliseconds,
            verified = r.Verified,
            ok = r.Ok,
        });

        if (!r.Ok)
            return new ActionResult(false, BackendId.Win32, r.Method,
                attempts, VerifyResult.NotRequested, sw.Elapsed,
                r.Status == SilentInputStatus.Unsupported
                    ? ErrorCode.Unsupported
                    : r.Status == SilentInputStatus.Timeout
                        ? ErrorCode.Timeout
                        : ErrorCode.Internal,
                r.Status == SilentInputStatus.Unsupported
                    ? $"silent execution not supported for {intent.Kind} on " +
                      $"this target — {r.Detail} — retry with silent:false"
                    : r.Detail);

        return new ActionResult(true, BackendId.Win32, r.Method, attempts,
            r.Verified ? VerifyResult.Verified : VerifyResult.Unverified,
            sw.Elapsed, ErrorCode.None, r.Detail,
            Evidence: r.Verified
                ? new VerifyEvidence(r.Method, Detail: r.Detail)
                : null);
    }
}
