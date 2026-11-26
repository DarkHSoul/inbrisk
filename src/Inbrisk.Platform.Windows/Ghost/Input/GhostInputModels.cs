using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;


/// <summary>
/// Supported execution action types for Ghost agent sessions.
/// </summary>
public enum GhostActionKind
{
    MouseMove,
    MouseClick,
    MouseDown,
    MouseUp,
    MouseDoubleClick,
    MouseRightClick,
    MouseMiddleClick,
    DragAndDrop,
    MouseWheel,
    TypeText,
    KeyPress,
    KeyDown,
    KeyUp,
    Hotkey,
    LaunchProcess,
    ActivateWindow,
    CloseWindow,
    Delay,
    WaitForWindow
}

/// <summary>
/// Declarative specification for a single hardware-level ghost action step.
/// </summary>
public sealed record GhostActionStep
{
    public GhostActionKind Kind { get; init; }

    // Coordinates (Physical desktop pixels)
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? ToX { get; init; }
    public int? ToY { get; init; }

    // Mouse settings
    public MouseButton Button { get; init; } = MouseButton.Left;
    public int ClickCount { get; init; } = 1;
    public int WheelDelta { get; init; } = 0;
    public bool SmoothMove { get; init; } = false;
    public int MoveSteps { get; init; } = 10;
    public int MoveDelayMs { get; init; } = 5;

    // Keyboard & Text settings
    public string? Text { get; init; }
    public string? Hotkey { get; init; }
    public KeyCode? Key { get; init; }
    public ushort? VirtualKey { get; init; }
    public int DelayBetweenKeysMs { get; init; } = 10;
    public int HoldDurationMs { get; init; } = 25;

    // Drag-and-drop settings
    public int DurationMs { get; init; } = 300;
    public int DragSteps { get; init; } = 20;

    // Process & Window management settings
    public string? ProcessPath { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? WindowTitle { get; init; }
    public string? ProcessName { get; init; }
    public IntPtr? WindowHandle { get; init; }
    public int TimeoutMs { get; init; } = 5000;
    public bool WaitForInputIdle { get; init; } = true;

    // Execution control
    public int? Milliseconds { get; init; }
    public bool FailFast { get; init; } = true;
    public string? Description { get; init; }


    public static GhostActionStep Move(int x, int y, bool smooth = false, int steps = 10, int delayMs = 5) => new()
    {
        Kind = GhostActionKind.MouseMove,
        X = x,
        Y = y,
        SmoothMove = smooth,
        MoveSteps = steps,
        MoveDelayMs = delayMs,
        Description = $"Move to ({x}, {y})"
    };

    public static GhostActionStep Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) => new()
    {
        Kind = count switch
        {
            2 when button == MouseButton.Left => GhostActionKind.MouseDoubleClick,
            _ when button == MouseButton.Right => GhostActionKind.MouseRightClick,
            _ when button == MouseButton.Middle => GhostActionKind.MouseMiddleClick,
            _ => GhostActionKind.MouseClick
        },
        X = x,
        Y = y,
        Button = button,
        ClickCount = count,
        Description = $"Click ({button}, count={count}) at ({x}, {y})"
    };

    public static GhostActionStep RightClick(int x, int y) => new()
    {
        Kind = GhostActionKind.MouseRightClick,
        X = x,
        Y = y,
        Button = MouseButton.Right,
        ClickCount = 1,
        Description = $"Right click at ({x}, {y})"
    };

    public static GhostActionStep DoubleClick(int x, int y, MouseButton button = MouseButton.Left) => new()
    {
        Kind = GhostActionKind.MouseDoubleClick,
        X = x,
        Y = y,
        Button = button,
        ClickCount = 2,
        Description = $"Double click at ({x}, {y})"
    };

    public static GhostActionStep MiddleClick(int x, int y) => new()
    {
        Kind = GhostActionKind.MouseMiddleClick,
        X = x,
        Y = y,
        Button = MouseButton.Middle,
        ClickCount = 1,
        Description = $"Middle click at ({x}, {y})"
    };

    public static GhostActionStep MouseDown(MouseButton button = MouseButton.Left, int? x = null, int? y = null) => new()
    {
        Kind = GhostActionKind.MouseDown,
        Button = button,
        X = x,
        Y = y,
        Description = $"Mouse down ({button})" + (x.HasValue && y.HasValue ? $" at ({x.Value}, {y.Value})" : "")
    };

    public static GhostActionStep MouseUp(MouseButton button = MouseButton.Left, int? x = null, int? y = null) => new()
    {
        Kind = GhostActionKind.MouseUp,
        Button = button,
        X = x,
        Y = y,
        Description = $"Mouse up ({button})" + (x.HasValue && y.HasValue ? $" at ({x.Value}, {y.Value})" : "")
    };

    public static GhostActionStep Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, int steps = 20, MouseButton button = MouseButton.Left) => new()
    {
        Kind = GhostActionKind.DragAndDrop,
        X = fromX,
        Y = fromY,
        ToX = toX,
        ToY = toY,
        DurationMs = durationMs,
        DragSteps = steps,
        Button = button,
        Description = $"Drag ({fromX}, {fromY}) -> ({toX}, {toY}) over {durationMs}ms"
    };

    public static GhostActionStep Wheel(int delta, int? x = null, int? y = null) => new()
    {
        Kind = GhostActionKind.MouseWheel,
        WheelDelta = delta,
        X = x,
        Y = y,
        Description = $"Mouse wheel scroll delta={delta}" + (x.HasValue && y.HasValue ? $" at ({x.Value}, {y.Value})" : "")
    };

    public static GhostActionStep Type(string text, int delayBetweenKeysMs = 10) => new()
    {
        Kind = GhostActionKind.TypeText,
        Text = text,
        DelayBetweenKeysMs = delayBetweenKeysMs,
        Description = $"Type text \"{(text.Length > 20 ? text[..20] + "..." : text)}\""
    };

    public static GhostActionStep PressKey(KeyCode key, int holdDurationMs = 25) => new()
    {
        Kind = GhostActionKind.KeyPress,
        Key = key,
        HoldDurationMs = holdDurationMs,
        Description = $"Key press {key}"
    };

    public static GhostActionStep PressVirtualKey(ushort vk, int holdDurationMs = 25) => new()
    {
        Kind = GhostActionKind.KeyPress,
        VirtualKey = vk,
        HoldDurationMs = holdDurationMs,
        Description = $"Key press VK 0x{vk:X2}"
    };

    public static GhostActionStep PressDown(KeyCode key) => new()
    {
        Kind = GhostActionKind.KeyDown,
        Key = key,
        Description = $"Key down {key}"
    };

    public static GhostActionStep PressUp(KeyCode key) => new()
    {
        Kind = GhostActionKind.KeyUp,
        Key = key,
        Description = $"Key up {key}"
    };

    public static GhostActionStep HotkeyCombo(string hotkeyCombo) => new()
    {
        Kind = GhostActionKind.Hotkey,
        Hotkey = hotkeyCombo,
        Description = $"Hotkey \"{hotkeyCombo}\""
    };

    public static GhostActionStep Launch(string processPath, string? args = null, string? workingDir = null, bool waitForInputIdle = true) => new()
    {
        Kind = GhostActionKind.LaunchProcess,
        ProcessPath = processPath,
        Arguments = args,
        WorkingDirectory = workingDir,
        WaitForInputIdle = waitForInputIdle,
        Description = $"Launch \"{processPath}\"" + (string.IsNullOrEmpty(args) ? "" : $" args: \"{args}\"")
    };

    public static GhostActionStep Activate(string windowTitleOrProcess, int timeoutMs = 5000) => new()
    {
        Kind = GhostActionKind.ActivateWindow,
        WindowTitle = windowTitleOrProcess,
        ProcessName = windowTitleOrProcess,
        TimeoutMs = timeoutMs,
        Description = $"Activate window \"{windowTitleOrProcess}\""
    };

    public static GhostActionStep Activate(IntPtr hwnd) => new()
    {
        Kind = GhostActionKind.ActivateWindow,
        WindowHandle = hwnd,
        Description = $"Activate window HWND 0x{hwnd:X}"
    };

    public static GhostActionStep Close(string? windowTitle = null, IntPtr? hwnd = null) => new()
    {
        Kind = GhostActionKind.CloseWindow,
        WindowTitle = windowTitle,
        WindowHandle = hwnd,
        Description = $"Close window {(hwnd.HasValue ? $"HWND 0x{hwnd.Value:X}" : $"\"{windowTitle}\"")}"
    };

    public static GhostActionStep Sleep(int milliseconds) => new()
    {
        Kind = GhostActionKind.Delay,
        Milliseconds = milliseconds,
        Description = $"Delay {milliseconds}ms"
    };

    public static GhostActionStep WaitForWindow(string windowTitleOrProcess, int timeoutMs = 10000) => new()
    {
        Kind = GhostActionKind.WaitForWindow,
        WindowTitle = windowTitleOrProcess,
        ProcessName = windowTitleOrProcess,
        TimeoutMs = timeoutMs,
        Description = $"Wait for window \"{windowTitleOrProcess}\" (timeout={timeoutMs}ms)"
    };

}

/// <summary>
/// Individual step execution result.
/// </summary>
public sealed record GhostStepResult(
    int StepIndex,
    GhostActionStep Step,
    bool Success,
    string? ErrorMessage = null,
    TimeSpan Duration = default,
    object? Output = null);

/// <summary>
/// Comprehensive batch execution report.
/// </summary>
public sealed record GhostBatchResult(
    bool Success,
    int StepsCompleted,
    int TotalSteps,
    IReadOnlyList<GhostStepResult> StepResults,
    string? FailureReason = null,
    TimeSpan TotalDuration = default);
