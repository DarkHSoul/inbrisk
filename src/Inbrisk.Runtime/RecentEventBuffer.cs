using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Bounded ring of recent coalesced events — feeds "recent events"
/// into canonical observations without flooding model context.</summary>
public sealed class RecentEventBuffer
{
    private readonly Queue<ObservedEvent> _q = new();
    private readonly int _capacity;
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private long _lastAddTicks;

    /// <summary>Timestamp of the most recent buffered event — consumers can
    /// prove "nothing happened since T" by comparing against it.</summary>
    public DateTimeOffset LastAddAt =>
        new(Interlocked.Read(ref _lastAddTicks), TimeSpan.Zero);

    public RecentEventBuffer(int capacity = 200) => _capacity = capacity;

    /// <summary>Released once per added event — waiters (verification polls)
    /// wake on the first new event instead of sleeping a full poll tick.
    /// Tokens accumulate harmlessly; callers treat a wake as a hint only.</summary>
    public bool WaitForEvent(int timeoutMs, CancellationToken ct = default)
        => _signal.Wait(timeoutMs, ct);

    public void Add(ObservedEvent e)
    {
        lock (_q)
        {
            _q.Enqueue(e);
            while (_q.Count > _capacity) _q.Dequeue();
        }
        Interlocked.Exchange(ref _lastAddTicks, DateTimeOffset.UtcNow.Ticks);
        _signal.Release();
    }

    /// <summary>Latest <paramref name="max"/> events, oldest first.</summary>
    public IReadOnlyList<ObservedEvent> Snapshot(int max)
    {
        lock (_q)
        {
            var arr = _q.ToArray();
            return arr.Length <= max ? arr : arr[^max..];
        }
    }
}
