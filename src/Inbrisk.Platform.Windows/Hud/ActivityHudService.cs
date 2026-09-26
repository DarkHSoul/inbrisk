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

    public ActivityHudService(bool enabled = true, bool animationsEnabled = true, bool alwaysVisible = false)
    {
        _enabled = enabled;
        _animationsEnabled = animationsEnabled;
        _alwaysVisible = alwaysVisible;
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
                _currentApp = null;
                _currentIcon = null;
            }
            else
            {
                _emergency = false;
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

            if (_alwaysVisible && _enabled)
            {
                SetStandby("Inbrisk Hazır");
            }
        }
        finally
        {
            _ready.Set();
        }

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.Message == WmAppTick) OnTick();
            else if (msg.Message == WmAppRebuild) RecalculatePosition();
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
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
                HCursor = IntPtr.Zero,
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
                             NativeMethods.WS_EX_TRANSPARENT |
                             NativeMethods.WS_EX_TOOLWINDOW |
                             NativeMethods.WS_EX_NOACTIVATE;

        _winW = 340;
        _winH = 46;
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

    private void RecalculatePosition()
    {
        try
        {
            var monitors = new WindowService().GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (primary != null)
            {
                _dpiScale = primary.DpiX / 96.0;
                var hasSubtitle = _state == HudState.Emergency || !string.IsNullOrEmpty(_pausedSubtitle);
                var baseW = hasSubtitle ? 380 : 320;
                var baseH = hasSubtitle ? 54 : 42;

                _winW = (int)(baseW * _dpiScale);
                _winH = (int)(baseH * _dpiScale);
                _winX = primary.Bounds.X + (primary.Bounds.Width - _winW) / 2;
                _winY = primary.Bounds.Y + (int)(16 * _dpiScale);

                AllocateFramebuffer(_winW, _winH);
                return;
            }
        }
        catch { }

        // Fallback using SystemMetrics
        var screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var hasSubFallback = _state == HudState.Emergency || !string.IsNullOrEmpty(_pausedSubtitle);
        var baseWFallback = hasSubFallback ? 380 : 320;
        var baseHFallback = hasSubFallback ? 54 : 42;
        _winW = baseWFallback;
        _winH = baseHFallback;
        _winX = (screenW - _winW) / 2;
        _winY = 16;
        AllocateFramebuffer(_winW, _winH);
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            // Click-through: always transparent to mouse
            return new IntPtr(NativeMethods.HTTRANSPARENT);
        }
        if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            var owner = OwnerOf(hwnd);
            if (owner != null && owner._threadId != 0)
                NativeMethods.PostThreadMessageW(owner._threadId, WmAppRebuild, IntPtr.Zero, IntPtr.Zero);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
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
        var targetH = (int)((hasSub ? 54 : 42) * _dpiScale);
        if (_winH != targetH)
        {
            RecalculatePosition();
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
                keepTicking = false;
                bobDisplacement = 0;
                overallOpacity = 0.95;
                break;

            case HudState.Working:
                keepTicking = true;
                if (_animationsEnabled)
                {
                    _bobPhaseDeg = (_bobPhaseDeg + dt * BobDegPerMs) % 360.0;
                    bobDisplacement = Math.Sin(_bobPhaseDeg * Math.PI / 180.0) * BobAmpPx;
                    _lastBobDisplacement = bobDisplacement;
                }
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

            var r = (float)(18 * _dpiScale);
            var pillRect = new RectangleF(
                4, 4,
                _winW - 8,
                _winH - 8);

            // Capsule Path
            using var path = CreatePillPath(pillRect, r);

            // Background fill
            var baseAlpha = (int)(215 * overallOpacity);
            var bgColor = _state == HudState.Emergency
                ? Color.FromArgb(baseAlpha, 0x3A, 0x14, 0x14)
                : Color.FromArgb(baseAlpha, 0x1B, 0x1B, 0x1F);

            using (var bgBrush = new SolidBrush(bgColor))
                g.FillPath(bgBrush, path);

            // Internal Bloom (Success Lime or Failure Red)
            if (bloomAlpha > 0.01)
            {
                var bAlpha = Math.Clamp((int)(bloomAlpha * 140 * overallOpacity), 0, 255);
                var bloomColor = (_state == HudState.Failure)
                    ? Color.FromArgb(bAlpha, 0xFF, 0x45, 0x45)
                    : Color.FromArgb(bAlpha, 0xB7, 0xFF, 0x3C);

                using var bloomBrush = new SolidBrush(bloomColor);
                g.FillPath(bloomBrush, path);
            }

            // Border
            var borderAlpha = (int)(45 * overallOpacity);
            var borderColor = _state == HudState.Emergency
                ? Color.FromArgb(borderAlpha * 2, 0xFF, 0x45, 0x45)
                : Color.FromArgb(borderAlpha, 0xFF, 0xFF, 0xFF);

            using (var borderPen = new Pen(borderColor, 1.2f))
                g.DrawPath(borderPen, path);

            // Content: App Icon / Logo + Text
            var iconLeft = 14 * (float)_dpiScale;
            var textLeft = 14 * (float)_dpiScale;

            if (_state != HudState.Emergency)
            {
                var icon = _currentIcon ?? AppIconExtractor.GetInbriskLogo(24);
                var iconSize = 22 * (float)_dpiScale;
                var iconY = (_winH - iconSize) / 2.0f;
                try { g.DrawImage(icon, iconLeft, iconY, iconSize, iconSize); } catch { }
                // Extra spacing as requested ("onun başına biraz boşlukla bu dursun")
                textLeft = iconLeft + iconSize + (12 * (float)_dpiScale);
            }

            // Typography
            var fontScale = (float)(9.5f * _dpiScale);
            using var font = new Font("Segoe UI", fontScale, FontStyle.Regular);
            using var boldFont = new Font("Segoe UI", fontScale, FontStyle.Bold);

            if (_state == HudState.Emergency)
            {
                var titleBrush = new SolidBrush(Color.FromArgb((int)(255 * overallOpacity), 0xFF, 0x6B, 0x6B));
                var subBrush = new SolidBrush(Color.FromArgb((int)(190 * overallOpacity), 0xDD, 0xDD, 0xDD));
                using var subFont = new Font("Segoe UI", fontScale * 0.85f, FontStyle.Regular);

                g.DrawString("■ Inbrisk durduruldu", boldFont, titleBrush, textLeft, 8 * (float)_dpiScale);
                g.DrawString("Ctrl+Alt+Shift+Pause — Resume", subFont, subBrush, textLeft, 26 * (float)_dpiScale);
                titleBrush.Dispose();
                subBrush.Dispose();
            }
            else if (!string.IsNullOrEmpty(_pausedSubtitle))
            {
                var titleBrush = new SolidBrush(Color.FromArgb((int)(255 * overallOpacity), 0xFF, 0xC1, 0x07)); // Amber
                var subBrush = new SolidBrush(Color.FromArgb((int)(200 * overallOpacity), 0xEE, 0xEE, 0xEE));
                using var subFont = new Font("Segoe UI", fontScale * 0.85f, FontStyle.Regular);

                g.DrawString(_currentText, boldFont, titleBrush, textLeft, 8 * (float)_dpiScale);
                g.DrawString(_pausedSubtitle, subFont, subBrush, textLeft, 26 * (float)_dpiScale);
                titleBrush.Dispose();
                subBrush.Dispose();
            }
            else
            {
                var textColor = (_state == HudState.SuccessBloom || _currentText.StartsWith("✓"))
                    ? Color.FromArgb((int)(255 * overallOpacity), 0xE8, 0xFF, 0xC2)
                    : Color.FromArgb((int)(240 * overallOpacity), 0xFA, 0xFA, 0xFA);

                using var textBrush = new SolidBrush(textColor);
                var textY = (_winH - font.Height) / 2.0f;
                g.DrawString(_currentText, font, textBrush, textLeft, textY);
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
