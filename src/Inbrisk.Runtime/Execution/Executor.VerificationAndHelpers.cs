using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed partial class Executor
{
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

    /// <summary>Shared targetability pre-check for every element-targeted
    /// action — the same guard Click applies. A disabled element can never
    /// take focus, so skipping this let TypeText/KeyPress/Hotkey dispatch
    /// keystrokes that leaked into whatever element actually held focus
    /// (wrong-target input). An offscreen element likewise cannot be
    /// focused for keyboard delivery; the caller must scroll it into view
    /// and re-resolve first.</summary>
    private ActionResult? GuardElementTargetable(ActionIntent intent,
        UiElement element, List<Attempt> attempts, Stopwatch sw)
    {
        if (element.Props.TryGetValue("enabled", out var isEn) && isEn is false or 0)
            return new ActionResult(false, null, "guard", attempts,
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Disabled,
                $"element {element.Id} ('{element.Name}') is disabled; cannot perform {intent.Kind}");
        return null;
    }

    /// <summary>Keyboard delivery requires a focusable target. Mirrors the
    /// offscreen rule Click applies to its coordinate fallback — focus
    /// cannot be moved to an element the provider reports offscreen.</summary>
    private ActionResult? GuardKeyboardTargetable(ActionIntent intent,
        UiElement element, List<Attempt> attempts, Stopwatch sw)
    {
        if (GuardElementTargetable(intent, element, attempts, sw) is { } d)
            return d;
        if (element.Props.TryGetValue("offscreen", out var isOff) && isOff is true or 1)
            return new ActionResult(false, null, "none", attempts,
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Unsupported,
                $"element {element.Id} is offscreen — scroll it into view, " +
                $"re-observe, then perform {intent.Kind} again");
        return null;
    }

    private void FocusElement(UiElement element, List<Attempt> attempts, CancellationToken ct = default)
    {
        var backend = _backends.First(b => b.Id == element.Handle.Backend);
        var r = backend.PerformNative(element,
            new ActionIntent(ActionKind.FocusElement, TargetRef.Element(element.Id)), ct);
        if (r == null)
        {
            attempts.Add(new Attempt(element.Handle.Backend, "focus-element",
                true, "no native focus path — input goes to the focused control",
                TimeSpan.Zero));
            return;
        }
        attempts.AddRange(r.Attempts);
        // a failed focus move must abort the action — typing into an
        // unfocused element lands the keystrokes on whatever holds focus
        if (!r.Success)
            throw new InbriskException(ErrorCode.Internal,
                $"focus move to element {element.Id} failed " +
                $"({r.ErrorMessage ?? "backend declined"}) — refusing to send " +
                "input to an unfocused target");
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
