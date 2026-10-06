using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Hud;

/// <summary>
/// Floating Activity Pill / HUD service.
/// Displays a lightweight, non-activating, topmost, click-through, DPI-aware,
/// capture-excluded capsule overlay at the top-center of the primary monitor.
/// </summary>
public sealed class ActivityHudService : IDisposable
{
    private const uint WmAppState = NativeMethods.WM_APP + 10;
    private const uint WmAppTick = NativeMethods.WM_APP + 11;
    private const uint WmAppRebuild = NativeMethods.WM_APP + 12;

    private const int GwlpUserdata = -21;

    // Bobbing parameters: ±2.5px displacement, ~1.8s full cycle
    private const double BobAmpPx = 2.5;
    private const double BobDegPerMs = 360.0 / 1800.0;

    // Motion durations in ms
    private const long SettleDurationMs = 300;
    private const long BloomRiseMs = 350;
    private const long BloomFadeMs = 400;
    private const long DissolveDurationMs = 300;
    private const long FailureHoldMs = 800;

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;

    // Pump thread state
    private IntPtr _hwnd;
    private GCHandle _hwndHandle;
    private System.Threading.Timer? _animTimer;
    private static NativeMethods.WndProc? _proc;
    private static ushort _classAtom;
    private static readonly object ClassGate = new();
    private static ActivityHudService? _current;
    private static IntPtr _hHandCursor = IntPtr.Zero;
    private static IntPtr _hSizeCursor = IntPtr.Zero;

    private static IntPtr GetSizeCursor()
    {
        if (_hSizeCursor != IntPtr.Zero) return _hSizeCursor;
        _hSizeCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_SIZEWE);
        if (_hSizeCursor == IntPtr.Zero)
            _hSizeCursor = GetHandCursor();
        return _hSizeCursor;
    }

    // Taskbar drag state — the pill is draggable horizontally inside the
    // taskbar band between the task-list buttons and the tray icons.
    private bool _dragging;
    private bool _dragMoved;
    private int _dragAnchorCursorX;
    private int _dragAnchorCursorY;
    private int _dragStartX;
    private int _dockGapDip = -1;
    private int _bandLeft, _bandRight;
    private long _lastBandCheckMs;

    private static IntPtr GetHandCursor()
    {
        if (_hHandCursor != IntPtr.Zero) return _hHandCursor;
        _hHandCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_HAND);
        if (_hHandCursor == IntPtr.Zero)
            _hHandCursor = NativeMethods.LoadCursorW(IntPtr.Zero, NativeMethods.IDC_ARROW);
        return _hHandCursor;
    }

    // Config & Preferences
    private volatile bool _enabled = true;
    private volatile bool _animationsEnabled = true;
    private volatile bool _alwaysVisible = true;
    private readonly int _frameMs = 16; // ~60fps for smooth bobbing

    // State machine
    private volatile HudState _state = HudState.Hidden;
    private string _currentText = "";
    private string? _pausedSubtitle;
    private string? _currentApp;
    private Bitmap? _currentIcon;
    private bool _emergency;
    private long _stateStartTime;
    private double _bobPhaseDeg;
    private double _lastBobDisplacement;
    private long _lastTickTime;

    // Dimensions
    private int _winX, _winY, _winW, _winH;
    private double _dpiScale = 1.0;
    private IntPtr _memDc, _dib, _oldBmp;
    private unsafe uint* _bits;

    public HudState State => _state;
    public string CurrentText => _currentText;
    public string? PausedSubtitle => _pausedSubtitle;
    public bool Enabled => _enabled;
    public bool AlwaysVisible => _alwaysVisible;
    public IntPtr Hwnd => _hwnd;
    public static string? ActiveHudText { get; internal set; }
    public Action? OnClick { get; set; }

    public ActivityHudService(bool enabled = true, bool animationsEnabled = true, bool alwaysVisible = false, int hudDockGapDip = -1)
    {
        _enabled = enabled;
        _animationsEnabled = animationsEnabled;
        _alwaysVisible = alwaysVisible;
        _dockGapDip = hudDockGapDip;
    }

    public void SetAlwaysVisible(bool alwaysVisible)
    {
        _alwaysVisible = alwaysVisible;
        if (alwaysVisible && _state == HudState.Hidden)
        {
            SetStandby();
        }
        else if (!alwaysVisible && _state == HudState.Standby)
        {
            _state = HudState.Hidden;
            Hide();
        }
    }

    public void Start()
    {
        if (_thread != null) return;
        _thread = new Thread(Pump) { IsBackground = true, Name = "InbriskActivityHud" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public bool WaitForReady(int ms = 5000) => _ready.Wait(ms);

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        PostTick();
    }

    public void SetAnimationsEnabled(bool enabled)
    {
        _animationsEnabled = enabled;
    }

    /// <summary>
    /// Displays a calm, idle Standby pill at the top of the screen (e.g. "Inbrisk Hazır").
    /// </summary>
    public void SetStandby(string text = "Inbrisk Hazır")
    {
        lock (_gate)
        {
            if (_emergency) return;
            _currentText = text;
            _pausedSubtitle = null;
            _currentApp = "Inbrisk";
            _currentIcon = AppIconExtractor.GetInbriskLogo(24);
            _state = HudState.Standby;
            _stateStartTime = Environment.TickCount64;
        }
        PostTick();
    }

    /// <summary>
    /// Updates semantic activity (e.g. "Spotify açılıyor…") and target app.
    /// Cancels any in-flight exit and immediately transitions into Working.
    /// </summary>
    public void SetActivity(string text, string? targetApp = null, long? hwndHint = null)
    {
        lock (_gate)
        {
            if (_emergency) return; // Emergency overrides normal activity
            _currentText = ActivityGranularity.Sanitize(text);
            ActiveHudText = _currentText;
            _pausedSubtitle = null;
            _currentApp = targetApp;
            _currentIcon = AppIconExtractor.GetIcon(targetApp, hwndHint);
            _state = HudState.Working;
            _stateStartTime = Environment.TickCount64;
        }
        PostTick();
    }

    /// <summary>
    /// Displays a calm, paused human takeover status showing the pause reason
    /// and the next planned step on the floating pill HUD.
    /// </summary>
    public void SetHumanTakeover(string reason, string? nextStep = null)
    {
        lock (_gate)
        {
            if (_emergency) return;
            _currentText = $"⏸ {ActivityGranularity.Sanitize(reason)}";
            ActiveHudText = _currentText;
            _pausedSubtitle = !string.IsNullOrWhiteSpace(nextStep)
                ? $"Sıradaki: {ActivityGranularity.Sanitize(nextStep)}"
                : null;
            _currentApp = "Human";
            _currentIcon = AppIconExtractor.GetInbriskLogo(24);
            _state = HudState.Standby;
            _stateStartTime = Environment.TickCount64;
        }
        PostTick();
    }

    /// <summary>
    /// Signals successful completion of current operation. Starts continuous
    /// settle -> success bloom -> dissolve exit sequence.
    /// </summary>
    public void SetSuccess(string? successText = null)
    {
        lock (_gate)
        {
            if (_emergency || _state == HudState.Hidden) return;
            ActiveHudText = null;
            _pausedSubtitle = null;
            if (!string.IsNullOrWhiteSpace(successText))
                _currentText = ActivityGranularity.Sanitize(successText);
            else if (!_currentText.StartsWith("✓"))
            {
                if (_currentText.Contains("durduruldu", StringComparison.OrdinalIgnoreCase))
                    _currentText = _alwaysVisible ? "Inbrisk Hazır" : "";
                else
                    _currentText = "✓ " + _currentText.TrimEnd('…');
            }

            _state = HudState.Settling;
            _stateStartTime = Environment.TickCount64;
        }
        PostTick();
    }

    /// <summary>
    /// Signals failure of current operation.
    /// </summary>
    public void SetFailure(string failureText)
    {
        lock (_gate)
        {
            if (_emergency) return;
            ActiveHudText = null;
            _pausedSubtitle = null;
            _currentText = "× " + ActivityGranularity.Sanitize(failureText);
            _state = HudState.Failure;
            _stateStartTime = Environment.TickCount64;
        }
        PostTick();
    }

    /// <summary>
    /// Emergency stop override. Stays visible until cleared.
    /// </summary>
    public void SetEmergency(bool stopped, string panicChord = "Ctrl+Alt+Pause", string resumeChord = "Ctrl+Alt+Shift+Pause")
    {
        lock (_gate)
        {
            _emergency = stopped;
            _pausedSubtitle = null;
            if (stopped)
            {
                _state = HudState.Emergency;
                _currentText = "■ Inbrisk durduruldu";
                ActiveHudText = _currentText;
                _currentApp = null;
                _currentIcon = null;
            }
            else
            {
                _emergency = false;
                ActiveHudText = null;
                // Clean transition — no success bloom. Always-visible
                // HUDs return to Standby; transient ones hide.
                if (_alwaysVisible)
                {
                    _currentText = "Inbrisk Hazır";
                    _currentApp = "Inbrisk";
                    _currentIcon = AppIconExtractor.GetInbriskLogo(24);
                    _state = HudState.Standby;
                }
                else
                {
                    _state = HudState.Hidden;
                    _currentText = ""; // no backing state may keep "Inbrisk durduruldu"
                }
                _stateStartTime = Environment.TickCount64;
            }
        }
        PostTick();
    }

    private void PostTick()
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, WmAppTick, IntPtr.Zero, IntPtr.Zero);
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
            System.Diagnostics.Debug.WriteLine($"inbrisk-hud pump exited: {e}");
        }
        finally
        {
            _ready.Set();
        }
    }

    private void PumpCore()
    {
        DesktopBridge.TrySwitchCurrentThread();
        _current = this;
        _threadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.SetProcessDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        try
        {
            RegisterWindowClass();
            CreateHudWindow();

            _animTimer = new System.Threading.Timer(_ =>
            {
                if (_threadId != 0)
                    NativeMethods.PostThreadMessageW(_threadId, WmAppTick, IntPtr.Zero, IntPtr.Zero);
            }, null, Timeout.Infinite, Timeout.Infinite);

            RecalculatePosition();
        }
        catch (Exception e)
        {
            // Init fault: HUD degrades to no-window; the pump still services
            // the queue so Dispose() can tear down cleanly.
            System.Diagnostics.Debug.WriteLine($"inbrisk-hud init fault: {e}");
        }
        finally
        {
            _ready.Set();
        }

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                if (msg.Message == WmAppTick) OnTick();
                else if (msg.Message == WmAppRebuild) RecalculatePosition();
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            catch (Exception e)
            {
                // Per-iteration containment: a transient wndproc/render fault
                // must not take the whole pump (or process) down.
                System.Diagnostics.Debug.WriteLine($"inbrisk-hud pump fault: {e}");
            }
        }

        Teardown();
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
                HCursor = GetHandCursor(),
                LpszClassName = "InbriskActivityHudWindow",
            };
            _classAtom = RegisterClassExW(ref wcx);
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref NativeMethods.WNDCLASSEXW lpwcx);

    private void CreateHudWindow()
    {
        _hwndHandle = GCHandle.Alloc(this);
        const uint exStyle = NativeMethods.WS_EX_TOPMOST |
                             NativeMethods.WS_EX_LAYERED |
                             NativeMethods.WS_EX_TOOLWINDOW |
                             NativeMethods.WS_EX_NOACTIVATE;

        _winW = 330;
        _winH = 44;
        _winX = 100;
        _winY = 20;

        _hwnd = NativeMethods.CreateWindowExW(
            exStyle,
            "InbriskActivityHudWindow",
            null,
            NativeMethods.WS_POPUP,
            _winX, _winY, _winW, _winH,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null),
            GCHandle.ToIntPtr(_hwndHandle));

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowLongPtr(_hwnd, GwlpUserdata, GCHandle.ToIntPtr(_hwndHandle));
            // Exclude from Inbrisk's AI vision / screenshots
            NativeMethods.SetWindowDisplayAffinity(_hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
        }

        AllocateFramebuffer(_winW, _winH);
    }

    private unsafe void AllocateFramebuffer(int w, int h)
    {
        if (_dib != IntPtr.Zero)
        {
            if (_oldBmp != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldBmp);
            NativeMethods.DeleteObject(_dib);
            NativeMethods.DeleteDC(_memDc);
        }

        var bi = new NativeMethods.BITMAPINFOHEADER
        {
            BiSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
            BiWidth = w,
            BiHeight = -h, // top-down
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = NativeMethods.BI_RGB,
        };

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _dib = NativeMethods.CreateDIBSection(screenDc, ref bi,
            NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

        _bits = (uint*)bits;
        _memDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        _oldBmp = NativeMethods.SelectObject(_memDc, _dib);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref Win32Rect pvParam, uint fWinIni);
    private const uint SpiGetWorkArea = 0x0030;

    private void RecalculatePosition()
    {
        try
        {
            var monitors = new WindowService().GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (primary != null)
            {
                _dpiScale = primary.DpiX / 96.0;
                var isEmergency = _state == HudState.Emergency;
                var hasSubtitle = isEmergency || !string.IsNullOrEmpty(_pausedSubtitle);
                var isWorking = _state == HudState.Working;

                int baseW;
                if (hasSubtitle) baseW = 280;
                else if (isWorking) baseW = Math.Clamp(170 + (_currentText.Length * 7), 210, 310);
                else baseW = 160; // Standby / Idle minimal footprint

                var baseH = 34; // Fits inside Windows 11 taskbar

                _winW = (int)(baseW * _dpiScale);
                _winH = (int)(baseH * _dpiScale);

                // Dock directly ON the Windows taskbar, immediately to the left of the tray icons
                var hTray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
                var rTray = new RECT();
                bool hasTaskbar = hTray != IntPtr.Zero && NativeMethods.GetWindowRect(hTray, out rTray) && (rTray.Right - rTray.Left) > 0;

                var hNotify = hasTaskbar ? NativeMethods.FindWindowExW(hTray, IntPtr.Zero, "TrayNotifyWnd", null) : IntPtr.Zero;
                var rNotify = new RECT();
                bool hasNotify = hNotify != IntPtr.Zero && NativeMethods.GetWindowRect(hNotify, out rNotify) && (rNotify.Right - rNotify.Left) > 0;

                if (hasTaskbar)
                {
                    int taskbarH = rTray.Bottom - rTray.Top;
                    _winY = rTray.Top + Math.Max(0, (taskbarH - _winH) / 2);

                    int trayLeft = hasNotify ? rNotify.Left : (rTray.Right - (int)(180 * _dpiScale));
                    var gapPx = _dockGapDip >= 0
                        ? (int)(_dockGapDip * _dpiScale)
                        : (int)(4 * _dpiScale);
                    _winX = ClampDockX(trayLeft - _winW - gapPx);

                    AllocateFramebuffer(_winW, _winH);
                    return;
                }

                // Fallback using SpiGetWorkArea if Shell_TrayWnd is hidden
                var wa = new Win32Rect();
                if (SystemParametersInfoW(SpiGetWorkArea, 0, ref wa, 0))
                {
                    _winX = wa.Right - _winW - (int)(180 * _dpiScale);
                    _winY = wa.Bottom + (int)(6 * _dpiScale);
                }
                else
                {
                    _winX = primary.Bounds.Right - _winW - (int)(180 * _dpiScale);
                    _winY = primary.Bounds.Bottom - _winH - (int)(8 * _dpiScale);
                }

                AllocateFramebuffer(_winW, _winH);
                return;
            }
        }
        catch { }

        // Fallback using SystemMetrics
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        _winW = 160;
        _winH = 34;
        _winX = screenW - _winW - 190;
        _winY = screenH - _winH - 7;
        AllocateFramebuffer(_winW, _winH);
    }

    /// <summary>
    /// Computes the taskbar "dock band": the free horizontal span between the
    /// right edge of the task-list (app icon) buttons and the left edge of the
    /// tray notify area. Returns false when the taskbar can't be located.
    /// </summary>
    private bool ComputeDockBand(out int leftBound, out int rightBound)
    {
        leftBound = rightBound = 0;
        var hTray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (hTray == IntPtr.Zero || !NativeMethods.GetWindowRect(hTray, out var rTray)
            || (rTray.Right - rTray.Left) <= 0)
            return false;

        var hNotify = NativeMethods.FindWindowExW(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (hNotify != IntPtr.Zero && NativeMethods.GetWindowRect(hNotify, out var rNotify)
            && (rNotify.Right - rNotify.Left) > 0)
            rightBound = rNotify.Left;
        else
            rightBound = rTray.Right - (int)(180 * _dpiScale);

        var hReBar = NativeMethods.FindWindowExW(hTray, IntPtr.Zero, "ReBarWindow32", null);
        var hTaskSw = NativeMethods.FindWindowExW(
            hReBar != IntPtr.Zero ? hReBar : hTray, IntPtr.Zero, "MSTaskSwWClass", null);
        if (hTaskSw == IntPtr.Zero)
            hTaskSw = NativeMethods.FindWindowExW(hTray, IntPtr.Zero, "MSTaskSwWClass", null);
        var hTaskList = hTaskSw != IntPtr.Zero
            ? NativeMethods.FindWindowExW(hTaskSw, IntPtr.Zero, "MSTaskListWClass", null)
            : IntPtr.Zero;
        // Win11 XAML taskbars may lack MSTaskListWClass — MSTaskSwWClass itself
        // is then the taskband holding the app buttons.
        var hBand = hTaskList != IntPtr.Zero ? hTaskList : hTaskSw;
        if (hBand != IntPtr.Zero && NativeMethods.GetWindowRect(hBand, out var rTask)
            && (rTask.Right - rTask.Left) > 0)
            leftBound = rTask.Right;
        else
            leftBound = rTray.Left;

        _bandLeft = leftBound;
        _bandRight = rightBound;
        return true;
    }

    /// <summary>Clamps a proposed pill X inside the taskbar band — never
    /// overlapping the tray icons or task buttons, never leaving the bar.</summary>
    private int ClampDockX(int x)
    {
        if (!ComputeDockBand(out var left, out var right)) return x;
        var pad = (int)(4 * _dpiScale);
        var maxX = Math.Max(left + pad, right - pad - _winW);
        return Math.Clamp(x, left + pad, maxX);
    }

    private void DragMove()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return;
        var dx = pt.X - _dragAnchorCursorX;
        if (!_dragMoved)
        {
            if (Math.Abs(dx) < NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG)
                && Math.Abs(pt.Y - _dragAnchorCursorY) < NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDRAG))
                return; // still inside click threshold
            _dragMoved = true;
        }

        var newX = ClampDockX(_dragStartX + dx);
        var hTray = NativeMethods.FindWindowW("Shell_TrayWnd", null);
        if (hTray != IntPtr.Zero && NativeMethods.GetWindowRect(hTray, out var rTray))
            _winY = rTray.Top + Math.Max(0, (rTray.Bottom - rTray.Top - _winH) / 2); // Y locked inside the bar
        if (newX == _winX) return;
        _winX = newX;
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, _winX, _winY, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private void EndDrag(bool moved)
    {
        _dragging = false;
        _dragMoved = false;
        NativeMethods.ReleaseCapture();
        if (!moved) return;
        if (ComputeDockBand(out _, out var right))
        {
            _dockGapDip = (int)Math.Round((right - (_winX + _winW)) / _dpiScale);
            try
            {
                var s = UserSettings.Load();
                s.HudDockGapDip = _dockGapDip;
                s.Save();
            }
            catch { }
        }
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            return new IntPtr(1); // HTCLIENT: interactive to mouse
        }
        if (msg == NativeMethods.WM_SETCURSOR)
        {
            var cur = OwnerOf(hwnd) is { _dragMoved: true } ? GetSizeCursor() : GetHandCursor();
            if (cur != IntPtr.Zero)
            {
                NativeMethods.SetCursor(cur);
                return new IntPtr(1);
            }
        }
        if (msg == NativeMethods.WM_LBUTTONDOWN)
        {
            var owner = OwnerOf(hwnd);
            if (owner != null && NativeMethods.GetCursorPos(out var pt))
            {
                owner._dragging = true;
                owner._dragMoved = false;
                owner._dragAnchorCursorX = pt.X;
                owner._dragAnchorCursorY = pt.Y;
                owner._dragStartX = owner._winX;
                NativeMethods.SetCapture(hwnd);
            }
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_MOUSEMOVE)
        {
            var owner = OwnerOf(hwnd);
            if (owner is { _dragging: true })
                owner.DragMove();
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_LBUTTONUP)
        {
            var owner = OwnerOf(hwnd);
            if (owner is { _dragging: true })
            {
                var moved = owner._dragMoved;
                owner.EndDrag(moved);
                if (!moved)
                    owner.HandleClick();
            }
            else
            {
                owner?.HandleClick();
            }
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_CAPTURECHANGED)
        {
            var owner = OwnerOf(hwnd);
            if (owner is { _dragging: true })
                owner.EndDrag(owner._dragMoved);
            return IntPtr.Zero;
        }
        if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            var owner = OwnerOf(hwnd);
            if (owner != null && owner._threadId != 0)
                NativeMethods.PostThreadMessageW(owner._threadId, WmAppRebuild, IntPtr.Zero, IntPtr.Zero);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void HandleClick()
    {
        if (OnClick != null)
        {
            try { OnClick.Invoke(); return; }
            catch { }
        }

        try
        {
            var exe = Environment.ProcessPath ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
        }
        catch { }
    }

    private static ActivityHudService? OwnerOf(IntPtr hwnd)
    {
        var p = NativeMethods.GetWindowLongPtr(hwnd, GwlpUserdata);
        if (p != IntPtr.Zero)
        {
            try { if (GCHandle.FromIntPtr(p).Target is ActivityHudService s) return s; }
            catch { }
        }
        return _current;
    }

    // ------------------------------------------------------------ animation tick

    private void OnTick()
    {
        if (!_enabled || _hwnd == IntPtr.Zero)
        {
            Hide();
            _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        var hasSub = _state == HudState.Emergency || !string.IsNullOrEmpty(_pausedSubtitle);
        var targetH = (int)((hasSub ? 46 : 34) * _dpiScale);
        if (_winH != targetH)
        {
            RecalculatePosition();
        }

        var now0 = Environment.TickCount64;
        if (now0 - _lastBandCheckMs >= 400)
        {
            _lastBandCheckMs = now0;
            var oldL = _bandLeft; var oldR = _bandRight;
            // Tray icons/task buttons grow and shrink the band (e.g. tray icons
            // arriving after shell start) — re-dock so the pill never sits on top
            // of the tray or drifts outside the bar.
            if (ComputeDockBand(out var lb, out var rb)
                && (lb != oldL || rb != oldR)
                && (_winX + _winW > rb - (int)(4 * _dpiScale) || _winX < lb + (int)(4 * _dpiScale)))
            {
                RecalculatePosition();
            }
        }

        var now = Environment.TickCount64;
        var dt = _lastTickTime == 0 ? _frameMs : Math.Clamp(now - _lastTickTime, 1, 100);
        _lastTickTime = now;

        var elapsedSinceState = now - _stateStartTime;

        double bobDisplacement = 0;
        double bloomAlpha = 0;
        double overallOpacity = 1.0;
        bool keepTicking = false;

        switch (_state)
        {
            case HudState.Hidden:
                Hide();
                _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                return;

            case HudState.Standby:
                keepTicking = true;
                if (_animationsEnabled)
                {
                    _bobPhaseDeg = (_bobPhaseDeg + dt * (360.0 / 2200.0)) % 360.0;
                }
                bobDisplacement = 0;
                overallOpacity = 0.96;
                break;

            case HudState.Working:
                keepTicking = true;
                if (_animationsEnabled)
                {
                    _bobPhaseDeg = (_bobPhaseDeg + dt * BobDegPerMs) % 360.0;
                }
                bobDisplacement = 0; // Solid on taskbar
                break;

            case HudState.Settling:
                keepTicking = true;
                if (!_animationsEnabled || elapsedSinceState >= SettleDurationMs)
                {
                    bobDisplacement = 0;
                    _lastBobDisplacement = 0;
                    _state = HudState.SuccessBloom;
                    _stateStartTime = now;
                }
                else
                {
                    var progress = (double)elapsedSinceState / SettleDurationMs;
                    // Ease out back to center
                    var ease = 1.0 - Math.Pow(1.0 - progress, 2);
                    bobDisplacement = _lastBobDisplacement * (1.0 - ease);
                }
                break;

            case HudState.SuccessBloom:
                keepTicking = true;
                if (elapsedSinceState < BloomRiseMs)
                {
                    var p = (double)elapsedSinceState / BloomRiseMs;
                    bloomAlpha = Math.Sin(p * Math.PI / 2.0); // 0 -> 1
                }
                else if (elapsedSinceState < BloomRiseMs + BloomFadeMs)
                {
                    var p = (double)(elapsedSinceState - BloomRiseMs) / BloomFadeMs;
                    bloomAlpha = 1.0 - p; // 1 -> 0
                }
                else
                {
                    bloomAlpha = 0;
                    _state = HudState.Exiting;
                    _stateStartTime = now;
                }
                break;

            case HudState.Exiting:
                keepTicking = true;
                if (elapsedSinceState >= DissolveDurationMs)
                {
                    if (_alwaysVisible)
                    {
                        _state = HudState.Standby;
                        _currentText = "Inbrisk Hazır";
                        _currentApp = "Inbrisk";
                        _currentIcon = AppIconExtractor.GetInbriskLogo(24);
                        _stateStartTime = now;
                        bobDisplacement = 0;
                        overallOpacity = 0.95;
                        keepTicking = false;
                    }
                    else
                    {
                        _state = HudState.Hidden;
                        Hide();
                        _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                        return;
                    }
                }
                else
                {
                    var dissolveP = (double)elapsedSinceState / DissolveDurationMs;
                    overallOpacity = Math.Clamp(1.0 - dissolveP, 0.0, 1.0);
                }
                break;

            case HudState.Failure:
                keepTicking = true;
                bloomAlpha = 0.5;
                if (elapsedSinceState > FailureHoldMs)
                {
                    _state = HudState.Exiting;
                    _stateStartTime = now;
                }
                break;

            case HudState.Emergency:
                // Emergency stays visible, static (no bobbing, no exit)
                keepTicking = false;
                bobDisplacement = 0;
                overallOpacity = 1.0;
                break;
        }

        RenderFrame(bobDisplacement, bloomAlpha, overallOpacity);

        if (keepTicking)
        {
            _animTimer?.Change(_frameMs, Timeout.Infinite);
        }
        else
        {
            _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    private void Hide()
    {
        // Clear the backing store too — a stale emergency/working frame may
        // not linger even while the window is invisible (covers the
        // disabled-HUD early return path as well).
        ClearFramebuffer();
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                var dstPoint = new POINT { X = _winX, Y = _winY };
                var dstSize = new NativeMethods.SIZE { Cx = _winW, Cy = _winH };
                var srcPoint = new POINT { X = 0, Y = 0 };
                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = 0x00,
                    BlendFlags = 0,
                    SourceConstantAlpha = 0,
                    AlphaFormat = 0x01
                };
                var screenDc = NativeMethods.GetDC(IntPtr.Zero);
                try
                {
                    NativeMethods.UpdateLayeredWindow(
                        _hwnd, screenDc, ref dstPoint, ref dstSize, _memDc, ref srcPoint, 0, ref blend, NativeMethods.ULW_ALPHA);
                }
                finally
                {
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            catch { }
            NativeMethods.ShowWindow(_hwnd, 0); // SW_HIDE
        }
    }

    private unsafe void ClearFramebuffer()
    {
        if (_bits != null)
            new Span<uint>(_bits, _winW * _winH).Clear();
    }

    /// <summary>Test hook: copy of the current backing framebuffer (BGRA
    /// premultiplied, _winW*_winH pixels). Lets tests prove no stale
    /// emergency frame survives a state transition even after the window
    /// is hidden.</summary>
    public unsafe byte[]? DebugReadPixels()
    {
        if (_bits == null) return null;
        var bytes = new byte[_winW * _winH * 4];
        Marshal.Copy((IntPtr)_bits, bytes, 0, bytes.Length);
        return bytes;
    }

    // ------------------------------------------------------------ render

    private unsafe void RenderFrame(double bobOffsetPx, double bloomAlpha, double overallOpacity)
    {
        if (_hwnd == IntPtr.Zero || _bits == null) return;

        // Clear framebuffer (premultiplied 0)
        new Span<uint>(_bits, _winW * _winH).Clear();

        using (var bmp = new Bitmap(_winW, _winH, _winW * 4,
                   PixelFormat.Format32bppPArgb, (IntPtr)_bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var r = (float)((_winH - 2) / 2.0f);
            var pillRect = new RectangleF(
                1, 1,
                _winW - 2,
                _winH - 2);

            // Capsule Path
            using var path = CreatePillPath(pillRect, r);

            // Background fill: Obsidian Dark Glass
            var baseAlpha = (int)(235 * overallOpacity);
            var bgColor = _state == HudState.Emergency
                ? Color.FromArgb(baseAlpha, 0x24, 0x0C, 0x0C)
                : Color.FromArgb(baseAlpha, 0x11, 0x11, 0x16);

            using (var bgBrush = new SolidBrush(bgColor))
                g.FillPath(bgBrush, path);

            // Internal Bloom (Success Lime or Failure Red)
            if (bloomAlpha > 0.01)
            {
                var bAlpha = Math.Clamp((int)(bloomAlpha * 140 * overallOpacity), 0, 255);
                var bloomColor = (_state == HudState.Failure)
                    ? Color.FromArgb(bAlpha, 0xFF, 0x45, 0x45)
                    : Color.FromArgb(bAlpha, 0x4A, 0xDE, 0x80);

                using var bloomBrush = new SolidBrush(bloomColor);
                g.FillPath(bloomBrush, path);
            }

            // Subtle brand border
            var borderAlpha = (int)(90 * overallOpacity);
            var borderColor = _state == HudState.Emergency
                ? Color.FromArgb((int)(200 * overallOpacity), 0xFF, 0x3B, 0x30)
                : (_state == HudState.Working
                    ? Color.FromArgb((int)(160 * overallOpacity), 0xFF, 0x57, 0x33)
                    : Color.FromArgb(borderAlpha, 0x3A, 0x3A, 0x48));

            using var borderPen = new Pen(borderColor, 1.1f);
                g.DrawPath(borderPen, path);

            // Content: App Icon / Logo + Status Dot + Text
            var iconLeft = 9 * (float)_dpiScale;
            var iconSize = 16 * (float)_dpiScale;
            var iconY = (_winH - iconSize) / 2.0f;
            var textLeft = 14 * (float)_dpiScale;

            if (_state != HudState.Emergency)
            {
                var icon = _currentIcon ?? AppIconExtractor.GetInbriskLogo(24);
                try { g.DrawImage(icon, iconLeft, iconY, iconSize, iconSize); } catch { }

                // Small animated status indicator dot / pulsing ring (Reference B style)
                var dotX = iconLeft + iconSize + (7 * (float)_dpiScale);
                var dotY = _winH / 2.0f;

                if (_state == HudState.Working)
                {
                    var pulseRadius = (float)((3.0 + 1.2 * Math.Sin(_bobPhaseDeg * Math.PI / 180.0)) * _dpiScale);
                    using var ringPen = new Pen(Color.FromArgb((int)(160 * overallOpacity), 0xFF, 0x57, 0x33), 1.2f);
                    using var dotBrush = new SolidBrush(Color.FromArgb((int)(245 * overallOpacity), 0xFF, 0x57, 0x33));
                    g.DrawEllipse(ringPen, dotX - pulseRadius, dotY - pulseRadius, pulseRadius * 2, pulseRadius * 2);
                    g.FillEllipse(dotBrush, dotX - 2.0f, dotY - 2.0f, 4.0f, 4.0f);
                    textLeft = dotX + (9 * (float)_dpiScale);
                }
                else
                {
                    // Standby calm breathing dot
                    var pulse = (float)(0.6 + 0.4 * Math.Sin(_bobPhaseDeg * Math.PI / 180.0));
                    var dotRadius = (float)(2.2f * _dpiScale);
                    var ringRadius = (float)((3.8f + 1.0f * pulse) * _dpiScale);
                    using var ringPen = new Pen(Color.FromArgb((int)(110 * pulse * overallOpacity), 0x22, 0xC5, 0x5E), 1.0f);
                    using var dotBrush = new SolidBrush(Color.FromArgb((int)(230 * pulse * overallOpacity), 0x22, 0xC5, 0x5E));
                    g.DrawEllipse(ringPen, dotX - ringRadius, dotY - ringRadius, ringRadius * 2, ringRadius * 2);
                    g.FillEllipse(dotBrush, dotX - dotRadius, dotY - dotRadius, dotRadius * 2, dotRadius * 2);
                    textLeft = dotX + (9 * (float)_dpiScale);
                }
            }
            else
            {
                textLeft = 12 * (float)_dpiScale;
            }

            // Typography
            var fontScale = (float)(9.0f * _dpiScale);
            using var font = new Font("Segoe UI", fontScale, FontStyle.Regular);
            using var boldFont = new Font("Segoe UI", fontScale, FontStyle.Bold);

            if (_state == HudState.Emergency)
            {
                using var titleBrush = new SolidBrush(Color.FromArgb((int)(255 * overallOpacity), 0xFF, 0x6B, 0x6B));
                var textY = (_winH - boldFont.Height) / 2.0f;
                g.DrawString("■ Inbrisk durduruldu", boldFont, titleBrush, textLeft, textY);
            }
            else if (!string.IsNullOrEmpty(_pausedSubtitle))
            {
                using var titleBrush = new SolidBrush(Color.FromArgb((int)(255 * overallOpacity), 0xFF, 0xC1, 0x07));
                var textY = (_winH - font.Height) / 2.0f;
                g.DrawString(_currentText, boldFont, titleBrush, textLeft, textY);
            }
            else if (_state == HudState.Standby)
            {
                // Reference B: "Inbrisk" in bold, " Hazır" in muted regular + right-side launcher glyph
                using var brandBrush = new SolidBrush(Color.FromArgb((int)(245 * overallOpacity), 0xF5, 0xF5, 0xF7));
                using var subBrush = new SolidBrush(Color.FromArgb((int)(160 * overallOpacity), 0x9E, 0x9E, 0xAB));
                var textY = (_winH - boldFont.Height) / 2.0f;

                var brandText = "Inbrisk";
                var statusText = " Hazır";
                var brandSize = g.MeasureString(brandText, boldFont);
                g.DrawString(brandText, boldFont, brandBrush, textLeft, textY);
                g.DrawString(statusText, font, subBrush, textLeft + brandSize.Width - (4 * (float)_dpiScale), textY);

                // Right-side launcher glyph [ ⚡ ]
                using var glyphBrush = new SolidBrush(Color.FromArgb((int)(150 * overallOpacity), 0xE5, 0x4B, 0x35));
                using var glyphFont = new Font("Segoe UI", fontScale * 0.85f, FontStyle.Regular);
                var glyph = "⚡";
                var glyphSize = g.MeasureString(glyph, glyphFont);
                var glyphX = _winW - glyphSize.Width - (9 * (float)_dpiScale);
                g.DrawString(glyph, glyphFont, glyphBrush, glyphX, textY);
            }
            else
            {
                var textColor = (_state == HudState.SuccessBloom || _currentText.StartsWith("✓"))
                    ? Color.FromArgb((int)(255 * overallOpacity), 0x4A, 0xDE, 0x80)
                    : Color.FromArgb((int)(245 * overallOpacity), 0xFA, 0xFA, 0xFC);

                using var textBrush = new SolidBrush(textColor);
                var textY = (_winH - font.Height) / 2.0f;
                g.DrawString(_currentText, font, textBrush, textLeft, textY);

                if (_state == HudState.Working)
                {
                    using var pauseBrush = new SolidBrush(Color.FromArgb((int)(140 * overallOpacity), 0x9E, 0x9E, 0xAB));
                    using var miniFont = new Font("Segoe UI", fontScale * 0.8f, FontStyle.Regular);
                    var pauseX = _winW - (18 * (float)_dpiScale);
                    g.DrawString("⏸", miniFont, pauseBrush, pauseX, textY);
                }
            }
        }

        // Apply Layered Window with bobbing vertical displacement
        var dstPoint = new POINT
        {
            X = _winX,
            Y = _winY + (int)Math.Round(bobOffsetPx)
        };
        var dstSize = new NativeMethods.SIZE { Cx = _winW, Cy = _winH };
        var srcPoint = new POINT { X = 0, Y = 0 };

        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = 0x00, // AC_SRC_OVER
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = 0x01 // AC_SRC_ALPHA (premultiplied)
        };

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            NativeMethods.UpdateLayeredWindow(
                _hwnd,
                screenDc,
                ref dstPoint,
                ref dstSize,
                _memDc,
                ref srcPoint,
                0,
                ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }

        NativeMethods.ShowWindow(_hwnd, 8); // SW_SHOWNA (show without activating)
    }

    private static GraphicsPath CreatePillPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void Teardown()
    {
        _animTimer?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_hwndHandle.IsAllocated)
            _hwndHandle.Free();

        if (_oldBmp != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldBmp);
        if (_dib != IntPtr.Zero) NativeMethods.DeleteObject(_dib);
        if (_memDc != IntPtr.Zero) NativeMethods.DeleteDC(_memDc);
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
