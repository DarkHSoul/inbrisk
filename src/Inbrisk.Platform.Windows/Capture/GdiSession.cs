using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Capture;

/// <summary>
/// Fallback continuous capture via GDI polling with an adaptive interval:
/// slow sampling when idle (SetActivityLevel(false)), fast when change is
/// expected. Used when WGC is unavailable and for multi-monitor desktop
/// coverage.
/// </summary>
internal sealed class GdiSession : ICaptureSession, IAdaptiveSource
{
    private readonly GdiCapture _capture;
    private readonly IWindowService _windows;
    private readonly Func<RectPx> _regionProvider;
    private readonly Thread _thread;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private volatile bool _running;
    private volatile bool _stop;
    private volatile int _intervalMs = 400; // idle
    private RawFrame? _latest;
    private long _seq, _emitted;
    private double _fpsEma;
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private bool _disposed;

    public CaptureTarget Target { get; }
    public CaptureBackendKind Backend => CaptureBackendKind.Gdi;
    public bool IsRunning => _running;
    public RawFrame? LatestFrame => _latest;
    public event Action<RawFrame>? FrameReceived;

    public GdiSession(CaptureTarget target, Func<RectPx> regionProvider,
        GdiCapture capture, IWindowService windows)
    {
        Target = target;
        _regionProvider = regionProvider;
        _capture = capture;
        _windows = windows;
        _thread = new Thread(PollLoop) { IsBackground = true, Name = "inbrisk-gdi-capture" };
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _running = false;
        _thread.Interrupt();
    }

    /// <summary>Adaptive hint: change in progress → sample fast.</summary>
    public void SetBusy(bool busy) =>
        _intervalMs = busy ? 60 : 400;

    private void PollLoop()
    {
        while (!_stop)
        {
            try
            {
                // window target vanished → end the session like WGC's item.Closed
                if (Target is CaptureTarget.Window wt &&
                    !Native.NativeMethods.IsWindow(new IntPtr(wt.Hwnd)))
                {
                    _running = false;
                    break;
                }
                var rect = _regionProvider();
                if (rect.IsEmpty || !_running) { Thread.Sleep(_intervalMs); continue; }
                var raw = _capture.CaptureRaw(rect);
                var frame = raw with { Sequence = Interlocked.Increment(ref _seq) - 1,
                    Transform = Target is CaptureTarget.Window wt2
                        ? raw.Transform with { SourceHwnd = wt2.Hwnd }
                        : raw.Transform };
                _latest = frame;
                _emitted++;
                var now = DateTimeOffset.Now;
                var dt = (now - _lastEmit).TotalSeconds;
                if (_lastEmit != DateTimeOffset.MinValue && dt > 0.001)
                    _fpsEma = _fpsEma <= 0 ? 1.0 / dt : _fpsEma * 0.8 + 0.2 / dt;
                _lastEmit = now;
                FrameReceived?.Invoke(frame);
            }
            catch (ThreadInterruptedException) { }
            catch (Exception) { /* transient capture failure — keep polling */ }

            try { Thread.Sleep(_intervalMs); }
            catch (ThreadInterruptedException) { }
        }
    }

    public CaptureMetrics Metrics => new(
        Backend, _fpsEma, _fpsEma, _emitted, 0,
        _latest?.Width ?? 0, _latest?.Height ?? 0, _startedAt);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _thread.Join(1000);
    }
}
