using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

/// <summary>
/// Pure Win32 native Picture-in-Picture (PiP) window host.
/// Stays topmost on the user's desktop, never steals focus (WS_EX_NOACTIVATE),
/// and passes clicks through (WS_EX_TRANSPARENT) so games or active applications
/// are never interrupted.
/// Supports dynamic click-through toggling, alpha opacity, resizing/repositioning,
/// and a clean WndProc message loop lifecycle.
/// </summary>
public sealed class GhostPipWindowHost : IDisposable
{
    private const string WindowClassName = "InbriskGhostPipWindowClass";
    private const int GwlpUserdata = -21;
    private const uint WM_ERASEBKGND = 0x0014;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private const uint WmAppSetClickThrough = NativeMethods.WM_APP + 210;
    private const uint WmAppSetOpacity = NativeMethods.WM_APP + 211;
    private const uint WmAppSetPosSize = NativeMethods.WM_APP + 212;
    private const uint WmAppSetVisible = NativeMethods.WM_APP + 213;
    private const uint WmAppInvalidate = NativeMethods.WM_APP + 214;
    private const uint WmAppToggleExpand = NativeMethods.WM_APP + 215;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly object ClassGate = new();
    private static ushort _classAtom;
    private static NativeMethods.WndProc? _wndProcDelegate;

    private readonly ManualResetEventSlim _ready = new();
    private readonly string _title;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd = IntPtr.Zero;
    private GCHandle _hwndHandle;

    private volatile bool _clickThrough;
    private volatile byte _opacity;
    private volatile int _x;
    private volatile int _y;
    private volatile int _width;
    private volatile int _height;
    private volatile bool _visible = true;
    private volatile bool _disposed;

    private int _savedCompactWidth = 320;
    private int _savedCompactHeight = 180;
    private int _savedCompactX = -1;
    private int _savedCompactY = -1;
    private bool _isExpanded;

    /// <summary>
    /// Raised during WM_PAINT so custom renderers (GDI / DirectX / Capture streams) can paint directly.
    /// Parameters: HDC, width, height.
    /// </summary>
    public event Action<IntPtr, int, int>? Paint;

    /// <summary>
    /// Raised when the native window handle has been created.
    /// </summary>
    public event Action<GhostPipWindowHost>? WindowCreated;

    /// <summary>
    /// Raised when the native window handle is destroyed.
    /// </summary>
    public event Action<GhostPipWindowHost>? WindowDestroyed;

    /// <summary>Height of the top drag handle bar in pixels when interactive.</summary>
    public const int HeaderHeight = 26;

    /// <summary>
    /// Raised when the user drags or resizes the PiP window.
    /// </summary>
    public event Action<int, int>? PositionChanged;

    /// <summary>
    /// Raised when the user drags or resizes the PiP window, providing full bounds (X, Y, Width, Height).
    /// </summary>
    public event Action<int, int, int, int>? BoundsChanged;

    /// <summary>
    /// Raised when an interactive mouse or input event occurs in the PiP client area below the header bar.
    /// </summary>
    public event Action<PipInputEventArgs>? OnInteractiveInput;

    /// <summary>
    /// Gets the native Win32 window handle (HWND).
    /// </summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>
    /// Gets whether the message pump thread is currently running.
    /// </summary>
    public bool IsRunning => _thread != null && _thread.IsAlive && _hwnd != IntPtr.Zero;

    /// <summary>
    /// Gets whether click-through (WS_EX_TRANSPARENT) is currently enabled.
    /// </summary>
    public bool ClickThrough => _clickThrough;

    /// <summary>
    /// Gets the current alpha opacity (0 = transparent, 255 = opaque).
    /// </summary>
    public byte Opacity => _opacity;

    /// <summary>
    /// Gets the current X coordinate.
    /// </summary>
    public int X => _x;

    /// <summary>
    /// Gets the current Y coordinate.
    /// </summary>
    public int Y => _y;

    /// <summary>
    /// Gets the current window width.
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Gets the current window height.
    /// </summary>
    public int Height => _height;

    /// <summary>
    /// Gets whether the window is currently visible.
    /// </summary>
    public bool Visible => _visible;

    /// <summary>
    /// Initializes a new instance of the <see cref="GhostPipWindowHost"/> class.
    /// </summary>
    /// <param name="x">Initial X position.</param>
    /// <param name="y">Initial Y position.</param>
    /// <param name="width">Initial window width.</param>
    /// <param name="height">Initial window height.</param>
    /// <param name="initialOpacity">Initial alpha opacity (0-255).</param>
    /// <param name="clickThrough">Whether mouse clicks pass through the window.</param>
    /// <param name="autoStart">Whether to automatically start the message pump thread.</param>
    /// <param name="title">Window title text.</param>
    public GhostPipWindowHost(
        int x = 100,
        int y = 100,
        int width = 480,
        int height = 270,
        byte initialOpacity = 255,
        bool clickThrough = true,
        bool autoStart = true,
        string title = "Inbrisk Ghost PiP")
    {
        _x = x;
        _y = y;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        _opacity = initialOpacity;
        _clickThrough = clickThrough;
        _title = title;

        if (autoStart)
        {
            Start();
        }
    }

    /// <summary>
    /// Starts the dedicated STA message pump thread and creates the PiP host window.
    /// Blocks until the window handle is initialized or timeout expires.
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds to wait for window initialization.</param>
    public bool Start(int timeoutMs = 5000)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostPipWindowHost));
        if (_thread != null && _thread.IsAlive) return true;

        _ready.Reset();
        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "InbriskGhostPipWindow"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        return _ready.Wait(timeoutMs);
    }

    /// <summary>
    /// Blocks until the message pump thread has completed initialization.
    /// </summary>
    public bool WaitForReady(int timeoutMs = 5000) => _ready.Wait(timeoutMs);

    /// <summary>
    /// Dynamically enables or disables click-through behavior (WS_EX_TRANSPARENT).
    /// When enabled, mouse clicks pass directly through to whatever window is beneath.
    /// When disabled, mouse clicks are received by this PiP window (interactive mode).
    /// </summary>
    public void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppSetClickThrough, enabled ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            ApplyClickThrough(enabled);
        }
    }

    /// <summary>
    /// Sets the window opacity (0 = completely transparent, 255 = completely opaque).
    /// </summary>
    public void SetOpacity(byte alpha)
    {
        _opacity = alpha;
        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppSetOpacity, new IntPtr(alpha), IntPtr.Zero);
        }
        else
        {
            ApplyOpacity(alpha);
        }
    }

    /// <summary>
    /// Updates the window position and size on the desktop.
    /// </summary>
    public void SetPositionAndSize(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppSetPosSize, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            ApplyPositionAndSize(_x, _y, _width, _height);
        }
    }

    /// <summary>
    /// Shows or hides the PiP window without taking focus.
    /// </summary>
    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppSetVisible, visible ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            ApplyVisibility(visible);
        }
    }

    /// <summary>
    /// Forces the window client area to be invalidated and repainted.
    /// </summary>
    public void Invalidate(bool erase = false)
    {
        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppInvalidate, erase ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }
        else if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, erase);
        }
    }

    /// <summary>
    /// Gets whether the PiP window is currently in expanded size mode.
    /// </summary>
    public bool IsExpanded => _isExpanded;

    /// <summary>
    /// Toggles the PiP window between compact size (~320x180) and 2x expanded size (~640x360).
    /// </summary>
    public void ToggleExpand()
    {
        if (_threadId != 0 && Thread.CurrentThread.ManagedThreadId != _thread?.ManagedThreadId)
        {
            NativeMethods.PostThreadMessageW(_threadId, WmAppToggleExpand, IntPtr.Zero, IntPtr.Zero);
        }
        else
        {
            ToggleExpandCore();
        }
    }

    /// <summary>
    /// Configures screen capture exclusion (WDA_EXCLUDEFROMCAPTURE) so screen recorders
    /// or Inbrisk's own desktop vision can omit this PiP overlay.
    /// </summary>
    public void SetExcludeFromCapture(bool exclude)
    {
        if (_hwnd != IntPtr.Zero)
        {
            uint affinity = exclude ? NativeMethods.WDA_EXCLUDEFROMCAPTURE : NativeMethods.WDA_NONE;
            NativeMethods.SetWindowDisplayAffinity(_hwnd, affinity);
        }
    }

    private void Pump()
    {
        try
        {
            PumpCore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostPipWindowHost] Pump crashed: {ex}");
        }
        finally
        {
            _ready.Set();
        }
    }

    private void PumpCore()
    {
        DesktopBridge.TrySwitchCurrentThread();
        _threadId = NativeMethods.GetCurrentThreadId();

        // PMv2 DPI awareness is required so coordinates match physical screen pixels
        NativeMethods.SetProcessDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        try
        {
            EnsureClassRegistered();
            CreatePipWindow();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostPipWindowHost] Window initialization fault: {ex}");
        }
        finally
        {
            _ready.Set();
        }

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                if (msg.Message == WmAppSetClickThrough)
                {
                    ApplyClickThrough(msg.WParam != IntPtr.Zero);
                }
                else if (msg.Message == WmAppSetOpacity)
                {
                    ApplyOpacity((byte)(msg.WParam.ToInt64() & 0xFF));
                }
                else if (msg.Message == WmAppSetPosSize)
                {
                    ApplyPositionAndSize(_x, _y, _width, _height);
                }
                else if (msg.Message == WmAppSetVisible)
                {
                    ApplyVisibility(msg.WParam != IntPtr.Zero);
                }
                else if (msg.Message == WmAppInvalidate)
                {
                    if (_hwnd != IntPtr.Zero)
                    {
                        NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, msg.WParam != IntPtr.Zero);
                    }
                }
                else if (msg.Message == WmAppToggleExpand)
                {
                    ToggleExpandCore();
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostPipWindowHost] Message pump exception: {ex}");
            }
        }

        Teardown();
    }

    private static void EnsureClassRegistered()
    {
        lock (ClassGate)
        {
            if (_classAtom != 0) return;

            _wndProcDelegate = StaticWndProc;
            var wcx = new NativeMethods.WNDCLASSEXW
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                Style = 0x0008, // CS_DBLCLKS
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                CbClsExtra = 0,
                CbWndExtra = 0,
                HInstance = NativeMethods.GetModuleHandleW(null),
                HIcon = IntPtr.Zero,
                HCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_ARROW),
                HbrBackground = IntPtr.Zero,
                LpszMenuName = null,
                LpszClassName = WindowClassName,
                HIconSm = IntPtr.Zero
            };

            _classAtom = NativeMethods.RegisterClassExW(ref wcx);
            if (_classAtom == 0)
            {
                int err = Marshal.GetLastWin32Error();
                Debug.WriteLine($"[GhostPipWindowHost] RegisterClassExW returned 0, error={err}");
            }
        }
    }

    private void CreatePipWindow()
    {
        _hwndHandle = GCHandle.Alloc(this);

        // Core requirement styles:
        // WS_POPUP | WS_VISIBLE | WS_MINIMIZEBOX | WS_SYSMENU
        // WS_EX_TOPMOST (0x08)
        // WS_EX_NOACTIVATE (0x08000000) -> Never steals focus from active game or app
        // WS_EX_TRANSPARENT (0x20) -> Click-through passed to window underneath
        // WS_EX_APPWINDOW (0x40000) -> Appears in taskbar like a standard application
        // WS_EX_LAYERED (0x80000) -> Supports opacity & alpha blending
        uint dwStyle = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE | NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_SYSMENU | NativeMethods.WS_THICKFRAME;
        uint dwExStyle = (uint)(NativeMethods.WS_EX_TOPMOST |
                                NativeMethods.WS_EX_NOACTIVATE |
                                NativeMethods.WS_EX_APPWINDOW |
                                NativeMethods.WS_EX_LAYERED);

        if (_clickThrough)
        {
            dwExStyle |= (uint)NativeMethods.WS_EX_TRANSPARENT;
        }

        _hwnd = NativeMethods.CreateWindowExW(
            dwExStyle,
            WindowClassName,
            _title,
            dwStyle,
            _x,
            _y,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null),
            IntPtr.Zero);

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, GwlpUserdata, GCHandle.ToIntPtr(_hwndHandle));

            // Apply layered attributes (alpha)
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, _opacity, NativeMethods.LWA_ALPHA);

            // Position as topmost without activation
            uint swpFlags = NativeMethods.SWP_NOACTIVATE;
            if (_visible)
            {
                swpFlags |= NativeMethods.SWP_SHOWWINDOW;
            }

            NativeMethods.SetWindowPos(_hwnd, HwndTopmost, _x, _y, _width, _height, swpFlags);

            // Screen capture: WDA_NONE allows Windows Snipping Tool, Win+Shift+S, and PrtScn screenshots
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_NONE);

            WindowCreated?.Invoke(this);
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            Debug.WriteLine($"[GhostPipWindowHost] CreateWindowExW failed, error={err}");
        }
    }

    private void ApplyClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        if (_hwnd == IntPtr.Zero) return;

        var curExStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        long newExStyle = curExStyle;
        if (enabled)
        {
            newExStyle |= NativeMethods.WS_EX_TRANSPARENT;
        }
        else
        {
            newExStyle &= ~NativeMethods.WS_EX_TRANSPARENT;
        }

        if (newExStyle != curExStyle)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newExStyle));
            NativeMethods.SetWindowPos(
                _hwnd,
                HwndTopmost,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    private void ApplyOpacity(byte alpha)
    {
        _opacity = alpha;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
        }
    }

    private void ToggleExpandCore()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Current monitor work area in absolute desktop coordinates
        int monLeft = 0;
        int monTop = 0;
        int monRight = 1920;
        int monBottom = 1080;
        try
        {
            var hMon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            if (NativeMethods.GetMonitorInfoW(hMon, ref mi))
            {
                monLeft = mi.RcWork.Left;
                monTop = mi.RcWork.Top;
                monRight = mi.RcWork.Right;
                monBottom = mi.RcWork.Bottom;
            }
        }
        catch { }

        // If currently compact or medium (width <= 480), expand to large mode
        if (_width <= 480)
        {
            _savedCompactWidth = _width;
            _savedCompactHeight = _height;
            _savedCompactX = _x;
            _savedCompactY = _y;

            int newWidth = Math.Max(640, _savedCompactWidth * 2);
            int newHeight = Math.Max(360, (newWidth * 9) / 16);

            // Expand strictly in place: keep current position
            int newX = _x;
            int newY = _y;

            // Only nudge if the expanded window would exceed its current monitor boundary
            if (newX + newWidth > monRight)
            {
                newX = monRight - newWidth;
            }
            if (newX < monLeft)
            {
                newX = monLeft;
            }

            if (newY + newHeight > monBottom)
            {
                newY = monBottom - newHeight;
            }
            if (newY < monTop)
            {
                newY = monTop;
            }

            _x = newX;
            _y = newY;
            _width = newWidth;
            _height = newHeight;
            _isExpanded = true;

            ApplyPositionAndSize(_x, _y, _width, _height);
            PositionChanged?.Invoke(_x, _y);
            BoundsChanged?.Invoke(_x, _y, _width, _height);
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
        else
        {
            // Restore to compact size in place
            int restoreWidth = _savedCompactWidth > 0 ? _savedCompactWidth : 320;
            int restoreHeight = _savedCompactHeight > 0 ? _savedCompactHeight : 180;
            int restoreX = _savedCompactX;
            int restoreY = _savedCompactY;

            // If no valid saved position on current monitor, keep current position
            if (restoreX < monLeft || restoreX > monRight)
            {
                restoreX = _x;
            }
            if (restoreY < monTop || restoreY > monBottom)
            {
                restoreY = _y;
            }

            _x = restoreX;
            _y = restoreY;
            _width = restoreWidth;
            _height = restoreHeight;
            _isExpanded = false;

            ApplyPositionAndSize(_x, _y, _width, _height);
            PositionChanged?.Invoke(_x, _y);
            BoundsChanged?.Invoke(_x, _y, _width, _height);
            NativeMethods.InvalidateRect(_hwnd, IntPtr.Zero, false);
        }
    }

    private void ApplyPositionAndSize(int x, int y, int width, int height)
    {
        _x = x;
        _y = y;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        if (_hwnd != IntPtr.Zero)
        {
            uint flags = NativeMethods.SWP_NOACTIVATE;
            if (_visible)
            {
                flags |= NativeMethods.SWP_SHOWWINDOW;
            }

            NativeMethods.SetWindowPos(
                _hwnd,
                HwndTopmost,
                _x, _y, _width, _height,
                flags);
        }
    }

    private void ApplyVisibility(bool visible)
    {
        _visible = visible;
        if (_hwnd == IntPtr.Zero) return;

        if (visible)
        {
            NativeMethods.ShowWindow(_hwnd, SW_SHOWNA);
            NativeMethods.SetWindowPos(
                _hwnd,
                HwndTopmost,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        else
        {
            NativeMethods.ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private IntPtr InstanceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_NCHITTEST:
                // Double-guard: When click-through is enabled, inform Windows to pass hit tests through
                if (_clickThrough)
                {
                    return new IntPtr(NativeMethods.HTTRANSPARENT);
                }

                // Interactive Mode:
                int screenX = unchecked((short)(long)lParam);
                int screenY = unchecked((short)((long)lParam >> 16));
                var pt = new POINT { X = screenX, Y = screenY };
                NativeMethods.ScreenToClient(hWnd, ref pt);

                const int border = 12;
                const int corner = 18;

                // 1. Resizing corners
                if (pt.X <= corner && pt.Y <= corner)
                    return new IntPtr(NativeMethods.HTTOPLEFT);
                if (pt.X >= _width - corner && pt.Y <= corner)
                    return new IntPtr(NativeMethods.HTTOPRIGHT);
                if (pt.X <= corner && pt.Y >= _height - corner)
                    return new IntPtr(NativeMethods.HTBOTTOMLEFT);
                if (pt.X >= _width - corner && pt.Y >= _height - corner)
                    return new IntPtr(NativeMethods.HTBOTTOMRIGHT);

                // 2. Resizing borders
                if (pt.X <= border)
                    return new IntPtr(NativeMethods.HTLEFT); // En soldan tutup drag ile boyut değiştirme!
                if (pt.X >= _width - border)
                    return new IntPtr(NativeMethods.HTRIGHT);
                if (pt.Y >= _height - border)
                    return new IntPtr(NativeMethods.HTBOTTOM);
                if (pt.Y <= border)
                    return new IntPtr(NativeMethods.HTTOP);

                // 3. Header Action Buttons (Far right of header: Expand & Minimize buttons)
                if (pt.Y < HeaderHeight && pt.X >= _width - 56)
                {
                    return new IntPtr(NativeMethods.HTCLIENT);
                }

                // 4. Header Bar: Native smooth dragging of the whole window!
                if (pt.Y < HeaderHeight)
                {
                    return new IntPtr(NativeMethods.HTCAPTION);
                }

                return new IntPtr(NativeMethods.HTCLIENT);

            case NativeMethods.WM_NCCALCSIZE:
                // Return 0 so client area fills entire window without standard OS borders
                if (wParam != IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_NCLBUTTONDOWN:
            {
                int hit = wParam.ToInt32();
                if (hit == NativeMethods.HTLEFT || hit == NativeMethods.HTRIGHT ||
                    hit == NativeMethods.HTBOTTOM || hit == NativeMethods.HTTOP ||
                    hit == NativeMethods.HTTOPLEFT || hit == NativeMethods.HTTOPRIGHT ||
                    hit == NativeMethods.HTBOTTOMLEFT || hit == NativeMethods.HTBOTTOMRIGHT)
                {
                    int scDirection = hit switch
                    {
                        NativeMethods.HTLEFT => 1,
                        NativeMethods.HTRIGHT => 2,
                        NativeMethods.HTTOP => 3,
                        NativeMethods.HTTOPLEFT => 4,
                        NativeMethods.HTTOPRIGHT => 5,
                        NativeMethods.HTBOTTOM => 6,
                        NativeMethods.HTBOTTOMLEFT => 7,
                        NativeMethods.HTBOTTOMRIGHT => 8,
                        _ => 0
                    };
                    if (scDirection > 0)
                    {
                        NativeMethods.ReleaseCapture();
                        NativeMethods.SendMessageW(hWnd, NativeMethods.WM_SYSCOMMAND, (IntPtr)(0xF000 + scDirection), lParam);
                        return IntPtr.Zero;
                    }
                }
                break;
            }

            case NativeMethods.WM_NCLBUTTONDBLCLK:
                if (wParam.ToInt32() == NativeMethods.HTCAPTION)
                {
                    ToggleExpandCore();
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_GETMINMAXINFO:
                if (lParam != IntPtr.Zero)
                {
                    var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                    mmi.ptMinTrackSize.X = 240;
                    mmi.ptMinTrackSize.Y = 135;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    return IntPtr.Zero;
                }
                break;

            case NativeMethods.WM_MOUSEMOVE:
            case NativeMethods.WM_LBUTTONDOWN:
            case NativeMethods.WM_LBUTTONUP:
            case NativeMethods.WM_RBUTTONDOWN:
            case NativeMethods.WM_RBUTTONUP:
            case NativeMethods.WM_LBUTTONDBLCLK:
            case NativeMethods.WM_RBUTTONDBLCLK:
            case NativeMethods.WM_MBUTTONDOWN:
            case NativeMethods.WM_MBUTTONUP:
            {
                if (!_clickThrough)
                {
                    int clientX = unchecked((short)(long)lParam);
                    int clientY = unchecked((short)((long)lParam >> 16));

                    // Header bar clicks (buttons or double clicks)
                    if (clientY < HeaderHeight)
                    {
                        if (msg == NativeMethods.WM_LBUTTONDOWN)
                        {
                            if (clientX >= _width - 28)
                            {
                                // Minimize button clicked
                                NativeMethods.ShowWindow(hWnd, 6 /* SW_MINIMIZE */);
                                return IntPtr.Zero;
                            }
                            if (clientX >= _width - 56)
                            {
                                // Expand toggle button clicked
                                ToggleExpandCore();
                                return IntPtr.Zero;
                            }
                        }
                        else if (msg == NativeMethods.WM_LBUTTONDBLCLK)
                        {
                            // Double-click on header bar
                            ToggleExpandCore();
                            return IntPtr.Zero;
                        }

                        return IntPtr.Zero;
                    }
                        if (msg == NativeMethods.WM_LBUTTONDOWN)
                        {
                            NativeMethods.SetFocus(hWnd);
                            NativeMethods.SetCapture(hWnd);
                        }
                        else if (msg == NativeMethods.WM_LBUTTONUP)
                        {
                            NativeMethods.ReleaseCapture();
                        }

                        PipInputEventType eventType = msg switch
                        {
                            NativeMethods.WM_MOUSEMOVE => PipInputEventType.MouseMove,
                            NativeMethods.WM_LBUTTONDOWN => PipInputEventType.MouseDown,
                            NativeMethods.WM_LBUTTONUP => PipInputEventType.MouseUp,
                            NativeMethods.WM_RBUTTONDOWN => PipInputEventType.MouseDown,
                            NativeMethods.WM_RBUTTONUP => PipInputEventType.MouseUp,
                            NativeMethods.WM_MBUTTONDOWN => PipInputEventType.MouseDown,
                            NativeMethods.WM_MBUTTONUP => PipInputEventType.MouseUp,
                            NativeMethods.WM_LBUTTONDBLCLK => PipInputEventType.DoubleClick,
                            NativeMethods.WM_RBUTTONDBLCLK => PipInputEventType.DoubleClick,
                            _ => PipInputEventType.MouseMove
                        };

                        MouseButton button = msg switch
                        {
                            NativeMethods.WM_RBUTTONDOWN or NativeMethods.WM_RBUTTONUP or NativeMethods.WM_RBUTTONDBLCLK => MouseButton.Right,
                            NativeMethods.WM_MBUTTONDOWN or NativeMethods.WM_MBUTTONUP => MouseButton.Middle,
                            _ => MouseButton.Left
                        };

                        OnInteractiveInput?.Invoke(new PipInputEventArgs(eventType, clientX, clientY, button));
                        return IntPtr.Zero;
                    }
                    break;
                }

            case NativeMethods.WM_MOUSEWHEEL:
            {
                if (!_clickThrough)
                {
                    int wheelScreenX = unchecked((short)(long)lParam);
                    int wheelScreenY = unchecked((short)((long)lParam >> 16));
                    var ptWheel = new POINT { X = wheelScreenX, Y = wheelScreenY };
                    NativeMethods.ScreenToClient(hWnd, ref ptWheel);

                    // Only handle mouse wheel in the client area below the 26px header bar
                    if (ptWheel.Y >= HeaderHeight)
                    {
                        int wheelDelta = unchecked((short)((long)wParam >> 16));
                        OnInteractiveInput?.Invoke(new PipInputEventArgs(PipInputEventType.MouseWheel, ptWheel.X, ptWheel.Y, MouseButton.Left, wheelDelta));
                        return IntPtr.Zero;
                    }
                }
                break;
            }

            case 0x0232: // WM_EXITSIZEMOVE
            case 0x0003: // WM_MOVE
            case 0x0005: // WM_SIZE
                if (NativeMethods.GetWindowRect(hWnd, out var wr))
                {
                    int nw = wr.Right - wr.Left;
                    int nh = wr.Bottom - wr.Top;
                    if (nw > 50 && nh > 50)
                    {
                        _x = wr.Left;
                        _y = wr.Top;
                        _width = nw;
                        _height = nh;
                        PositionChanged?.Invoke(_x, _y);
                        BoundsChanged?.Invoke(_x, _y, _width, _height);
                    }
                }
                break;

            case NativeMethods.WM_PAINT:
                var ps = new NativeMethods.PAINTSTRUCT();
                var hdc = NativeMethods.BeginPaint(hWnd, out ps);
                try
                {
                    if (Paint != null)
                    {
                        Paint.Invoke(hdc, _width, _height);
                    }
                    else
                    {
                        // Default background fill: clean modern dark canvas (#18181B)
                        var rect = ps.RcPaint;
                        if (rect.Right > 0 && rect.Bottom > 0)
                        {
                            var brush = NativeMethods.CreateSolidBrush(0x001B1818); // BGR format
                            try
                            {
                                NativeMethods.FillRect(hdc, ref rect, brush);
                            }
                            finally
                            {
                                NativeMethods.DeleteObject(brush);
                            }
                        }
                    }
                }
                finally
                {
                    NativeMethods.EndPaint(hWnd, ref ps);
                }
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                return new IntPtr(1);

            case NativeMethods.WM_DESTROY:
                WindowDestroyed?.Invoke(this);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var ptr = NativeMethods.GetWindowLongPtr(hWnd, GwlpUserdata);
        if (ptr != IntPtr.Zero)
        {
            try
            {
                var handle = GCHandle.FromIntPtr(ptr);
                if (handle.IsAllocated && handle.Target is GhostPipWindowHost host)
                {
                    return host.InstanceWndProc(hWnd, msg, wParam, lParam);
                }
            }
            catch (InvalidOperationException)
            {
                // Handle freed during teardown
            }
        }

        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void Teardown()
    {
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, GwlpUserdata, IntPtr.Zero);
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_hwndHandle.IsAllocated)
        {
            _hwndHandle.Free();
        }
    }

    /// <summary>
    /// Gracefully tears down the native window and message pump thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
            _threadId = 0;
        }

        _ready.Dispose();
    }
}
