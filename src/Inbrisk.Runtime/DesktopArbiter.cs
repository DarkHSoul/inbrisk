using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Thread-safe and task-aware desktop concurrency arbiter. Enforces mutually exclusive physical
/// input ownership while allowing parallel read-only observation and per-task cancellation.
/// </summary>
public sealed class DesktopArbiter : IDesktopArbiter, IDisposable
{
    private static readonly Lazy<DesktopArbiter> _shared = new(() => new DesktopArbiter());
    public static DesktopArbiter Shared => _shared.Value;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _physicalSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<string, DesktopLease> _activeLeases = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _taskCts = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _taskLeases = new();

    private volatile DesktopLease? _currentPhysicalLease;
    private readonly Timer _watchdogTimer;
    private bool _disposed;

    /// <summary>
    /// Optional hook invoked when an exclusive physical lease expires or is forcibly revoked
    /// to release held hardware keys and mouse buttons.
    /// </summary>
    public Action? OnInputCleanup { get; set; }

    public event Action<InputLeaseInfo>? LeaseAcquired;
    public event Action<InputLeaseInfo>? LeaseReleased;
    public event Action<string, string?>? TaskCancelled;

    public DesktopArbiter()
    {
        // Periodic watchdog running every 1000ms to clean up expired leases
        _watchdogTimer = new Timer(WatchdogTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public async Task<IInputLease> AcquireAsync(
        string ownerId,
        LeaseKind kind,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? leaseDuration = null,
        long? targetHwnd = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        CheckExpiredLeases();

        var effTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var effDuration = leaseDuration ?? TimeSpan.FromSeconds(30);

        var taskCts = GetOrCreateTaskCts(ownerId);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, taskCts.Token);

        if (kind == LeaseKind.PhysicalInput)
        {
            var sw = Stopwatch.StartNew();
            bool entered;
            try
            {
                entered = await _physicalSemaphore.WaitAsync(effTimeout, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (taskCts.IsCancellationRequested)
                    throw new OperationCanceledException($"Task '{ownerId}' was cancelled by DesktopArbiter.");
                throw;
            }

            if (!entered)
            {
                var cur = _currentPhysicalLease;
                var curOwner = cur != null ? $"held by '{cur.OwnerId}' ({cur.Description})" : "locked";
                throw new TimeoutException($"Timed out after {effTimeout.TotalMilliseconds:0}ms waiting for PhysicalInput lease ({curOwner}).");
            }

            var lease = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            lock (_gate)
            {
                _currentPhysicalLease = lease;
            }

            LeaseAcquired?.Invoke(lease.ToInfo());
            return lease;
        }
        else
        {
            // ReadOnly or WindowInput: can be acquired concurrently
            var lease = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            LeaseAcquired?.Invoke(lease.ToInfo());
            return lease;
        }
    }

    public bool TryAcquire(
        string ownerId,
        LeaseKind kind,
        string description,
        out IInputLease? lease,
        TimeSpan? leaseDuration = null,
        long? targetHwnd = null)
    {
        ThrowIfDisposed();
        CheckExpiredLeases();

        var effDuration = leaseDuration ?? TimeSpan.FromSeconds(30);
        var taskCts = GetOrCreateTaskCts(ownerId);

        if (taskCts.IsCancellationRequested)
        {
            lease = null;
            return false;
        }

        if (kind == LeaseKind.PhysicalInput)
        {
            if (!_physicalSemaphore.Wait(0))
            {
                lease = null;
                return false;
            }

            var created = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            lock (_gate)
            {
                _currentPhysicalLease = created;
            }

            LeaseAcquired?.Invoke(created.ToInfo());
            lease = created;
            return true;
        }
        else
        {
            var created = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            LeaseAcquired?.Invoke(created.ToInfo());
            lease = created;
            return true;
        }
    }

    public bool CancelTask(string ownerId, string? reason = null)
    {
        ThrowIfDisposed();
        bool found = false;

        if (_taskCts.TryGetValue(ownerId, out var cts))
        {
            try
            {
                cts.Cancel();
                found = true;
            }
            catch (ObjectDisposedException) { }
        }

        if (_taskLeases.TryGetValue(ownerId, out var leaseIds))
        {
            foreach (var leaseId in leaseIds.Keys)
            {
                if (_activeLeases.TryGetValue(leaseId, out var lease))
                {
                    lease.Release();
                    found = true;
                }
            }
        }

        if (found)
        {
            TaskCancelled?.Invoke(ownerId, reason);
        }

        return found;
    }

    public void CancelAll(string? reason = null)
    {
        ThrowIfDisposed();
        CancelAllInternal(reason);
    }

    private void CancelAllInternal(string? reason)
    {
        foreach (var kvp in _taskCts)
        {
            try { kvp.Value.Cancel(); } catch { }
        }

        foreach (var lease in _activeLeases.Values)
        {
            lease.Release();
        }

        _activeLeases.Clear();
        _taskLeases.Clear();

        lock (_gate)
        {
            _currentPhysicalLease = null;
        }

        OnInputCleanup?.Invoke();
    }

    public IReadOnlyList<InputLeaseInfo> GetActiveLeases()
    {
        return _activeLeases.Values.Select(l => l.ToInfo()).ToList();
    }

    public InputLeaseInfo? GetExclusiveOwner()
    {
        return _currentPhysicalLease?.ToInfo();
    }

    private DesktopLease CreateLeaseInternal(
        string ownerId,
        LeaseKind kind,
        string description,
        TimeSpan duration,
        long? targetHwnd,
        CancellationTokenSource taskCts)
    {
        var leaseId = $"lease_{Guid.NewGuid():N}"[..14];
        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(duration);

        var lease = new DesktopLease(this, leaseId, ownerId, kind, description, now, expires, targetHwnd, taskCts.Token);
        _activeLeases[leaseId] = lease;

        var set = _taskLeases.GetOrAdd(ownerId, _ => new ConcurrentDictionary<string, byte>());
        set[leaseId] = 1;

        return lease;
    }

    internal void ReleaseLease(DesktopLease lease)
    {
        if (!_activeLeases.TryRemove(lease.LeaseId, out _))
            return;

        if (_taskLeases.TryGetValue(lease.OwnerId, out var set))
            set.TryRemove(lease.LeaseId, out _);

        if (lease.Kind == LeaseKind.PhysicalInput)
        {
            lock (_gate)
            {
                if (_currentPhysicalLease == lease)
                {
                    _currentPhysicalLease = null;
                }
            }
            try
            {
                _physicalSemaphore.Release();
            }
            catch (SemaphoreFullException) { }
        }

        LeaseReleased?.Invoke(lease.ToInfo());
    }

    private CancellationTokenSource GetOrCreateTaskCts(string ownerId)
    {
        while (true)
        {
            if (_taskCts.TryGetValue(ownerId, out var existing))
            {
                if (!existing.IsCancellationRequested)
                    return existing;

                // If already cancelled, replace with fresh CTS for new request on same owner
                var fresh = new CancellationTokenSource();
                if (_taskCts.TryUpdate(ownerId, fresh, existing))
                {
                    existing.Dispose();
                    return fresh;
                }
            }
            else
            {
                var fresh = new CancellationTokenSource();
                if (_taskCts.TryAdd(ownerId, fresh))
                    return fresh;
                fresh.Dispose();
            }
        }
    }

    private void WatchdogTick(object? state)
    {
        try
        {
            CheckExpiredLeases();
        }
        catch { }
    }

    private void CheckExpiredLeases()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var lease in _activeLeases.Values)
        {
            if (now > lease.ExpiresAt)
            {
                var wasPhysical = lease.Kind == LeaseKind.PhysicalInput;
                lease.Release();
                if (wasPhysical)
                {
                    OnInputCleanup?.Invoke();
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DesktopArbiter));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watchdogTimer.Dispose();
        CancelAllInternal("DesktopArbiter disposed");
        _physicalSemaphore.Dispose();
    }

    internal sealed class DesktopLease : IInputLease
    {
        private readonly DesktopArbiter _arbiter;
        private int _released;

        public string LeaseId { get; }
        public string OwnerId { get; }
        public LeaseKind Kind { get; }
        public string Description { get; }
        public DateTimeOffset AcquiredAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public long? TargetHwnd { get; }
        public CancellationToken CancellationToken { get; }

        public bool IsActive => Volatile.Read(ref _released) == 0;

        public DesktopLease(
            DesktopArbiter arbiter,
            string leaseId,
            string ownerId,
            LeaseKind kind,
            string description,
            DateTimeOffset acquiredAt,
            DateTimeOffset expiresAt,
            long? targetHwnd,
            CancellationToken ct)
        {
            _arbiter = arbiter;
            LeaseId = leaseId;
            OwnerId = ownerId;
            Kind = kind;
            Description = description;
            AcquiredAt = acquiredAt;
            ExpiresAt = expiresAt;
            TargetHwnd = targetHwnd;
            CancellationToken = ct;
        }

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _arbiter.ReleaseLease(this);
            }
        }

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        public InputLeaseInfo ToInfo() =>
            new(LeaseId, OwnerId, Kind, Description, AcquiredAt, ExpiresAt, TargetHwnd, IsActive);
    }
}
