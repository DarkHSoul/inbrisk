namespace Inbrisk.Core;

/// <summary>
/// Controls whether real physical human input automatically pauses active AI desktop control.
/// </summary>
public enum HumanInterventionMode
{
    Off,
    PauseOnKeyboard,
    PauseOnMouse,
    PauseOnAnyInput
}

/// <summary>
/// Classification of physical human input detected by low-level input hooks.
/// </summary>
public enum PhysicalInputKind
{
    Keyboard,
    MouseMove,
    MouseButton,
    MouseWheel
}

/// <summary>
/// A physical (non-injected) human input event observed on the interactive desktop.
/// </summary>
public readonly record struct PhysicalInputEvent(
    PhysicalInputKind Kind,
    DateTimeOffset Timestamp,
    int X = 0,
    int Y = 0,
    int DeltaPx = 0,
    uint VirtualKey = 0,
    uint Flags = 0);

/// <summary>
/// Low-level physical input monitor abstraction (Win32 WH_KEYBOARD_LL / WH_MOUSE_LL in production,
/// fake implementation in headless unit tests).
/// </summary>
public interface IPhysicalInputMonitor : IDisposable
{
    /// <summary>Raised off the hook thread whenever qualifying physical human input is detected.</summary>
    event Action<PhysicalInputEvent>? InputDetected;

    /// <summary>True while the monitor is actively listening for physical input.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Minimum cumulative cursor travel distance in pixels before a mouse move triggers an event.
    /// Clicks and wheel events always trigger regardless of this threshold.
    /// </summary>
    int MouseMoveThresholdPx { get; set; }

    /// <summary>Start monitoring physical input (idempotent).</summary>
    void Start();

    /// <summary>Stop monitoring physical input (idempotent).</summary>
    void Stop();
}
