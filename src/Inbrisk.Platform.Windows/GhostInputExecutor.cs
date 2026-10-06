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

#region Action Data Contracts & Enums

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

    #region Fluent Factory Methods

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

    #endregion
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

#endregion

/// <summary>
/// Hardware-level independent mouse, keyboard, and window action execution engine for Windows sessions
/// (e.g. Session 2 / Ghost RDP sessions).
/// Operates directly via Win32 SendInput, native window management APIs, and process orchestration.
/// Guarantees zero stuck keys/buttons, strict failFast sequencing, and safe release semantics.
/// </summary>
public sealed class GhostInputExecutor : IDisposable
{
    #region Win32 Constants & Markers

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const IntPtr SC_CLOSE = 0xF060;

    private const int SW_SHOWNORMAL = 1;
    private const int SW_SHOWMINIMIZED = 2;
    private const int SW_MAXIMIZE = 3;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    // Marker stamped onto dwExtraInfo: 0x1B8A0002 (Inbrisk Ghost Input Signature)
    private static readonly IntPtr Marker = new(unchecked((int)(0x1B8A0000u | 0x0002)));

    #endregion

    #region State & Tracking

    private readonly object _stateGate = new();
    private readonly HashSet<uint> _heldButtons = new();
    private readonly HashSet<ushort> _heldVirtualKeys = new();
    private bool _isDisposed;

    /// <summary>Optional target session ID (for multi-session diagnostics).</summary>
    public int? TargetSessionId { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="GhostInputExecutor"/>.
    /// </summary>
    /// <param name="targetSessionId">Optional target session identifier.</param>
    public GhostInputExecutor(int? targetSessionId = null)
    {
        TargetSessionId = targetSessionId;
    }

    #region Batch Execution (Fail-Fast)

    /// <summary>
    /// Executes a sequential batch of ghost action steps with fail-fast guarantee.
    /// If any step fails or is cancelled, execution immediately stops and releases any held input.
    /// </summary>
    /// <param name="steps">Sequence of actions to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="failFast">Whether to abort execution upon the first failing step (default true).</param>
    /// <returns>Batch summary detailing individual step outcomes and execution metrics.</returns>
    public async Task<GhostBatchResult> ExecuteBatchAsync(
        IReadOnlyList<GhostActionStep> steps,
        CancellationToken ct = default,
        bool failFast = true)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(steps);

        var totalWatch = Stopwatch.StartNew();
        var results = new List<GhostStepResult>(steps.Count);
        int completedCount = 0;

        try
        {
            for (int i = 0; i < steps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                var stepWatch = Stopwatch.StartNew();
                bool stepSuccess = false;
                string? stepError = null;
                object? stepOutput = null;

                try
                {
                    stepOutput = await ExecuteSingleStepAsync(step, ct).ConfigureAwait(false);
                    stepSuccess = true;
                    completedCount++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    stepError = "Operation cancelled by caller.";
                    results.Add(new GhostStepResult(i, step, false, stepError, stepWatch.Elapsed));
                    ReleaseAll();
                    return new GhostBatchResult(
                        Success: false,
                        StepsCompleted: completedCount,
                        TotalSteps: steps.Count,
                        StepResults: results,
                        FailureReason: stepError,
                        TotalDuration: totalWatch.Elapsed);
                }
                catch (Exception ex)
                {
                    stepError = ex.Message;
                    Debug.WriteLine($"[GhostInputExecutor] Step {i} failed: {ex}");
                }
                finally
                {
                    stepWatch.Stop();
                    results.Add(new GhostStepResult(i, step, stepSuccess, stepError, stepWatch.Elapsed, stepOutput));
                }

                if (!stepSuccess && (failFast || step.FailFast))
                {
                    ReleaseAll();
                    return new GhostBatchResult(
                        Success: false,
                        StepsCompleted: completedCount,
                        TotalSteps: steps.Count,
                        StepResults: results,
                        FailureReason: $"Step {i} ({step.Kind}) failed: {stepError}",
                        TotalDuration: totalWatch.Elapsed);
                }
            }

            return new GhostBatchResult(
                Success: true,
                StepsCompleted: completedCount,
                TotalSteps: steps.Count,
                StepResults: results,
                FailureReason: null,
                TotalDuration: totalWatch.Elapsed);
        }
        finally
        {
            totalWatch.Stop();
        }
    }

    private async Task<object?> ExecuteSingleStepAsync(GhostActionStep step, CancellationToken ct)
    {
        switch (step.Kind)
        {
            case GhostActionKind.MouseMove:
                if (!step.X.HasValue || !step.Y.HasValue)
                    throw new ArgumentException("MouseMove requires X and Y coordinates.");
                MoveMouse(step.X.Value, step.Y.Value, step.SmoothMove, step.MoveSteps, step.MoveDelayMs, ct);
                return (step.X.Value, step.Y.Value);

            case GhostActionKind.MouseClick:
                if (!step.X.HasValue || !step.Y.HasValue)
                    throw new ArgumentException("MouseClick requires X and Y coordinates.");
                Click(step.X.Value, step.Y.Value, step.Button, step.ClickCount, ct);
                return true;

            case GhostActionKind.MouseDoubleClick:
                if (!step.X.HasValue || !step.Y.HasValue)
                    throw new ArgumentException("MouseDoubleClick requires X and Y coordinates.");
                Click(step.X.Value, step.Y.Value, step.Button, 2, ct);
                return true;

            case GhostActionKind.MouseRightClick:
                if (!step.X.HasValue || !step.Y.HasValue)
                    throw new ArgumentException("MouseRightClick requires X and Y coordinates.");
                Click(step.X.Value, step.Y.Value, MouseButton.Right, 1, ct);
                return true;

            case GhostActionKind.MouseMiddleClick:
                if (!step.X.HasValue || !step.Y.HasValue)
                    throw new ArgumentException("MouseMiddleClick requires X and Y coordinates.");
                Click(step.X.Value, step.Y.Value, MouseButton.Middle, 1, ct);
                return true;

            case GhostActionKind.MouseDown:
                MouseDown(step.Button, step.X, step.Y);
                return true;

            case GhostActionKind.MouseUp:
                MouseUp(step.Button, step.X, step.Y);
                return true;

            case GhostActionKind.DragAndDrop:
                if (!step.X.HasValue || !step.Y.HasValue || !step.ToX.HasValue || !step.ToY.HasValue)
                    throw new ArgumentException("DragAndDrop requires X, Y, ToX, and ToY coordinates.");
                DragAndDrop(step.X.Value, step.Y.Value, step.ToX.Value, step.ToY.Value,
                    step.Button, step.DurationMs, step.DragSteps, ct);
                return true;

            case GhostActionKind.MouseWheel:
                Scroll(step.WheelDelta, step.X, step.Y);
                return step.WheelDelta;

            case GhostActionKind.TypeText:
                if (string.IsNullOrEmpty(step.Text))
                    return true;
                TypeText(step.Text, step.DelayBetweenKeysMs, ct);
                return step.Text.Length;

            case GhostActionKind.KeyPress:
                if (step.VirtualKey.HasValue)
                    KeyPress(step.VirtualKey.Value, step.HoldDurationMs, ct);
                else if (step.Key.HasValue)
                    KeyPress(step.Key.Value, step.HoldDurationMs, ct);
                else
                    throw new ArgumentException("KeyPress requires either Key or VirtualKey.");
                return true;

            case GhostActionKind.KeyDown:
                if (step.VirtualKey.HasValue)
                    KeyDown(step.VirtualKey.Value);
                else if (step.Key.HasValue)
                    KeyDown(step.Key.Value);
                else
                    throw new ArgumentException("KeyDown requires either Key or VirtualKey.");
                return true;

            case GhostActionKind.KeyUp:
                if (step.VirtualKey.HasValue)
                    KeyUp(step.VirtualKey.Value);
                else if (step.Key.HasValue)
                    KeyUp(step.Key.Value);
                else
                    throw new ArgumentException("KeyUp requires either Key or VirtualKey.");
                return true;

            case GhostActionKind.Hotkey:
                if (string.IsNullOrWhiteSpace(step.Hotkey))
                    throw new ArgumentException("Hotkey step requires non-empty Hotkey string.");
                ExecuteHotkey(step.Hotkey, ct);
                return step.Hotkey;

            case GhostActionKind.LaunchProcess:
                if (string.IsNullOrWhiteSpace(step.ProcessPath))
                    throw new ArgumentException("LaunchProcess requires ProcessPath.");
                var proc = LaunchProcess(step.ProcessPath, step.Arguments, step.WorkingDirectory,
                    step.WaitForInputIdle, step.TimeoutMs);
                return proc?.Id;

            case GhostActionKind.ActivateWindow:
                if (step.WindowHandle.HasValue && step.WindowHandle.Value != IntPtr.Zero)
                {
                    bool activated = ActivateWindow(step.WindowHandle.Value);
                    if (!activated)
                        throw new InvalidOperationException($"Could not activate window HWND 0x{step.WindowHandle.Value:X}");
                    return true;
                }
                string targetWin = step.WindowTitle ?? step.ProcessName ?? "";
                if (string.IsNullOrWhiteSpace(targetWin))
                    throw new ArgumentException("ActivateWindow requires WindowHandle, WindowTitle, or ProcessName.");
                bool foundAndActivated = await ActivateWindowAsync(targetWin, step.TimeoutMs, ct).ConfigureAwait(false);
                if (!foundAndActivated)
                    throw new InvalidOperationException($"Window not found or failed to activate: \"{targetWin}\"");
                return true;

            case GhostActionKind.CloseWindow:
                if (step.WindowHandle.HasValue && step.WindowHandle.Value != IntPtr.Zero)
                    return CloseWindow(step.WindowHandle.Value);
                if (!string.IsNullOrWhiteSpace(step.WindowTitle))
                    return CloseWindow(step.WindowTitle);
                throw new ArgumentException("CloseWindow requires WindowHandle or WindowTitle.");

            case GhostActionKind.Delay:
                int ms = step.Milliseconds ?? 100;
                if (ms > 0)
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                return ms;

            case GhostActionKind.WaitForWindow:
                string waitTarget = step.WindowTitle ?? step.ProcessName ?? "";
                if (string.IsNullOrWhiteSpace(waitTarget))
                    throw new ArgumentException("WaitForWindow requires WindowTitle or ProcessName.");
                var hwnd = await WaitForWindowAsync(waitTarget, step.TimeoutMs, ct).ConfigureAwait(false);
                if (hwnd == IntPtr.Zero)
                    throw new TimeoutException($"Timeout waiting for window: \"{waitTarget}\"");
                return hwnd;

            default:
                throw new NotSupportedException($"Ghost action kind '{step.Kind}' is not supported.");
        }
    }

    #endregion

    #region Mouse Actions (SendInput)

    /// <summary>
    /// Moves the mouse pointer to the specified physical desktop coordinates.
    /// Combines SetCursorPos and SendInput with normalized coordinates for multi-session reliability.
    /// </summary>
    public void MoveMouse(int x, int y, bool smooth = false, int steps = 10, int delayMs = 5, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (!smooth || steps <= 1)
        {
            InternalMove(x, y);
            return;
        }

        var (curX, curY) = GetCursorPosition();
        for (int i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            double progress = (double)i / steps;
            int stepX = (int)Math.Round(curX + (x - curX) * progress);
            int stepY = (int)Math.Round(curY + (y - curY) * progress);
            InternalMove(stepX, stepY);
            if (delayMs > 0 && i < steps)
                Thread.Sleep(delayMs);
        }
        InternalMove(x, y);
    }

    /// <summary>
    /// Performs a mouse click or multi-click at the designated location.
    /// </summary>
    public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        MoveMouse(x, y, smooth: false, ct: ct);

        var (downFlag, upFlag) = GetButtonFlags(button);

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SendMouseFlag(downFlag);
            lock (_stateGate) _heldButtons.Add(downFlag);

            Thread.Sleep(20);

            SendMouseFlag(upFlag);
            lock (_stateGate) _heldButtons.Remove(downFlag);

            if (i + 1 < count)
                Thread.Sleep(60);
        }
    }

    /// <summary>
    /// Presses and holds a mouse button.
    /// </summary>
    public void MouseDown(MouseButton button = MouseButton.Left, int? x = null, int? y = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (x.HasValue && y.HasValue)
            InternalMove(x.Value, y.Value);

        var (downFlag, _) = GetButtonFlags(button);
        SendMouseFlag(downFlag);
        lock (_stateGate) _heldButtons.Add(downFlag);
    }

    /// <summary>
    /// Releases a held mouse button.
    /// </summary>
    public void MouseUp(MouseButton button = MouseButton.Left, int? x = null, int? y = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (x.HasValue && y.HasValue)
            InternalMove(x.Value, y.Value);

        var (downFlag, upFlag) = GetButtonFlags(button);
        SendMouseFlag(upFlag);
        lock (_stateGate) _heldButtons.Remove(downFlag);
    }

    /// <summary>
    /// Performs a smooth drag-and-drop gesture between two points.
    /// Guarantees mouse button release in the finally block.
    /// </summary>
    public void DragAndDrop(
        int fromX, int fromY, int toX, int toY,
        MouseButton button = MouseButton.Left,
        int durationMs = 300,
        int steps = 20,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var (downFlag, upFlag) = GetButtonFlags(button);
        InternalMove(fromX, fromY);
        Thread.Sleep(25);

        SendMouseFlag(downFlag);
        lock (_stateGate) _heldButtons.Add(downFlag);

        try
        {
            Thread.Sleep(25);
            steps = Math.Max(4, steps);
            int sleepPerStep = Math.Max(5, durationMs / steps);

            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                double progress = (double)i / steps;
                int intermediateX = (int)Math.Round(fromX + (toX - fromX) * progress);
                int intermediateY = (int)Math.Round(fromY + (toY - fromY) * progress);
                InternalMove(intermediateX, intermediateY);
                Thread.Sleep(sleepPerStep);
            }

            InternalMove(toX, toY);
            Thread.Sleep(25);
        }
        finally
        {
            SendMouseFlag(upFlag);
            lock (_stateGate) _heldButtons.Remove(downFlag);
        }
    }

    /// <summary>
    /// Scrolls the mouse wheel by the specified delta.
    /// Positive delta scrolls up/forward, negative scrolls down/backward.
    /// </summary>
    public void Scroll(int wheelDelta, int? x = null, int? y = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (x.HasValue && y.HasValue)
            InternalMove(x.Value, y.Value);

        var input = new INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new INPUTUNION
            {
                Mi = new MOUSEINPUT
                {
                    MouseData = (uint)wheelDelta,
                    DwFlags = NativeMethods.MOUSEEVENTF_WHEEL,
                    DwExtraInfo = Marker
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Gets the current cursor position in desktop coordinates.
    /// </summary>
    public (int X, int Y) GetCursorPosition()
    {
        if (NativeMethods.GetCursorPos(out var pt))
            return (pt.X, pt.Y);
        return (0, 0);
    }

    private void InternalMove(int x, int y)
    {
        NativeMethods.SetCursorPos(x, y);

        // Normalize coordinates for SendInput across primary / virtual desktop
        int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        if (screenWidth <= 0) screenWidth = 1920;
        if (screenHeight <= 0) screenHeight = 1080;

        int normX = (int)Math.Round((x * 65535.0) / Math.Max(1, screenWidth - 1));
        int normY = (int)Math.Round((y * 65535.0) / Math.Max(1, screenHeight - 1));
        normX = Math.Clamp(normX, 0, 65535);
        normY = Math.Clamp(normY, 0, 65535);

        var input = new INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new INPUTUNION
            {
                Mi = new MOUSEINPUT
                {
                    Dx = normX,
                    Dy = normY,
                    DwFlags = NativeMethods.MOUSEEVENTF_MOVE | NativeMethods.MOUSEEVENTF_ABSOLUTE | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    DwExtraInfo = Marker
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private void SendMouseFlag(uint flag)
    {
        var input = new INPUT
        {
            Type = NativeMethods.INPUT_MOUSE,
            U = new INPUTUNION
            {
                Mi = new MOUSEINPUT
                {
                    DwFlags = flag,
                    DwExtraInfo = Marker
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static (uint Down, uint Up) GetButtonFlags(MouseButton button) => button switch
    {
        MouseButton.Right => (NativeMethods.MOUSEEVENTF_RIGHTDOWN, NativeMethods.MOUSEEVENTF_RIGHTUP),
        MouseButton.Middle => (NativeMethods.MOUSEEVENTF_MIDDLEDOWN, NativeMethods.MOUSEEVENTF_MIDDLEUP),
        _ => (NativeMethods.MOUSEEVENTF_LEFTDOWN, NativeMethods.MOUSEEVENTF_LEFTUP),
    };

    #endregion

    #region Keyboard Actions (ScanCode / VirtualKey / Unicode)

    /// <summary>
    /// Types arbitrary Unicode text into the currently focused window using KEYEVENTF_UNICODE.
    /// Handles special characters, multi-byte runes, carriage returns, and newlines properly.
    /// </summary>
    public void TypeText(string text, int delayBetweenKeysMs = 10, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (string.IsNullOrEmpty(text))
            return;

        for (int i = 0; i < text.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            char c = text[i];

            if (c == '\r')
                continue;

            if (c == '\n')
            {
                KeyPress(KeyCode.Enter, 20, ct);
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
                continue;
            }

            if (c == '\t')
            {
                KeyPress(KeyCode.Tab, 20, ct);
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
                continue;
            }

            var down = new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    Ki = new KEYBDINPUT
                    {
                        WScan = c,
                        DwFlags = NativeMethods.KEYEVENTF_UNICODE,
                        DwExtraInfo = Marker
                    }
                }
            };

            var up = new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    Ki = new KEYBDINPUT
                    {
                        WScan = c,
                        DwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                        DwExtraInfo = Marker
                    }
                }
            };

            NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());

            if (delayBetweenKeysMs > 0)
                Thread.Sleep(delayBetweenKeysMs);
        }
    }

    /// <summary>
    /// Executes a hotkey sequence parsed from a string representation (e.g. "ctrl+s", "alt+f4", "ctrl+shift+esc").
    /// </summary>
    public void ExecuteHotkey(string hotkeyCombo, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hotkeyCombo);

        var (modifiers, targetKey) = ParseHotkeyString(hotkeyCombo);
        ExecuteHotkey(modifiers, targetKey, ct);
    }

    /// <summary>
    /// Executes a hotkey sequence with specified modifiers and primary key.
    /// Modifiers are pressed down in sequence and released in reverse order in a finally block.
    /// </summary>
    public void ExecuteHotkey(IReadOnlyList<KeyCode> modifiers, KeyCode targetKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var pressedModifiers = new List<ushort>();
        try
        {
            foreach (var mod in modifiers)
            {
                ct.ThrowIfCancellationRequested();
                ushort vk = KeyCodeToVk(mod);
                SendKey(vk, down: true);
                pressedModifiers.Add(vk);
                lock (_stateGate) _heldVirtualKeys.Add(vk);
            }

            Thread.Sleep(20);
            ct.ThrowIfCancellationRequested();

            ushort targetVk = KeyCodeToVk(targetKey);
            SendKey(targetVk, down: true);
            Thread.Sleep(30);
            SendKey(targetVk, down: false);
        }
        finally
        {
            for (int i = pressedModifiers.Count - 1; i >= 0; i--)
            {
                ushort modVk = pressedModifiers[i];
                try
                {
                    SendKey(modVk, down: false);
                    lock (_stateGate) _heldVirtualKeys.Remove(modVk);
                }
                catch { /* Ensure all modifier releases complete */ }
            }
        }
    }

    /// <summary>
    /// Simulates a single key press and release with hold duration.
    /// </summary>
    public void KeyPress(KeyCode key, int holdDurationMs = 25, CancellationToken ct = default)
    {
        KeyPress(KeyCodeToVk(key), holdDurationMs, ct);
    }

    /// <summary>
    /// Simulates a single key press and release by Virtual Key code.
    /// </summary>
    public void KeyPress(ushort vk, int holdDurationMs = 25, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SendKey(vk, down: true);
        lock (_stateGate) _heldVirtualKeys.Add(vk);

        if (holdDurationMs > 0)
            Thread.Sleep(holdDurationMs);

        SendKey(vk, down: false);
        lock (_stateGate) _heldVirtualKeys.Remove(vk);
    }

    /// <summary>
    /// Presses and holds a key down.
    /// </summary>
    public void KeyDown(KeyCode key) => KeyDown(KeyCodeToVk(key));

    /// <summary>
    /// Presses and holds a key down by Virtual Key code.
    /// </summary>
    public void KeyDown(ushort vk)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SendKey(vk, down: true);
        lock (_stateGate) _heldVirtualKeys.Add(vk);
    }

    /// <summary>
    /// Releases a held key.
    /// </summary>
    public void KeyUp(KeyCode key) => KeyUp(KeyCodeToVk(key));

    /// <summary>
    /// Releases a held key by Virtual Key code.
    /// </summary>
    public void KeyUp(ushort vk)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SendKey(vk, down: false);
        lock (_stateGate) _heldVirtualKeys.Remove(vk);
    }

    private static void SendKey(ushort vk, bool down)
    {
        uint flags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP;
        ushort scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);

        if (IsExtendedKey(vk))
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                Ki = new KEYBDINPUT
                {
                    WVk = vk,
                    WScan = scan,
                    DwFlags = flags,
                    DwExtraInfo = Marker
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 // PageUp, PageDown, End, Home
        or 0x25 or 0x26 or 0x27 or 0x28 // Left, Up, Right, Down
        or 0x2D or 0x2E // Insert, Delete
        or 0x5B or 0x5C or 0x5D // LWin, RWin, Apps
        or 0xA3 or 0xA5 // RControl, RMenu
        or 0x6F; // Divide

    private static (IReadOnlyList<KeyCode> Modifiers, KeyCode Key) ParseHotkeyString(string combo)
    {
        var parts = combo.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<KeyCode>();
        KeyCode? primaryKey = null;

        foreach (var part in parts)
        {
            string lower = part.ToLowerInvariant();
            if (lower is "ctrl" or "control")
                modifiers.Add(KeyCode.Ctrl);
            else if (lower is "shift")
                modifiers.Add(KeyCode.Shift);
            else if (lower is "alt" or "menu")
                modifiers.Add(KeyCode.Alt);
            else if (lower is "win" or "windows" or "super" or "cmd")
                modifiers.Add(KeyCode.Win);
            else
            {
                primaryKey = ParseKeyName(lower);
            }
        }

        if (!primaryKey.HasValue)
            throw new ArgumentException($"Invalid hotkey string: \"{combo}\" (no primary key found)");

        return (modifiers, primaryKey.Value);
    }

    private static KeyCode ParseKeyName(string name) => name switch
    {
        "enter" or "return" => KeyCode.Enter,
        "esc" or "escape" => KeyCode.Escape,
        "tab" => KeyCode.Tab,
        "space" => KeyCode.Space,
        "backspace" or "back" => KeyCode.Backspace,
        "del" or "delete" => KeyCode.Delete,
        "up" => KeyCode.Up,
        "down" => KeyCode.Down,
        "left" => KeyCode.Left,
        "right" => KeyCode.Right,
        "home" => KeyCode.Home,
        "end" => KeyCode.End,
        "pageup" or "pgup" => KeyCode.PageUp,
        "pagedown" or "pgdn" => KeyCode.PageDown,
        "pause" => KeyCode.Pause,
        "f1" => KeyCode.F1,
        "f2" => KeyCode.F2,
        "f3" => KeyCode.F3,
        "f4" => KeyCode.F4,
        "f5" => KeyCode.F5,
        "f6" => KeyCode.F6,
        "f7" => KeyCode.F7,
        "f8" => KeyCode.F8,
        "f9" => KeyCode.F9,
        "f10" => KeyCode.F10,
        "f11" => KeyCode.F11,
        "f12" => KeyCode.F12,
        _ when name.Length == 1 && name[0] >= 'a' && name[0] <= 'z' => (KeyCode)((int)KeyCode.A + (name[0] - 'a')),
        _ when name.Length == 1 && name[0] >= '0' && name[0] <= '9' => (KeyCode)((int)KeyCode.D0 + (name[0] - '0')),
        _ => throw new ArgumentException($"Unrecognized key name: \"{name}\"")
    };

    public static ushort KeyCodeToVk(KeyCode key) => key switch
    {
        KeyCode.Enter => 0x0D,
        KeyCode.Escape => 0x1B,
        KeyCode.Tab => 0x09,
        KeyCode.Space => 0x20,
        KeyCode.Backspace => 0x08,
        KeyCode.Delete => 0x2E,
        KeyCode.Left => 0x25,
        KeyCode.Up => 0x26,
        KeyCode.Right => 0x27,
        KeyCode.Down => 0x28,
        KeyCode.Home => 0x24,
        KeyCode.End => 0x23,
        KeyCode.PageUp => 0x21,
        KeyCode.PageDown => 0x22,
        KeyCode.Ctrl => 0xA2, // VK_LCONTROL
        KeyCode.Shift => 0xA0, // VK_LSHIFT
        KeyCode.Alt => 0xA4, // VK_LMENU
        KeyCode.Win => 0x5B, // VK_LWIN
        KeyCode.Pause => 0x13,
        KeyCode.F1 => 0x70,
        KeyCode.F2 => 0x71,
        KeyCode.F3 => 0x72,
        KeyCode.F4 => 0x73,
        KeyCode.F5 => 0x74,
        KeyCode.F6 => 0x75,
        KeyCode.F7 => 0x76,
        KeyCode.F8 => 0x77,
        KeyCode.F9 => 0x78,
        KeyCode.F10 => 0x79,
        KeyCode.F11 => 0x7A,
        KeyCode.F12 => 0x7B,
        >= KeyCode.A and <= KeyCode.Z => (ushort)(0x41 + (int)key - (int)KeyCode.A),
        >= KeyCode.D0 and <= KeyCode.D9 => (ushort)(0x30 + (int)key - (int)KeyCode.D0),
        _ => throw new NotSupportedException($"No VirtualKey mapping for {key}")
    };

    #endregion

    #region Window & Process Management

    /// <summary>
    /// Launches an application or process inside the current environment.
    /// Resolves common application names (Notepad, Chrome, Blender, etc.) automatically.
    /// </summary>
    public Process LaunchProcess(
        string fileName,
        string? arguments = null,
        string? workingDirectory = null,
        bool waitForInputIdle = true,
        int waitTimeoutMs = 5000)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string resolvedExe = ResolveExecutablePath(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = true
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch process for \"{resolvedExe}\".");

        if (waitForInputIdle)
        {
            try
            {
                process.WaitForInputIdle(waitTimeoutMs);
            }
            catch
            {
                // Non-GUI or console apps might throw InvalidOperationException on WaitForInputIdle
            }
        }

        return process;
    }

    /// <summary>
    /// Attempts to bring the specified window to the foreground and activate it.
    /// Uses Win32 thread input attachment to bypass standard Windows focus-lock restrictions.
    /// </summary>
    public bool SetForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return false;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, SW_RESTORE);
        else
            NativeMethods.ShowWindow(hWnd, SW_SHOW);

        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
        uint foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);

        bool attached = false;
        if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
        {
            attached = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
        }

        try
        {
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        return NativeMethods.GetForegroundWindow() == hWnd;
    }

    /// <summary>
    /// Activates the window represented by the given HWND.
    /// </summary>
    public bool ActivateWindow(IntPtr hWnd) => SetForegroundWindow(hWnd);

    /// <summary>
    /// Finds and activates a window by title or process name within the given timeout.
    /// </summary>
    public async Task<bool> ActivateWindowAsync(
        string windowTitleOrProcess,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = FindTargetWindow(windowTitleOrProcess, windowTitleOrProcess);
            if (hwnd != IntPtr.Zero)
            {
                if (SetForegroundWindow(hwnd))
                    return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Closes a window gracefully by sending WM_CLOSE.
    /// </summary>
    public bool CloseWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return false;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        return PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Closes a window found by title.
    /// </summary>
    public bool CloseWindow(string windowTitle)
    {
        var hwnd = FindTargetWindow(windowTitle: windowTitle);
        return hwnd != IntPtr.Zero && CloseWindow(hwnd);
    }

    /// <summary>
    /// Waits asynchronously until a matching window appears or timeout expires.
    /// </summary>
    public async Task<IntPtr> WaitForWindowAsync(
        string windowTitleOrProcess,
        int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = FindTargetWindow(windowTitleOrProcess, windowTitleOrProcess);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Searches top-level windows matching title, process name, or process ID.
    /// </summary>
    public IntPtr FindTargetWindow(string? windowTitle = null, string? processName = null, int? processId = null)
    {
        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (processId.HasValue)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                if (pid == processId.Value)
                {
                    found = hwnd;
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(windowTitle))
            {
                var sb = new StringBuilder(512);
                NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(processName))
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false;
                    }
                }
                catch { /* Access denied or process exited */ }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string ResolveExecutablePath(string raw)
    {
        string trimmed = raw.Trim().Trim('"');

        if (File.Exists(trimmed))
            return trimmed;

        string lower = Path.GetFileNameWithoutExtension(trimmed).ToLowerInvariant();

        switch (lower)
        {
            case "notepad":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

            case "chrome":
                string chromePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(chromePath)) return chromePath;
                string chromeX86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(chromeX86)) return chromeX86;
                return "chrome.exe";

            case "blender":
                string blenderDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Blender Foundation");
                if (Directory.Exists(blenderDir))
                {
                    var found = Directory.GetFiles(blenderDir, "blender.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
                return "blender.exe";

            case "calc" or "calculator":
                return "calc.exe";

            case "explorer":
                return "explorer.exe";

            case "cmd":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

            case "powershell":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

            default:
                if (!Path.HasExtension(trimmed))
                    return trimmed + ".exe";
                return trimmed;
        }
    }

    #endregion

    #region Safety & Release Semantics

    /// <summary>
    /// Sweeps and releases all currently held mouse buttons and virtual keys.
    /// Always called upon batch failure, cancel, or disposal to ensure no inputs remain stuck.
    /// </summary>
    public void ReleaseAll()
    {
        lock (_stateGate)
        {
            // Release mouse buttons
            foreach (var downFlag in _heldButtons.ToArray())
            {
                uint upFlag = downFlag switch
                {
                    NativeMethods.MOUSEEVENTF_LEFTDOWN => NativeMethods.MOUSEEVENTF_LEFTUP,
                    NativeMethods.MOUSEEVENTF_RIGHTDOWN => NativeMethods.MOUSEEVENTF_RIGHTUP,
                    NativeMethods.MOUSEEVENTF_MIDDLEDOWN => NativeMethods.MOUSEEVENTF_MIDDLEUP,
                    _ => 0
                };

                if (upFlag != 0)
                {
                    try { SendMouseFlag(upFlag); } catch { }
                }
            }
            _heldButtons.Clear();

            // Release virtual keys
            foreach (var vk in _heldVirtualKeys.ToArray())
            {
                try { SendKey(vk, down: false); } catch { }
            }
            _heldVirtualKeys.Clear();

            // Safety sweep standard modifiers unconditionally
            ushort[] standardModifiers = { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };
            foreach (var mod in standardModifiers)
            {
                try { SendKey(mod, down: false); } catch { }
            }
        }
    }

    /// <summary>
    /// Disposes resources and ensures all held hardware inputs are cleanly released.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        ReleaseAll();
        GC.SuppressFinalize(this);
    }

    #endregion
}
