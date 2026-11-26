using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;


/// <summary>
/// High-speed Keyboard Forwarder and Global Hotkey Listener service.
/// Intercepts WM_KEYDOWN, WM_KEYUP, and WM_CHAR messages when the PiP window is focused
/// in interactive mode, and forwards Unicode characters and virtual key codes to Session 2
/// across the Ghost IPC bus.
/// Manages global hotkeys (RegisterHotKey):
/// - Ctrl + Alt + I: Toggle interactive mode (quick user intervention)
/// - Ctrl + Shift + P: Toggle PiP window visibility (hide / show)
/// - Esc: If fullscreen, restore back to mini PiP box
/// </summary>
public sealed partial class GhostKeyboardForwarder : IDisposable
{

    private const int SW_HIDE = 0;

    private const int SW_SHOWNOACTIVATE = 4;
    private const int SW_SHOWNA = 8;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndMessage = new(-3);

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_QUIT = 0x0012;

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;

    private const uint WM_CHAR = 0x0102;
    private const uint WM_SYSKEYDOWN = 0x0104;

    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_SYSCHAR = 0x0106;

    private const uint WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x0001;

    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;

    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const int VK_ESCAPE = 0x1B;
    public const int VK_I = 0x49;

    public const int VK_P = 0x50;

    public const int SM_CXSCREEN = 0;

    public const int SM_CYSCREEN = 1;
    private const int GWL_EXSTYLE = -20;

    private const uint WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>Hotkey ID for Ctrl + Alt + I (Toggle interactive mode).</summary>
    public const int HotKeyToggleInteractiveId = 0x1B8A01;

    /// <summary>Hotkey ID for Ctrl + Shift + P (Toggle visibility).</summary>
    public const int HotKeyToggleVisibilityId = 0x1B8A02;

    /// <summary>Hotkey ID for Esc (Exit fullscreen mode).</summary>
    public const int HotKeyEscapeFullscreenId = 0x1B8A03;

    private const string ListenerWindowClass = "InbriskHotkeyListenerWindowClass";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    private static class Win32
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string? lpModuleName);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(int nVirtKey);
    }



    private static readonly object ClassLock = new();

    private static bool _classRegistered;
    private static WndProcDelegate? _staticWndProc;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _listenerReady = new();

    private IGhostIpcBus? _ipcBus;
    private GhostPipManager? _pipManager;

    private IntPtr _targetHwnd = IntPtr.Zero;

    private Thread? _listenerThread;

    private uint _listenerThreadId;
    private IntPtr _listenerHwnd = IntPtr.Zero;

    private SubclassProc? _subclassDelegate;
    private IntPtr _subclassedHwnd = IntPtr.Zero;

    private static readonly UIntPtr SubclassId = new(0x1B8A);

    private volatile bool _isInteractive;

    private volatile bool _forwardingEnabled = true;
    private volatile bool _isFullscreen;

    private volatile bool _registerEscOnlyWhenFullscreen = true;
    private volatile bool _isListening;

    private volatile bool _disposed;
    private volatile bool _panicReleaseSent;

    private static (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win)[]? _emergencyChords;

    private (int X, int Y, int Width, int Height)? _savedBounds;



    /// <summary>Raised when interactive mode is toggled (e.g. via Ctrl+Alt+I).</summary>
    public event Action<bool>? InteractiveModeToggled;

    /// <summary>Raised when PiP window visibility is toggled (e.g. via Ctrl+Shift+P).</summary>
    public event Action<bool>? VisibilityToggled;

    /// <summary>Raised when fullscreen state changes.</summary>
    public event Action<bool>? FullscreenChanged;

    /// <summary>Raised when Esc is pressed in fullscreen mode requesting exit back to mini PiP.</summary>
    public event Action? FullscreenExitRequested;

    /// <summary>Raised when Esc key is pressed.</summary>
    public event Action? EscapePressed;

    /// <summary>Raised whenever a global hotkey is triggered.</summary>
    public event Action<int>? HotKeyPressed;

    /// <summary>Raised when a keyboard event has been successfully forwarded to Session 2.</summary>
    public event Action<GhostKeyboardPayload>? KeyForwarded;

    /// <summary>Raised when an error occurs during IPC key forwarding.</summary>
    public event Action<Exception>? ForwardingError;



    /// <summary>Gets or sets the IPC bus used to dispatch keyboard events to Session 2.</summary>
    public IGhostIpcBus? IpcBus
    {
        get => _ipcBus;
        set => _ipcBus = value;
    }

    /// <summary>Gets or sets the associated PiP manager instance.</summary>
    public GhostPipManager? PipManager
    {
        get => _pipManager;
        set
        {
            _pipManager = value;
            if (_pipManager != null && _targetHwnd == IntPtr.Zero)
            {
                _targetHwnd = _pipManager.WindowHandle;
            }
        }
    }

    /// <summary>Gets or sets the target PiP native window handle.</summary>
    public IntPtr TargetHwnd
    {
        get => _targetHwnd != IntPtr.Zero ? _targetHwnd : (_pipManager?.WindowHandle ?? IntPtr.Zero);
        set
        {
            if (_targetHwnd != value)
            {
                DetachWindowSubclass();
                _targetHwnd = value;
                if (value != IntPtr.Zero && _isListening)
                {
                    AttachWindowSubclass(value);
                }
            }
        }
    }

    /// <summary>Gets whether interactive mode is currently active.</summary>
    public bool IsInteractive => _pipManager?.IsInteractive ?? _isInteractive;

    /// <summary>Gets whether the background hotkey listener is active.</summary>
    public bool IsListening => _isListening;

    /// <summary>Gets whether the PiP window is currently expanded to fullscreen.</summary>
    public bool IsFullscreen => _isFullscreen;

    /// <summary>Gets or sets whether keyboard forwarding to Session 2 is currently enabled.</summary>
    public bool ForwardingEnabled
    {
        get => _forwardingEnabled;
        set => _forwardingEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether the global Esc hotkey is registered only during fullscreen mode (default: true).
    /// Prevents hijacking the user's Esc key in other applications when PiP is running in mini box mode.
    /// </summary>
    public bool RegisterEscOnlyWhenFullscreen
    {
        get => _registerEscOnlyWhenFullscreen;
        set
        {
            _registerEscOnlyWhenFullscreen = value;
            UpdateEscHotkeyRegistration();
        }
    }



    /// <summary>
    /// Initializes a new instance of the <see cref="GhostKeyboardForwarder"/> class.
    /// </summary>
    public GhostKeyboardForwarder()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GhostKeyboardForwarder"/> class with specified IPC bus and PiP manager.
    /// </summary>
    public GhostKeyboardForwarder(
        IGhostIpcBus? ipcBus,
        GhostPipManager? pipManager = null,
        IntPtr targetHwnd = default,
        bool autoStart = true)
    {
        _ipcBus = ipcBus;
        _pipManager = pipManager;
        _targetHwnd = targetHwnd != IntPtr.Zero ? targetHwnd : (pipManager?.WindowHandle ?? IntPtr.Zero);

        if (autoStart)
        {
            Start();
        }
    }



    /// <summary>
    /// Starts the dedicated background hotkey listener and attaches to the target PiP window if present.
    /// </summary>
    public void Start(int timeoutMs = 5000)
    {
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(GhostKeyboardForwarder));
            if (_isListening) return;

            if (OperatingSystem.IsWindows())
            {
                _listenerReady.Reset();
                _listenerThread = new Thread(ListenerPump)
                {
                    IsBackground = true,
                    Name = "InbriskGhostHotkeyListener"
                };
                _listenerThread.SetApartmentState(ApartmentState.STA);
                _listenerThread.Start();

                _listenerReady.Wait(timeoutMs);

                var effectiveHwnd = TargetHwnd;
                if (effectiveHwnd != IntPtr.Zero)
                {
                    AttachWindowSubclass(effectiveHwnd);
                }
            }

            _isListening = true;
        }
    }

    /// <summary>
    /// Stops the hotkey listener and cleans up native hooks and window subclasses.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_isListening) return;

            DetachWindowSubclass();

            if (_listenerHwnd != IntPtr.Zero)
            {
                UnregisterAllHotkeys();
            }

            if (_listenerThreadId != 0)
            {
                Win32.PostThreadMessageW(_listenerThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                if (_listenerThreadId != Win32.GetCurrentThreadId())
                {
                    _listenerThread?.Join(1500);
                }
                _listenerThread = null;
                _listenerThreadId = 0;
            }

            _isListening = false;
        }
    }



    /// <summary>Attaches an IPC bus instance and returns this forwarder.</summary>
    public GhostKeyboardForwarder WithIpcBus(IGhostIpcBus bus)
    {
        IpcBus = bus;
        return this;
    }

    /// <summary>Attaches a GhostPipManager instance and returns this forwarder.</summary>
    public GhostKeyboardForwarder WithPipManager(GhostPipManager manager)
    {
        PipManager = manager;
        return this;
    }

    /// <summary>Attaches a target window handle and returns this forwarder.</summary>
    public GhostKeyboardForwarder WithTargetWindow(IntPtr hwnd)
    {
        TargetHwnd = hwnd;
        return this;
    }



    /// <summary>
    /// Disposes resources, unregisters all global hotkeys, and detaches native window hooks.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _listenerReady.Dispose();
        GC.SuppressFinalize(this);
    }
}
