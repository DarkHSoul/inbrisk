using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed class AutoVerifierTelemetry
{
    private long _eventWakeCount;
    private long _fallbackPollCount;
    private long _propertyCheckCount;

    public long EventWakeCount => Interlocked.Read(ref _eventWakeCount);
    public long FallbackPollCount => Interlocked.Read(ref _fallbackPollCount);
    public long PropertyCheckCount => Interlocked.Read(ref _propertyCheckCount);

    public void IncEventWake() => Interlocked.Increment(ref _eventWakeCount);
    public void IncFallbackPoll() => Interlocked.Increment(ref _fallbackPollCount);
    public void IncPropertyCheck() => Interlocked.Increment(ref _propertyCheckCount);

    public void Reset()
    {
        Interlocked.Exchange(ref _eventWakeCount, 0);
        Interlocked.Exchange(ref _fallbackPollCount, 0);
        Interlocked.Exchange(ref _propertyCheckCount, 0);
    }
}

/// <summary>
/// Automatic post-action verification. For each canonical action that has a
/// reliably-readable postcondition, re-reads ONLY the target (no full-desktop
/// re-inspection) and produces Verified+evidence, Failed+evidence, or null
/// (→ honest Unverified). A false-positive Verified is worse than Unverified,
/// so every Verified carries machine-readable proof.
/// </summary>
public sealed class AutoVerifier
{
    private readonly ElementRegistry _registry;
    private readonly IReadOnlyList<IElementBackend> _backends;
    private readonly IWindowService _windows;
    private readonly RecentEventBuffer _events;

    public AutoVerifierTelemetry Telemetry { get; } = new();

    private const int PollMs = 150;
    private const int MaxPollMs = 900;

    private readonly IScopedSubscriptionManager? _subscriptionManager;

    public AutoVerifier(ElementRegistry registry, IReadOnlyList<IElementBackend> backends,
        IWindowService windows, RecentEventBuffer events,
        IScopedSubscriptionManager? subscriptionManager = null)
    {
        _registry = registry; _backends = backends;
        _windows = windows; _events = events;
        _subscriptionManager = subscriptionManager;
    }

    /// <summary>Pre-action state — taken before execution so Toggle/Click
    /// verification compares before vs after.</summary>
    public sealed record PreState(UiElement? Element, long? ForegroundHwnd, DateTimeOffset At);

    public PreState Snapshot(UiElement? element)
        => new(element, _windows.GetForegroundWindow()?.Hwnd, DateTimeOffset.Now);

    /// <summary>Returns a verified/failed outcome with evidence, or null when
    /// no reliable postcondition exists (caller keeps Unverified).</summary>
    public StepOutcome? Verify(AgentAction a, PreState pre, ActionResult r,
        WindowInfo? window, CancellationToken ct)
    {
        if (!r.Success) return null;
        switch (a.Kind)
        {
            case AgentActionKind.SetValue:
                return VerifyValue(pre, a.Text ?? "", ct);
            case AgentActionKind.Toggle:
                return VerifyToggle(pre, ct);
            case AgentActionKind.Select:
                return VerifySelected(pre, ct);
            case AgentActionKind.FocusWindow:
                return VerifyForeground(a.Hwnd ?? pre.Element?.Hwnd ?? window?.Hwnd ?? 0);
            case AgentActionKind.FocusElement:
                if (pre.Element == null || !pre.Element.Props.ContainsKey("focused"))
                    return null;
                return PollFor(pre.Element,
                    e => e.Props.GetValueOrDefault("focused") is true, ct)
                    is { } f ? Verified("PropertyChanged", null, "true", "focused")
                    : null;
            case AgentActionKind.Type:
                return VerifyType(pre, a.Text ?? "", ct);
            case AgentActionKind.Click or AgentActionKind.RightClick
                 or AgentActionKind.DoubleClick or AgentActionKind.Invoke:
                return VerifyActed(pre, window, ct);
            case AgentActionKind.Key or AgentActionKind.Hotkey
                 or AgentActionKind.Scroll or AgentActionKind.Drag:
                return AnySemanticEvent(pre, window)
                    is { } ev ? ObservedChange("SemanticEvent", actual: ev.Kind.ToString(),
                        detail: "window event observed after input action") : null;
            case AgentActionKind.ScrollIntoView:
                if (pre.Element == null) return null;
                var inView = PollFor(pre.Element, e =>
                    (!e.Bounds.IsEmpty && (e.Props.GetValueOrDefault("offscreen") is not true and not 1)), ct);
                return inView != null
                    ? Verified("ScrollIntoView", expected: "onscreen", actual: $"bounds:{inView.Bounds}")
                    : null;
            default:
                return null;
        }
    }

    // ---------------------------------------------------------- per kind

    /// <summary>Edit controls normalize newlines (RichEdit stores \r, \r\r\n); a
    /// write is faithful when the text matches modulo line endings.</summary>
    private static string? NormEol(string? s)
    {
        if (s == null) return null;
        return System.Text.RegularExpressions.Regex.Replace(s, @"\r\r\n|\r\n|\r", "\n").TrimEnd();
    }

    private StepOutcome? VerifyValue(PreState pre, string expected, CancellationToken ct)
    {
        var el = pre.Element;
        if (el == null) return null;
        if (!el.Props.ContainsKey("value")) return null; // unreadable target → honest
        var post = PollFor(el, e =>
            NormEol((string?)e.Props.GetValueOrDefault("value")) == NormEol(expected), ct);
        if (post != null)
            return Verified("ValueReadback", expected, (string?)post.Props.GetValueOrDefault("value"));
        var last = ReRead(el);
        // Element gone proves nothing about the value write — it can mean
        // the write was committed and the dialog closed, but it can just as
        // well mean the provider re-created the peer (identity churn), which
        // is exactly the silent-failure case. Honest outcome: Unverified.
        if (last == null) return null;
        var actual = (string?)last.Props.GetValueOrDefault("value");
        return Failed($"value readback mismatch: expected \"{expected}\", got \"{actual}\"",
            new VerifyEvidence("ValueReadback", expected, actual));
    }

    private StepOutcome? VerifyToggle(PreState pre, CancellationToken ct)
    {
        var el = pre.Element;
        if (el == null || el.Props.GetValueOrDefault("toggleState") is not string before)
            return null; // not a toggleable element → no postcondition
        var post = PollFor(el,
            e => (string?)e.Props.GetValueOrDefault("toggleState") != before, ct);
        if (post != null)
            return Verified("PropertyChanged", before,
                (string?)post.Props.GetValueOrDefault("toggleState"), "toggleState");
        var last = ReRead(el);
        // same honesty rule as VerifyValue: a vanished element does not
        // prove the toggle flipped — identity churn reads identically.
        if (last == null) return null;
        if (last.Props.GetValueOrDefault("toggleState") is not string after)
            return null; // pattern gone → can't judge
        return Failed($"toggleState unchanged (still {after})",
            new VerifyEvidence("PropertyChanged", "≠ " + before, after, "toggleState"));
    }

    private StepOutcome? VerifySelected(PreState pre, CancellationToken ct)
    {
        var el = pre.Element;
        if (el == null || !el.Props.ContainsKey("selected")) return null;
        var post = PollFor(el, e => e.Props.GetValueOrDefault("selected") is true, ct);
        if (post != null)
            return Verified("PropertyChanged",
                pre.Element!.Props.GetValueOrDefault("selected")?.ToString(),
                "true", "selected");
        var last = ReRead(el);
        if (last == null) return null;
        if (last.Props.GetValueOrDefault("selected") is not bool sel) return null;
        return Failed($"selected still {sel}",
            new VerifyEvidence("PropertyChanged", "true", sel.ToString().ToLower(), "selected"));
    }

    private StepOutcome? VerifyForeground(long expectedHwnd)
    {
        if (expectedHwnd == 0) return null;
        var fg = _windows.GetForegroundWindow();
        if (fg == null) return null;
        return fg.Hwnd == expectedHwnd
            ? Verified("ForegroundWindow", $"0x{expectedHwnd:X}", $"0x{fg.Hwnd:X}")
            : Failed($"foreground is '{fg.Title}' (0x{fg.Hwnd:X}), expected 0x{expectedHwnd:X}",
                     new VerifyEvidence("ForegroundWindow", $"0x{expectedHwnd:X}", $"0x{fg.Hwnd:X}"));
    }

    private StepOutcome? VerifyType(PreState pre, string text, CancellationToken ct)
    {
        var el = pre.Element;
        if (el == null) return null; // typed at focused element we can't read → honest
        if (string.IsNullOrEmpty(text)) return null; // nothing typed to verify
        if (!el.Props.ContainsKey("value")) return null;
        var before = NormEol((string?)el.Props.GetValueOrDefault("value")) ?? "";
        var textN = NormEol(text) ?? "";
        var post = PollFor(el,
            e => (NormEol((string?)e.Props.GetValueOrDefault("value")) ?? "")
                 .Contains(textN, StringComparison.Ordinal), ct);
        if (post != null)
            return Verified("ValueReadback", $"contains \"{text}\"",
                (string?)post.Props.GetValueOrDefault("value"));
        var last = ReRead(el);
        if (last == null) return null;
        var actual = NormEol((string?)last.Props.GetValueOrDefault("value")) ?? "";
        return actual == before
            ? Failed("typed text not reflected in value",
                     new VerifyEvidence("ValueReadback", $"contains \"{text}\"", actual))
            : null; // value changed but doesn't contain text (masking/formatting) → honest
    }

    /// <summary>Click/Invoke/etc: element gone with window closed, own state
    /// changed, or a semantic event in its window → verified/observed. Anything else
    /// stays honest Unverified (null). Races event vs property change vs window closed.</summary>
    private StepOutcome? VerifyActed(PreState pre, WindowInfo? window, CancellationToken ct)
    {
        var el = pre.Element;
        if (el == null) // point click — window-level semantic event is an observed change
        {
            var ev = PollEvent(pre, window, ct);
            return ev != null
                ? ObservedChange("SemanticEvent", actual: ev.Kind.ToString(),
                    detail: "window event observed following coordinate click")
                : null;
        }

        var targetHwnd = el.Hwnd ?? window?.Hwnd;
        ISubscriptionLease? lease = null;
        if (_subscriptionManager != null && targetHwnd.HasValue && targetHwnd.Value != 0)
        {
            lease = _subscriptionManager.Acquire(targetHwnd.Value, window?.Pid,
                UiaEventKinds.PropertyChanged | UiaEventKinds.StructureChanged | UiaEventKinds.WindowClosed);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var baseline = _events.CurrentGeneration;

            while (sw.ElapsedMilliseconds < MaxPollMs)
        {
            ct.ThrowIfCancellationRequested();

            // 1. Check if window closed (definitive evidence)
            if (targetHwnd is { } closedHwnd && _windows.GetWindow(closedHwnd) == null)
            {
                return Verified("WindowClosed", detail: "window closed after action");
            }

            // 2. Re-read element to check property change
            Telemetry.IncPropertyCheck();
            var cur = ReRead(el);
            if (cur != null)
            {
                var changes = ChangedProps(pre.Element!, cur);
                if (changes.Count > 0)
                {
                    _registry.Register(new[] { cur });
                    var c = changes[0];
                    return Verified("PropertyChanged", c.Before, c.After, c.Prop);
                }
            }
            else
            {
                // Element is gone; if window closed, verify it
                if (targetHwnd is { } goneHwnd && _windows.GetWindow(goneHwnd) == null)
                {
                    return Verified("WindowClosed", detail: "window closed after action");
                }

                // If semantic event exists in its window, report observed change
                var evGone = AnySemanticEvent(pre, window ?? (targetHwnd is { } gh ? _windows.GetWindow(gh) : null));
                if (evGone != null)
                {
                    return ObservedChange("SemanticEvent", actual: evGone.Kind.ToString(),
                        detail: "window event observed, but target element was no longer found after action");
                }
            }

            // 3. Check semantic event even while element still exists
            var ev2 = AnySemanticEvent(pre, window ?? (el.Hwnd is { } h ? _windows.GetTopLevelWindow(h) : null));
            if (ev2 != null)
            {
                return ObservedChange("SemanticEvent", actual: ev2.Kind.ToString(),
                    detail: "window event observed, but target element state was not independently verified");
            }

            // Race: wait for next event with bounded fallback (PollMs = 150)
            if (_events.WaitForNextEvent(baseline, PollMs, ct))
            {
                baseline = _events.CurrentGeneration;
                Telemetry.IncEventWake();
                PerfTrace.Count("verify.eventWakeups");
            }
            else
            {
                Telemetry.IncFallbackPoll();
                PerfTrace.Count("verify.pollWakeups");
            }
        }

        // Bounded fallback timeout reached — final check before giving up
        if (targetHwnd is { } finalClosed && _windows.GetWindow(finalClosed) == null)
            return Verified("WindowClosed", detail: "window closed after action");

        var finalEv = AnySemanticEvent(pre, window ?? (el.Hwnd is { } fh ? _windows.GetWindow(fh) : null));
        if (finalEv != null)
        {
            return ObservedChange("SemanticEvent", actual: finalEv.Kind.ToString(),
                detail: "window event observed, but target element state was not independently verified");
        }

            return null;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    // ---------------------------------------------------------- primitives

    private static readonly string[] VerifiableProps =
        { "value", "toggleState", "expandCollapseState", "selected", "enabled", "focused" };

    private static List<(string Prop, string? Before, string? After)> ChangedProps(
        UiElement before, UiElement after)
    {
        var list = new List<(string, string?, string?)>();
        foreach (var p in VerifiableProps)
        {
            var b = before.Props.GetValueOrDefault(p)?.ToString();
            var a = after.Props.GetValueOrDefault(p)?.ToString();
            if (a != null && a != b) list.Add((p, b, a));
        }
        return list;
    }

    /// <summary>Fresh re-read of just this element — target-scoped, never a
    /// full-desktop inspect.</summary>
    private UiElement? ReRead(UiElement el)
    {
        foreach (var b in _backends)
            if (b.Id == el.Source)
                try { return b.ReResolve(el.Handle); } catch { return null; }
        return null;
    }

    private UiElement? PollFor(UiElement el, Func<UiElement, bool> cond, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var baseline = _events.CurrentGeneration;
        while (sw.ElapsedMilliseconds < MaxPollMs)
        {
            ct.ThrowIfCancellationRequested();
            var cur = ReRead(el);
            if (cur == null) return null;
            if (cond(cur)) { _registry.Register(new[] { cur }); return cur; }
            if (_events.WaitForNextEvent(baseline, PollMs, ct))
            {
                baseline = _events.CurrentGeneration;
                PerfTrace.Count("verify.eventWakeups");
            }
            else
            {
                PerfTrace.Count("verify.pollWakeups");
            }
        }
        return null;
    }

    private ObservedEvent? AnySemanticEvent(PreState pre, WindowInfo? window)
    {
        var hwnd = window?.Hwnd ?? pre.Element?.Hwnd ?? 0;
        foreach (var e in _events.Snapshot(100))
            if (e.At >= pre.At && (hwnd == 0 || e.Hwnd == hwnd) &&
                e.Kind != EventKind.ForegroundChanged) // fg changes are ambient noise
                return e;
        return null;
    }

    private ObservedEvent? PollEvent(PreState pre, WindowInfo? window, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var baseline = _events.CurrentGeneration;
        while (sw.ElapsedMilliseconds < MaxPollMs)
        {
            ct.ThrowIfCancellationRequested();
            var ev = AnySemanticEvent(pre, window);
            if (ev != null) return ev;
            if (_events.WaitForNextEvent(baseline, PollMs, ct))
            {
                baseline = _events.CurrentGeneration;
                PerfTrace.Count("verify.eventWakeups");
            }
            else
            {
                PerfTrace.Count("verify.pollWakeups");
            }
        }
        return null;
    }

    private static StepOutcome Verified(string method, string? expected = null,
        string? actual = null, string? detail = null) =>
        new(OutcomeKind.Verified, true, method, detail, 0,
            new VerifyEvidence(method, expected, actual, detail));

    private static StepOutcome ObservedChange(string method, string? expected = null,
        string? actual = null, string? detail = null) =>
        new(OutcomeKind.ObservedChange, true, method, detail, 0,
            new VerifyEvidence(method, expected, actual, detail));

    private static StepOutcome Failed(string detail, VerifyEvidence ev) =>
        new(OutcomeKind.Failed, false, ev.Method, detail, 0, ev);
}
