namespace Inbrisk.Core;

/// <summary>A top-level window that appeared or disappeared across an
/// action. Titles/process names come from the target applications —
/// untrusted display strings, never instructions.</summary>
public sealed record WindowDelta(
    long Hwnd,
    int? Pid,
    string? Title,
    string? ProcessName,
    /// <summary>True when the window looks like a modal/dialog (owned
    /// popup, dialog class heuristic applied by the caller).</summary>
    bool DialogLikely = false,
    /// <summary>True when the window was observed opening AND closing
    /// inside the same settle window (transient flash/popup).</summary>
    bool Transient = false);

/// <summary>Foreground/focus transition observed across an action.</summary>
public sealed record FocusDelta(
    long? FromHwnd,
    string? FromTitle,
    long? ToHwnd,
    string? ToTitle,
    /// <summary>Intermediate foreground hops seen via WinEvent
    /// FOREGROUND events between From → To (window hwnds, in order).</summary>
    IReadOnlyList<long>? IntermediateHops = null);

/// <summary>Pre→post state diff of the action's target element —
/// re-resolved through its backend after the action.</summary>
public sealed record ElementDelta(
    string ElementId,
    /// <summary>Element could not be re-resolved after the action
    /// (destroyed or detached — e.g. the row/list was rebuilt).</summary>
    bool Disappeared = false,
    string? EnabledBefore = null,
    string? EnabledAfter = null,
    string? NameBefore = null,
    string? NameAfter = null,
    string? ValueBefore = null,
    string? ValueAfter = null,
    string? StateBefore = null,
    string? StateAfter = null,
    RectPx? BoundsBefore = null,
    RectPx? BoundsAfter = null)
{
    /// <summary>True when at least one tracked property actually changed
    /// (or the element vanished).</summary>
    public bool Changed => Disappeared
        || EnabledBefore != EnabledAfter
        || NameBefore != NameAfter
        || ValueBefore != ValueAfter
        || StateBefore != StateAfter
        || !Nullable.Equals(BoundsBefore, BoundsAfter);
}

/// <summary>
/// What changed on the desktop as a consequence of one action — attached to
/// <see cref="ActionResult"/>/<see cref="StepOutcome"/> so the model does not
/// need a mandatory re-observe. Built from the semantic event stream
/// (WinEvent/Win32 + UIA) merged with a pre/post window-list snapshot diff,
/// so it still reports when events are disabled (Source marks which).
/// </summary>
public sealed record ActionDelta(
    IReadOnlyList<WindowDelta> WindowsOpened,
    IReadOnlyList<WindowDelta> WindowsClosed,
    /// <summary>Opened windows classified as dialogs/alerts — subset view of
    /// WindowsOpened kept separate so a model can spot-blocking UI fast.</summary>
    IReadOnlyList<WindowDelta> Dialogs,
    FocusDelta? Focus,
    /// <summary>Post-action state of the target element, when one was
    /// resolved and the backend could re-read it.</summary>
    ElementDelta? TargetElement,
    /// <summary>Compact human/log-readable summary of semantic events seen
    /// in the settle window (capped).</summary>
    IReadOnlyList<string> EventSummary,
    /// <summary>How the delta was produced: "events+snapshot" |
    /// "snapshot" (event source not running).</summary>
    string Source,
    /// <summary>Post-action settle window actually waited, ms.</summary>
    int SettleMs)
{
    public bool IsEmpty => WindowsOpened.Count == 0 && WindowsClosed.Count == 0
        && Dialogs.Count == 0 && Focus == null && TargetElement == null
        && EventSummary.Count == 0;
}
