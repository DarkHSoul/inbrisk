namespace Inbrisk.Runtime.Adapters;

/// <summary>
/// A timestamped, sequenced CDP event entry.
/// </summary>
public sealed record SequencedCdpEvent<T>(long Sequence, DateTimeOffset Timestamp, T Data);

/// <summary>
/// Thread-safe bounded ring buffer with drop-oldest eviction policy and telemetry notifications.
/// Ensures persistent event ingestion (Network, Console, Runtime, Log) never causes unbounded memory growth.
/// </summary>
public sealed class BoundedCdpEventBuffer<T>
{
    private readonly Queue<SequencedCdpEvent<T>> _queue = new();
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Action? _onDropped;
    private long _currentSequence;
    private long _droppedCount;

    public int Capacity => _capacity;
    public long CurrentSequence => Interlocked.Read(ref _currentSequence);
    public long DroppedCount => Interlocked.Read(ref _droppedCount);
    public bool Truncated => DroppedCount > 0;
    public int Count { get { lock (_lock) return _queue.Count; } }

    public BoundedCdpEventBuffer(int capacity = 500, Action? onDropped = null)
    {
        _capacity = capacity > 0 ? capacity : 500;
        _onDropped = onDropped;
    }

    public long Enqueue(T item)
    {
        lock (_lock)
        {
            var seq = Interlocked.Increment(ref _currentSequence);
            var entry = new SequencedCdpEvent<T>(seq, DateTimeOffset.UtcNow, item);
            _queue.Enqueue(entry);

            while (_queue.Count > _capacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _droppedCount);
                _onDropped?.Invoke();
            }

            return seq;
        }
    }

    public IReadOnlyList<SequencedCdpEvent<T>> GetEventsSince(long baselineSequence)
    {
        lock (_lock)
        {
            return _queue.Where(e => e.Sequence > baselineSequence).ToList();
        }
    }

    public IReadOnlyList<SequencedCdpEvent<T>> Snapshot()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
        }
    }
}
