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
    Notification,
    LiveRegionChanged,
    PropertyChanged,
}

public sealed record UiaNotificationData(
    int NotificationKind,
    int NotificationProcessing,
    string? DisplayString,
    string? ActivityId);

/// <summary>Semantic coalescing identity for event debouncing and deduplication.</summary>
public readonly record struct EventCoalescingKey(
    EventKind Kind,
    long Hwnd,
    string ElementId,
    int? PropertyId = null,
    string? StructureChange = null
)
{
    public static EventCoalescingKey FromEvent(ObservedEvent e)
    {
        int? propId = e.PropertyId;
        if (!propId.HasValue)
        {
            if (e.Kind == EventKind.NameChanged) propId = 30005; // UiaIds.NameProperty
            else if (e.Kind == EventKind.ValueChanged) propId = 30045; // UiaIds.ValueValueProperty
            else if (e.Detail != null && e.Detail.StartsWith("prop="))
            {
                var endIdx = e.Detail.IndexOfAny([' ', '-', '>']);
                var numStr = endIdx > 5 ? e.Detail[5..endIdx] : e.Detail[5..];
                if (int.TryParse(numStr, out var parsed)) propId = parsed;
            }
        }
        return new EventCoalescingKey(
            e.Kind,
            e.Hwnd ?? 0,
            e.ElementId ?? "",
            propId,
            e.Kind == EventKind.StructureChanged ? e.Detail : null
        );
    }
}

/// <summary>A semantic change signal from WinEvents or UIA events.</summary>
public sealed record ObservedEvent(
    EventKind Kind,
    DateTimeOffset At,
    long? Hwnd = null,
    int? Pid = null,
    string? ElementId = null,
    string? Detail = null,
    RectPx? Bounds = null,
    UiaNotificationData? NotificationData = null,
    int? PropertyId = null)
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
