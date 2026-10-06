using System;
using System.Threading;

namespace Inbrisk.Core;

/// <summary>
/// Fine-grained internal telemetry counters for Phase G session and request caches.
/// Tracks hits, misses, single-flight coalescing, stale-publish rejections, and invalidations.
/// </summary>
public sealed class CacheTelemetry
{
    private long _hitCount;
    private long _missCount;
    private long _insertCount;
    private long _evictionCount;
    private long _expirationCount;
    private long _invalidationCount;
    private long _singleFlightReuseCount;
    private long _rejectedStalePublishCount;
    private long _pidReuseRejectCount;
    private long _producerDisposeCancellationCount;
    private long _producerDisposeDrainTimeoutCount;

    public long HitCount => Interlocked.Read(ref _hitCount);
    public long MissCount => Interlocked.Read(ref _missCount);
    public long InsertCount => Interlocked.Read(ref _insertCount);
    public long EvictionCount => Interlocked.Read(ref _evictionCount);
    public long ExpirationCount => Interlocked.Read(ref _expirationCount);
    public long InvalidationCount => Interlocked.Read(ref _invalidationCount);
    public long SingleFlightReuseCount => Interlocked.Read(ref _singleFlightReuseCount);
    public long RejectedStalePublishCount => Interlocked.Read(ref _rejectedStalePublishCount);
    public long PidReuseRejectCount => Interlocked.Read(ref _pidReuseRejectCount);
    public long ProducerDisposeCancellationCount => Interlocked.Read(ref _producerDisposeCancellationCount);
    public long ProducerDisposeDrainTimeoutCount => Interlocked.Read(ref _producerDisposeDrainTimeoutCount);

    public void IncHit() => Interlocked.Increment(ref _hitCount);
    public void IncMiss() => Interlocked.Increment(ref _missCount);
    public void IncInsert() => Interlocked.Increment(ref _insertCount);
    public void IncEviction() => Interlocked.Increment(ref _evictionCount);
    public void IncExpiration() => Interlocked.Increment(ref _expirationCount);
    public void IncInvalidation() => Interlocked.Increment(ref _invalidationCount);
    public void IncSingleFlightReuse() => Interlocked.Increment(ref _singleFlightReuseCount);
    public void IncRejectedStalePublish() => Interlocked.Increment(ref _rejectedStalePublishCount);
    public void IncPidReuseReject() => Interlocked.Increment(ref _pidReuseRejectCount);
    public void IncProducerDisposeCancellation() => Interlocked.Increment(ref _producerDisposeCancellationCount);
    public void IncProducerDisposeDrainTimeout() => Interlocked.Increment(ref _producerDisposeDrainTimeoutCount);

    public void Reset()
    {
        Interlocked.Exchange(ref _hitCount, 0);
        Interlocked.Exchange(ref _missCount, 0);
        Interlocked.Exchange(ref _insertCount, 0);
        Interlocked.Exchange(ref _evictionCount, 0);
        Interlocked.Exchange(ref _expirationCount, 0);
        Interlocked.Exchange(ref _invalidationCount, 0);
        Interlocked.Exchange(ref _singleFlightReuseCount, 0);
        Interlocked.Exchange(ref _rejectedStalePublishCount, 0);
        Interlocked.Exchange(ref _pidReuseRejectCount, 0);
        Interlocked.Exchange(ref _producerDisposeCancellationCount, 0);
        Interlocked.Exchange(ref _producerDisposeDrainTimeoutCount, 0);
    }
}
