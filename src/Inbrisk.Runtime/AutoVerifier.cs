using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

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

    private const int PollMs = 150;
    private const int MaxPollMs = 900;

    public AutoVerifier(ElementRegistry registry, IReadOnlyList<IElementBackend> backends,
        IWindowService windows, RecentEventBuffer events)
    {
        _registry = registry; _backends = backends;
        _windows = windows; _events = events;
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

    /// <summary>Edit controls normalize newlines (RichEdit stores \r); a
    /// write is faithful when the text matches modulo line endings.</summary>
    private static string? NormEol(string? s)
        => s?.Replace("\r\n", "\n").Replace("\r", "\n");

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
        if (last == null) return Verified("ElementGone", detail: "element no longer present");
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
        if (last == null) return Verified("ElementGone", detail: "element no longer present");
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
        if (last == null) return Verified("ElementGone", detail: "element no longer present");
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

    /// <summary>Click/Invoke/etc: element gone (dialog closed), own state
    /// changed, or a semantic event in its window → verified. Anything else
    /// stays Unverified.</summary>
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
        // element state props that can legitimately change
        var post = PollFor(el, e => ChangedProps(pre.Element!, e).Count > 0, ct);
        if (post != null)
        {
            var changes = ChangedProps(el, post);
            var c = changes[0];
            return Verified("PropertyChanged", c.Before, c.After, c.Prop);
        }
        var last = ReRead(el);
        if (last == null)
            return Verified("ElementGone", detail: "element no longer present after action");
        var ev2 = AnySemanticEvent(pre, window ?? (el.Hwnd is { } h ? _windows.GetWindow(h) : null));
        return ev2 != null
            ? ObservedChange("SemanticEvent", actual: ev2.Kind.ToString(),
                detail: "window event observed, but target element state was not independently verified")
            : null;
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
        while (sw.ElapsedMilliseconds < MaxPollMs)
        {
            ct.ThrowIfCancellationRequested();
            var cur = ReRead(el);
            if (cur == null) return null;
            if (cond(cur)) { _registry.Register(new[] { cur }); return cur; }
            // event-driven fast path: a semantic event wakes the re-check
            // instantly; PollMs stays as the bounded provider fallback
            PerfTrace.Count(_events.WaitForEvent(PollMs, ct)
                ? "verify.eventWakeups" : "verify.pollWakeups");
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
        while (sw.ElapsedMilliseconds < MaxPollMs)
        {
            ct.ThrowIfCancellationRequested();
            var ev = AnySemanticEvent(pre, window);
            if (ev != null) return ev;
            PerfTrace.Count(_events.WaitForEvent(PollMs, ct)
                ? "verify.eventWakeups" : "verify.pollWakeups");
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
