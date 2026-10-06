using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Mcp;

public sealed record FindCacheKey(
    long? ScopeHwnd,
    long? Hwnd,
    int? Pid,
    Role? Role,
    string? Name,
    string? AutomationId,
    string? ScopeElementId,
    string? ValueContains,
    string? ValueEquals,
    string? ClassName,
    bool? Enabled,
    bool FirstOnly,
    bool IncludeOffscreen,
    int? Limit = null,
    string? Detail = null,
    string? NameNotContains = null
);

public sealed record FindCacheEntry(
    FindCacheKey Key,
    IReadOnlyList<UiElement> Elements,
    DateTimeOffset CreatedAt,
    HashSet<long> AffectedHwnds,
    HashSet<int> AffectedPids,
    long MutationVersion
);

/// <summary>
/// Session-scoped cache for find results (McpSession -> SessionFindCache).
/// Retains results across tool turns with a 400ms TTL and strict MutationVersion tracking.
/// Features:
/// - Single-flight coalescing for concurrent equivalent misses.
/// - Cancellation isolation (cancelling one consumer does not abort the producer for others).
/// - Stale-publish prevention (discards results if MutationVersion changed during computation).
/// - Scoped invalidation on UI events/mutations without clearing unrelated windows.
/// - LRU bounded capacity of 128 entries.
/// </summary>
public sealed class SessionFindCache : IDisposable
{
    private readonly Func<long> _mutationVersionProvider;
    private readonly object _lock = new();
    private readonly Dictionary<FindCacheKey, LinkedListNode<FindCacheEntry>> _entries = new();
    private readonly LinkedList<FindCacheEntry> _lruList = new();
    private readonly ConcurrentDictionary<FindCacheKey, Task<IReadOnlyList<UiElement>>> _inFlight = new();
    private readonly ConcurrentDictionary<Task, byte> _detached = new();
    private readonly int _maxCapacity;
    private readonly TimeSpan _ttl;
    private volatile bool _disposed;
    private readonly CancellationTokenSource _disposeCts = new();

    public CacheTelemetry Telemetry { get; }

    /// <summary>Active producer tasks logically owned by the cache prior to disposal/timeout.</summary>
    public int ActiveOwnedProducerCount => _inFlight.Count;

    /// <summary>Producer tasks that exceeded the drain timeout upon disposal and remain physically running.</summary>
    public int DetachedProducerCount => _detached.Count;

    /// <summary>Total physically running producer tasks (owned + detached).</summary>
    public int InFlightCount => _inFlight.Count + _detached.Count;

    public int EntryCount
    {
        get
        {
            lock (_lock) return _entries.Count;
        }
    }

    public SessionFindCache(
        Func<long> mutationVersionProvider,
        int maxCapacity = 128,
        TimeSpan? ttl = null,
        CacheTelemetry? telemetry = null)
    {
        _mutationVersionProvider = mutationVersionProvider ?? throw new ArgumentNullException(nameof(mutationVersionProvider));
        _maxCapacity = maxCapacity > 0 ? maxCapacity : 128;
        _ttl = ttl ?? TimeSpan.FromMilliseconds(400);
        Telemetry = telemetry ?? new CacheTelemetry();
    }

    public Task<IReadOnlyList<UiElement>> GetOrCreateAsync(
        FindCacheKey key,
        Func<CancellationToken, Task<IReadOnlyList<UiElement>>> factory,
        CancellationToken ct = default) => GetOrComputeAsync(key, factory, ct);

    public async Task<IReadOnlyList<UiElement>> GetOrComputeAsync(
        FindCacheKey key,
        Func<CancellationToken, Task<IReadOnlyList<UiElement>>> factory,
        CancellationToken ct = default)
    {
        if (_disposed)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        // 1. Try get fresh entry under lock
        var now = DateTimeOffset.UtcNow;
        var currentVersion = _mutationVersionProvider();

        bool wasDisposed = false;
        Task<IReadOnlyList<UiElement>>? task = null;
        bool isProducer = false;

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
                    PerfTrace.Count("find:cache=hit");
                    return entry.Elements;
                }
                else
                {
                    // Expired or version changed
                    _entries.Remove(key);
                    _lruList.Remove(node);
                    if (entry.MutationVersion == currentVersion)
                        Telemetry.IncExpiration();
                    else
                        Telemetry.IncInvalidation();
                }
            }

            if (!wasDisposed)
            {
                if (_inFlight.TryGetValue(key, out var inFlightTask))
                {
                    task = inFlightTask;
                }
                else
                {
                    isProducer = true;
                    Telemetry.IncMiss();
                    PerfTrace.Count("find:cache=miss");
                    var startVersion = _mutationVersionProvider();
                    task = Task.Run(async () =>
                    {
                        try
                        {
                            var res = await factory(_disposeCts.Token).ConfigureAwait(false);
                            var endVersion = _mutationVersionProvider();
                            if (!_disposed && endVersion == startVersion)
                            {
                                PublishEntry(key, res, startVersion);
                            }
                            else
                            {
                                Telemetry.IncRejectedStalePublish();
                            }
                            return res;
                        }
                        finally
                        {
                            _inFlight.TryRemove(key, out _);
                        }
                    }, _disposeCts.Token);
                    _inFlight[key] = task;
                }
            }
        }

        if (wasDisposed || task == null)
        {
            return await factory(ct).ConfigureAwait(false);
        }

        if (!isProducer)
        {
            Telemetry.IncSingleFlightReuse();
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    public IReadOnlyList<UiElement> GetOrCompute(
        FindCacheKey key,
        Func<IReadOnlyList<UiElement>> factory)
    {
        var now = DateTimeOffset.UtcNow;
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
                    return entry.Elements;
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
        IReadOnlyList<UiElement> result;
        try
        {
            result = factory();
        }
        catch
        {
            throw;
        }

        var endVersion = _mutationVersionProvider();
        if (endVersion != startVersion)
        {
            Telemetry.IncRejectedStalePublish();
            return result;
        }

        PublishEntry(key, result, startVersion);
        return result;
    }

    public int Count => EntryCount;

    public bool ContainsKey(string key)
    {
        lock (_lock)
        {
            return _entries.Keys.Any(k => k.Name == key);
        }
    }

    public (List<UiElement> Els, DateTimeOffset At, HashSet<long> Hwnds, HashSet<int> Pids, long Ver) this[string key]
    {
        get
        {
            lock (_lock)
            {
                var kv = _entries.FirstOrDefault(x => x.Key.Name == key);
                if (kv.Value != null)
                {
                    var e = kv.Value.Value;
                    return (e.Elements.ToList(), e.CreatedAt, e.AffectedHwnds, e.AffectedPids, e.MutationVersion);
                }
                throw new KeyNotFoundException(key);
            }
        }
        set
        {
            var k = new FindCacheKey(null, value.Hwnds.FirstOrDefault(), value.Pids.FirstOrDefault(), null, key, null, null, null, null, null, null, false, false);
            PublishEntry(k, value.Els, value.Ver, value.Hwnds, value.Pids, value.At);
        }
    }

    public bool TryGetValue(string key, out (List<UiElement> Els, DateTimeOffset At, HashSet<long> Hwnds, HashSet<int> Pids, long Ver) value)
    {
        lock (_lock)
        {
            var kv = _entries.FirstOrDefault(x => x.Key.Name == key);
            if (kv.Value != null)
            {
                var e = kv.Value.Value;
                value = (e.Elements.ToList(), e.CreatedAt, e.AffectedHwnds, e.AffectedPids, e.MutationVersion);
                return true;
            }
            value = default;
            return false;
        }
    }

    private void PublishEntry(
        FindCacheKey key,
        IReadOnlyList<UiElement> elements,
        long version,
        HashSet<long>? explicitHwnds = null,
        HashSet<int>? explicitPids = null,
        DateTimeOffset? createdAt = null)
    {
        var hwnds = explicitHwnds != null ? new HashSet<long>(explicitHwnds) : new HashSet<long>(elements.Where(e => e.Hwnd.HasValue).Select(e => e.Hwnd!.Value));
        if (key.Hwnd.HasValue) hwnds.Add(key.Hwnd.Value);
        if (key.ScopeHwnd.HasValue) hwnds.Add(key.ScopeHwnd.Value);

        var pids = explicitPids != null ? new HashSet<int>(explicitPids) : new HashSet<int>(elements.Where(e => e.Pid.HasValue).Select(e => e.Pid!.Value));
        if (key.Pid.HasValue) pids.Add(key.Pid.Value);

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

            var entry = new FindCacheEntry(
                Key: key,
                Elements: elements,
                CreatedAt: createdAt ?? DateTimeOffset.UtcNow,
                AffectedHwnds: hwnds,
                AffectedPids: pids,
                MutationVersion: version
            );

            var newNode = _lruList.AddFirst(entry);
            _entries[key] = newNode;
            Telemetry.IncInsert();
        }
    }

    public void InvalidateScope(long? hwnd, int? pid)
    {
        lock (_lock)
        {
            if (!hwnd.HasValue && !pid.HasValue)
            {
                // Global invalidation
                var count = _entries.Count;
                _entries.Clear();
                _lruList.Clear();
                if (count > 0)
                    Telemetry.IncInvalidation();
                return;
            }

            var toRemove = new List<LinkedListNode<FindCacheEntry>>();
            foreach (var node in _entries.Values)
            {
                var e = node.Value;
                bool match = false;
                if (hwnd.HasValue && e.AffectedHwnds.Contains(hwnd.Value))
                    match = true;
                if (pid.HasValue && e.AffectedPids.Contains(pid.Value))
                    match = true;

                if (match)
                    toRemove.Add(node);
            }

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
        InvalidateScope(null, null);
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
