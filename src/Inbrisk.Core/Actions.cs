namespace Inbrisk.Core;

public enum ActionKind
{
    Click, RightClick, DoubleClick, MiddleClick, Drag, Scroll, MouseMove,
    Invoke, SetValue, Toggle, Select, Expand, Collapse, ScrollIntoView,
    TypeText, KeyPress, Hotkey,
    FocusWindow, FocusElement, ClipboardRead, ClipboardWrite,
}

/// <summary>What an action targets. Exactly one field is set.</summary>
public sealed record TargetRef
{
    public string? ElementId { get; init; }
    public (int X, int Y)? Point { get; init; }
    public long? Hwnd { get; init; }
    public FindSpec? Selector { get; init; }

    public static TargetRef Element(string id) => new() { ElementId = id };
    public static TargetRef At(int x, int y) => new() { Point = (x, y) };
    public static TargetRef Window(long hwnd) => new() { Hwnd = hwnd };
    public static TargetRef Find(FindSpec spec) => new() { Selector = spec };
}

public enum VerifyKind { None, PropertyEquals, ElementGone, ElementExists, RegionChanged, RegionStable, ForegroundIs }

public sealed record VerifySpec(
    VerifyKind Kind,
    string? Property = null,
    object? Expected = null,
    RectPx? Region = null,
    long? Hwnd = null,
    int TimeoutMs = 3000);

public enum SafetyClass { Safe, Confirm, Deny }

public sealed record ActionIntent(
    ActionKind Kind,
    TargetRef Target,
    IReadOnlyDictionary<string, object?>? Args = null,
    VerifySpec? Verify = null);

public enum ErrorCode
{
    None, NotFound, Stale, StaleUnresolvable,
    TargetElevated, SecureDesktopActive, Timeout,
    CaptureFailed, InputBlocked, Unsupported, Disabled,
    PolicyDenied, ConfirmationRequired, Internal, Busy
}

public sealed class InbriskException(ErrorCode code, string message)
    : Exception(message)
{
    public ErrorCode Code { get; } = code;
}

public sealed record Attempt(
    BackendId Backend,
    string Method,
    bool Success,
    string? Error,
    TimeSpan Duration);

public enum VerifyResult { NotRequested, Verified, Failed, Unverified }

public sealed record ActionResult(
    bool Success,
    BackendId? BackendUsed,
    string Method,
    IReadOnlyList<Attempt> Attempts,
    VerifyResult Verification,
    TimeSpan Duration,
    ErrorCode Error = ErrorCode.None,
    string? ErrorMessage = null,
    /// <summary>Why this action counts as verified — auditable proof,
    /// e.g. PropertyChanged ToggleState_Off→On or ValueReadback "hello".</summary>
    VerifyEvidence? Evidence = null);

/// <summary>Per-call execution context — carries cancellation, the run's
/// confirmer and correlation ids without touching process-global state.
/// Two concurrent runs never share this.</summary>
public sealed record ActionContext(
    CancellationToken Ct = default,
    Func<ActionIntent, UiElement?, WindowInfo?, bool>? Confirmer = null,
    string? RunId = null,
    int? StepId = null,
    string? ActionId = null);
