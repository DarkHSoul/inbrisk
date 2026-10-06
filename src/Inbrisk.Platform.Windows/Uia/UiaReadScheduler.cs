using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// Dedicated MTA-initialized parallel UIA read scheduler with process-scoped read lanes.
/// Ensures independent target processes do not block each other, isolates stuck providers,
/// enforces read/write consistency barriers, provides queue backpressure, and bounds poisoned workers.
/// </summary>
public sealed class UiaReadScheduler : IDisposable
{
    internal sealed class WorkerState
    {
        public Thread Thread { get; }
        public volatile bool IsAbandoned;
        public WorkerState(Thread thread) => Thread = thread;
    }

    internal sealed class ReadWorkItem
    {
        public Func<IUIAutomation?, object?> Fn { get; }
        public TaskCompletionSource<object?> Tcs { get; }
        public CancellationToken Ct { get; }
        public long DeadlineTicks { get; }
        public long EnqueuedTicks { get; }
        public string? IntentName { get; }
        public int Pid { get; }
        public WorkerState? ExecutingWorker { get; set; }

        public ReadWorkItem(
            Func<IUIAutomation?, object?> fn,
            TaskCompletionSource<object?> tcs,
            CancellationToken ct,
            long deadlineTicks,
            long enqueuedTicks,
            string? intentName,
            int pid)
        {
            Fn = fn;
            Tcs = tcs;
            Ct = ct;
            DeadlineTicks = deadlineTicks;
            EnqueuedTicks = enqueuedTicks;
            IntentName = intentName;
            Pid = pid;
        }
    }

    public sealed class ProcessReadLane : IDisposable
    {
        public int Pid { get; }
        internal BlockingCollection<ReadWorkItem> Queue { get; } = new(new ConcurrentQueue<ReadWorkItem>(), 64);
        public int ActiveReads;
        public int PendingWrites;
        public int ActiveWrites;
        public int MaxConcurrentWrites;
        public int ReadWriteOverlapCount;
        public int WriteWriteOverlapCount;
        public readonly object BarrierLock = new();
        internal readonly SemaphoreSlim WriteGate = new(1, 1);
        internal TaskCompletionSource? ReadsDrainedTcs;
        internal TaskCompletionSource? WritesDrainedTcs;
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
        public volatile bool IsDisposed;
        public volatile bool IsPoisoned;

        public ProcessReadLane(int pid)
        {
            Pid = pid;
        }

        public void Dispose()
        {
            IsDisposed = true;
            try
            {
                WriteGate.Dispose();
                Queue.CompleteAdding();
                while (Queue.TryTake(out var item))
                {
                    item.Tcs.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                }
            }
            catch { }
        }
    }

    private readonly object _lanesGate = new();
    private readonly ConcurrentDictionary<int, ProcessReadLane> _lanes = new();
    private readonly List<Thread> _globalWorkers = new();
    private readonly AutoResetEvent _workAvailable = new(false);
    private readonly Timer _idleEvictionTimer;
    private int _globalWorkerCount;
    private int _activeReadCount;
    private int _globalQueuedReads;
    private int _roundRobinIndex;
    private int _poisonedWorkerCount;
    private int _abandonedWorkerCount;
    private int _workerReplacementCount;
    private int _workerReplacementRejectedCount;
    private volatile bool _isDisposed;

    /// <summary>Bounds the mutation barrier's drain wait on in-flight
    /// reads. Reads are caller-bounded at ~10s (ScheduleReadAsync), but a
    /// timed-out read leaves its worker abandoned mid-COM-call and
    /// ActiveReads stays elevated until that call returns — possibly
    /// never. Past this bound a pending mutation proceeds anyway
    /// (overlap is recorded via ReadWriteOverlapCount); 15s covers the
    /// maximum legitimate read window plus margin.</summary>
    private const int ReadDrainTimeoutMs = 15_000;

    /// <summary>Bounds the mutation write-gate wait. A scope is
    /// legitimately held for a whole action chain (waits included), so
    /// this is generous — but a leaked or permanently stuck holder must
    /// surface as Busy, not hang every future mutation on the PID.</summary>
    private const int WriteGateTimeoutMs = 120_000;

    public int MaxConcurrencyPerProcess { get; set; } = 2;
    public int MaxGlobalWorkers { get; set; } = 8;
    public int MaxAbandonedWorkers { get; set; } = 8;
    public int MaxLanes { get; set; } = 128;
    public int MaxGlobalQueuedReads { get; set; } = 512;

    public int GlobalWorkerCount => Volatile.Read(ref _globalWorkerCount);
    public int ActiveLaneCount => _lanes.Count;
    public int GlobalQueuedReads => Volatile.Read(ref _globalQueuedReads);
    public int PoisonedWorkerCount => Volatile.Read(ref _poisonedWorkerCount);
    public int AbandonedWorkerCount => Volatile.Read(ref _abandonedWorkerCount);
    public int WorkerReplacementCount => Volatile.Read(ref _workerReplacementCount);
    public int WorkerReplacementRejectedCount => Volatile.Read(ref _workerReplacementRejectedCount);

    /// <summary>Optional custom factory for headless/synthetic tests.</summary>
    public Func<IUIAutomation?>? UiaFactory { get; set; }

    // Telemetry hooks
    public Action? OnReadQueued { get; set; }
    public Action? OnReadDequeued { get; set; }
    public Action? OnReadActiveStarted { get; set; }
    public Action? OnReadActiveFinished { get; set; }
    public Action<double>? OnReadQueueWaitRecorded { get; set; }
    public Action<double>? OnReadExecutionRecorded { get; set; }
    public Action? OnReadTimeout { get; set; }
    public Action? OnReadCancelled { get; set; }
    public Action<int>? OnLanePid { get; set; }
    public Action? OnWriteQueued { get; set; }
    public Action? OnWriteDequeued { get; set; }
    public Action<double>? OnWriteQueueWaitRecorded { get; set; }
    public Action<double>? OnWriteExecutionRecorded { get; set; }
    public Action? OnPoisonedWorker { get; set; }
    public Action? OnWorkerReplaced { get; set; }
    public Action? OnWorkerAbandoned { get; set; }
    public Action? OnWorkerReplacementRejected { get; set; }
    public Action? OnSyncOverAsyncFallback { get; set; }

    public UiaReadScheduler()
    {
        _idleEvictionTimer = new Timer(EvictIdleLanes, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public async Task<T> ScheduleReadAsync<T>(
        int pid,
        Func<IUIAutomation?, T> work,
        int timeoutMs = 10000,
        CancellationToken ct = default,
        string? intentName = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ct.ThrowIfCancellationRequested();

        if (AbandonedWorkerCount >= MaxAbandonedWorkers)
        {
            throw new InbriskException(ErrorCode.Busy,
                $"Worker pool exhausted: maximum abandoned workers reached ({MaxAbandonedWorkers})");
        }

        var lane = GetOrCreateLane(pid);
        EnsureGlobalWorkers();
        OnLanePid?.Invoke(pid);

        // Global queued reads bound
        if (Interlocked.Increment(ref _globalQueuedReads) > MaxGlobalQueuedReads)
        {
            Interlocked.Decrement(ref _globalQueuedReads);
            throw new InbriskException(ErrorCode.Busy,
                $"Global UIA read queue capacity reached ({MaxGlobalQueuedReads})");
        }

        // Read/Write barrier wait: if a mutation is active or pending on this PID, wait briefly
        await WaitForReadBarrierAsync(lane, ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueuedTicks = Stopwatch.GetTimestamp();
        var deadlineTicks = enqueuedTicks + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        var item = new ReadWorkItem(u => work(u), tcs, ct, deadlineTicks, enqueuedTicks, intentName, pid);

        OnReadQueued?.Invoke();
        try
        {
            if (!lane.Queue.TryAdd(item, 50, ct))
            {
                Interlocked.Decrement(ref _globalQueuedReads);
                OnReadDequeued?.Invoke();
                throw new InbriskException(ErrorCode.Busy,
                    $"UIA read queue saturated for PID {pid} (capacity: 64)");
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _globalQueuedReads);
            OnReadDequeued?.Invoke();
            OnReadCancelled?.Invoke();
            throw;
        }

        _workAvailable.Set();

        using var reg = ct.CanBeCanceled ? ct.UnsafeRegister(static state =>
        {
            var s = (TaskCompletionSource<object?>)state!;
            s.TrySetCanceled();
        }, tcs) : default;

        try
        {
            var result = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
            return (T)result!;
        }
        catch (TimeoutException)
        {
            OnReadTimeout?.Invoke();
            lane.IsPoisoned = true;

            // If a worker had picked up this item and is currently executing it:
            if (item.ExecutingWorker != null)
            {
                item.ExecutingWorker.IsAbandoned = true;
                Interlocked.Increment(ref _poisonedWorkerCount);
                Interlocked.Increment(ref _abandonedWorkerCount);
                OnPoisonedWorker?.Invoke();
                OnWorkerAbandoned?.Invoke();

                lock (_lanesGate)
                {
                    if (AbandonedWorkerCount <= MaxAbandonedWorkers)
                    {
                        Interlocked.Increment(ref _workerReplacementCount);
                        OnWorkerReplaced?.Invoke();
                        SpawnSingleWorker();
                    }
                    else
                    {
                        Interlocked.Increment(ref _workerReplacementRejectedCount);
                        OnWorkerReplacementRejected?.Invoke();
                    }
                }
            }

            throw new InbriskException(ErrorCode.Timeout,
                $"UIA read call for PID {pid} timed out after {timeoutMs}ms");
        }
        catch (OperationCanceledException)
        {
            OnReadCancelled?.Invoke();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _globalQueuedReads);
        }
    }

    public T ScheduleRead<T>(
        int pid,
        Func<IUIAutomation?, T> work,
        int timeoutMs = 10000,
        CancellationToken ct = default,
        string? intentName = null)
    {
        OnSyncOverAsyncFallback?.Invoke();
        return ScheduleReadAsync(pid, work, timeoutMs, ct, intentName)
            .GetAwaiter()
            .GetResult();
    }

    public Task<AsyncMutationScope> EnterMutationBarrierAsync(int pid, CancellationToken ct = default)
    {
        if (pid <= 0) return Task.FromResult(new AsyncMutationScope(this, pid, null, null));

        // Copy-on-write: isolate this async branch so sibling branches spawned from the same
        // parent context do not see this branch's Rank 2 acquisition.
        var parentCtx = Inbrisk.Core.LockOrderTracker.CurrentContext;
        var branchCtx = new Inbrisk.Core.LockContext { HeldProcessBarriers = parentCtx.HeldProcessBarriers + 1 };
        Inbrisk.Core.LockOrderTracker.CurrentContext = branchCtx;

        try
        {
            return EnterMutationBarrierCoreAsync(pid, branchCtx, parentCtx, ct);
        }
        catch
        {
            branchCtx.HeldProcessBarriers--;
            Inbrisk.Core.LockOrderTracker.CurrentContext = parentCtx;
            throw;
        }
    }

    private async Task<AsyncMutationScope> EnterMutationBarrierCoreAsync(
        int pid,
        Inbrisk.Core.LockContext branchCtx,
        Inbrisk.Core.LockContext parentCtx,
        CancellationToken ct)
    {
        var lane = GetOrCreateLane(pid);
        OnWriteQueued?.Invoke();
        var waitSw = Stopwatch.StartNew();

        Interlocked.Increment(ref lane.PendingWrites);

        try
        {
            // Bounded: a scope is legitimately held for a whole action
            // chain (incl. waits), so the bound is generous — but a leaked
            // or permanently-stuck holder must error as Busy, not hang
            // every future mutation on the PID forever (ct may be None via
            // NotifyMutationStarting's sync-over-async path).
            await lane.WriteGate.WaitAsync(TimeSpan.FromMilliseconds(WriteGateTimeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            branchCtx.HeldProcessBarriers--;
            Inbrisk.Core.LockOrderTracker.CurrentContext = parentCtx;
            Interlocked.Decrement(ref lane.PendingWrites);
            OnWriteDequeued?.Invoke();
            throw new InbriskException(ErrorCode.Busy,
                $"UIA mutation write-gate for PID {pid} did not free within {WriteGateTimeoutMs}ms");
        }
        catch
        {
            branchCtx.HeldProcessBarriers--;
            Inbrisk.Core.LockOrderTracker.CurrentContext = parentCtx;
            Interlocked.Decrement(ref lane.PendingWrites);
            OnWriteDequeued?.Invoke();
            throw;
        }

        Task? drainTask = null;
        lock (lane.BarrierLock)
        {
            if (lane.ActiveReads > 0)
            {
                lane.ReadsDrainedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drainTask = lane.ReadsDrainedTcs.Task;
            }
        }

        if (drainTask != null)
        {
            try
            {
                // Bounded: a timed-out read leaves its worker abandoned
                // mid-COM-call — ScheduleReadAsync poisons the lane, but
                // ActiveReads only drops when the worker's finally runs,
                // i.e. when the (possibly stuck) call returns. An
                // unbounded drain wait can therefore never converge and
                // hangs every mutation on this PID. Past the bound the
                // read is treated as abandoned and the write proceeds;
                // the overlap is recorded via ReadWriteOverlapCount below.
                await drainTask.WaitAsync(TimeSpan.FromMilliseconds(ReadDrainTimeoutMs), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { /* proceed — see note above */ }
            catch
            {
                branchCtx.HeldProcessBarriers--;
                Inbrisk.Core.LockOrderTracker.CurrentContext = parentCtx;
                lock (lane.BarrierLock)
                {
                    Interlocked.Decrement(ref lane.PendingWrites);
                    if (lane.ActiveWrites == 0 && lane.PendingWrites == 0)
                    {
                        lane.WritesDrainedTcs?.TrySetResult();
                        lane.WritesDrainedTcs = null;
                    }
                }
                lane.WriteGate.Release();
                OnWriteDequeued?.Invoke();
                throw;
            }
        }

        lock (lane.BarrierLock)
        {
            Interlocked.Decrement(ref lane.PendingWrites);
            lane.ActiveWrites++;
            if (lane.ActiveWrites > lane.MaxConcurrentWrites)
                lane.MaxConcurrentWrites = lane.ActiveWrites;

            if (lane.ActiveReads > 0)
                Interlocked.Increment(ref lane.ReadWriteOverlapCount);
            if (lane.ActiveWrites > 1)
                Interlocked.Increment(ref lane.WriteWriteOverlapCount);

            OnWriteDequeued?.Invoke();
            OnWriteQueueWaitRecorded?.Invoke(waitSw.ElapsedMilliseconds);
        }

        return new AsyncMutationScope(this, pid, branchCtx, parentCtx);
    }

    public void ReleaseMutationBarrier(int pid)
    {
        if (pid <= 0) return;
        if (_lanes.TryGetValue(pid, out var lane))
        {
            lock (lane.BarrierLock)
            {
                if (lane.ActiveWrites > 0)
                {
                    lane.ActiveWrites--;
                }
                if (lane.ActiveWrites == 0 && lane.PendingWrites == 0)
                {
                    lane.WritesDrainedTcs?.TrySetResult();
                    lane.WritesDrainedTcs = null;
                }
                Monitor.PulseAll(lane.BarrierLock);
            }
            lane.WriteGate.Release();
            _workAvailable.Set();
        }
    }

    public void NotifyMutationStarting(int pid)
    {
        OnSyncOverAsyncFallback?.Invoke();
        EnterMutationBarrierAsync(pid).GetAwaiter().GetResult();
    }

    public void NotifyMutationCompleted(int pid)
    {
        ReleaseMutationBarrier(pid);
    }

    public IDisposable EnterMutationBarrier(int pid)
    {
        OnSyncOverAsyncFallback?.Invoke();
        return EnterMutationBarrierAsync(pid).GetAwaiter().GetResult();
    }

    public sealed class AsyncMutationScope : IAsyncDisposable, IDisposable
    {
        private readonly UiaReadScheduler _scheduler;
        private readonly int _pid;
        private readonly Inbrisk.Core.LockContext? _branchCtx;
        private readonly Inbrisk.Core.LockContext? _parentCtx;
        private int _disposed;

        public AsyncMutationScope(
            UiaReadScheduler scheduler,
            int pid,
            Inbrisk.Core.LockContext? branchCtx,
            Inbrisk.Core.LockContext? parentCtx)
        {
            _scheduler = scheduler;
            _pid = pid;
            _branchCtx = branchCtx;
            _parentCtx = parentCtx;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                if (_branchCtx != null)
                {
                    _branchCtx.HeldProcessBarriers--;
                }
                if (_parentCtx != null)
                {
                    Inbrisk.Core.LockOrderTracker.CurrentContext = _parentCtx;
                }
                _scheduler.ReleaseMutationBarrier(_pid);
            }
        }
    }

    private async Task WaitForReadBarrierAsync(ProcessReadLane lane, CancellationToken ct)
    {
        Task? waitTask = null;
        lock (lane.BarrierLock)
        {
            if (lane.ActiveWrites > 0 || lane.PendingWrites > 0)
            {
                lane.WritesDrainedTcs ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTask = lane.WritesDrainedTcs.Task;
            }
        }
        if (waitTask != null)
        {
            try
            {
                await waitTask.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
        }
    }

    public ProcessReadLane GetOrCreateLane(int pid)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (pid <= 0)
            throw new ArgumentOutOfRangeException(nameof(pid), "Process ID must be greater than zero.");

        if (_lanes.TryGetValue(pid, out var existing))
            return existing;

        if (_lanes.Count >= MaxLanes)
        {
            EvictIdleLanes(null);
            if (_lanes.Count >= MaxLanes && !_lanes.ContainsKey(pid))
            {
                throw new InbriskException(ErrorCode.Busy,
                    $"Maximum process read lanes limit reached ({MaxLanes})");
            }
        }

        return _lanes.GetOrAdd(pid, p => new ProcessReadLane(p));
    }

    private void EnsureGlobalWorkers()
    {
        if (_globalWorkers.Count >= MaxGlobalWorkers) return;
        lock (_lanesGate)
        {
            while (_globalWorkers.Count < MaxGlobalWorkers)
            {
                SpawnSingleWorker();
            }
        }
    }

    private void SpawnSingleWorker()
    {
        var idx = _globalWorkers.Count + 1;
        Interlocked.Increment(ref _globalWorkerCount);
        var t = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = $"inbrisk-uia-read-worker-{idx}"
        };
        t.SetApartmentState(ApartmentState.MTA);
        _globalWorkers.Add(t);
        t.Start();
    }

    private void WorkerLoop()
    {
        Native.DesktopBridge.TrySwitchCurrentThread();
        var workerState = new WorkerState(Thread.CurrentThread);
        IUIAutomation? uia = null;
        try
        {
            if (UiaFactory != null)
            {
                uia = UiaFactory();
            }
            else
            {
                try { uia = (IUIAutomation)new CUIAutomation8Class(); }
                catch { uia = (IUIAutomation)new CUIAutomationClass(); }
            }
        }
        catch
        {
            // In headless/test environments where COM is unavailable, allow uia to be null
        }

        try
        {
            while (!_isDisposed)
            {
                ReadWorkItem? item = null;
                ProcessReadLane? assignedLane = null;

                // Round-robin selection across eligible lanes
                var lanes = _lanes.Values.ToArray();
                if (lanes.Length > 0)
                {
                    var startIdx = (uint)Interlocked.Increment(ref _roundRobinIndex) % (uint)lanes.Length;
                    for (var i = 0; i < lanes.Length; i++)
                    {
                        var lane = lanes[(startIdx + i) % lanes.Length];
                        if (lane.IsDisposed) continue;

                        lock (lane.BarrierLock)
                        {
                            if (lane.ActiveWrites > 0 || lane.PendingWrites > 0)
                                continue;

                            if (lane.ActiveReads >= MaxConcurrencyPerProcess)
                                continue;

                            if (lane.Queue.TryTake(out item))
                            {
                                assignedLane = lane;
                                lane.ActiveReads++;
                                if (lane.ActiveWrites > 0)
                                    lane.ReadWriteOverlapCount++;
                                break;
                            }
                        }
                    }
                }

                if (item == null || assignedLane == null)
                {
                    _workAvailable.WaitOne(50);
                    continue;
                }

                OnReadDequeued?.Invoke();
                assignedLane.LastActivity = DateTimeOffset.UtcNow;

                var queueWaitMs = (Stopwatch.GetTimestamp() - item.EnqueuedTicks) * 1000.0 / Stopwatch.Frequency;
                OnReadQueueWaitRecorded?.Invoke(queueWaitMs);

                // Pre-execution deadline & cancellation check
                if (item.Ct.IsCancellationRequested || Stopwatch.GetTimestamp() > item.DeadlineTicks)
                {
                    item.Tcs.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                    OnReadCancelled?.Invoke();
                    lock (assignedLane.BarrierLock)
                    {
                        Interlocked.Decrement(ref assignedLane.ActiveReads);
                        if (assignedLane.ActiveReads == 0)
                        {
                            assignedLane.ReadsDrainedTcs?.TrySetResult();
                            assignedLane.ReadsDrainedTcs = null;
                            Monitor.PulseAll(assignedLane.BarrierLock);
                        }
                    }
                    _workAvailable.Set();
                    continue;
                }

                Interlocked.Increment(ref _activeReadCount);
                OnReadActiveStarted?.Invoke();
                var sw = Stopwatch.StartNew();

                item.ExecutingWorker = workerState;

                try
                {
                    if (item.Ct.IsCancellationRequested)
                    {
                        item.Tcs.TrySetCanceled(item.Ct);
                        OnReadCancelled?.Invoke();
                        continue;
                    }

                    var result = item.Fn(uia);
                    item.Tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
                finally
                {
                    sw.Stop();
                    OnReadExecutionRecorded?.Invoke(sw.Elapsed.TotalMilliseconds);
                    OnReadActiveFinished?.Invoke();
                    Interlocked.Decrement(ref _activeReadCount);
                    lock (assignedLane.BarrierLock)
                    {
                        Interlocked.Decrement(ref assignedLane.ActiveReads);
                        if (assignedLane.ActiveReads == 0)
                        {
                            assignedLane.ReadsDrainedTcs?.TrySetResult();
                            assignedLane.ReadsDrainedTcs = null;
                            Monitor.PulseAll(assignedLane.BarrierLock);
                        }
                    }
                    assignedLane.LastActivity = DateTimeOffset.UtcNow;
                    _workAvailable.Set();
                }

                // If this worker was abandoned while in a timed-out call, terminate the thread
                if (workerState.IsAbandoned)
                {
                    Interlocked.Decrement(ref _abandonedWorkerCount);
                    return;
                }
            }
        }
        catch { }
        finally
        {
            Interlocked.Decrement(ref _globalWorkerCount);
        }
    }

    private void EvictIdleLanes(object? state)
    {
        // Timer boundary: an unhandled timer-callback exception kills the
        // process — eviction is best-effort, next tick retries.
        try
        {
            EvictIdleLanesCore();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"inbrisk UIA lane eviction failed: {e}");
        }
    }

    private void EvictIdleLanesCore()
    {
        if (_isDisposed) return;
        var now = DateTimeOffset.UtcNow;
        var idleThreshold = TimeSpan.FromSeconds(30);

        foreach (var kvp in _lanes)
        {
            var lane = kvp.Value;
            if (lane.Queue.Count == 0 &&
                Volatile.Read(ref lane.ActiveReads) == 0 &&
                Volatile.Read(ref lane.ActiveWrites) == 0 &&
                Volatile.Read(ref lane.PendingWrites) == 0 &&
                (now - lane.LastActivity) > idleThreshold)
            {
                if (_lanes.TryRemove(kvp.Key, out var removedLane))
                {
                    removedLane.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try { _idleEvictionTimer.Dispose(); } catch { }
        _workAvailable.Set();

        foreach (var lane in _lanes.Values)
        {
            lane.Dispose();
        }
        _lanes.Clear();

        lock (_lanesGate)
        {
            foreach (var w in _globalWorkers)
            {
                try
                {
                    if (w.IsAlive) w.Join(50);
                }
                catch { }
            }
            _globalWorkers.Clear();
        }
    }
}
