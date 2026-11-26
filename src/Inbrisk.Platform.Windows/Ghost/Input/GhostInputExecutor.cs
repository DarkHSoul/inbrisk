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
/// Hardware-level independent mouse, keyboard, and window action execution engine for Windows sessions
/// (e.g. Session 2 / Ghost RDP sessions).
/// Operates directly via Win32 SendInput, native window management APIs, and process orchestration.
/// Guarantees zero stuck keys/buttons, strict failFast sequencing, and safe release semantics.
/// </summary>
public sealed partial class GhostInputExecutor : IDisposable
{

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



    private const int MaxSteps = 500;

    private readonly object _stateGate = new();
    private readonly HashSet<uint> _heldButtons = new();

    private readonly HashSet<ushort> _heldVirtualKeys = new();
    private bool _isDisposed;

    /// <summary>Optional target session ID (for multi-session diagnostics).</summary>
    public int? TargetSessionId { get; }


    /// <summary>
    /// Initializes a new instance of the <see cref="GhostInputExecutor"/>.
    /// </summary>
    /// <param name="targetSessionId">Optional target session identifier.</param>
    public GhostInputExecutor(int? targetSessionId = null)
    {
        TargetSessionId = targetSessionId;
    }


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

        if (steps.Count > MaxSteps)
        {
            return new GhostBatchResult(
                Success: false,
                StepsCompleted: 0,
                TotalSteps: steps.Count,
                StepResults: Array.Empty<GhostStepResult>(),
                FailureReason: $"Batch exceeds the {MaxSteps}-step limit.",
                TotalDuration: TimeSpan.Zero);
        }

        if (EmergencyGate.IsStopped)
        {
            return new GhostBatchResult(
                Success: false,
                StepsCompleted: 0,
                TotalSteps: steps.Count,
                StepResults: Array.Empty<GhostStepResult>(),
                FailureReason: "Computer control stopped by the local emergency hotkey; only the local user can resume.",
                TotalDuration: TimeSpan.Zero);
        }

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

                if (EmergencyGate.IsStopped)
                {
                    stepError = "Computer control stopped by the local emergency hotkey; only the local user can resume.";
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

                if (!stepSuccess && failFast && step.FailFast)
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
                    step.WaitForInputIdle, Math.Clamp(step.TimeoutMs, 100, 120_000));
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
                bool foundAndActivated = await ActivateWindowAsync(targetWin, Math.Clamp(step.TimeoutMs, 100, 120_000), ct).ConfigureAwait(false);
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
                int ms = Math.Clamp(step.Milliseconds ?? 100, 0, 300_000);
                if (ms > 0)
                    await Task.Delay(ms, ct).ConfigureAwait(false);
                return ms;

            case GhostActionKind.WaitForWindow:
                string waitTarget = step.WindowTitle ?? step.ProcessName ?? "";
                if (string.IsNullOrWhiteSpace(waitTarget))
                    throw new ArgumentException("WaitForWindow requires WindowTitle or ProcessName.");
                var hwnd = await WaitForWindowAsync(waitTarget, Math.Clamp(step.TimeoutMs, 100, 120_000), ct).ConfigureAwait(false);
                if (hwnd == IntPtr.Zero)
                    throw new TimeoutException($"Timeout waiting for window: \"{waitTarget}\"");
                return hwnd;

            default:
                throw new NotSupportedException($"Ghost action kind '{step.Kind}' is not supported.");
        }
    }



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
}
