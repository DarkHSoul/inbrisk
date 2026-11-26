using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

/// <para>
/// Because input is posted directly to ghost window message queues without calling
/// <c>SendInput</c> or <c>SetCursorPos</c>, physical mouse cursor and keyboard focus
/// on the user's active "Default" desktop remain 100% undisturbed (e.g. while gaming or typing).
/// </para>
/// Also provides PiP overlay coordinate mapping for interactive picture-in-picture modes.
/// </summary>
public sealed partial class GhostDesktopInput : IDisposable
{

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
    private const uint WM_NCHITTEST = 0x0084;

    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const uint WM_CLOSE = 0x0010;

    private const uint WM_POINTERUPDATE = 0x0245;
    private const uint WM_POINTERDOWN = 0x0246;

    private const uint WM_POINTERUP = 0x0247;

    // Non-Client Hit Test Codes
    private const int HTNOWHERE = 0;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    private const int HTMINBUTTON = 8;
    private const int HTMAXBUTTON = 9;

    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;

    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;

    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;

    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int HTCLOSE = 20;

    // SetWindowPos Flags
    private const uint SWP_NOSIZE = 0x0001;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;

    private const uint SWP_NOACTIVATE = 0x0010;

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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd,
            uint Msg,
            IntPtr wParam,
            IntPtr lParam,
            uint fuFlags,
            uint uTimeout,
            out IntPtr lpdwResult);

        [DllImport("user32.dll")]
        public static extern IntPtr GetLastActivePopup(IntPtr hWnd);
    }



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

    private IntPtr _capturedMouseHwnd = IntPtr.Zero;
    private readonly Dictionary<uint, IntPtr> _heldKeysHwnd = new();

    // Interactive Ghost Window Dragging & Resizing State
    private bool _isDraggingWindow;

    private IntPtr _dragHwnd = IntPtr.Zero;
    private int _dragHitTest;

    private int _dragStartMouseX;
    private int _dragStartMouseY;

    private RECT _dragStartWindowRect;



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
        if (_hDesktop == IntPtr.Zero)
        {
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostDesktopInput] EnsureDesktopAttached error: {ex.Message}");
            }
        }

        if (_hDesktop != IntPtr.Zero && !_isDesktopBound && Thread.CurrentThread == _workerThread)
        {
            _isDesktopBound = Win32.SetThreadDesktop(_hDesktop);
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
            if (Thread.CurrentThread == _workerThread && _hDesktop != IntPtr.Zero)
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

    /// <summary>
    /// Releases any held mouse buttons.
    /// </summary>
    public void ReleaseAll()
    {
        RunOnGhostThread(() =>
        {
            _isDraggingWindow = false;
            _dragHwnd = IntPtr.Zero;

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

            _capturedMouseHwnd = IntPtr.Zero;

            foreach (var kv in _heldKeysHwnd)
            {
                IntPtr hwnd = kv.Value;
                if (!IsWindowOnGhostDesktop(hwnd))
                {
                    continue;
                }

                uint vk = kv.Key;
                uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
                uint lParam = 1 | ((scanCode & 0xFF) << 16) | (1u << 30) | (1u << 31);
                if (IsExtendedKey(vk))
                {
                    lParam |= (1u << 24);
                }

                Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lParam);
            }
            _heldKeysHwnd.Clear();
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



    /// <summary>
    /// Releases all held inputs and tears down the dedicated ghost desktop worker thread.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            ReleaseAll();
        }
        catch { }

        _disposed = true;

        _queue.CompleteAdding();

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(1500);
        }

        _queue.Dispose();
        _workerReady.Dispose();
    }
}
