namespace Inbrisk.Core;

public enum EventKind
{
    ForegroundChanged,
    WindowOpened,
    WindowClosed,
    WindowShown,
    NameChanged,
    ValueChanged,
    StructureChanged,
    FocusChanged,
    LocationChanged,
    StateChanged,
}

/// <summary>A semantic change signal from WinEvents or UIA events.</summary>
public sealed record ObservedEvent(
    EventKind Kind,
    DateTimeOffset At,
    long? Hwnd = null,
    int? Pid = null,
    string? ElementId = null,
    string? Detail = null,
    RectPx? Bounds = null)
{
    public override string ToString() =>
        $"{At:HH:mm:ss.fff} {Kind} hwnd={(Hwnd.HasValue ? $"0x{Hwnd.Value:X}" : "-")} {Detail ?? ""}".TrimEnd();
}

/// <summary>One merged view of the desktop handed to a caller/model.</summary>
public sealed record Observation(
    WindowInfo? ActiveWindow,
    IReadOnlyList<WindowInfo> Windows,
    IReadOnlyList<UiElement> Elements,
    FrameTransform? Frame,
    IReadOnlyList<ObservedEvent> Events,
    DateTimeOffset At);
