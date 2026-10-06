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

    private readonly string _lockFilePath;
    private FileStream? _crossProcessLock;

    private volatile DesktopLease? _currentPhysicalLease;
    private readonly Timer _watchdogTimer;
    private bool _disposed;
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Optional hook invoked when an exclusive physical lease expires or is forcibly revoked
    /// to release held hardware keys and mouse buttons.
    /// </summary>
    public Action? OnInputCleanup { get; set; }

    public event Action<InputLeaseInfo>? LeaseAcquired;
    public event Action<InputLeaseInfo>? LeaseReleased;
    public event Action<string, string?>? TaskCancelled;

    public DesktopArbiter(string? lockFilePath = null)
    {
        _lockFilePath = lockFilePath ?? Path.Combine(Path.GetTempPath(), "inbrisk", "physical_input.lock");
        // Periodic watchdog running every 200ms to clean up expired leases
        _watchdogTimer = new Timer(WatchdogTick, null, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));
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
        Inbrisk.Core.LockOrderTracker.AssertCanAcquireRank1();

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

            FileStream? fs = null;
            try
            {
                var remaining = effTimeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                    remaining = TimeSpan.FromMilliseconds(50);
                fs = await AcquireCrossProcessLockAsync(remaining, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _physicalSemaphore.Release();
                if (ex is OperationCanceledException && taskCts.IsCancellationRequested)
                    throw new OperationCanceledException($"Task '{ownerId}' was cancelled by DesktopArbiter.");
                throw new TimeoutException($"Timed out waiting for physical input lock (cross-process contention): {ex.Message}", ex);
            }

            var lease = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            lock (_gate)
            {
                _crossProcessLock = fs;
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
        Inbrisk.Core.LockOrderTracker.AssertCanAcquireRank1();

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

            if (!TryAcquireCrossProcessLock(out var fs))
            {
                _physicalSemaphore.Release();
                lease = null;
                return false;
            }

            var created = CreateLeaseInternal(ownerId, kind, description, effDuration, targetHwnd, taskCts);
            lock (_gate)
            {
                _crossProcessLock = fs;
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

    private async Task<FileStream> AcquireCrossProcessLockAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var lockDir = Path.GetDirectoryName(_lockFilePath);
        if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
        {
            Directory.CreateDirectory(lockDir);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockFilePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) // File locked by another process
            {
                if (sw.Elapsed >= timeout)
                    throw;
            }
            catch (UnauthorizedAccessException)
            {
                if (sw.Elapsed >= timeout)
                    throw;
            }

            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"Timed out waiting for cross-process input lock at '{_lockFilePath}'.");

            var delayMs = Math.Min(30, (int)remaining.TotalMilliseconds);
            if (delayMs <= 0) delayMs = 1;
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
    }

    private bool TryAcquireCrossProcessLock(out FileStream? lockStream)
    {
        lockStream = null;
        var lockDir = Path.GetDirectoryName(_lockFilePath);
        if (!string.IsNullOrEmpty(lockDir) && !Directory.Exists(lockDir))
        {
            try { Directory.CreateDirectory(lockDir); } catch { return false; }
        }

        try
        {
            lockStream = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return true;
        }
        catch
        {
            return false;
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
            _crossProcessLock?.Dispose();
            _crossProcessLock = null;
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
                    _crossProcessLock?.Dispose();
                    _crossProcessLock = null;
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
        lock (_gate)
        {
            _crossProcessLock?.Dispose();
            _crossProcessLock = null;
        }
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
