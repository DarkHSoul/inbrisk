using System;

namespace Inbrisk.Core;

/// <summary>
/// Type of interactive input event occurring within the Picture-in-Picture (PiP) window.
/// </summary>
public enum PipInputEventType
{
    /// <summary>Mouse pointer moved over client area.</summary>
    MouseMove,

    /// <summary>Mouse button pressed down.</summary>
    MouseDown,

    /// <summary>Mouse button released up.</summary>
    MouseUp,

    /// <summary>Mouse button double-clicked.</summary>
    DoubleClick,

    /// <summary>Mouse wheel scrolled.</summary>
    MouseWheel
}

/// <summary>
/// Event arguments for interactive input actions captured on the PiP client area.
/// </summary>
public sealed class PipInputEventArgs : EventArgs
{
    /// <summary>Header bar height in pixels.</summary>
    public const int HeaderHeight = 26;

    /// <summary>Gets the input event classification.</summary>
    public PipInputEventType EventType { get; init; }

    /// <summary>Gets the client-relative X coordinate in the PiP window.</summary>
    public int X { get; init; }

    /// <summary>Gets the client-relative Y coordinate in the PiP window.</summary>
    public int Y { get; init; }

    /// <summary>Gets the mouse button involved in the action.</summary>
    public MouseButton Button { get; init; } = MouseButton.Left;

    /// <summary>Gets the mouse wheel scroll delta (typically ±120 per notch).</summary>
    public int Delta { get; init; }

    /// <summary>Gets the timestamp in UTC ticks when the event was received.</summary>
    public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;

    /// <summary>Alias for X coordinate.</summary>
    public int ClientX => X;

    /// <summary>Alias for Y coordinate.</summary>
    public int ClientY => Y;

    /// <summary>Gets Y coordinate relative to the content area below the 26px header bar.</summary>
    public int ContentY => Math.Max(0, Y - HeaderHeight);

    public PipInputEventArgs() { }

    public PipInputEventArgs(
        PipInputEventType eventType,
        int x,
        int y,
        MouseButton button = MouseButton.Left,
        int delta = 0)
    {
        EventType = eventType;
        X = x;
        Y = y;
        Button = button;
        Delta = delta;
        TimestampTicks = DateTime.UtcNow.Ticks;
    }
}
