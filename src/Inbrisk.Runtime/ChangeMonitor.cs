using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// The local watch pipeline on top of a capture session:
///   frames → FrameDiffer → FrameDiff events → StabilityTracker
/// Adaptive: signals the GDI session to poll faster while change is in flight
/// (WGC self-regulates — it only delivers frames on repaint).
/// </summary>
public sealed class ChangeMonitor : IDisposable
{
    private readonly ICaptureSession _session;
    private readonly FrameDiffer _differ = new();
    private readonly StabilityTracker _stability;
    private readonly ITelemetrySink? _telemetry;
    private readonly object _gate = new();
    private RawFrame? _prev;
    private long _sampled, _received;
    private DateTimeOffset _lastMetricEmit = DateTimeOffset.MinValue;
    private bool _disposed;

    public event Action<FrameDiff>? Diff;
    public event Action<StabilityInfo>? StabilityChanged;

    public ICaptureSession Session => _session;
    public StabilityInfo Stability => _stability.Current;
    public StabilityTracker Tracker => _stability;

    public ChangeMonitor(ICaptureSession session, ITelemetrySink? telemetry = null,
        int quietMs = 800)
    {
        _session = session;
        _telemetry = telemetry;
        _stability = new StabilityTracker(quietMs);
        _stability.StateChanged += s => RaiseStabilityChanged(s);
        _stability.StateChanged += OnStabilityTransition;
        _session.FrameReceived += OnFrame;
    }

    /// <summary>Feed semantic events so UIA/WinEvent signals count as change.
    /// LocationChanged is ambient noise (cursor/drag moves) and would keep
    /// the tracker permanently "changing" — it never counts toward change.</summary>
    public void FeedSemantic(ObservedEvent e)
    {
        if (e.Kind == EventKind.LocationChanged) return;
        _stability.FeedSemantic(e);
    }

    private void OnFrame(RawFrame frame)
    {
        // Boundary: invoked on the capture session's delivery thread — a
        // fault propagates back into the capture loop.
        try
        {
            if (_disposed) return;
            var prev = _prev;
            Interlocked.Increment(ref _received);
            FrameDiff? diff = null;
            lock (_gate)
            {
                diff = _differ.Compare(prev, frame);
                _prev = frame;
                Interlocked.Increment(ref _sampled);
            }
            _stability.FeedDiff(diff);
            LastChangedDesktopRegions = diff.DesktopRegions().ToList();
            RaiseDiff(diff);
            MaybeEmitMetrics(diff);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"inbrisk changemonitor frame fault: {ex}");
        }
    }

    // Per-subscriber delivery: one bad subscriber must neither abort the
    // remaining subscribers nor escape the capture thread.
    private void RaiseDiff(FrameDiff d)
    {
        var subs = Diff;
        if (subs == null) return;
        foreach (Action<FrameDiff> sub in subs.GetInvocationList())
        {
            try { sub(d); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk changemonitor subscriber fault: {ex}");
            }
        }
    }

    private void RaiseStabilityChanged(StabilityInfo s)
    {
        var subs = StabilityChanged;
        if (subs == null) return;
        foreach (Action<StabilityInfo> sub in subs.GetInvocationList())
        {
            try { sub(s); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk changemonitor subscriber fault: {ex}");
            }
        }
    }

    private void OnStabilityTransition(StabilityInfo s)
    {
        // adaptive: while changing, ask the backend to sample faster (GDI polls;
        // WGC ignores — it's compositor-driven)
        if (_session is IAdaptiveSource a) a.SetBusy(s.State == StabilityState.Changing);
    }

    private void MaybeEmitMetrics(FrameDiff d)
    {
        var now = DateTimeOffset.Now;
        if ((now - _lastMetricEmit).TotalSeconds < 2) return;
        _lastMetricEmit = now;
        var m = _session.Metrics;
        _telemetry?.EmitPipeline(new PipelineTelemetry(now, "capture",
            Target: _session.Target.ToString(), Backend: m.Backend.ToString(),
            CaptureFps: m.CaptureFps, SampledFps: _sampled / Math.Max(1, (now - m.StartedAt).TotalSeconds),
            FramesReceived: m.FramesReceived, FramesDropped: m.FramesDropped,
            FrameWidth: m.FrameWidth, FrameHeight: m.FrameHeight,
            ChangedFraction: d.ChangedFraction, ChangedRegions: d.ImageRegions.Count,
            StableForMs: _stability.Current.StableFor.TotalMilliseconds));
    }

    /// <summary>Union of the most recent diff regions in desktop space.</summary>
    public IReadOnlyList<RectPx> LastChangedDesktopRegions { get; private set; }
        = Array.Empty<RectPx>();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.FrameReceived -= OnFrame;
        _stability.Dispose();
        _session.Dispose();
    }
}
