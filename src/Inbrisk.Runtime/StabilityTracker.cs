using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Tracks change/stable state from frame diffs + semantic events.
/// Changing → first diff above threshold (or semantic event).
/// Stable   → no change signal for QuietMs (checked on a timer so it still
///            fires when the capture stream goes idle).
/// </summary>
public sealed class StabilityTracker : IDisposable
{
    private readonly int _quietMs;
    private readonly double _changeThreshold;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private StabilityInfo _current = new(StabilityState.Unknown, null, TimeSpan.Zero, 0);
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;

    /// <summary>Fired on every state transition (Changing ⇄ Stable).</summary>
    public event Action<StabilityInfo>? StateChanged;
    /// <summary>Fired on every incoming change signal (throttled by tracker).</summary>
    public event Action? ChangeDetected;

    public StabilityTracker(int quietMs = 800, double changeThreshold = 0.003)
    {
        _quietMs = quietMs;
        _changeThreshold = changeThreshold;
        _timer = new Timer(_ => Tick(), null, 100, 100);
    }

    public StabilityInfo Current
    {
        get { lock (_gate) return _current; }
    }

    public void FeedDiff(FrameDiff d)
    {
        if (d.HasChange && d.ChangedFraction >= _changeThreshold)
            MarkChange(d.ChangedFraction);
        else
            Tick();
    }

    public void FeedSemantic(ObservedEvent e) => MarkChange(0);

    private void MarkChange(double fraction)
    {
        lock (_gate)
        {
            _lastChange = DateTimeOffset.Now;
            if (_current.State != StabilityState.Changing)
            {
                _current = new StabilityInfo(StabilityState.Changing,
                    _lastChange, TimeSpan.Zero, fraction);
                RaiseState(_current);
            }
            RaiseChange();
        }
    }

    private void Tick()
    {
        // Boundary: runs on a thread-pool timer (and from capture/event
        // threads via FeedDiff) — subscriber faults must never escape.
        lock (_gate)
        {
            if (_current.State == StabilityState.Changing &&
                (DateTimeOffset.Now - _lastChange).TotalMilliseconds >= _quietMs)
            {
                _current = new StabilityInfo(StabilityState.Stable, _lastChange,
                    DateTimeOffset.Now - _lastChange, _current.LastChangedFraction);
                RaiseState(_current);
            }
            else if (_current.State == StabilityState.Unknown)
            {
                _current = new StabilityInfo(StabilityState.Stable, null,
                    TimeSpan.Zero, 0);
                RaiseState(_current);
            }
            else if (_current.State == StabilityState.Stable && _lastChange != DateTimeOffset.MinValue)
            {
                _current = _current with { StableFor = DateTimeOffset.Now - _lastChange };
            }
        }
    }

    // Per-subscriber delivery: one bad subscriber must neither abort the
    // remaining subscribers nor escape the raiser's thread/timer.
    private void RaiseState(StabilityInfo s)
    {
        var subs = StateChanged;
        if (subs == null) return;
        foreach (Action<StabilityInfo> sub in subs.GetInvocationList())
        {
            try { sub(s); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk stability subscriber fault: {ex}");
            }
        }
    }

    private void RaiseChange()
    {
        var subs = ChangeDetected;
        if (subs == null) return;
        foreach (Action sub in subs.GetInvocationList())
        {
            try { sub(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk stability subscriber fault: {ex}");
            }
        }
    }

    public void Dispose() => _timer.Dispose();
}
