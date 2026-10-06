using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp;

public sealed record SnapshotCacheKey(
    long? Hwnd,
    string? ElementId,
    int? MaxElements,
    string? Detail,
    bool Relational
);

public sealed record SnapshotCacheEntry(
    SnapshotCacheKey Key,
    CallToolResult Result,
    DateTimeOffset CreatedAt,
    long? AffectedHwnd,
    long MutationVersion
);

/// <summary>
/// Session-scoped short-lived cache for computer_inspect results (McpSession -> SessionSnapshotCache).
/// Invalidated by StructureChanged, WindowClosed, or MutationVersion changes.
/// Features stale-publish prevention and LRU bounding to 64 entries.
/// </summary>
public sealed class SessionSnapshotCache : IDisposable
{
    private readonly Func<long> _mutationVersionProvider;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Dictionary<SnapshotCacheKey, LinkedListNode<SnapshotCacheEntry>> _entries = new();
    private readonly LinkedList<SnapshotCacheEntry> _lruList = new();
    private readonly int _maxCapacity;
    private readonly TimeSpan _ttl;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<SnapshotCacheKey, Task<CallToolResult>> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _detached = new();

    public CacheTelemetry Telemetry { get; }

    /// <summary>Active producer tasks logically owned by the cache prior to disposal/timeout.</summary>
    public int ActiveOwnedProducerCount => _inFlight.Count;

    /// <summary>Producer tasks that exceeded the drain timeout upon disposal and remain physically running.</summary>
    public int DetachedProducerCount => _detached.Count;

    /// <summary>Total physically running producer tasks (owned + detached).</summary>
    public int InFlightCount => _inFlight.Count + _detached.Count;

    public int MaxCapacity => _maxCapacity;

    public TimeSpan Ttl => _ttl;

    public int EntryCount
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    public SessionSnapshotCache(
        Func<long> mutationVersionProvider,
        int maxCapacity = 64,
        TimeSpan? ttl = null,
        CacheTelemetry? telemetry = null,
        Func<DateTimeOffset>? clock = null)
    {
        _mutationVersionProvider = mutationVersionProvider ?? throw new ArgumentNullException(nameof(mutationVersionProvider));
        _maxCapacity = maxCapacity > 0 ? maxCapacity : 64;
        _ttl = ttl ?? TimeSpan.FromMilliseconds(500);
        Telemetry = telemetry ?? new CacheTelemetry();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<CallToolResult> GetOrComputeAsync(
        SnapshotCacheKey key,
        Func<CancellationToken, Task<CallToolResult>> factory,
        CancellationToken ct = default)
    {
        if (_disposed)
            return await factory(ct).ConfigureAwait(false);

        var now = _clock();
        var currentVersion = _mutationVersionProvider();

        bool wasDisposed = false;
        lock (_lock)
        {
            if (_disposed)
            {
                wasDisposed = true;
            }
            else if (_entries.TryGetValue(key, out var node))
            {
                var entry = node.Value;
                if (entry.MutationVersion == currentVersion && (now - entry.CreatedAt) < _ttl)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    Telemetry.IncHit();
                    return entry.Result;
                }
                else
                {
                    _entries.Remove(key);
                    _lruList.Remove(node);
                    if (entry.MutationVersion == currentVersion)
                        Telemetry.IncExpiration();
                    else
                        Telemetry.IncInvalidation();
                }
            }
        }

        if (wasDisposed)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        bool isProducer = false;
        var startVersion = _mutationVersionProvider();

        var task = _inFlight.GetOrAdd(key, k =>
        {
            isProducer = true;
            Telemetry.IncMiss();
            return Task.Run(async () =>
            {
                try
                {
                    var result = await factory(_disposeCts.Token).ConfigureAwait(false);
                    var endVersion = _mutationVersionProvider();
                    if (!_disposed && endVersion == startVersion)
                    {
                        PublishEntry(k, result, startVersion);
                    }
                    else
                    {
                        Telemetry.IncRejectedStalePublish();
                    }
                    return result;
                }
                finally
                {
                    _inFlight.TryRemove(k, out _);
                }
            }, _disposeCts.Token);
        });

        if (!isProducer)
        {
            Telemetry.IncSingleFlightReuse();
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    public CallToolResult GetOrCompute(
        SnapshotCacheKey key,
        Func<CallToolResult> factory)
    {
        var now = _clock();
        var currentVersion = _mutationVersionProvider();

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                var entry = node.Value;
                if (entry.MutationVersion == currentVersion && (now - entry.CreatedAt) < _ttl)
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    Telemetry.IncHit();
                    return entry.Result;
                }
                else
                {
                    _entries.Remove(key);
                    _lruList.Remove(node);
                    if (entry.MutationVersion == currentVersion)
                        Telemetry.IncExpiration();
                    else
                        Telemetry.IncInvalidation();
                }
            }
        }

        Telemetry.IncMiss();
        var startVersion = _mutationVersionProvider();
        var result = factory();

        var endVersion = _mutationVersionProvider();
        if (endVersion != startVersion)
        {
            Telemetry.IncRejectedStalePublish();
            return result;
        }

        PublishEntry(key, result, startVersion);
        return result;
    }

    private void PublishEntry(SnapshotCacheKey key, CallToolResult result, long version)
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            if (_entries.TryGetValue(key, out var existingNode))
            {
                _entries.Remove(key);
                _lruList.Remove(existingNode);
            }

            while (_entries.Count >= _maxCapacity && _lruList.Last != null)
            {
                var oldest = _lruList.Last;
                _entries.Remove(oldest.Value.Key);
                _lruList.Remove(oldest);
                Telemetry.IncEviction();
            }

            var entry = new SnapshotCacheEntry(
                Key: key,
                Result: result,
                CreatedAt: _clock(),
                AffectedHwnd: key.Hwnd,
                MutationVersion: version
            );

            var newNode = _lruList.AddFirst(entry);
            _entries[key] = newNode;
            Telemetry.IncInsert();
        }
    }

    public void InvalidateScope(long? hwnd)
    {
        lock (_lock)
        {
            if (!hwnd.HasValue)
            {
                var count = _entries.Count;
                _entries.Clear();
                _lruList.Clear();
                if (count > 0)
                    Telemetry.IncInvalidation();
                return;
            }

            var toRemove = _entries.Values
                .Where(n => n.Value.AffectedHwnd == hwnd.Value)
                .ToList();

            foreach (var node in toRemove)
            {
                _entries.Remove(node.Value.Key);
                _lruList.Remove(node);
                Telemetry.IncInvalidation();
            }
        }
    }

    public void InvalidateAll()
    {
        InvalidateScope(null);
    }

    public void Clear()
    {
        InvalidateAll();
    }

    public void Dispose()
    {
        Dispose(drainTimeoutMs: 1000);
    }

    public void Dispose(int drainTimeoutMs)
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _disposeCts.Cancel();
            Telemetry.IncProducerDisposeCancellation();
        }
        catch { }

        Task[] runningTasks;
        lock (_lock)
        {
            runningTasks = _inFlight.Values.ToArray();
        }

        if (runningTasks.Length > 0)
        {
            try
            {
                var drainAll = Task.WhenAll(runningTasks);
                if (!drainAll.Wait(drainTimeoutMs))
                {
                    Telemetry.IncProducerDisposeDrainTimeout();
                }
            }
            catch
            {
                // Expected when in-flight tasks observe cancellation
            }
        }

        lock (_lock)
        {
            _entries.Clear();
            _lruList.Clear();

            // Release logical cache ownership of in-flight tasks.
            // Any tasks that are still incomplete after the drain timeout become detached
            // and continue to be tracked by DetachedProducerCount and InFlightCount until completion.
            foreach (var kvp in _inFlight)
            {
                var t = kvp.Value;
                if (!t.IsCompleted)
                {
                    _detached.TryAdd(t, 0);
                    _ = t.ContinueWith(_ => _detached.TryRemove(t, out byte _), TaskScheduler.Default);
                }
            }
            _inFlight.Clear();
        }

        try { _disposeCts.Dispose(); } catch { }
    }
}
