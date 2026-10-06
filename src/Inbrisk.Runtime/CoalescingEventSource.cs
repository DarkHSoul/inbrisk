using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Debounce/coalesce layer between raw event sources and consumers.
/// Critical transitions (open/close/foreground/focus/notification) pass immediately;
/// noisy kinds (structure/name/value/location churn) are coalesced per
/// EventCoalescingKey (Kind, Hwnd, ElementId, PropertyId, StructureChange) —
/// the latest wins within each window while preserving semantic distinctions.
/// No event kind is ever dropped entirely: each pending key emits its
/// most recent event on flush.
/// </summary>
public sealed class CoalescingEventSource : IEventSource
{
    private readonly IEventSource _inner;
    private readonly int _windowMs;
    private readonly Dictionary<EventCoalescingKey, ObservedEvent> _pending = new();
    private readonly object _gate = new();
    private readonly Timer _flushTimer;
    private bool _started;

    public long RawCount;
    public long EmittedCount;

    /// <summary>These kinds are delivered immediately — losing an open/close or notification is worse than noise.</summary>
    private static readonly HashSet<EventKind> Immediate =
    [
        EventKind.ForegroundChanged, EventKind.WindowOpened,
        EventKind.WindowClosed, EventKind.FocusChanged, EventKind.WindowShown,
        EventKind.Notification,
    ];

    public event Action<ObservedEvent>? Event;

    public CoalescingEventSource(IEventSource inner, int windowMs = 120)
    {
        _inner = inner;
        _windowMs = windowMs;
        _inner.Event += OnRaw;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Fraction of raw events suppressed by coalescing (0..1).</summary>
    public double ReductionRatio =>
        RawCount == 0 ? 0 : 1.0 - (double)EmittedCount / RawCount;

    public void OnRaw(ObservedEvent e)
    {
        Interlocked.Increment(ref RawCount);
        if (Immediate.Contains(e.Kind))
        {
            Interlocked.Increment(ref EmittedCount);
            Event?.Invoke(e);
            return;
        }
        var key = EventCoalescingKey.FromEvent(e);
        lock (_gate)
            _pending[key] = e;
    }

    public void Flush()
    {
        List<ObservedEvent> batch;
        lock (_gate)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending.Values];
            _pending.Clear();
        }
        foreach (var e in batch)
        {
            Interlocked.Increment(ref EmittedCount);
            Event?.Invoke(e);
        }
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _inner.Start();
        _flushTimer.Change(_windowMs, _windowMs);
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        _inner.Dispose();
    }
}
