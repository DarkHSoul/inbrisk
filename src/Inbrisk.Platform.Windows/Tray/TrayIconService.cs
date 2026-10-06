using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Tray;

/// <summary>
/// Windows notification area (System Tray) icon service.
/// User-owned control surface providing status, toggles, Settings entry,
/// and local Emergency Stop / Resume authority.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WmTrayCallback = NativeMethods.WM_APP + 20;
    private const uint TrayIconId = 1001;

    // Menu Command IDs
    private const uint CmdShowHud = 2001;
    private const uint CmdShowPerimeter = 2002;
    private const uint CmdOpenSettings = 2003;
    private const uint CmdEmergencyStop = 2004;
    private const uint CmdResume = 2005;
    private const uint CmdQuit = 2006;
    private const uint CmdPreviewHud = 2007;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hwnd;
    private GCHandle _hwndHandle;
    private Mutex? _trayMutex;
    private bool _hasTrayAuthority;
    private uint _wmTaskbarCreated;

    private static NativeMethods.WndProc? _proc;
    private static ushort _classAtom;
    private static readonly object ClassGate = new();
    private static TrayIconService? _current;

    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconAdded;

    // Runtime state bindings
    private string _mcpClientName = "Devin";
    private bool _isMcpConnected;
    private bool _isWorking;
    private bool _isEmergency;
    private bool _hudEnabled = true;
    private bool _perimeterEnabled = true;

    // Actions
    public Action? OnOpenSettings { get; set; }
    public Action? OnEmergencyStop { get; set; }
    public Action? OnResume { get; set; }
    public Action<bool>? OnToggleHud { get; set; }
    public Action<bool>? OnTogglePerimeter { get; set; }
    public Action? OnPreviewHud { get; set; }
    public Action? OnQuit { get; set; }

    private readonly bool _deduplicate;

    public bool HasAuthority => _hasTrayAuthority;
    public IntPtr Hwnd => _hwnd;

    public TrayIconService(UserSettings? settings = null, bool deduplicate = true)
    {
        _deduplicate = deduplicate;
        if (settings != null)
        {
            _hudEnabled = settings.HudEnabled;
            _perimeterEnabled = settings.PerimeterEnabled;
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Pump) { IsBackground = true, Name = "InbriskTrayIcon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public bool WaitForReady(int ms = 5000) => _ready.Wait(ms);

    public void UpdateStatus(bool mcpConnected, string? clientName, bool working, bool emergency, bool hudEnabled, bool perimeterEnabled)
    {
        lock (_gate)
        {
            _isMcpConnected = mcpConnected;
            if (!string.IsNullOrEmpty(clientName)) _mcpClientName = clientName;
            _isWorking = working;
            _isEmergency = emergency;
            _hudEnabled = hudEnabled;
            _perimeterEnabled = perimeterEnabled;
        }
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;

        string tip;
        lock (_gate)
        {
            if (_isEmergency)
                tip = "Inbrisk — STOPPED (Emergency)";
            else if (_isWorking)
                tip = $"Inbrisk — Working ({_mcpClientName})";
            else if (_isMcpConnected)
                tip = $"Inbrisk — Ready ({_mcpClientName})";
            else
                tip = "Inbrisk — Standby";
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            HWnd = _hwnd,
            UId = TrayIconId,
            UFlags = NativeMethods.NIF_TIP,
            SzTip = tip
        };
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
    }

    // ------------------------------------------------------------ pump

    private void Pump()
    {
        // Boundary: bare message-pump thread — nothing may escape or the
        // process dies; _ready must always be released so Start() never hangs.
        try
        {
            PumpCore();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"inbrisk-tray pump exited: {e}");
        }
        finally
        {
            _ready.Set();
        }
    }

    private void PumpCore()
    {
        _threadId = NativeMethods.GetCurrentThreadId();

        // Mutex for single tray icon deduplication across processes
        if (_deduplicate)
        {
            try
            {
                _trayMutex = new Mutex(initiallyOwned: true, @"Global\InbriskTrayIconMutex", out var createdNew);
                _hasTrayAuthority = createdNew;
            }
            catch
            {
                _hasTrayAuthority = false;
            }

            if (!_hasTrayAuthority)
            {
                _ready.Set();
                return; // Only the owner process manages the tray icon
            }
        }
        else
        {
            _hasTrayAuthority = true;
        }

        _current = this;
        try
        {
            RegisterWindowClass();
            _hwndHandle = GCHandle.Alloc(this);
            _hwnd = NativeMethods.CreateWindowExW(
                0,
                "InbriskTrayMessageWindow",
                "InbriskTray",
                0, 0, 0, 0, 0,
                IntPtr.Zero, IntPtr.Zero,
                NativeMethods.GetModuleHandleW(null),
                GCHandle.ToIntPtr(_hwndHandle));

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, -21, GCHandle.ToIntPtr(_hwndHandle));
            }

            _wmTaskbarCreated = NativeMethods.RegisterWindowMessageW("TaskbarCreated");

            CreateTrayIcon();
        }
        catch (Exception e)
        {
            // Init fault: tray degrades to no-window; the pump still services
            // the queue so Dispose() can tear down cleanly.
            Debug.WriteLine($"inbrisk-tray init fault: {e}");
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
            catch (Exception e)
            {
                // Per-iteration containment: a menu-callback/wndproc fault
                // must not take the whole pump (or process) down.
                Debug.WriteLine($"inbrisk-tray pump fault: {e}");
            }
        }

        RemoveTrayIcon();
        if (_hwndHandle.IsAllocated) _hwndHandle.Free();
        try { _trayMutex?.ReleaseMutex(); } catch { }
        _trayMutex?.Dispose();
    }

    private void RegisterWindowClass()
    {
        lock (ClassGate)
        {
            if (_classAtom != 0) return;
            _proc = StaticWndProc;
            var wcx = new NativeMethods.WNDCLASSEXW
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                Style = 0,
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                HInstance = NativeMethods.GetModuleHandleW(null),
                LpszClassName = "InbriskTrayMessageWindow",
            };
            _classAtom = RegisterClassExW(ref wcx);
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref NativeMethods.WNDCLASSEXW lpwcx);

    private void CreateTrayIcon()
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            using var bmp = AppIconExtractor.GetInbriskLogo(32);
            _hIcon = bmp.GetHicon();
        }
        catch
        {
            _hIcon = NativeMethods.LoadIconW(IntPtr.Zero, NativeMethods.IDI_APPLICATION);
        }

        var nid = new NativeMethods.NOTIFYICONDATAW
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            HWnd = _hwnd,
            UId = TrayIconId,
            UFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            UCallbackMessage = WmTrayCallback,
            HIcon = _hIcon,
            SzTip = "Inbrisk — Ready"
        };

        _iconAdded = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref nid);
    }

    private void RemoveTrayIcon()
    {
        if (_iconAdded && _hwnd != IntPtr.Zero)
        {
            var nid = new NativeMethods.NOTIFYICONDATAW
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
                HWnd = _hwnd,
                UId = TrayIconId,
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

    public void OpenSettings()
    {
        if (OnOpenSettings != null)
        {
            try { OnOpenSettings.Invoke(); return; }
            catch (Exception ex) { Debug.WriteLine($"OnOpenSettings error: {ex.Message}"); }
        }

        try
        {
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "settings") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to open settings: {ex.Message}");
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var owner = OwnerOf(hwnd);
        if (owner != null)
        {
            if (msg == owner._wmTaskbarCreated)
            {
                // Explorer restarted — re-register tray icon
                owner.CreateTrayIcon();
                return IntPtr.Zero;
            }

            if (msg == WmTrayCallback)
            {
                var lparamMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (lparamMsg is NativeMethods.WM_LBUTTONUP or NativeMethods.WM_LBUTTONDBLCLK or 0x0400 /* NIN_SELECT */ or 0x0401 /* NIN_KEYSELECT */)
                {
                    owner.OpenSettings();
                }
                else if (lparamMsg is NativeMethods.WM_RBUTTONUP or NativeMethods.WM_CONTEXTMENU)
                {
                    owner.ShowContextMenu();
                }
                return IntPtr.Zero;
            }
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static TrayIconService? OwnerOf(IntPtr hwnd)
    {
        var p = NativeMethods.GetWindowLongPtr(hwnd, -21); // GWLP_USERDATA
        if (p != IntPtr.Zero)
        {
            try
            {
                if (GCHandle.FromIntPtr(p).Target is TrayIconService s) return s;
            }
            catch { }
        }
        return _current;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private void ShowContextMenu()
    {
        if (_hwnd == IntPtr.Zero) return;

        GetCursorPos(out var pt);
        var hMenu = NativeMethods.CreatePopupMenu();

        bool emergency, working, connected, hudOn, perimOn;
        string client;

        lock (_gate)
        {
            emergency = _isEmergency;
            working = _isWorking;
            connected = _isMcpConnected;
            client = _mcpClientName;
            hudOn = _hudEnabled;
            perimOn = _perimeterEnabled;
        }

        if (emergency)
        {
            // Emergency Menu
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | NativeMethods.MF_DISABLED, (UIntPtr)0, "INBRISK — STOPPED");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdResume, "Resume Control");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdOpenSettings, "Settings...");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdQuit, "Quit Inbrisk");
        }
        else
        {
            // Normal Menu
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | NativeMethods.MF_DISABLED, (UIntPtr)0, "Inbrisk");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);

            var mcpStatus = connected ? $"MCP: Connected — {client}" : "MCP: Standby";
            var runStatus = working ? "Status: Working" : "Status: Ready";
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | NativeMethods.MF_DISABLED, (UIntPtr)0, mcpStatus);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | NativeMethods.MF_DISABLED, (UIntPtr)0, runStatus);

            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | (hudOn ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED), (UIntPtr)CmdShowHud, "Show Activity HUD");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING | (perimOn ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED), (UIntPtr)CmdShowPerimeter, "Show Perimeter Smoke");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdPreviewHud, "HUD Önizleme (Test Et)");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdOpenSettings, "Settings...");

            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdEmergencyStop, "Emergency Stop");
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_SEPARATOR, (UIntPtr)0, null);
            NativeMethods.AppendMenuW(hMenu, NativeMethods.MF_STRING, (UIntPtr)CmdQuit, "Quit Inbrisk");
        }

        NativeMethods.SetForegroundWindow(_hwnd);
        var cmd = NativeMethods.TrackPopupMenuEx(
            hMenu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON,
            pt.X, pt.Y,
            _hwnd,
            IntPtr.Zero);

        NativeMethods.PostMessageW(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        NativeMethods.DestroyMenu(hMenu);

        // Dispatch selection
        switch (cmd)
        {
            case CmdOpenSettings:
                OpenSettings();
                break;
            case CmdShowHud:
                OnToggleHud?.Invoke(!hudOn);
                break;
            case CmdShowPerimeter:
                OnTogglePerimeter?.Invoke(!perimOn);
                break;
            case CmdPreviewHud:
                OnPreviewHud?.Invoke();
                break;
            case CmdEmergencyStop:
                OnEmergencyStop?.Invoke();
                break;
            case CmdResume:
                OnResume?.Invoke();
                break;
            case CmdQuit:
                OnQuit?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(2000);
            _threadId = 0;
        }
        _ready.Dispose();
    }
}
