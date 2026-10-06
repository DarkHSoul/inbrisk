using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Bounded ring of recent coalesced events — feeds "recent events"
/// into canonical observations without flooding model context.</summary>
public sealed class RecentEventBuffer : IDisposable, IEventWaiter
{
    private readonly Queue<ObservedEvent> _q = new();
    private readonly int _capacity;
    private readonly object _sync = new();
    private long _eventGeneration;
    private long _lastAddTicks;
    private bool _disposed;

    /// <summary>Timestamp of the most recent buffered event — consumers can
    /// prove "nothing happened since T" by comparing against it.</summary>
    public DateTimeOffset LastAddAt =>
        new(Interlocked.Read(ref _lastAddTicks), TimeSpan.Zero);

    /// <summary>Monotonically increasing generation counter of added events.</summary>
    public long CurrentGeneration => Interlocked.Read(ref _eventGeneration);

    private int _activeWaiters;

    /// <summary>Number of threads currently waiting in WaitForNextEvent.</summary>
    public int ActiveWaiters => Volatile.Read(ref _activeWaiters);

    public RecentEventBuffer(int capacity = 200) => _capacity = capacity;

    /// <summary>
    /// Waits until at least one event with generation > baselineGeneration arrives,
    /// or until timeoutMs expires or ct is cancelled.
    /// Eliminates historical event token accumulation and busy-spin loops.
    /// </summary>
    public bool WaitForNextEvent(long baselineGeneration, int timeoutMs, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _activeWaiters);
        try
        {
            if (timeoutMs < 0) timeoutMs = 0;
            var sw = Stopwatch.StartNew();
            lock (_sync)
            {
                if (_disposed) return false;
                while (Interlocked.Read(ref _eventGeneration) <= baselineGeneration)
                {
                    if (_disposed) return false;
                    ct.ThrowIfCancellationRequested();
                    var remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    var waitSlice = Math.Min(remaining, 50);
                    Monitor.Wait(_sync, waitSlice);
                }
                return !_disposed;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeWaiters);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Wakes only on an event arriving after this wait begins.
    /// Preserves bounded fallback polling without token accumulation.</summary>
    public bool WaitForEvent(int timeoutMs, CancellationToken ct = default)
        => WaitForNextEvent(CurrentGeneration, timeoutMs, ct);

    public void Add(ObservedEvent e)
    {
        lock (_sync)
        {
            _q.Enqueue(e);
            while (_q.Count > _capacity) _q.Dequeue();
            Interlocked.Exchange(ref _lastAddTicks, DateTimeOffset.UtcNow.Ticks);
            Interlocked.Increment(ref _eventGeneration);
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Latest <paramref name="max"/> events, oldest first.</summary>
    public IReadOnlyList<ObservedEvent> Snapshot(int max)
    {
        lock (_sync)
        {
            var arr = _q.ToArray();
            return arr.Length <= max ? arr : arr[^max..];
        }
    }
}
