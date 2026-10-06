using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

/// <summary>
/// Windows Notification Area (System Tray) manager for Inbrisk Ghost OS.
/// Controls the tray icon lifecycle, status indicator, and native Win32 context menu
/// for PiP window interactivity, full-screen toggle, opacity settings, and session stop/exit.
/// </summary>
public sealed class GhostTrayApp : IDisposable
{
    private const uint WmTrayCallback = NativeMethods.WM_APP + 30;
    private const uint GhostTrayIconId = 2001;
    private const string WindowClassName = "InbriskGhostTrayMessageWindowClass";
    private const int GwlpUserdata = -21;

    // Win32 menu constants
    private const uint MF_POPUP = 0x00000010;

    // Menu Command IDs
    private const uint CmdStatus = 2100;
    private const uint CmdInteractive = 2101;
    private const uint CmdFullScreen = 2102;
    private const uint CmdVisibility = 2103;
    private const uint CmdOpacity50 = 2104;
    private const uint CmdOpacity80 = 2105;
    private const uint CmdOpacity100 = 2106;
    private const uint CmdStop = 2107;
    private const uint CmdExit = 2108;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd = IntPtr.Zero;
    private GCHandle _hwndHandle;
    private uint _wmTaskbarCreated;

    private static NativeMethods.WndProc? _wndProcDelegate;
    private static ushort _classAtom;
    private static readonly object ClassGate = new();
    private static GhostTrayApp? _current;

    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconAdded;
    private volatile bool _disposed;

    // Runtime state bindings
    private string _statusText = "🟢 Ghost OS: RUNNING";
    private string _tooltipText = "Inbrisk Ghost OS — RUNNING";
    private bool _isInteractive;
    private bool _isFullScreen;
    private bool _isVisible = true;
    private byte _opacity = 255;

    #region Events

    /// <summary>
    /// Raised when PiP interactive mode (click-through toggle) is changed.
    /// </summary>
    public event Action<bool>? OnInteractiveToggled;

    /// <summary>
    /// Raised when PiP full-screen display state is toggled.
    /// </summary>
    public event Action<bool>? OnFullScreenToggled;

    /// <summary>
    /// Raised when PiP live preview visibility is changed.
    /// </summary>
    public event Action<bool>? OnVisibilityToggled;

    /// <summary>
    /// Raised when PiP alpha opacity is changed (e.g. 128 for 50%, 204 for 80%, 255 for 100%).
    /// </summary>
    public event Action<byte>? OnOpacityChanged;

    /// <summary>
    /// Raised when the user requests stopping the Ghost OS session.
    /// </summary>
    public event Action? OnStopRequested;

    /// <summary>
    /// Raised when the user requests quitting the tray application.
    /// </summary>
    public event Action? OnExitRequested;

    /// <summary>
    /// Raised when the tray icon is clicked with the primary mouse button.
    /// </summary>
    public event Action? OnTrayIconClicked;

    #endregion

    #region Properties

    /// <summary>
    /// Gets or sets whether PiP interactive mode (clickable) is active.
    /// </summary>
    public bool IsInteractive
    {
        get { lock (_gate) return _isInteractive; }
        set => SetInteractive(value);
    }

    /// <summary>
    /// Gets or sets whether PiP is displayed in full-screen mode.
    /// </summary>
    public bool IsFullScreen
    {
        get { lock (_gate) return _isFullScreen; }
        set => SetFullScreen(value);
    }

    /// <summary>
    /// Gets or sets whether PiP preview window is visible.
    /// </summary>
    public bool IsVisible
    {
        get { lock (_gate) return _isVisible; }
        set => SetVisible(value);
    }

    /// <summary>
    /// Gets or sets current PiP opacity (0-255).
    /// </summary>
    public byte Opacity
    {
        get { lock (_gate) return _opacity; }
        set => SetOpacity(value);
    }

    /// <summary>
    /// Gets or sets the menu header status label.
    /// </summary>
    public string StatusText
    {
        get { lock (_gate) return _statusText; }
        set => SetStatus(value);
    }

    /// <summary>
    /// Gets or sets the system tray tooltip text.
    /// </summary>
    public string TooltipText
    {
        get { lock (_gate) return _tooltipText; }
        set
        {
            lock (_gate) _tooltipText = value;
            UpdateTrayTooltip();
        }
    }

    /// <summary>
    /// Gets the native Win32 window handle receiving tray messages.
    /// </summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>
    /// Gets whether the background message pump is running.
    /// </summary>
    public bool IsRunning => _thread != null && _thread.IsAlive && _hwnd != IntPtr.Zero;

    #endregion

    public GhostTrayApp(string? initialStatus = null, bool visible = true)
    {
        if (!string.IsNullOrEmpty(initialStatus))
        {
            _statusText = initialStatus;
            _tooltipText = $"Inbrisk Ghost OS — {initialStatus}";
        }
        _isVisible = visible;
    }

    /// <summary>
    /// Starts the background STA message pump thread and registers the system tray icon.
    /// </summary>
    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Pump) { IsBackground = true, Name = "InbriskGhostTray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>
    /// Waits for the tray window and message loop to become ready.
    /// </summary>
    public bool WaitForReady(int ms = 5000) => _ready.Wait(ms);

    #region State Mutation

    public void SetInteractive(bool enabled)
    {
        lock (_gate) _isInteractive = enabled;
    }

    public void SetFullScreen(bool fullScreen)
    {
        lock (_gate) _isFullScreen = fullScreen;
    }

    public void SetVisible(bool visible)
    {
        lock (_gate) _isVisible = visible;
    }

    public void SetOpacity(byte alpha)
    {
        lock (_gate) _opacity = alpha;
    }

    public void SetStatus(string status, string? tooltip = null)
    {
        lock (_gate)
        {
            _statusText = status;
            if (tooltip != null) _tooltipText = tooltip;
        }
        UpdateTrayTooltip();
    }

    public void ToggleInteractive()
    {
        bool val;
        lock (_gate)
        {
            _isInteractive = !_isInteractive;
            val = _isInteractive;
        }
        OnInteractiveToggled?.Invoke(val);
    }

    public void ToggleFullScreen()
    {
        bool val;
        lock (_gate)
        {
            _isFullScreen = !_isFullScreen;
            val = _isFullScreen;
        }
        OnFullScreenToggled?.Invoke(val);
    }

    public void ToggleVisibility()
    {
        bool val;
        lock (_gate)
        {
            _isVisible = !_isVisible;
            val = _isVisible;
        }
        OnVisibilityToggled?.Invoke(val);
    }

    #endregion

    #region Pump & Window Management

    private void Pump()
    {
        try
        {
            PumpCore();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostTrayApp] Pump fault: {ex}");
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
        _current = this;

        try
        {
            EnsureClassRegistered();
            _hwndHandle = GCHandle.Alloc(this);
            _hwnd = NativeMethods.CreateWindowExW(
                0,
                WindowClassName,
                "InbriskGhostTrayMessageWindow",
                0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero,
                NativeMethods.GetModuleHandleW(null),
                GCHandle.ToIntPtr(_hwndHandle));

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, GwlpUserdata, GCHandle.ToIntPtr(_hwndHandle));
            }

            _wmTaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            CreateTrayIcon();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostTrayApp] Initialization error: {ex}");
        }
        finally
        {
            _ready.Set();
        }

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostTrayApp] Message pump error: {ex}");
            }
        }

        RemoveTrayIcon();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_hwndHandle.IsAllocated)
        {
            _hwndHandle.Free();
        }
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
                Style = 0,
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                HInstance = NativeMethods.GetModuleHandleW(null),
                LpszClassName = WindowClassName
            };
            _classAtom = NativeMethods.RegisterClassExW(ref wcx);
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var owner = OwnerOf(hwnd);
        if (owner != null)
        {
            if (msg == owner._wmTaskbarCreated)
            {
                // Taskbar / Explorer restarted -> re-create tray icon
                owner.CreateTrayIcon();
                return IntPtr.Zero;
            }

            if (msg == WmTrayCallback)
            {
                var lparamMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (lparamMsg is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
                {
                    owner.ShowContextMenu();
                }
                else if (lparamMsg is NativeMethods.WM_LBUTTONUP or 0x0400 /* NIN_SELECT */)
                {
                    owner.HandleLeftClick();
                }
                else if (lparamMsg is NativeMethods.WM_LBUTTONDBLCLK)
                {
                    owner.ToggleFullScreen();
                }
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static GhostTrayApp? OwnerOf(IntPtr hwnd)
    {
        var p = NativeMethods.GetWindowLongPtr(hwnd, GwlpUserdata);
        if (p != IntPtr.Zero)
        {
            try
            {
                if (GCHandle.FromIntPtr(p).Target is GhostTrayApp app) return app;
            }
            catch { }
        }
        return _current;
    }

    #endregion

    #region Tray Icon (NIM_ADD, NIM_MODIFY, NIM_DELETE)

    /// <summary>
    /// Creates or re-registers the tray icon using NIM_ADD.
    /// </summary>
    public void CreateTrayIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_hIcon == IntPtr.Zero)
        {
            try
            {
                using var bmp = AppIconExtractor.GetInbriskLogo(32);
                _hIcon = bmp.GetHicon();
            }
            catch
            {
                _hIcon = NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
            }
        }

        string tip;
        lock (_gate) tip = _tooltipText;

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            HWnd = _hwnd,
            UId = GhostTrayIconId,
            UFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            UCallbackMessage = WmTrayCallback,
            HIcon = _hIcon,
            SzTip = string.IsNullOrEmpty(tip) ? "Inbrisk Ghost OS" : tip
        };

        _iconAdded = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    /// <summary>
    /// Updates the tray icon tooltip text using NIM_MODIFY.
    /// </summary>
    public void UpdateTrayTooltip()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;

        string tip;
        lock (_gate) tip = _tooltipText;

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            HWnd = _hwnd,
            UId = GhostTrayIconId,
            UFlags = NativeMethods.NIF_TIP,
            SzTip = string.IsNullOrEmpty(tip) ? "Inbrisk Ghost OS" : tip
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Updates the tray icon image and/or tooltip using NIM_MODIFY.
    /// </summary>
    public void UpdateTrayIcon(IntPtr hIcon, string? tooltip = null)
    {
        lock (_gate)
        {
            if (_hIcon != IntPtr.Zero && _hIcon != hIcon)
            {
                NativeMethods.DestroyIcon(_hIcon);
            }
            _hIcon = hIcon;
            if (tooltip != null) _tooltipText = tooltip;
        }

        if (!_iconAdded || _hwnd == IntPtr.Zero) return;

        string tip;
        lock (_gate) tip = _tooltipText;

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            HWnd = _hwnd,
            UId = GhostTrayIconId,
            UFlags = NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            HIcon = _hIcon,
            SzTip = string.IsNullOrEmpty(tip) ? "Inbrisk Ghost OS" : tip
        };

        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    /// <summary>
    /// Deletes the tray icon from the notification area using NIM_DELETE and frees icon resources.
    /// </summary>
    public void RemoveTrayIcon()
    {
        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var nid = new NativeMethods.NOTIFYICONDATAW
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                HWnd = _hwnd,
                UId = GhostTrayIconId
            };
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref nid);
            _iconAdded = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    #endregion

    #region Context Menu

    private void HandleLeftClick()
    {
        if (OnTrayIconClicked != null)
        {
            OnTrayIconClicked.Invoke();
        }
        else
        {
            ToggleVisibility();
        }
    }

    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero) return;

        NativeMethods.GetCursorPos(out var pt);
        var hMenu = NativeMethods.CreatePopupMenu();
        var hOpacitySubMenu = NativeMethods.CreatePopupMenu();

        bool interactive, fullScreen, visible;
        byte opacity;
        string status;

        lock (_gate)
        {
            interactive = _isInteractive;
            fullScreen = _isFullScreen;
            visible = _isVisible;
            opacity = _opacity;
            status = _statusText;
        }

        // 1. Status Title (disabled/read-only)
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | NativeMethods.MF_DISABLED, (UIntPtr)0, status);
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);

        // 2. Interactive Mode (Checkable)
        var interactiveLabel = (interactive ? "[✓] " : "[ ] ") + "PiP: İnteraktif Mod (Tıklanabilir)";
        NativeMethods.AppendMenuW(
            hMenu,
            NativeMethods.MF_STRING | (interactive ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
            (UIntPtr)CmdInteractive,
            interactiveLabel);

        // 3. Full-Screen Toggle
        var fullScreenLabel = "↗️ Tam Ekrana Genişlet / Küçült";
        NativeMethods.AppendMenuW(
            hMenu,
            NativeMethods.MF_STRING | (fullScreen ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
            (UIntPtr)CmdFullScreen,
            fullScreenLabel);

        // 4. Live Preview Visibility (Checkable)
        var visibilityLabel = (visible ? "[✓] " : "[ ] ") + "PiP Canlı Önizlemeyi Göster";
        NativeMethods.AppendMenuW(
            hMenu,
            NativeMethods.MF_STRING | (visible ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
            (UIntPtr)CmdVisibility,
            visibilityLabel);

        // 5. Opacity Submenu
        bool is50 = opacity <= 165 && opacity >= 100;
        bool is80 = opacity > 165 && opacity < 230;
        bool is100 = opacity >= 230;

        NativeMethods.AppendMenuW(hOpacitySubMenu, NativeMethods.MF_STRING | (is50 ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED), (UIntPtr)CmdOpacity50, "%50");
        NativeMethods.AppendMenuW(hOpacitySubMenu, NativeMethods.MF_STRING | (is80 ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED), (UIntPtr)CmdOpacity80, "%80");
        NativeMethods.AppendMenuW(hOpacitySubMenu, NativeMethods.MF_STRING | (is100 ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED), (UIntPtr)CmdOpacity100, "%100");

        NativeMethods.AppendMenuW(hMenu, MF_POPUP, (UIntPtr)(ulong)hOpacitySubMenu.ToInt64(), "⚙️ Opaklık: %50 / %80 / %100");

        // 6. Separator
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);

        // 7. Stop Session
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdStop, "⏹️ Oturumu Durdur");

        // 8. Exit
        NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdExit, "❌ Çıkış");

        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenuEx(
            hMenu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            pt.X, pt.Y,
            _hwnd,
            IntPtr.Zero);

        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);

        switch (cmd)
        {
            case CmdInteractive:
                ToggleInteractive();
                break;
            case CmdFullScreen:
                ToggleFullScreen();
                break;
            case CmdVisibility:
                ToggleVisibility();
                break;
            case CmdOpacity50:
                SetOpacity(128);
                OnOpacityChanged?.Invoke(128);
                break;
            case CmdOpacity80:
                SetOpacity(204);
                OnOpacityChanged?.Invoke(204);
                break;
            case CmdOpacity100:
                SetOpacity(255);
                OnOpacityChanged?.Invoke(255);
                break;
            case CmdStop:
                OnStopRequested?.Invoke();
                break;
            case CmdExit:
                OnExitRequested?.Invoke();
                Dispose();
                break;
        }
    }

    #endregion

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
