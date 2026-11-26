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

public sealed partial class GhostInputExecutor : IDisposable
{


    /// <summary>
    /// Moves the mouse pointer to the specified physical desktop coordinates.
    /// Combines SetCursorPos and SendInput with normalized coordinates for multi-session reliability.
    /// </summary>
    public void MoveMouse(int x, int y, bool smooth = false, int steps = 10, int delayMs = 5, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        steps = Math.Clamp(steps, 1, 200);
        delayMs = Math.Clamp(delayMs, 0, 100);

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
        count = Math.Clamp(count, 1, 8);
        MoveMouse(x, y, smooth: false, ct: ct);

        var (downFlag, upFlag) = GetButtonFlags(button);

        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SendMouseFlag(downFlag);
            lock (_stateGate) _heldButtons.Add(downFlag);

            try
            {
                Thread.Sleep(20);
            }
            finally
            {
                SendMouseFlag(upFlag);
                lock (_stateGate) _heldButtons.Remove(downFlag);
            }

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
            steps = Math.Clamp(steps, 4, 200);
            durationMs = Math.Clamp(durationMs, 25, 10_000);
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
}
