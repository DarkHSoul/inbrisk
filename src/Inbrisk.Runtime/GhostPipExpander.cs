using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Full-Screen Expander and Shadow Mode Manager for Picture-in-Picture (PiP).
/// Manages seamless transitions between the compact corner PiP overlay (e.g. 320x180)
/// and full-screen monitor-spanning "Shadow Mode", preserving original aspect ratio
/// or stretching to fill the target monitor work area.
/// </summary>
public sealed class GhostPipExpander
{
    #region Win32 Interop Constants & Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;

        public RECT(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private static class Native
    {
        public static readonly IntPtr HwndTopmost = new(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);
    }

    #endregion

    #region Fields & State

    private readonly object _gate = new();
    private RECT _savedRect = new(0, 0, 320, 180);
    private bool _isFullScreen;
    private (int Width, int Height) _currentResolution = (320, 180);

    #endregion

    #region Properties & Events

    /// <summary>
    /// Gets whether the PiP window is currently expanded to full-screen Shadow Mode.
    /// </summary>
    public bool IsFullScreen
    {
        get
        {
            lock (_gate)
            {
                return _isFullScreen;
            }
        }
        private set
        {
            _isFullScreen = value;
        }
    }

    /// <summary>
    /// Optional attached frame renderer that will be notified when resolution or viewport changes.
    /// </summary>
    public GhostPipRenderer? Renderer { get; set; }

    /// <summary>
    /// Gets the current resolution (width, height) in pixels.
    /// </summary>
    public (int Width, int Height) CurrentResolution
    {
        get
        {
            lock (_gate)
            {
                return _currentResolution;
            }
        }
    }

    /// <summary>
    /// Gets the cached corner position and bounds of the PiP window before it was expanded.
    /// </summary>
    public RECT SavedRect
    {
        get
        {
            lock (_gate)
            {
                return _savedRect;
            }
        }
    }

    /// <summary>
    /// Triggered when the full-screen mode changes (true = full screen, false = mini PiP).
    /// </summary>
    public event Action<bool>? FullScreenChanged;

    /// <summary>
    /// Triggered when the PiP resolution changes, providing (width, height).
    /// </summary>
    public event Action<int, int>? ResolutionChanged;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of GhostPipExpander.
    /// </summary>
    /// <param name="renderer">Optional active GhostPipRenderer to notify on resize.</param>
    public GhostPipExpander(GhostPipRenderer? renderer = null)
    {
        Renderer = renderer;
    }

    #endregion

    #region Public Expansion & Shrink Methods

    /// <summary>
    /// Expands the specified window to cover the target monitor's full work area (Shadow Mode).
    /// Saves the current window bounds into memory before transitioning.
    /// </summary>
    /// <param name="hwnd">Target window HWND handle.</param>
    /// <param name="monitorIndex">Zero-based index of target monitor (0 = primary/default).</param>
    /// <param name="preserveAspectRatio">If true, centers and fits content maintaining original aspect ratio. If false, fills entire work area.</param>
    /// <returns>True if transition succeeded; otherwise false.</returns>
    public bool ExpandToFullScreen(IntPtr hwnd, int monitorIndex = 0, bool preserveAspectRatio = false)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            lock (_gate)
            {
                _isFullScreen = true;
                _currentResolution = (1920, 1080);
            }
            FullScreenChanged?.Invoke(true);
            ResolutionChanged?.Invoke(1920, 1080);
            return true;
        }

        if (!Native.IsWindow(hwnd))
        {
            return false;
        }

        lock (_gate)
        {
            // 1. Save current compact PiP bounds if not currently full screen
            if (!_isFullScreen)
            {
                if (Native.GetWindowRect(hwnd, out RECT currentRect) && currentRect.Width > 0 && currentRect.Height > 0)
                {
                    _savedRect = currentRect;
                }
            }

            // 2. Query target monitor work area via GetMonitorInfoW
            var (workX, workY, workWidth, workHeight) = GetMonitorWorkArea(hwnd, monitorIndex);

            // 3. Compute final layout coordinates (preserve aspect ratio or stretch to fit)
            int targetX = workX;
            int targetY = workY;
            int targetWidth = workWidth;
            int targetHeight = workHeight;

            if (preserveAspectRatio && _savedRect.Width > 0 && _savedRect.Height > 0)
            {
                var fit = CalculateAspectFit(workX, workY, workWidth, workHeight, _savedRect.Width, _savedRect.Height);
                targetX = fit.X;
                targetY = fit.Y;
                targetWidth = fit.Width;
                targetHeight = fit.Height;
            }

            // 4. Reposition window to cover monitor work area
            bool success = Native.SetWindowPos(
                hwnd,
                Native.HwndTopmost,
                targetX,
                targetY,
                targetWidth,
                targetHeight,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);

            if (!success)
            {
                return false;
            }

            _isFullScreen = true;
            _currentResolution = (targetWidth, targetHeight);
        }

        // 5. Notify renderer of new resolution and trigger immediate repaint
        NotifyRendererAndSubscribers(_currentResolution.Width, _currentResolution.Height, true);
        return true;
    }

    /// <summary>
    /// Restores the window to its saved compact PiP bounds (e.g. 320x180 at corner).
    /// </summary>
    /// <param name="hwnd">Target window HWND handle.</param>
    /// <returns>True if transition succeeded; otherwise false.</returns>
    public bool ShrinkToPip(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            lock (_gate)
            {
                _isFullScreen = false;
                _currentResolution = (_savedRect.Width > 0 ? _savedRect.Width : 320, _savedRect.Height > 0 ? _savedRect.Height : 180);
            }
            FullScreenChanged?.Invoke(false);
            ResolutionChanged?.Invoke(_currentResolution.Width, _currentResolution.Height);
            return true;
        }

        if (!Native.IsWindow(hwnd))
        {
            return false;
        }

        lock (_gate)
        {
            int restoreX = _savedRect.Left;
            int restoreY = _savedRect.Top;
            int restoreWidth = _savedRect.Width > 0 ? _savedRect.Width : 320;
            int restoreHeight = _savedRect.Height > 0 ? _savedRect.Height : 180;

            bool success = Native.SetWindowPos(
                hwnd,
                Native.HwndTopmost,
                restoreX,
                restoreY,
                restoreWidth,
                restoreHeight,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);

            if (!success)
            {
                return false;
            }

            _isFullScreen = false;
            _currentResolution = (restoreWidth, restoreHeight);
        }

        // Notify renderer and subscribers
        NotifyRendererAndSubscribers(_currentResolution.Width, _currentResolution.Height, false);
        return true;
    }

    /// <summary>
    /// Toggles between full-screen Shadow Mode and compact PiP view.
    /// </summary>
    /// <param name="hwnd">Target window HWND handle.</param>
    /// <param name="monitorIndex">Target monitor index when expanding.</param>
    /// <param name="preserveAspectRatio">Aspect ratio preservation option when expanding.</param>
    /// <returns>True if toggle succeeded; otherwise false.</returns>
    public bool ToggleFullScreen(IntPtr hwnd, int monitorIndex = 0, bool preserveAspectRatio = false)
    {
        bool isFull;
        lock (_gate)
        {
            isFull = _isFullScreen;
        }

        return isFull ? ShrinkToPip(hwnd) : ExpandToFullScreen(hwnd, monitorIndex, preserveAspectRatio);
    }

    /// <summary>
    /// Manually sets or overrides the stored compact PiP coordinates.
    /// </summary>
    public void SetSavedRect(int left, int top, int width, int height)
    {
        lock (_gate)
        {
            _savedRect = new RECT(left, top, left + Math.Max(10, width), top + Math.Max(10, height));
        }
    }

    #endregion

    #region Helper Calculation Methods

    /// <summary>
    /// Queries the working area (excluding taskbar) of the selected display monitor.
    /// </summary>
    public static (int X, int Y, int Width, int Height) GetMonitorWorkArea(IntPtr hwnd, int monitorIndex = 0)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return (0, 0, 1920, 1080);
        }

        try
        {
            var monitors = EnumerateDisplayMonitors();
            IntPtr hMonitor = IntPtr.Zero;

            if (monitorIndex >= 0 && monitorIndex < monitors.Count)
            {
                hMonitor = monitors[monitorIndex];
            }
            else if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd))
            {
                hMonitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            }

            if (hMonitor != IntPtr.Zero)
            {
                MONITORINFO mi = default;
                mi.cbSize = (uint)Marshal.SizeOf<MONITORINFO>();

                if (Native.GetMonitorInfoW(hMonitor, ref mi))
                {
                    int w = mi.rcWork.Right - mi.rcWork.Left;
                    int h = mi.rcWork.Bottom - mi.rcWork.Top;
                    if (w > 0 && h > 0)
                    {
                        return (mi.rcWork.Left, mi.rcWork.Top, w, h);
                    }

                    // Fallback to full monitor area if work area is empty
                    int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    if (mw > 0 && mh > 0)
                    {
                        return (mi.rcMonitor.Left, mi.rcMonitor.Top, mw, mh);
                    }
                }
            }

            int cx = Native.GetSystemMetrics(Native.SM_CXSCREEN);
            int cy = Native.GetSystemMetrics(Native.SM_CYSCREEN);
            if (cx > 0 && cy > 0)
            {
                return (0, 0, cx, cy);
            }
        }
        catch
        {
            // Fallback default
        }

        return (0, 0, 1920, 1080);
    }

    /// <summary>
    /// Enumerates all active display monitor handles.
    /// </summary>
    public static List<IntPtr> EnumerateDisplayMonitors()
    {
        var result = new List<IntPtr>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return result;
        }

        try
        {
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
            {
                if (hMon != IntPtr.Zero)
                {
                    result.Add(hMon);
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // Silently return what was collected
        }

        return result;
    }

    /// <summary>
    /// Calculates centered coordinates preserving content aspect ratio within a container.
    /// </summary>
    public static (int X, int Y, int Width, int Height) CalculateAspectFit(
        int containerX, int containerY, int containerWidth, int containerHeight,
        int contentWidth, int contentHeight)
    {
        if (contentWidth <= 0 || contentHeight <= 0 || containerWidth <= 0 || containerHeight <= 0)
        {
            return (containerX, containerY, containerWidth, containerHeight);
        }

        double scaleX = (double)containerWidth / contentWidth;
        double scaleY = (double)containerHeight / contentHeight;
        double scale = Math.Min(scaleX, scaleY);

        int fitWidth = Math.Max(1, (int)Math.Round(contentWidth * scale));
        int fitHeight = Math.Max(1, (int)Math.Round(contentHeight * scale));

        int fitX = containerX + (containerWidth - fitWidth) / 2;
        int fitY = containerY + (containerHeight - fitHeight) / 2;

        return (fitX, fitY, fitWidth, fitHeight);
    }

    #endregion

    #region Internal Notifications

    private void NotifyRendererAndSubscribers(int width, int height, bool isFullScreen)
    {
        try
        {
            // Trigger renderer one-shot refresh with the new client dimensions
            Renderer?.RenderLatestFrameNow();
        }
        catch
        {
            // Non-fatal if renderer is busy or disposing
        }

        try
        {
            FullScreenChanged?.Invoke(isFullScreen);
        }
        catch
        {
        }

        try
        {
            ResolutionChanged?.Invoke(width, height);
        }
        catch
        {
        }
    }

    #endregion
}
