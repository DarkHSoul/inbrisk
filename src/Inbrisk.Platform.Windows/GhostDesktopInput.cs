using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

#region Virtual Key Definitions

/// <summary>
/// Virtual key codes for ghost desktop keyboard input injection.
/// Compatible with Win32 VK codes and convertible from <see cref="KeyCode"/>.
/// </summary>
public enum VirtualKey : ushort
{
    None = 0,
    LeftButton = 0x01,
    RightButton = 0x02,
    Cancel = 0x03,
    MiddleButton = 0x04,
    Back = 0x08,
    Backspace = 0x08,
    Tab = 0x09,
    Clear = 0x0C,
    Return = 0x0D,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Ctrl = 0x11,
    Menu = 0x12,
    Alt = 0x12,
    Pause = 0x13,
    Capital = 0x14,
    CapsLock = 0x14,
    Escape = 0x1B,
    Esc = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    Select = 0x29,
    Print = 0x2A,
    Execute = 0x2B,
    Snapshot = 0x2C,
    PrintScreen = 0x2C,
    Insert = 0x2D,
    Delete = 0x2E,
    Help = 0x2F,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    LeftWindows = 0x5B,
    RightWindows = 0x5C,
    Apps = 0x5D,
    Sleep = 0x5F,
    NumPad0 = 0x60,
    NumPad1 = 0x61,
    NumPad2 = 0x62,
    NumPad3 = 0x63,
    NumPad4 = 0x64,
    NumPad5 = 0x65,
    NumPad6 = 0x66,
    NumPad7 = 0x67,
    NumPad8 = 0x68,
    NumPad9 = 0x69,
    Multiply = 0x6A,
    Add = 0x6B,
    Separator = 0x6C,
    Subtract = 0x6D,
    Decimal = 0x6E,
    Divide = 0x6F,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    F13 = 0x7C,
    F14 = 0x7D,
    F15 = 0x7E,
    F16 = 0x7F,
    F17 = 0x80,
    F18 = 0x81,
    F19 = 0x82,
    F20 = 0x83,
    F21 = 0x84,
    F22 = 0x85,
    F23 = 0x86,
    F24 = 0x87,
    NumLock = 0x90,
    Scroll = 0x91,
    LeftShift = 0xA0,
    RightShift = 0xA1,
    LeftControl = 0xA2,
    RightControl = 0xA3,
    LeftMenu = 0xA4,
    RightMenu = 0xA5,
    OemSemicolon = 0xBA,
    OemPlus = 0xBB,
    OemComma = 0xBC,
    OemMinus = 0xBD,
    OemPeriod = 0xBE,
    OemQuestion = 0xBF,
    OemTilde = 0xC0,
    OemOpenBrackets = 0xDB,
    OemPipe = 0xDC,
    OemCloseBrackets = 0xDD,
    OemQuotes = 0xDE
}

#endregion

/// <summary>
/// High-performance, isolated input injection engine for Windows Ghost Desktops.
/// Targets ONLY windows residing on the isolated desktop (<c>InbriskGhostDesktop</c>) via
/// thread-bound desktop switching and <c>PostMessageW</c>.
/// <para>
/// Because input is posted directly to ghost window message queues without calling
/// <c>SendInput</c> or <c>SetCursorPos</c>, physical mouse cursor and keyboard focus
/// on the user's active "Default" desktop remain 100% undisturbed (e.g. while gaming or typing).
/// </para>
/// Also provides PiP overlay coordinate mapping for interactive picture-in-picture modes.
/// </summary>
public sealed class GhostDesktopInput : IDisposable
{
    #region Win32 Constants & Interop

    public const string DefaultDesktopName = "InbriskGhostDesktop";

    private const uint DESKTOP_ALL_ACCESS = 0x01FF | 0x000F0000;
    private const uint MAPVK_VK_TO_VSC = 0;

    // Window Messages
    private const uint WM_SETFOCUS = 0x0007;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_RBUTTONDBLCLK = 0x0206;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MBUTTONDBLCLK = 0x0209;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_MOUSEHWHEEL = 0x020E;

    // Mouse Key Flags (WParam)
    private const uint MK_LBUTTON = 0x0001;
    private const uint MK_RBUTTON = 0x0002;
    private const uint MK_SHIFT = 0x0004;
    private const uint MK_CONTROL = 0x0008;
    private const uint MK_MBUTTON = 0x0010;

    // ChildWindowFromPointEx Flags
    private const uint CWP_SKIPINVISIBLE = 0x0001;
    private const uint CWP_SKIPDISABLED = 0x0002;
    private const uint CWP_SKIPTRANSPARENT = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private static class Win32
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenDesktopW(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateDesktopW(
            string lpszDesktop,
            IntPtr lpszDevice,
            IntPtr pDevmode,
            uint dwFlags,
            uint dwDesiredAccess,
            IntPtr lpsa);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll")]
        public static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        public delegate bool EnumDesktopWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumDesktopWindowsProc lpfn, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT Point);

        [DllImport("user32.dll")]
        public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, POINT pt, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern short VkKeyScanW(char ch);
    }

    #endregion

    #region State & Fields

    private readonly string _desktopName;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly ManualResetEventSlim _workerReady = new(false);

    private Thread? _workerThread;
    private IntPtr _hDesktop = IntPtr.Zero;
    private bool _isDesktopBound;
    private volatile bool _disposed;

    private uint _heldMouseKeys;
    private int _lastMouseX;
    private int _lastMouseY;
    private IntPtr _lastTargetHwnd = IntPtr.Zero;

    #endregion

    #region Configuration Properties

    /// <summary>Gets the name of the target Windows desktop.</summary>
    public string DesktopName => _desktopName;

    /// <summary>Gets or sets the virtual/ghost desktop width in pixels (default: 1920).</summary>
    public int DesktopWidth { get; set; } = 1920;

    /// <summary>Gets or sets the virtual/ghost desktop height in pixels (default: 1080).</summary>
    public int DesktopHeight { get; set; } = 1080;

    /// <summary>Gets or sets the PiP window width for coordinate normalization (default: 320).</summary>
    public int PipWidth { get; set; } = 320;

    /// <summary>Gets or sets the PiP window height for coordinate normalization (default: 180).</summary>
    public int PipHeight { get; set; } = 180;

    /// <summary>Gets or sets the top header offset height of the PiP window (default: 26, matching GhostPipWindowHost).</summary>
    public int PipHeaderHeight { get; set; } = 26;

    /// <summary>
    /// Gets or sets whether interactive PiP input forwarding is currently enabled.
    /// When disabled, PiP click forwarding methods return immediately without injecting input.
    /// </summary>
    public bool IsPipInteractive { get; set; } = true;

    /// <summary>
    /// Optional explicit target HWND override for keyboard input injection.
    /// When null, keyboard target is dynamically resolved from the ghost desktop foreground/focus window.
    /// </summary>
    public IntPtr? TargetHwnd { get; set; }

    /// <summary>Gets the most recent window handle targeted by mouse operations.</summary>
    public IntPtr LastTargetHwnd => _lastTargetHwnd;

    /// <summary>Gets the last injected mouse cursor coordinates in desktop space.</summary>
    public (int X, int Y) LastCursorPosition => (_lastMouseX, _lastMouseY);

    /// <summary>Gets whether the injector worker thread successfully bound to the ghost desktop.</summary>
    public bool IsDesktopBound => _isDesktopBound;

    #endregion

    #region Construction & Lifecycle

    /// <summary>
    /// Initializes a new instance of the <see cref="GhostDesktopInput"/> injector.
    /// Automatically provisions a clean worker thread bound to the ghost desktop.
    /// </summary>
    /// <param name="desktopName">Name of the target desktop (default: "InbriskGhostDesktop").</param>
    /// <param name="desktopWidth">Width of the ghost desktop.</param>
    /// <param name="desktopHeight">Height of the ghost desktop.</param>
    /// <param name="autoStart">Whether to start the background worker thread immediately.</param>
    public GhostDesktopInput(
        string desktopName = DefaultDesktopName,
        int desktopWidth = 1920,
        int desktopHeight = 1080,
        bool autoStart = true)
    {
        _desktopName = string.IsNullOrWhiteSpace(desktopName) ? DefaultDesktopName : desktopName;
        DesktopWidth = Math.Max(1, desktopWidth);
        DesktopHeight = Math.Max(1, desktopHeight);

        if (autoStart)
        {
            EnsureWorkerStarted();
        }
    }

    private void EnsureWorkerStarted()
    {
        if (_workerThread != null && _workerThread.IsAlive) return;

        lock (_workerReady)
        {
            if (_workerThread != null && _workerThread.IsAlive) return;

            _workerReady.Reset();
            _workerThread = new Thread(WorkerLoop)
            {
                Name = $"GhostDesktopInputWorker_{_desktopName}",
                IsBackground = true
            };
            _workerThread.Start();
            _workerReady.Wait(3000);
        }
    }

    private void EnsureDesktopAttached()
    {
        if (_hDesktop != IntPtr.Zero) return;

        try
        {
            _hDesktop = Win32.OpenDesktopW(_desktopName, 0, false, DESKTOP_ALL_ACCESS);
            if (_hDesktop == IntPtr.Zero)
            {
                _hDesktop = Win32.OpenDesktopW(
                    _desktopName,
                    0,
                    false,
                    0x0001 | 0x0040 | 0x0080 | 0x0002);
            }

            if (_hDesktop == IntPtr.Zero)
            {
                _hDesktop = Win32.CreateDesktopW(
                    _desktopName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    DESKTOP_ALL_ACCESS,
                    IntPtr.Zero);
            }

            if (_hDesktop != IntPtr.Zero)
            {
                _isDesktopBound = Win32.SetThreadDesktop(_hDesktop);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostDesktopInput] EnsureDesktopAttached error: {ex.Message}");
        }
    }

    private void WorkerLoop()
    {
        try
        {
            EnsureDesktopAttached();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostDesktopInput] Worker initialization error: {ex.Message}");
        }
        finally
        {
            _workerReady.Set();
        }

        try
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GhostDesktopInput] Action dispatch fault: {ex.Message}");
                }
            }
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        finally
        {
            if (_hDesktop != IntPtr.Zero)
            {
                Win32.CloseDesktop(_hDesktop);
                _hDesktop = IntPtr.Zero;
                _isDesktopBound = false;
            }
        }
    }

    private void RunOnGhostThread(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Thread.CurrentThread == _workerThread)
        {
            action();
            return;
        }

        EnsureWorkerStarted();

        if (_workerThread != null && _workerThread.IsAlive)
        {
            using var ev = new ManualResetEventSlim(false);
            Exception? caughtEx = null;

            _queue.Add(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
                finally
                {
                    ev.Set();
                }
            });

            ev.Wait();
            if (caughtEx != null)
            {
                throw new InvalidOperationException($"GhostDesktop input injection failed: {caughtEx.Message}", caughtEx);
            }
        }
        else
        {
            // Inline fallback if worker failed to start
            action();
        }
    }

    #endregion

    #region Mouse Input Injection (PostMessageW)

    /// <summary>
    /// Injects a mouse move event to the window under the specified desktop coordinates on the ghost desktop.
    /// Does NOT move the user's physical mouse cursor on "Default".
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    public void SendMouseMove(int desktopX, int desktopY)
    {
        RunOnGhostThread(() =>
        {
            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt);
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
            {
                _lastTargetHwnd = hwnd;
            }

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, WM_MOUSEMOVE, (IntPtr)_heldMouseKeys, lParam);
        });
    }

    /// <summary>
    /// Injects a mouse button press (Down) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to press.</param>
    public void SendMouseDown(int desktopX, int desktopY, MouseButton button)
    {
        RunOnGhostThread(() =>
        {
            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt);
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
            {
                _lastTargetHwnd = hwnd;
                IntPtr root = Win32.GetAncestor(hwnd, 2 /* GA_ROOT */);
                if (root == IntPtr.Zero) root = hwnd;

                Win32.SetForegroundWindow(root);
                Win32.SetActiveWindow(root);
                Win32.SetFocus(hwnd);
                Win32.PostMessageW(root, WM_ACTIVATE, (IntPtr)1 /* WA_ACTIVE */, IntPtr.Zero);
                Win32.PostMessageW(hwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
            }

            uint msg;
            switch (button)
            {
                case MouseButton.Right:
                    _heldMouseKeys |= MK_RBUTTON;
                    msg = WM_RBUTTONDOWN;
                    break;
                case MouseButton.Middle:
                    _heldMouseKeys |= MK_MBUTTON;
                    msg = WM_MBUTTONDOWN;
                    break;
                case MouseButton.Left:
                default:
                    _heldMouseKeys |= MK_LBUTTON;
                    msg = WM_LBUTTONDOWN;
                    break;
            }

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, WM_MOUSEMOVE, (IntPtr)_heldMouseKeys, lParam);
            Win32.PostMessageW(hwnd, msg, (IntPtr)_heldMouseKeys, lParam);
        });
    }

    /// <summary>
    /// Injects a mouse button release (Up) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to release.</param>
    public void SendMouseUp(int desktopX, int desktopY, MouseButton button)
    {
        RunOnGhostThread(() =>
        {
            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt);
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
            {
                _lastTargetHwnd = hwnd;
            }
            else if (_lastTargetHwnd != IntPtr.Zero && Win32.IsWindow(_lastTargetHwnd))
            {
                hwnd = _lastTargetHwnd;
                POINT pt = new POINT { X = desktopX, Y = desktopY };
                Win32.ScreenToClient(hwnd, ref pt);
                clientPt = pt;
            }

            uint msg;
            switch (button)
            {
                case MouseButton.Right:
                    _heldMouseKeys &= ~MK_RBUTTON;
                    msg = WM_RBUTTONUP;
                    break;
                case MouseButton.Middle:
                    _heldMouseKeys &= ~MK_MBUTTON;
                    msg = WM_MBUTTONUP;
                    break;
                case MouseButton.Left:
                default:
                    _heldMouseKeys &= ~MK_LBUTTON;
                    msg = WM_LBUTTONUP;
                    break;
            }

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, msg, (IntPtr)_heldMouseKeys, lParam);
        });
    }

    /// <summary>
    /// Injects a complete mouse click (or double-click) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to click (default: Left).</param>
    /// <param name="doubleClick">Whether to perform a double-click gesture.</param>
    public void SendMouseClick(int desktopX, int desktopY, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        SendMouseMove(desktopX, desktopY);
        SendMouseDown(desktopX, desktopY, button);
        Thread.Sleep(15);
        SendMouseUp(desktopX, desktopY, button);

        if (doubleClick)
        {
            Thread.Sleep(30);
            RunOnGhostThread(() =>
            {
                IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt);
                if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
                {
                    _lastTargetHwnd = hwnd;
                }

                uint dblMsg = button switch
                {
                    MouseButton.Right => WM_RBUTTONDBLCLK,
                    MouseButton.Middle => WM_MBUTTONDBLCLK,
                    _ => WM_LBUTTONDBLCLK
                };

                uint upMsg = button switch
                {
                    MouseButton.Right => WM_RBUTTONUP,
                    MouseButton.Middle => WM_MBUTTONUP,
                    _ => WM_LBUTTONUP
                };

                uint btnFlag = button switch
                {
                    MouseButton.Right => MK_RBUTTON,
                    MouseButton.Middle => MK_MBUTTON,
                    _ => MK_LBUTTON
                };

                _heldMouseKeys |= btnFlag;
                IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
                Win32.PostMessageW(hwnd, dblMsg, (IntPtr)_heldMouseKeys, lParam);

                Thread.Sleep(15);

                _heldMouseKeys &= ~btnFlag;
                Win32.PostMessageW(hwnd, upMsg, (IntPtr)_heldMouseKeys, lParam);
            });
        }
    }

    /// <summary>
    /// Injects a vertical mouse scroll (wheel) event at the specified desktop coordinates.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="delta">Scroll delta (typically multiples of 120; positive is scroll up, negative is scroll down).</param>
    public void SendMouseScroll(int desktopX, int desktopY, int delta)
    {
        RunOnGhostThread(() =>
        {
            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out _);
            if (hwnd == IntPtr.Zero || hwnd == Win32.GetDesktopWindow())
            {
                hwnd = FindKeyboardTarget();
            }

            // WM_MOUSEWHEEL: wParam high word is delta, low word is keys
            IntPtr wParam = unchecked((IntPtr)((((uint)delta & 0xFFFF) << 16) | ((uint)_heldMouseKeys & 0xFFFF)));
            // WM_MOUSEWHEEL: lParam holds absolute screen coordinates
            IntPtr lParam = unchecked((IntPtr)((((uint)desktopY & 0xFFFF) << 16) | ((uint)desktopX & 0xFFFF)));

            Win32.PostMessageW(hwnd, WM_MOUSEWHEEL, wParam, lParam);
        });
    }

    /// <summary>
    /// Performs a mouse drag-and-drop gesture on the ghost desktop from start to end coordinates.
    /// </summary>
    public void SendMouseDrag(
        int startX, int startY, int endX, int endY,
        MouseButton button = MouseButton.Left,
        int steps = 10,
        int stepDelayMs = 10)
    {
        SendMouseMove(startX, startY);
        Thread.Sleep(10);
        SendMouseDown(startX, startY, button);
        Thread.Sleep(15);

        steps = Math.Max(1, steps);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            int currX = (int)Math.Round(startX + (endX - startX) * t);
            int currY = (int)Math.Round(startY + (endY - startY) * t);
            SendMouseMove(currX, currY);
            if (stepDelayMs > 0 && i < steps)
            {
                Thread.Sleep(stepDelayMs);
            }
        }

        Thread.Sleep(15);
        SendMouseUp(endX, endY, button);
    }

    #endregion

    #region Keyboard Input Injection (PostMessageW)

    /// <summary>
    /// Injects a key press (KeyDown + KeyUp) using a <see cref="VirtualKey"/>.
    /// Posts directly to the focused or target window on the ghost desktop.
    /// </summary>
    /// <param name="key">Virtual key code to press.</param>
    public void SendKeyPress(VirtualKey key)
    {
        SendKeyDown(key);
        Thread.Sleep(20);
        SendKeyUp(key);
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key presses.
    /// </summary>
    public void SendKeyPress(KeyCode key) => SendKeyPress(ToVirtualKey(key));

    /// <summary>
    /// Overload for raw virtual key numeric codes.
    /// </summary>
    public void SendKeyPress(ushort key) => SendKeyPress((VirtualKey)key);

    /// <summary>
    /// Overload for raw integer virtual key codes.
    /// </summary>
    public void SendKeyPress(int key) => SendKeyPress((VirtualKey)key);

    /// <summary>
    /// Injects a key down event to the ghost desktop target window.
    /// </summary>
    public void SendKeyDown(VirtualKey key)
    {
        RunOnGhostThread(() =>
        {
            IntPtr targetHwnd = FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero) return;

            uint vk = (uint)key;
            uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            uint lParam = 1 | (scanCode << 16);
            if (IsExtendedKey(vk))
            {
                lParam |= (1u << 24);
            }

            Win32.PostMessageW(targetHwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)lParam);
        });
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key down.
    /// </summary>
    public void SendKeyDown(KeyCode key) => SendKeyDown(ToVirtualKey(key));

    /// <summary>
    /// Injects a key up event to the ghost desktop target window.
    /// </summary>
    public void SendKeyUp(VirtualKey key)
    {
        RunOnGhostThread(() =>
        {
            IntPtr targetHwnd = FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero) return;

            uint vk = (uint)key;
            uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            uint lParam = 1 | (scanCode << 16) | (1u << 30) | (1u << 31);
            if (IsExtendedKey(vk))
            {
                lParam |= (1u << 24);
            }

            Win32.PostMessageW(targetHwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lParam);
        });
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key up.
    /// </summary>
    public void SendKeyUp(KeyCode key) => SendKeyUp(ToVirtualKey(key));

    /// <summary>
    /// Types Unicode text into the target window on the ghost desktop via <c>WM_CHAR</c>
    /// or <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> pairs for console windows (<c>ConsoleWindowClass</c>).
    /// Properly converts newlines and tabs to their respective virtual key events.
    /// </summary>
    /// <param name="text">Text string to type.</param>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        RunOnGhostThread(() =>
        {
            IntPtr targetHwnd = FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero || targetHwnd == Win32.GetDesktopWindow()) return;

            bool isConsole = IsConsoleWindowClass(targetHwnd);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;

                if (c == '\n')
                {
                    SendKeyPressCore(targetHwnd, VirtualKey.Enter);
                    continue;
                }

                if (c == '\t')
                {
                    SendKeyPressCore(targetHwnd, VirtualKey.Tab);
                    continue;
                }

                if (isConsole)
                {
                    SendConsoleChar(targetHwnd, c);
                }
                else
                {
                    uint scanCode = Win32.MapVirtualKeyW((uint)c, MAPVK_VK_TO_VSC);
                    uint lParam = 1 | (scanCode << 16);
                    Win32.PostMessageW(targetHwnd, WM_CHAR, (IntPtr)c, (IntPtr)lParam);
                }
            }
        });
    }

    /// <summary>
    /// Checks whether the specified window or its root belongs to the Windows Console Host (<c>ConsoleWindowClass</c>).
    /// </summary>
    public static bool IsConsoleWindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        Win32.GetClassNameW(hwnd, sb, 64);
        if (string.Equals(sb.ToString(), "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
            return true;

        IntPtr root = Win32.GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero && root != hwnd)
        {
            sb.Clear();
            Win32.GetClassNameW(root, sb, 64);
            return string.Equals(sb.ToString(), "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void SendConsoleChar(IntPtr hwnd, char c)
    {
        short res = Win32.VkKeyScanW(c);
        if (res == -1)
        {
            uint scanCode = Win32.MapVirtualKeyW((uint)c, MAPVK_VK_TO_VSC);
            uint lParam = 1 | (scanCode << 16);
            Win32.PostMessageW(hwnd, WM_CHAR, (IntPtr)c, (IntPtr)lParam);
            return;
        }

        byte vk = (byte)(res & 0xFF);
        byte shiftState = (byte)((res >> 8) & 0xFF);
        bool shift = (shiftState & 1) != 0;
        bool ctrl = (shiftState & 2) != 0;
        bool alt = (shiftState & 4) != 0;

        uint shiftSc = Win32.MapVirtualKeyW((uint)VirtualKey.Shift, MAPVK_VK_TO_VSC);
        uint ctrlSc = Win32.MapVirtualKeyW((uint)VirtualKey.Control, MAPVK_VK_TO_VSC);
        uint altSc = Win32.MapVirtualKeyW((uint)VirtualKey.Alt, MAPVK_VK_TO_VSC);

        if (shift) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Shift, (IntPtr)(1 | (shiftSc << 16)));
        if (ctrl) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Control, (IntPtr)(1 | (ctrlSc << 16)));
        if (alt) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Alt, (IntPtr)(1 | (altSc << 16)));

        uint sc = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        uint downLParam = 1 | (sc << 16);
        if (IsExtendedKey(vk)) downLParam |= (1u << 24);
        Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)downLParam);

        Thread.Sleep(5);

        uint upLParam = 1 | (sc << 16) | (1u << 30) | (1u << 31);
        if (IsExtendedKey(vk)) upLParam |= (1u << 24);
        Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)upLParam);

        if (alt) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Alt, (IntPtr)(1 | (altSc << 16) | (1u << 30) | (1u << 31)));
        if (ctrl) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Control, (IntPtr)(1 | (ctrlSc << 16) | (1u << 30) | (1u << 31)));
        if (shift) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Shift, (IntPtr)(1 | (shiftSc << 16) | (1u << 30) | (1u << 31)));
    }

    private static void SendKeyPressCore(IntPtr hwnd, VirtualKey key)
    {
        uint vk = (uint)key;
        uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
        uint downLParam = 1 | (scanCode << 16);
        if (IsExtendedKey(vk)) downLParam |= (1u << 24);
        Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)downLParam);

        Thread.Sleep(15);

        uint upLParam = 1 | (scanCode << 16) | (1u << 30) | (1u << 31);
        if (IsExtendedKey(vk)) upLParam |= (1u << 24);
        Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)upLParam);
    }

    #endregion

    #region Picture-in-Picture (PiP) Coordinate Mapping & Forwarding

    /// <summary>
    /// Maps relative coordinates from the PiP window overlay (e.g. 320x180) to absolute
    /// physical desktop coordinates on the ghost desktop (e.g. 1920x1080).
    /// </summary>
    /// <param name="pipX">X pixel coordinate inside the PiP overlay.</param>
    /// <param name="pipY">Y pixel coordinate inside the PiP overlay.</param>
    /// <returns>Physical desktop coordinates (DesktopX, DesktopY).</returns>
    public (int DesktopX, int DesktopY) MapPipToDesktop(int pipX, int pipY)
    {
        return MapPipToDesktopCoordinates(
            pipX, pipY,
            PipWidth, PipHeight,
            DesktopWidth, DesktopHeight,
            PipHeaderHeight);
    }

    /// <summary>
    /// Pure function to map coordinates from any PiP overlay dimensions to ghost desktop coordinates.
    /// Handles optional top header bar offsets and clamps coordinates within valid bounds.
    /// </summary>
    public static (int DesktopX, int DesktopY) MapPipToDesktopCoordinates(
        int pipX, int pipY,
        int pipWidth, int pipHeight,
        int desktopWidth = 1920, int desktopHeight = 1080,
        int pipHeaderHeight = 0)
    {
        int effectiveWidth = Math.Max(1, pipWidth);
        int effectiveHeight = Math.Max(1, pipHeight - pipHeaderHeight);
        int effectiveY = Math.Max(0, pipY - pipHeaderHeight);

        int mappedX = (int)Math.Round((double)pipX / effectiveWidth * desktopWidth);
        int mappedY = (int)Math.Round((double)effectiveY / effectiveHeight * desktopHeight);

        int clampedX = Math.Clamp(mappedX, 0, Math.Max(0, desktopWidth - 1));
        int clampedY = Math.Clamp(mappedY, 0, Math.Max(0, desktopHeight - 1));

        return (clampedX, clampedY);
    }

    /// <summary>
    /// Injects a mouse move event originating from an interactive PiP click/hover.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseMove(int pipX, int pipY)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseMove(dx, dy);
    }

    /// <summary>
    /// Injects a mouse click event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseClick(int pipX, int pipY, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseClick(dx, dy, button, doubleClick);
    }

    /// <summary>
    /// Injects a mouse down event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseDown(int pipX, int pipY, MouseButton button)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseDown(dx, dy, button);
    }

    /// <summary>
    /// Injects a mouse up event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseUp(int pipX, int pipY, MouseButton button)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseUp(dx, dy, button);
    }

    /// <summary>
    /// Injects a mouse scroll event originating from an interactive PiP wheel action.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseScroll(int pipX, int pipY, int delta)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseScroll(dx, dy, delta);
    }

    /// <summary>
    /// Forwards an interactive PiP input event to the ghost desktop.
    /// Automatically maps PiP coordinates to ghost desktop resolution if not already mapped.
    /// </summary>
    public void ForwardPipInput(PipInputEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsPipInteractive) return;

        if (e.EventType == PipInputEventType.KeyDown)
        {
            SendKeyDown((VirtualKey)e.KeyCode);
            return;
        }

        if (e.EventType == PipInputEventType.KeyUp)
        {
            SendKeyUp((VirtualKey)e.KeyCode);
            return;
        }

        if (e.EventType == PipInputEventType.Char)
        {
            if (e.Character != '\0')
            {
                SendText(e.Character.ToString());
            }
            return;
        }

        var (dx, dy) = e.IsMapped ? (e.X, e.Y) : MapPipToDesktop(e.X, e.Y);

        switch (e.EventType)
        {
            case PipInputEventType.MouseMove:
                SendMouseMove(dx, dy);
                break;
            case PipInputEventType.MouseDown:
                SendMouseDown(dx, dy, e.Button);
                break;
            case PipInputEventType.MouseUp:
                SendMouseUp(dx, dy, e.Button);
                break;
            case PipInputEventType.DoubleClick:
                SendMouseClick(dx, dy, e.Button, doubleClick: true);
                break;
            case PipInputEventType.MouseWheel:
                SendMouseScroll(dx, dy, e.Delta);
                break;
        }
    }

    /// <summary>
    /// Forwards a keyboard key action (KeyDown, KeyUp, Char) directly to the ghost desktop.
    /// </summary>
    public void ForwardKeyInput(PipInputEventType eventType, int keyCode, char character)
    {
        if (eventType == PipInputEventType.KeyDown)
        {
            SendKeyDown((VirtualKey)keyCode);
        }
        else if (eventType == PipInputEventType.KeyUp)
        {
            SendKeyUp((VirtualKey)keyCode);
        }
        else if (eventType == PipInputEventType.Char)
        {
            if (character != '\0')
            {
                SendText(character.ToString());
            }
        }
    }

    /// <summary>
    /// Attaches this input injector to a <see cref="GhostPipWindowHost"/> instance to automatically
    /// forward interactive mouse and wheel events to the ghost desktop.
    /// </summary>
    public void AttachToPipHost(GhostPipWindowHost pipHost)
    {
        ArgumentNullException.ThrowIfNull(pipHost);
        pipHost.OnInteractiveInput += ForwardPipInput;
    }

    /// <summary>
    /// Detaches this input injector from a <see cref="GhostPipWindowHost"/> instance.
    /// </summary>
    public void DetachFromPipHost(GhostPipWindowHost pipHost)
    {
        ArgumentNullException.ThrowIfNull(pipHost);
        pipHost.OnInteractiveInput -= ForwardPipInput;
    }

    #endregion

    #region Helper & Resolution Methods

    /// <summary>
    /// Resolves the specific target HWND and computes client coordinates for a given ghost desktop point.
    /// Enumerates visible windows specifically on the isolated ghost desktop (_hDesktop) in Z-order.
    /// </summary>
    private IntPtr GetWindowAtPoint(int desktopX, int desktopY, out POINT clientPt)
    {
        EnsureDesktopAttached();

        IntPtr hitTopHwnd = IntPtr.Zero;

        if (_hDesktop != IntPtr.Zero)
        {
            Win32.EnumDesktopWindows(_hDesktop, (hWnd, lParam) =>
            {
                if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                {
                    return true;
                }

                if (!Win32.GetWindowRect(hWnd, out RECT rect))
                {
                    return true;
                }

                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 16 || h <= 16)
                {
                    return true;
                }

                var sbClass = new StringBuilder(256);
                Win32.GetClassNameW(hWnd, sbClass, 256);
                string cls = sbClass.ToString();

                if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Default IME", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (desktopX >= rect.Left && desktopX < rect.Right &&
                    desktopY >= rect.Top && desktopY < rect.Bottom)
                {
                    hitTopHwnd = hWnd;
                    return false; // Found topmost hit!
                }

                return true;
            }, IntPtr.Zero);
        }

        if (hitTopHwnd == IntPtr.Zero)
        {
            clientPt = new POINT { X = desktopX, Y = desktopY };
            return Win32.GetDesktopWindow();
        }

        // Drill down into visible child controls
        IntPtr targetHwnd = hitTopHwnd;
        POINT curPt = new POINT { X = desktopX, Y = desktopY };
        Win32.ScreenToClient(targetHwnd, ref curPt);

        while (true)
        {
            IntPtr child = Win32.ChildWindowFromPointEx(
                targetHwnd,
                curPt,
                CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);

            if (child == IntPtr.Zero || child == targetHwnd || !Win32.IsWindow(child))
            {
                break;
            }

            POINT screenPt = new POINT { X = desktopX, Y = desktopY };
            Win32.ScreenToClient(child, ref screenPt);
            curPt = screenPt;
            targetHwnd = child;
        }

        clientPt = curPt;
        return targetHwnd;
    }

    /// <summary>
    /// Dynamically locates the active keyboard input target window on the ghost desktop.
    /// Strictly restricts targets to windows belonging to the isolated desktop (_hDesktop).
    /// </summary>
    private IntPtr FindKeyboardTarget()
    {
        // 1. Explicit target override
        if (TargetHwnd.HasValue && TargetHwnd.Value != IntPtr.Zero && Win32.IsWindow(TargetHwnd.Value))
        {
            return TargetHwnd.Value;
        }

        // 2. Last target window touched by mouse actions on the ghost desktop
        if (_lastTargetHwnd != IntPtr.Zero && Win32.IsWindow(_lastTargetHwnd))
        {
            uint threadId = Win32.GetWindowThreadProcessId(_lastTargetHwnd, out _);
            if (threadId != 0)
            {
                var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (Win32.GetGUIThreadInfo(threadId, ref gui))
                {
                    if (gui.hwndFocus != IntPtr.Zero && Win32.IsWindow(gui.hwndFocus))
                        return gui.hwndFocus;
                    if (gui.hwndActive != IntPtr.Zero && Win32.IsWindow(gui.hwndActive))
                        return gui.hwndActive;
                }
            }
            return _lastTargetHwnd;
        }

        // 3. Topmost visible window on _hDesktop
        EnsureDesktopAttached();
        IntPtr topWin = IntPtr.Zero;
        if (_hDesktop != IntPtr.Zero)
        {
            Win32.EnumDesktopWindows(_hDesktop, (hWnd, lParam) =>
            {
                if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                {
                    return true;
                }

                var sbClass = new StringBuilder(256);
                Win32.GetClassNameW(hWnd, sbClass, 256);
                string cls = sbClass.ToString();

                if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cls, "Default IME", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                topWin = hWnd;
                return false; // Found topmost
            }, IntPtr.Zero);
        }

        if (topWin != IntPtr.Zero)
        {
            _lastTargetHwnd = topWin;
            uint threadId = Win32.GetWindowThreadProcessId(topWin, out _);
            if (threadId != 0)
            {
                var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (Win32.GetGUIThreadInfo(threadId, ref gui))
                {
                    if (gui.hwndFocus != IntPtr.Zero && Win32.IsWindow(gui.hwndFocus))
                        return gui.hwndFocus;
                    if (gui.hwndActive != IntPtr.Zero && Win32.IsWindow(gui.hwndActive))
                        return gui.hwndActive;
                }
            }
            return topWin;
        }

        // 4. Fallback to desktop window
        return Win32.GetDesktopWindow();
    }

    private static IntPtr MakeLParam(int x, int y)
    {
        return unchecked((IntPtr)((((uint)y & 0xFFFF) << 16) | ((uint)x & 0xFFFF)));
    }

    private static bool IsExtendedKey(uint vk)
    {
        return vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PageUp, PageDown, End, Home, Left, Up, Right, Down
            or 0x2D or 0x2E // Insert, Delete
            or 0x6F // NumPad Divide
            or 0xA3 // Right Control
            or 0xA5 // Right Menu (Alt)
            or 0x5B or 0x5C or 0x5D // Win keys, Apps
            or 0x90 // NumLock
            or 0x2C // PrintScreen
            => true,
            _ => false
        };
    }

    /// <summary>
    /// Releases any held mouse buttons.
    /// </summary>
    public void ReleaseAll()
    {
        RunOnGhostThread(() =>
        {
            if (_heldMouseKeys != 0)
            {
                int x = _lastMouseX;
                int y = _lastMouseY;

                if ((_heldMouseKeys & MK_LBUTTON) != 0)
                    SendMouseUp(x, y, MouseButton.Left);
                if ((_heldMouseKeys & MK_RBUTTON) != 0)
                    SendMouseUp(x, y, MouseButton.Right);
                if ((_heldMouseKeys & MK_MBUTTON) != 0)
                    SendMouseUp(x, y, MouseButton.Middle);

                _heldMouseKeys = 0;
            }
        });
    }

    /// <summary>
    /// Converts a core <see cref="KeyCode"/> to the corresponding <see cref="VirtualKey"/>.
    /// </summary>
    public static VirtualKey ToVirtualKey(KeyCode code) => code switch
    {
        KeyCode.Enter => VirtualKey.Enter,
        KeyCode.Escape => VirtualKey.Escape,
        KeyCode.Tab => VirtualKey.Tab,
        KeyCode.Space => VirtualKey.Space,
        KeyCode.Backspace => VirtualKey.Backspace,
        KeyCode.Delete => VirtualKey.Delete,
        KeyCode.Left => VirtualKey.Left,
        KeyCode.Right => VirtualKey.Right,
        KeyCode.Up => VirtualKey.Up,
        KeyCode.Down => VirtualKey.Down,
        KeyCode.Home => VirtualKey.Home,
        KeyCode.End => VirtualKey.End,
        KeyCode.PageUp => VirtualKey.PageUp,
        KeyCode.PageDown => VirtualKey.PageDown,
        KeyCode.Ctrl => VirtualKey.Control,
        KeyCode.Shift => VirtualKey.Shift,
        KeyCode.Alt => VirtualKey.Alt,
        KeyCode.Win => VirtualKey.LeftWindows,
        KeyCode.Pause => VirtualKey.Pause,
        KeyCode.F1 => VirtualKey.F1,
        KeyCode.F2 => VirtualKey.F2,
        KeyCode.F3 => VirtualKey.F3,
        KeyCode.F4 => VirtualKey.F4,
        KeyCode.F5 => VirtualKey.F5,
        KeyCode.F6 => VirtualKey.F6,
        KeyCode.F7 => VirtualKey.F7,
        KeyCode.F8 => VirtualKey.F8,
        KeyCode.F9 => VirtualKey.F9,
        KeyCode.F10 => VirtualKey.F10,
        KeyCode.F11 => VirtualKey.F11,
        KeyCode.F12 => VirtualKey.F12,
        KeyCode.A => VirtualKey.A,
        KeyCode.B => VirtualKey.B,
        KeyCode.C => VirtualKey.C,
        KeyCode.D => VirtualKey.D,
        KeyCode.E => VirtualKey.E,
        KeyCode.F => VirtualKey.F,
        KeyCode.G => VirtualKey.G,
        KeyCode.H => VirtualKey.H,
        KeyCode.I => VirtualKey.I,
        KeyCode.J => VirtualKey.J,
        KeyCode.K => VirtualKey.K,
        KeyCode.L => VirtualKey.L,
        KeyCode.M => VirtualKey.M,
        KeyCode.N => VirtualKey.N,
        KeyCode.O => VirtualKey.O,
        KeyCode.P => VirtualKey.P,
        KeyCode.Q => VirtualKey.Q,
        KeyCode.R => VirtualKey.R,
        KeyCode.S => VirtualKey.S,
        KeyCode.T => VirtualKey.T,
        KeyCode.U => VirtualKey.U,
        KeyCode.V => VirtualKey.V,
        KeyCode.W => VirtualKey.W,
        KeyCode.X => VirtualKey.X,
        KeyCode.Y => VirtualKey.Y,
        KeyCode.Z => VirtualKey.Z,
        KeyCode.D0 => VirtualKey.D0,
        KeyCode.D1 => VirtualKey.D1,
        KeyCode.D2 => VirtualKey.D2,
        KeyCode.D3 => VirtualKey.D3,
        KeyCode.D4 => VirtualKey.D4,
        KeyCode.D5 => VirtualKey.D5,
        KeyCode.D6 => VirtualKey.D6,
        KeyCode.D7 => VirtualKey.D7,
        KeyCode.D8 => VirtualKey.D8,
        KeyCode.D9 => VirtualKey.D9,
        _ => VirtualKey.None
    };

    #endregion

    #region IDisposable

    /// <summary>
    /// Releases all held inputs and tears down the dedicated ghost desktop worker thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            ReleaseAll();
        }
        catch { }

        _queue.CompleteAdding();

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(1500);
        }

        _queue.Dispose();
        _workerReady.Dispose();
    }

    #endregion
}
