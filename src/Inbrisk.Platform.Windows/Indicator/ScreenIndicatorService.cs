using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Indicator;

/// <summary>Which monitor edge a strip window covers.</summary>
public enum StripEdge { Top, Right, Bottom, Left }

/// <summary>
/// Indicator colors: a base RGB per state plus the "hot" RGB blended into
/// the densest ~3px of the glow (a tint shift, never a stripe). Configurable
/// via %LOCALAPPDATA%\inbrisk\settings.json (idleColor/emergencyColor);
/// picked up by each new process.
/// </summary>
public sealed record GlowPalette(
    byte R, byte G, byte B, byte HiR, byte HiG, byte HiB,
    byte EmR, byte EmG, byte EmB, byte EmHiR, byte EmHiG, byte EmHiB)
{
    /// <summary>Default palette: lime #B7FF3C/#D7FF78, emergency #FF3B3B/#FF6B6B.</summary>
    public static GlowPalette Default { get; } = new(
        0xB7, 0xFF, 0x3C, 0xD7, 0xFF, 0x78,
        0xFF, 0x3B, 0x3B, 0xFF, 0x6B, 0x6B);

    /// <summary>Build a palette from base colors — the dense-pixel highlight
    /// is derived by lerping the base toward white (~35%), matching how the
    /// defaults relate.</summary>
    public static GlowPalette FromColors(
        (byte R, byte G, byte B) normal, (byte R, byte G, byte B) emergency)
    {
        static byte Hi(byte c) => (byte)(c + (255 - c) * 0.35);
        return new GlowPalette(
            normal.R, normal.G, normal.B, Hi(normal.R), Hi(normal.G), Hi(normal.B),
            emergency.R, emergency.G, emergency.B,
            Hi(emergency.R), Hi(emergency.G), Hi(emergency.B));
    }
}

/// <summary>
/// Pure glow-geometry math — adaptive, resolution/DPI-agnostic. There is no
/// core line: the profile is a single gaussian that peaks at the physical
/// screen edge and feathers inward.
///   sigma — gaussian falloff in px, ~4–9 (≈0.4% of the monitor's short
///           edge) so the haze reads ~10–16 physical px on any panel.
///   strip — window thickness ≈ where the gaussian is effectively zero,
///           with headroom for the Active radius swell so the tail never
///           clips visibly at the strip boundary.
/// </summary>
public static class GlowMetrics
{
    public static (double Sigma, int Strip) MetricsFor(int monW, int monH)
    {
        var shortEdge = Math.Min(monW, monH);
        var sigma = Math.Clamp(shortEdge / 240.0, 4.0, 9.0);
        return (sigma, (int)Math.Ceiling(sigma * 3.8));
    }

    /// <summary>Breathing wave: smooth 0→1→0 over a 360° phase — cosine
    /// ease in both directions, never a mechanical blink.</summary>
    public static double BreathSample(double phaseDeg)
        => (1.0 - Math.Cos(phaseDeg * Math.PI / 180.0)) / 2.0;
}

/// <summary>
/// Per-monitor perimeter glow showing Inbrisk's control state — pure
/// gaussian light haze, NO visible line/stroke at any point:
///   ConnectedIdle    → static subtle electric-lime (#B7FF3C) ambient glow
///   Active           → the same glow breathing (opacity/intensity only —
///                      geometry never jumps)
///   EmergencyStopped → static red glow (#FF3B3B), animation hard-stopped
///   Disconnected     → no overlay
///
/// Implementation: 4 thin layered strip windows per monitor rendered through
/// UpdateLayeredWindow with a premultiplied 32bpp DIB — per-pixel alpha is
/// what makes the feathered glow possible (color-key transparency cannot).
/// The gaussian peaks at the monitor's physical edge and feathers ~10–14px
/// inward. Strips are click-through, no-activate, tool windows, topmost,
/// and excluded from capture via WDA_EXCLUDEFROMCAPTURE.
///
/// Animation model: rendered values (core/glow alpha, radius scale) chase a
/// moving target each tick — Active's target is the breathing wave itself,
/// Idle's is a static endpoint. Transitions therefore never reset or jump:
/// Active→Idle settles from whatever the wave was doing, and activity
/// resuming mid-settle re-joins the wave from the current visual state.
/// All window work happens on a dedicated STA pump thread.
/// </summary>
public sealed class ScreenIndicatorService : IDisposable
{
    private const uint WmAppState = NativeMethods.WM_APP + 1;
    private const uint WmAppRebuild = NativeMethods.WM_APP + 2;
    private const uint WmAppTick = NativeMethods.WM_APP + 3;
    private const int GwlpUserdata = -21;

    // breathing cadence — one full cycle ≈ 2s (180°/s)
    private const double BreathDegPerMs = 180.0 / 1000.0;
    // chase rates per tick: fast ramp into Active (~250ms feel),
    // slow settle into Idle (~600ms feel), fast fade-out on disconnect
    // (~250ms to transparent — no lingering haze once the client is gone)
    private const double ChaseUp = 0.30, ChaseDown = 0.10, FadeOut = 0.40;
    private const double SettleEps = 0.004;

    // idle endpoints — translucent smoke, never a line: edge intensity
    // stays near 0.15 and the gaussian feathers ~13px inward
    private const double IdleIntA = 0.15, IdleRadK = 1.0;
    // breathing endpoints (low → high): real opacity fade 0.14 ↔ 0.34 —
    // the trough never reaches 0, so it reads as smoke thickening and
    // thinning, never ON/OFF blinking. Radius breathes subtly beside it.
    private const double ActIntLo = 0.14, ActIntHi = 0.34;
    private const double ActRadLo = 1.00, ActRadHi = 1.30;
    // emergency: static translucent red smoke, stronger than idle so the
    // state reads instantly — still no line
    private const double EmIntA = 0.30;

    private static readonly IntPtr HwndMessage = new(-3);

    private volatile bool _enabled;
    private readonly int _topologyPollMs;
    private readonly int _animFrameMs;
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private uint _threadId;
    private volatile IndicatorState _state = IndicatorState.Disconnected;

    // pump-thread owned
    private IntPtr _msgWindow;
    private readonly List<Strip> _strips = new();
    private readonly List<GCHandle> _stripHandles = new();
    private List<Monitor> _monitors = new();
    private System.Threading.Timer? _animTimer;
    private System.Threading.Timer? _topologyTimer;
    private long _lastTick;
    private static NativeMethods.WndProc? _proc; // keep delegate alive for the class
    private static ushort _classAtom;
    private static readonly object ClassGate = new();

    // rendered animation state (pump thread only) — chases the target each
    // tick so transitions are continuous by construction
    private double _phaseDeg;
    private double _intA = IdleIntA, _radK = IdleRadK;
    private bool _dirty = true;
    private bool _emergencyRendered; // palette of the last painted frame —
    // a fade-out must keep the color the user actually saw (red stays red)

    private sealed record Monitor(RectPx Bounds, int Dpi);

    private sealed unsafe class Strip
    {
        public required ScreenIndicatorService Owner;
        public IntPtr Hwnd;
        public StripEdge Edge;
        public int X, Y, W, H;   // strip window rect (screen coords)
        public double Sigma;     // this monitor's adaptive gaussian falloff
        public IntPtr MemDc, Dib, OldBmp;
        public uint* Bits;       // premultiplied BGRA pixels
        public bool Horizontal => Edge is StripEdge.Top or StripEdge.Bottom;
    }

    /// <summary>Breathing phase in degrees — advances while Active; tests use
    /// it to verify the animation is alive and that emergency halts it.</summary>
    public double CurrentPhase => _phaseDeg;

    /// <summary>Currently rendered edge intensity (0..1) — tests assert
    /// smooth settle: Active→Idle must decay continuously, never snap.</summary>
    internal double RenderedGlowAlpha => _intA;

    /// <summary>Copy one strip's premultiplied BGRA framebuffer — tests
    /// assert the glow profile (bright core at the edge, alpha decaying
    /// inward) deterministically instead of eyeballing a screenshot.</summary>
    internal unsafe byte[]? DebugReadStripPixels(int index)
    {
        Strip? s;
        lock (_strips) s = index < _strips.Count ? _strips[index] : null;
        if (s == null || s.Bits == null) return null;
        var buf = new byte[s.W * s.H * 4];
        Marshal.Copy((IntPtr)s.Bits, buf, 0, buf.Length);
        return buf;
    }

    /// <summary>Strip window size — tests pair it with DebugReadStripPixels.</summary>
    internal (int W, int H)? DebugStripSize(int index)
    {
        lock (_strips)
            return index < _strips.Count ? (_strips[index].W, _strips[index].H) : null;
    }
    public IndicatorState State => _state;
    public bool CaptureExclusionSupported { get; private set; } = true;
    public bool Enabled => _enabled;
    public int StripCount { get { lock (_strips) return _strips.Count; } }
    /// <summary>Perimeter wall visibility condition: strictly active work or emergency stop with live strips.</summary>
    public bool PerimeterVisible { get { lock (_strips) return _strips.Count > 0 && (_state is IndicatorState.Active or IndicatorState.EmergencyStopped); } }

    private readonly GlowPalette _palette;

    public ScreenIndicatorService(bool enabled = true,
        int topologyPollMs = 2000, int animFrameMs = 33,
        GlowPalette? palette = null)
    {
        _enabled = enabled;
        _topologyPollMs = topologyPollMs;
        _animFrameMs = animFrameMs;
        _palette = palette ?? GlowPalette.Default;
    }

    public void Start()
    {
        if (!_enabled || _thread != null) return;
        _thread = new Thread(Pump) { IsBackground = true, Name = "InbriskScreenIndicator" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Drive the visual state — callable from any thread.</summary>
    public void SetState(IndicatorState state)
    {
        _state = state;
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, WmAppState, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Enable or disable perimeter indicator rendering.</summary>
    public void SetEnabled(bool enabled)
    {
        var was = _enabled;
        _enabled = enabled;
        if (!enabled)
            SetState(IndicatorState.Disconnected);
        else if (!was && _thread == null)
            Start();
    }

    /// <summary>Blocks until the pump finished the first topology build —
    /// tests wait on this before asserting window geometry.</summary>
    public bool WaitForReady(int ms = 5000) => _ready.Wait(ms);

    /// <summary>Snapshot of current strip windows (tests).</summary>
    public IReadOnlyList<IntPtr> StripHwnds
    {
        get { lock (_strips) return _strips.Select(s => s.Hwnd).ToArray(); }
    }

    /// <summary>Force a topology rebuild (tests simulate layout changes).</summary>
    public void RefreshTopology()
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, WmAppRebuild, IntPtr.Zero, IntPtr.Zero);
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
            System.Diagnostics.Debug.WriteLine($"inbrisk-indicator pump exited: {e}");
        }
        finally
        {
            _ready.Set();
        }
    }

    private void PumpCore()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // DPI awareness is a per-THREAD context — the pump must be PMv2 itself
        // or EnumDisplayMonitors/window placement silently use virtualized
        // (logical) coordinates on scaled monitors. Dpi.EnsurePerMonitorV2's
        // once-per-process flag is not sufficient here.
        NativeMethods.SetProcessDpiAwarenessContext(
            NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            RegisterIndicatorClass();
            _msgWindow = NativeMethods.CreateWindowExW(0, "InbriskIndicatorStrip",
                null, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero,
                NativeMethods.GetModuleHandleW(null), IntPtr.Zero);

            _animTimer = new System.Threading.Timer(_ =>
            {
                if (_threadId != 0)
                    NativeMethods.PostThreadMessageW(_threadId, WmAppTick, IntPtr.Zero, IntPtr.Zero);
            }, null, Timeout.Infinite, Timeout.Infinite);
            _topologyTimer = new System.Threading.Timer(_ =>
            {
                if (_threadId != 0)
                    NativeMethods.PostThreadMessageW(_threadId, WmAppRebuild, IntPtr.Zero, IntPtr.Zero);
            }, null, _topologyPollMs, _topologyPollMs);

            ApplyState(); // builds strips if already connected
        }
        catch (Exception e)
        {
            // Init fault: indicator degrades to no-window; the pump still
            // services the queue so Dispose() can tear down cleanly.
            System.Diagnostics.Debug.WriteLine($"inbrisk-indicator init fault: {e}");
        }
        finally { _ready.Set(); }

        while (NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                if (msg.Message == WmAppState) ApplyState();
                else if (msg.Message == WmAppRebuild) Rebuild(force: false);
                else if (msg.Message == WmAppTick) Tick();
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
            catch (Exception e)
            {
                // Per-iteration containment: a transient fault must not take
                // the whole pump (or process) down.
                System.Diagnostics.Debug.WriteLine($"inbrisk-indicator pump fault: {e}");
            }
        }
        Teardown();
    }

    /// <summary>One animation tick: advance the breathing wave while Active,
    /// chase the current target, repaint only when the visual changed. When
    /// idle has fully settled the timer stops — zero steady-state CPU.</summary>
    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = Math.Clamp(now - _lastTick, 1, 100);
        _lastTick = now;

        bool moving;
        if (_state == IndicatorState.Active)
        {
            _phaseDeg += dt * BreathDegPerMs;
            var v = GlowMetrics.BreathSample(_phaseDeg);
            moving = Chase(
                ActIntLo + (ActIntHi - ActIntLo) * v,
                ActRadLo + (ActRadHi - ActRadLo) * v,
                ChaseUp);
        }
        else if (_state is IndicatorState.ConnectedIdle or IndicatorState.Disconnected)
        {
            // ConnectedIdle & Disconnected: NO ACTIVE WORK = NO PERIMETER WALL.
            // Decay rendered alpha to zero and destroy strips.
            moving = Chase(0.0, _radK, FadeOut);
            if (_dirty)
            {
                RenderFade();
                _dirty = false;
            }
            if (!moving)
            {
                DestroyStrips();
                _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            return;
        }
        else return; // EmergencyStopped never ticks

        if (_dirty)
        {
            RenderAll(emergency: false);
            _dirty = false;
        }
    }

    /// <summary>Step the rendered values toward the target. Returns false
    /// once converged — callers use that to park the tick timer.</summary>
    private bool Chase(double intA, double radK, double k)
    {
        var di = Math.Abs(intA - _intA);
        var dr = Math.Abs(radK - _radK);
        if (Math.Max(di, dr) < SettleEps)
        {
            _intA = intA; _radK = radK;
            return false;
        }
        _intA += (intA - _intA) * k;
        _radK += (radK - _radK) * k;
        _dirty = true;
        return true;
    }

    /// <summary>Runs on the pump thread — reconciles windows + visuals with
    /// the desired state.</summary>
    private void ApplyState()
    {
        if (_state is IndicatorState.Disconnected or IndicatorState.ConnectedIdle)
        {
            if (_strips.Count == 0)
            {
                _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }
            // short clean fade-out (~250-500ms): keep the palette the user is
            // seeing (emergency red fades as red, lime as lime) and start
            // the chase from the actually-rendered intensity
            if (_emergencyRendered) _intA = EmIntA;
            _dirty = true;
            _animTimer?.Change(0, _animFrameMs);
            return;
        }
        if (_strips.Count == 0) Rebuild(force: true);

        if (_state == IndicatorState.EmergencyStopped)
        {
            // safety outranks aesthetics: terminate the wave immediately,
            // render the static emergency frame, never wait for a settle
            _animTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            RenderAll(emergency: true);
            return;
        }

        // Active renders through the chase model — the tick loop wakes (or keeps running)
        _lastTick = Environment.TickCount64;
        _dirty = true;
        _animTimer?.Change(0, _animFrameMs);
    }

    private void Rebuild(bool force)
    {
        var monitors = new WindowService().GetMonitors()
            .Select(m => new Monitor(m.Bounds, m.DpiX)).ToList();
        if (!force && SameTopology(monitors)) return;
        _monitors = monitors;
        DestroyStrips();
        if (_state is IndicatorState.Disconnected or IndicatorState.ConnectedIdle) return;

        foreach (var m in _monitors)
        {
            var (sigma, t) =
                GlowMetrics.MetricsFor(m.Bounds.Width, m.Bounds.Height);
            var b = m.Bounds;
            CreateStrip(StripEdge.Top, b.X, b.Y, b.Width, t, sigma);
            // vertical strips are inset by t at both ends — the corner
            // squares are already covered by the horizontal strips, and
            // overlapping two layered windows would double-blend the
            // alpha into visibly brighter corners
            CreateStrip(StripEdge.Right, b.X + b.Width - t, b.Y + t, t, b.Height - 2 * t, sigma);
            CreateStrip(StripEdge.Bottom, b.X, b.Y + b.Height - t, b.Width, t, sigma);
            CreateStrip(StripEdge.Left, b.X, b.Y + t, t, b.Height - 2 * t, sigma);
        }
        _dirty = true;
        RenderAll(_state == IndicatorState.EmergencyStopped);
    }

    private bool SameTopology(List<Monitor> next)
    {
        if (next.Count != _monitors.Count) return false;
        for (var i = 0; i < next.Count; i++)
            if (next[i].Bounds != _monitors[i].Bounds || next[i].Dpi != _monitors[i].Dpi)
                return false;
        return true;
    }

    private unsafe void CreateStrip(StripEdge edge, int x, int y, int w, int h,
        double sigma)
    {
        const uint ex = NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_LAYERED |
            NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW |
            NativeMethods.WS_EX_NOACTIVATE;
        var strip = new Strip { Owner = this, Edge = edge, X = x, Y = y,
            W = w, H = h, Sigma = sigma };
        var handle = GCHandle.Alloc(strip);
        var hwnd = NativeMethods.CreateWindowExW(ex, "InbriskIndicatorStrip",
            null, NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null), GCHandle.ToIntPtr(handle));
        if (hwnd == IntPtr.Zero) { handle.Free(); return; }
        strip.Hwnd = hwnd;
        _stripHandles.Add(handle);

        // 32bpp premultiplied DIB — the strip's whole framebuffer
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
        strip.Dib = NativeMethods.CreateDIBSection(screenDc, ref bi,
            NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        if (strip.Dib == IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(hwnd);
            handle.Free();
            _stripHandles.Remove(handle);
            return;
        }
        strip.Bits = (uint*)bits;
        strip.MemDc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        strip.OldBmp = NativeMethods.SelectObject(strip.MemDc, strip.Dib);

        // never let Inbrisk's own captures see the indicator — the model must
        // not mistake its activity glow for UI content
        if (!NativeMethods.SetWindowDisplayAffinity(hwnd,
                NativeMethods.WDA_EXCLUDEFROMCAPTURE))
            CaptureExclusionSupported = false;

        lock (_strips) _strips.Add(strip);
    }

    private void DestroyStrips()
    {
        Strip[] snapshot;
        lock (_strips) { snapshot = _strips.ToArray(); _strips.Clear(); }
        foreach (var s in snapshot)
        {
            NativeMethods.DestroyWindow(s.Hwnd);
            if (s.OldBmp != IntPtr.Zero)
                NativeMethods.SelectObject(s.MemDc, s.OldBmp);
            if (s.Dib != IntPtr.Zero) NativeMethods.DeleteObject(s.Dib);
            if (s.MemDc != IntPtr.Zero) NativeMethods.DeleteDC(s.MemDc);
        }
        foreach (var h in _stripHandles) h.Free();
        _stripHandles.Clear();
    }

    private void Teardown()
    {
        _animTimer?.Dispose();
        _topologyTimer?.Dispose();
        DestroyStrips();
        if (_msgWindow != IntPtr.Zero) NativeMethods.DestroyWindow(_msgWindow);
    }

    // ------------------------------------------------------------ render

    private void RenderAll(bool emergency)
    {
        _emergencyRendered = emergency;
        Strip[] snapshot;
        lock (_strips) snapshot = _strips.ToArray();
        var intA = emergency ? EmIntA : _intA;
        var radK = emergency ? 1.0 : _radK;
        foreach (var s in snapshot) Render(s, intA, radK, emergency);
    }

    /// <summary>Disconnect fade frame — same palette as the last painted
    /// frame, alpha driven by the decaying _intA.</summary>
    private void RenderFade()
    {
        Strip[] snapshot;
        lock (_strips) snapshot = _strips.ToArray();
        foreach (var s in snapshot) Render(s, _intA, _radK, _emergencyRendered);
    }

    /// <summary>Paint one strip's DIB and push it to the layered window.
    /// Pure gaussian: alpha peaks at the physical screen edge (d=0) and
    /// feathers inward — there is deliberately no core plateau, so no
    /// visible line can form. One pass per animated frame.</summary>
    private unsafe void Render(Strip s, double intA,
        double radK, bool emergency)
    {
        var t = Math.Min(s.Horizontal ? s.H : s.W, 64);
        var sigma = Math.Max(2.0, s.Sigma * radK);

        // per-distance pixel (premultiplied BGRA), d = px inward from edge
        Span<uint> px = stackalloc uint[64];
        for (var d = 0; d < t; d++)
        {
            var g = d / sigma;
            var a = Math.Clamp(intA * Math.Exp(-0.5 * g * g), 0, 1);

            // highlight only inside the glow's densest ~3px — a tint shift,
            // not a stripe
            var mix = Math.Exp(-0.5 * (d / 2.2) * (d / 2.2));
            byte r, gg, b;
            if (emergency)
            {
                r = (byte)(_palette.EmR + (_palette.EmHiR - _palette.EmR) * mix);
                gg = (byte)(_palette.EmG + (_palette.EmHiG - _palette.EmG) * mix);
                b = (byte)(_palette.EmB + (_palette.EmHiB - _palette.EmB) * mix);
            }
            else
            {
                r = (byte)(_palette.R + (_palette.HiR - _palette.R) * mix);
                gg = (byte)(_palette.G + (_palette.HiG - _palette.G) * mix);
                b = (byte)(_palette.B + (_palette.HiB - _palette.B) * mix);
            }
            // little-endian uint → memory bytes B,G,R,A (DIB byte order)
            var ab = (byte)(a * 255);
            px[d] = ((uint)ab << 24)
                | ((uint)(r * ab / 255) << 16)
                | ((uint)(gg * ab / 255) << 8)
                | (uint)(b * ab / 255);
        }

        var len = s.Horizontal ? s.W : s.H;
        if (s.Horizontal)
        {
            // row d holds one colour; d=0 is the topmost row for Top,
            // the bottom row for Bottom
            for (var row = 0; row < s.H; row++)
            {
                var d = s.Edge == StripEdge.Top ? row : s.H - 1 - row;
                var p = s.Bits + (long)row * s.W;
                var v = px[Math.Min(d, t - 1)];
                for (var x = 0; x < len; x++) p[x] = v;
            }
        }
        else
        {
            // column d holds one colour; d=0 is leftmost for Left,
            // rightmost for Right — build one row then replicate
            var w = Math.Min(s.W, 64);
            Span<uint> rowBuf = stackalloc uint[64];
            for (var col = 0; col < w; col++)
            {
                var d = s.Edge == StripEdge.Left ? col : s.W - 1 - col;
                rowBuf[col] = px[Math.Min(d, t - 1)];
            }
            for (var row = 0; row < s.H; row++)
            {
                var p = s.Bits + (long)row * s.W;
                for (var col = 0; col < w; col++) p[col] = rowBuf[col];
            }
        }
        Push(s);
    }

    private static void Push(Strip s)
    {
        var dst = new POINT { X = s.X, Y = s.Y };
        var src = new POINT { X = 0, Y = 0 };
        var size = new NativeMethods.SIZE { Cx = s.W, Cy = s.H };
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            NativeMethods.UpdateLayeredWindow(s.Hwnd, screenDc, ref dst,
                ref size, s.MemDc, ref src, 0, ref blend,
                NativeMethods.ULW_ALPHA);
        }
        finally { NativeMethods.ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // ------------------------------------------------------------ wndproc

    private static void RegisterIndicatorClass()
    {
        lock (ClassGate)
        {
            if (_classAtom != 0) return;
            _proc = IndicatorWndProc;
            var wc = new NativeMethods.WNDCLASSEXW
            {
                CbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEXW>(),
                LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                HInstance = NativeMethods.GetModuleHandleW(null),
                LpszClassName = "InbriskIndicatorStrip",
            };
            _classAtom = NativeMethods.RegisterClassExW(ref wc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCTW
    {
        public IntPtr LpCreateParams;
        public IntPtr HInstance, HMenu, HwndParent;
        public int Cy, Cx, Y, X;
        public int Style;
        public IntPtr LpszName, LpszClass;
        public uint DwExStyle;
    }

    private const uint WmNccreate = 0x0081;

    private static IntPtr IndicatorWndProc(IntPtr hwnd, uint msg,
        IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmNccreate)
        {
            var cs = Marshal.PtrToStructure<CREATESTRUCTW>(lParam);
            NativeMethods.SetWindowLongPtr(hwnd, GwlpUserdata, cs.LpCreateParams);
        }
        else if (msg == NativeMethods.WM_PAINT)
        {
            // layered windows render via UpdateLayeredWindow, not WM_PAINT —
            // just validate the region
            var ps = new NativeMethods.PAINTSTRUCT();
            NativeMethods.BeginPaint(hwnd, out ps);
            NativeMethods.EndPaint(hwnd, ref ps);
            return IntPtr.Zero;
        }
        else if (msg == NativeMethods.WM_DISPLAYCHANGE)
        {
            var owner = StripOf(hwnd)?.Owner;
            if (owner != null && owner._threadId != 0)
                NativeMethods.PostThreadMessageW(owner._threadId, WmAppRebuild,
                    IntPtr.Zero, IntPtr.Zero);
        }
        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static Strip? StripOf(IntPtr hwnd)
    {
        var p = NativeMethods.GetWindowLongPtr(hwnd, GwlpUserdata);
        if (p == IntPtr.Zero) return null;
        try { return GCHandle.FromIntPtr(p).Target as Strip; }
        catch (InvalidOperationException) { return null; } // freed during teardown
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT,
                IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(3000);
            _threadId = 0;
        }
        _ready.Dispose();
    }
}
