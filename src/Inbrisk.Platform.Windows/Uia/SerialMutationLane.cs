using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// Single authoritative mutation and input lane for all UI state alterations (click, invoke,
/// set value, type, key, focus, close, etc.).
/// 
/// Design Rationale & Invariants:
/// 1. Single Authoritative Lane (Max Concurrency = 1): Concurrent UI mutations in Windows desktop
///    cause focus stealing, race conditions in control pattern invocation, and corrupted control states.
///    This lane guarantees mutations never overlap.
/// 2. Strict FIFO Ordering: Caller submission sequence is strictly preserved using a single-consumer
///    FIFO dispatch loop.
/// 3. Shared Across Logical Sessions: Multiple sessions (e.g. MCP clients, background agents, autonomous tools)
///    route all mutations through this lane and coordinate exclusive physical input leases via DesktopArbiter.
/// 4. Starvation-Free Reader-Writer Fairness:
///    - Read traffic does not starve mutation: Queued mutations pause new read admissions and drain active
///      reads, ensuring prompt mutation execution under heavy read traffic.
///    - Mutation traffic does not permanently starve reads: After a bounded budget of consecutive mutations
///      (MaxConsecutiveMutationsBeforeYield), the lane yields a fair execution window to waiting reads.
/// </summary>
public sealed class SerialMutationLane : IDisposable, IAsyncDisposable
{
    internal sealed class MutationWorkItem
    {
        public string OwnerId { get; }
        public Func<Task<object?>> Action { get; }
        public TaskCompletionSource<object?> Tcs { get; }
        public CancellationToken CancellationToken { get; }
        public string? Description { get; }
        public TimeSpan? Timeout { get; }
        public long EnqueuedTicks { get; }

        public MutationWorkItem(
            string ownerId,
            Func<Task<object?>> action,
            TaskCompletionSource<object?> tcs,
            CancellationToken ct,
            string? description,
            TimeSpan? timeout)
        {
            OwnerId = ownerId;
            Action = action;
            Tcs = tcs;
            CancellationToken = ct;
            Description = description;
            Timeout = timeout;
            EnqueuedTicks = Stopwatch.GetTimestamp();
        }
    }

    private static readonly Lazy<SerialMutationLane> _shared = new(() => new SerialMutationLane());
    public static SerialMutationLane Shared => _shared.Value;

    private readonly IDesktopArbiter? _arbiter;
    private readonly BlockingCollection<MutationWorkItem> _queue = new();
    private readonly Thread _workerThread;
    private readonly AutoResetEvent _readCompletedSignal = new(false);
    private readonly object _gate = new();

    private volatile bool _disposed;
    private int _activeMutationCount;
    private int _peakConcurrency;
    private long _completedCount;
    private int _consecutiveMutationsExecuted;
    private int _maxConsecutiveMutationsBeforeYield;

    // Coordinated UiaReadPool for starvation-free fairness
    private UiaReadPool? _readPool;

    public IDesktopArbiter? Arbiter => _arbiter;
    public int ActiveMutationCount => Volatile.Read(ref _activeMutationCount);
    public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);
    public int QueuedCount => _queue.Count;
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public int ConsecutiveMutationsExecuted => Volatile.Read(ref _consecutiveMutationsExecuted);

    public int MaxConsecutiveMutationsBeforeYield
    {
        get => Volatile.Read(ref _maxConsecutiveMutationsBeforeYield);
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Yield threshold must be at least 1.");
            Volatile.Write(ref _maxConsecutiveMutationsBeforeYield, value);
        }
    }

    public SerialMutationLane(
        IDesktopArbiter? arbiter = null,
        UiaReadPool? readPool = null,
        int maxConsecutiveMutationsBeforeYield = 3)
    {
        _arbiter = arbiter;
        _maxConsecutiveMutationsBeforeYield = maxConsecutiveMutationsBeforeYield;

        if (readPool != null)
        {
            LinkReadPool(readPool);
        }

        _workerThread = new Thread(ProcessQueueLoop)
        {
            Name = "inbrisk-serial-mutation-lane",
            IsBackground = true
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();
    }

    public void LinkReadPool(UiaReadPool pool)
    {
        lock (_gate)
        {
            _readPool = pool;
        }
    }

    internal void OnReadCompleted()
    {
        _readCompletedSignal.Set();
    }

    private void ProcessQueueLoop()
    {
        Native.DesktopBridge.TrySwitchCurrentThread();

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;

                // 1. Skip cancelled items before starting
                if (item.CancellationToken.IsCancellationRequested || item.Tcs.Task.IsCanceled)
                {
                    item.Tcs.TrySetCanceled(item.CancellationToken.IsCancellationRequested ? item.CancellationToken : default);
                    continue;
                }

                try
                {
                    ExecuteItemAsync(item).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
        catch (Exception) { }
    }

    private async Task ExecuteItemAsync(MutationWorkItem item)
    {
        UiaReadPool? pool;
        lock (_gate)
        {
            pool = _readPool;
        }

        // 2. Fairness coordination: prevent continuous read traffic from starving this mutation
        if (pool != null)
        {
            pool.PauseReadsForMutation();

            // Check if mutation budget reached and waiting reads need a fair window
            if (Volatile.Read(ref _consecutiveMutationsExecuted) >= _maxConsecutiveMutationsBeforeYield &&
                (pool.PendingReaderCount > 0 || pool.QueuedCount > 0))
            {
                pool.OpenFairReadWindow();
                try
                {
                    // Allow waiting reads to execute without permanent starvation
                    _readCompletedSignal.WaitOne(40);
                }
                finally
                {
                    pool.CloseFairReadWindow();
                    Volatile.Write(ref _consecutiveMutationsExecuted, 0);
                }
            }

            // Wait for any remaining active reads to drain before mutating
            try
            {
                await pool.WaitForActiveReadsDrainedAsync(TimeSpan.FromSeconds(5), item.CancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
        }

        // 3. Acquire DesktopArbiter lease if arbiter is configured (shared across logical sessions)
        IInputLease? lease = null;
        if (_arbiter != null)
        {
            var timeout = item.Timeout ?? TimeSpan.FromSeconds(10);
            lease = await _arbiter.AcquireAsync(
                item.OwnerId,
                LeaseKind.PhysicalInput,
                item.Description ?? "SerialMutation",
                timeout: timeout,
                ct: item.CancellationToken).ConfigureAwait(false);
        }

        try
        {
            var active = Interlocked.Increment(ref _activeMutationCount);

            // Update peak concurrency metric
            int currentPeak;
            do
            {
                currentPeak = Volatile.Read(ref _peakConcurrency);
                if (active <= currentPeak) break;
            } while (Interlocked.CompareExchange(ref _peakConcurrency, active, currentPeak) != currentPeak);

            try
            {
                if (item.CancellationToken.IsCancellationRequested)
                {
                    item.Tcs.TrySetCanceled(item.CancellationToken);
                    return;
                }

                var result = await item.Action().ConfigureAwait(false);
                Interlocked.Increment(ref _completedCount);
                Interlocked.Increment(ref _consecutiveMutationsExecuted);
                item.Tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce)
            {
                item.Tcs.TrySetCanceled(oce.CancellationToken);
            }
            catch (Exception ex)
            {
                item.Tcs.TrySetException(ex);
            }
            finally
            {
                Interlocked.Decrement(ref _activeMutationCount);
            }
        }
        finally
        {
            lease?.Dispose();

            // If no further mutations are immediately queued, resume reads
            if (pool != null && _queue.Count == 0)
            {
                pool.ResumeReadsAfterMutation();
            }
        }
    }

    /// <summary>
    /// Executes a mutation asynchronously with caller-supplied owner and async action.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(
        string ownerId,
        Func<Task<T>> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new MutationWorkItem(
            ownerId,
            async () =>
            {
                var r = await action().ConfigureAwait(false);
                return (object?)r;
            },
            tcs,
            ct,
            description,
            timeout);

        _queue.Add(item, ct);

        using var reg = ct.CanBeCanceled ? ct.UnsafeRegister(static state =>
        {
            var s = (TaskCompletionSource<object?>)state!;
            s.TrySetCanceled();
        }, tcs) : default;

        var effTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var res = await tcs.Task.WaitAsync(effTimeout, ct).ConfigureAwait(false);
        return (T)res!;
    }

    /// <summary>
    /// Executes a synchronous mutation action on the serial mutation lane.
    /// </summary>
    public Task<T> ExecuteAsync<T>(
        string ownerId,
        Func<T> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync(ownerId, () => Task.FromResult(action()), ct, description, timeout);

    /// <summary>
    /// Executes an async mutation without return value.
    /// </summary>
    public Task ExecuteAsync(
        string ownerId,
        Func<Task> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync<object?>(ownerId, async () =>
        {
            await action().ConfigureAwait(false);
            return null;
        }, ct, description, timeout);

    /// <summary>
    /// Executes a synchronous mutation action without return value.
    /// </summary>
    public Task ExecuteAsync(
        string ownerId,
        Action action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync<object?>(ownerId, () =>
        {
            action();
            return Task.FromResult<object?>(null);
        }, ct, description, timeout);

    // Overloads with default ownerId ("default")
    public Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync("default", action, ct, description, timeout);

    public Task<T> ExecuteAsync<T>(
        Func<T> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync("default", action, ct, description, timeout);

    public Task ExecuteAsync(
        Func<Task> action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync("default", action, ct, description, timeout);

    public Task ExecuteAsync(
        Action action,
        CancellationToken ct = default,
        string? description = null,
        TimeSpan? timeout = null) =>
        ExecuteAsync("default", action, ct, description, timeout);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _queue.CompleteAdding();
            while (_queue.TryTake(out var item))
            {
                item.Tcs.TrySetCanceled(item.CancellationToken.IsCancellationRequested ? item.CancellationToken : default);
            }
            _readCompletedSignal.Set();
            _readCompletedSignal.Dispose();
            _queue.Dispose();
        }
        catch { }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
