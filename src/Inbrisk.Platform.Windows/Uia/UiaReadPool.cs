using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// Bounded parallel read pool for semantically read-only UIA/system inspection requests
/// (window enumeration, metadata, inspect, find, etc.).
/// 
/// Design Rationale & Invariants:
/// 1. Dedicated MTA worker threads: Cross-thread COM access in UIAutomation causes marshaling overhead,
///    deadlocks, or RPC_E_WRONG_THREAD. Each worker thread in this pool initializes its own COM context
///    and owns a dedicated IUIAutomation instance that never escapes its owning thread.
/// 2. Explicitly bounded concurrency: Configurable worker count (default 2-4 readers) prevents UIA provider
///    thread-pool exhaustion on target desktop processes.
/// 3. Bounded queue with pre-execution cancellation: If a queued read is cancelled before an MTA worker starts
///    executing it, the work delegate is skipped completely to conserve system resources.
/// 4. Deterministic output ordering: When parallel reads are gathered into an aggregate result (GatherAsync),
///    the results strictly preserve caller input index ordering regardless of completion order.
/// 5. Starvation-free reader-writer fairness: Integrates with SerialMutationLane so continuous read traffic
///    never starves mutations, and continuous mutation traffic never permanently starves reads.
/// </summary>
public sealed class UiaReadPool : IDisposable, IAsyncDisposable
{
    internal sealed class ReadWorkItem
    {
        public Func<IUIAutomation?, object?> Work { get; }
        public TaskCompletionSource<object?> Tcs { get; }
        public CancellationToken CancellationToken { get; }
        public long EnqueuedTicks { get; }
        public string? IntentName { get; }

        public ReadWorkItem(
            Func<IUIAutomation?, object?> work,
            TaskCompletionSource<object?> tcs,
            CancellationToken ct,
            string? intentName)
        {
            Work = work;
            Tcs = tcs;
            CancellationToken = ct;
            EnqueuedTicks = Stopwatch.GetTimestamp();
            IntentName = intentName;
        }
    }

    private readonly object _gate = new();
    private readonly int _maxConcurrency;
    private readonly int _maxQueueCapacity;
    private readonly Func<IUIAutomation?>? _uiaFactory;
    private readonly BlockingCollection<ReadWorkItem> _queue;
    private readonly List<Thread> _workerThreads = new();
    private readonly List<int> _workerThreadIds = new();

    private volatile bool _disposed;
    private int _activeReadCount;
    private int _peakConcurrency;
    private long _completedCount;
    private long _cancelledCount;

    // Fairness coordination with SerialMutationLane
    private SerialMutationLane? _mutationLane;
    private readonly object _fairnessLock = new();
    private volatile bool _readPausedForMutation;
    private volatile bool _readWindowForced;
    private int _pendingReaderCount;
    private readonly ManualResetEventSlim _readAllowedEvent = new(true);
    private readonly ManualResetEventSlim _readsDrainedEvent = new(true);

    public int MaxConcurrency => _maxConcurrency;
    public int MaxQueueCapacity => _maxQueueCapacity;
    public int ActiveReadCount => Volatile.Read(ref _activeReadCount);
    public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);
    public int QueuedCount => _queue.Count;
    public long CompletedCount => Interlocked.Read(ref _completedCount);
    public long CancelledCount => Interlocked.Read(ref _cancelledCount);
    public int PendingReaderCount => Volatile.Read(ref _pendingReaderCount);

    public IReadOnlyList<int> WorkerThreadIds
    {
        get
        {
            lock (_gate)
            {
                return _workerThreadIds.ToArray();
            }
        }
    }

    public UiaReadPool(
        int maxConcurrency = 4,
        int maxQueueCapacity = 128,
        Func<IUIAutomation?>? uiaFactory = null,
        SerialMutationLane? mutationLane = null)
    {
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), "Concurrency must be at least 1.");
        if (maxQueueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxQueueCapacity), "Queue capacity must be at least 1.");

        _maxConcurrency = maxConcurrency;
        _maxQueueCapacity = maxQueueCapacity;
        _uiaFactory = uiaFactory;
        _queue = new BlockingCollection<ReadWorkItem>(new ConcurrentQueue<ReadWorkItem>(), _maxQueueCapacity);

        if (mutationLane != null)
        {
            LinkMutationLane(mutationLane);
        }

        SpawnWorkers();
    }

    public void LinkMutationLane(SerialMutationLane lane)
    {
        lock (_fairnessLock)
        {
            _mutationLane = lane;
            lane.LinkReadPool(this);
        }
    }

    private void SpawnWorkers()
    {
        lock (_gate)
        {
            for (var i = 0; i < _maxConcurrency; i++)
            {
                var workerIndex = i + 1;
                var thread = new Thread(WorkerLoop)
                {
                    Name = $"inbrisk-readpool-{workerIndex}",
                    IsBackground = true
                };
                thread.SetApartmentState(ApartmentState.MTA);
                _workerThreads.Add(thread);
                thread.Start();
            }
        }
    }

    private void WorkerLoop()
    {
        lock (_gate)
        {
            _workerThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
        }

        Native.DesktopBridge.TrySwitchCurrentThread();

        // Own dedicated COM instance created strictly on this MTA thread
        IUIAutomation? uia = null;
        try
        {
            if (_uiaFactory != null)
            {
                uia = _uiaFactory();
            }
            else
            {
                try { uia = (IUIAutomation)new CUIAutomation8Class(); }
                catch { uia = (IUIAutomation)new CUIAutomationClass(); }
            }
        }
        catch
        {
            // In headless/test environments where UIA COM is unregistered, uia can be null
        }

        try
        {
            foreach (var item in _queue.GetConsumingEnumerable())
            {
                if (_disposed) break;

                // 1. Cancellation check before starting: if cancelled in queue, skip completely
                if (item.CancellationToken.IsCancellationRequested || item.Tcs.Task.IsCanceled)
                {
                    Interlocked.Increment(ref _cancelledCount);
                    item.Tcs.TrySetCanceled(item.CancellationToken.IsCancellationRequested ? item.CancellationToken : default);
                    continue;
                }

                // 2. Fairness gate: Wait if reads are paused for a waiting mutation,
                // unless a fair read window has been opened to prevent read starvation.
                WaitForReadPermission(item.CancellationToken);

                if (item.CancellationToken.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _cancelledCount);
                    item.Tcs.TrySetCanceled(item.CancellationToken);
                    continue;
                }

                // 3. Mark read active and notify barrier
                int currentActive;
                lock (_fairnessLock)
                {
                    currentActive = Interlocked.Increment(ref _activeReadCount);
                    _readsDrainedEvent.Reset();
                }

                // Track peak concurrency
                int currentPeak;
                do
                {
                    currentPeak = Volatile.Read(ref _peakConcurrency);
                    if (currentActive <= currentPeak) break;
                } while (Interlocked.CompareExchange(ref _peakConcurrency, currentActive, currentPeak) != currentPeak);

                try
                {
                    // Re-check cancellation right before invoking delegate
                    if (item.CancellationToken.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _cancelledCount);
                        item.Tcs.TrySetCanceled(item.CancellationToken);
                        continue;
                    }

                    var result = item.Work(uia);
                    Interlocked.Increment(ref _completedCount);
                    item.Tcs.TrySetResult(result);
                }
                catch (OperationCanceledException oce)
                {
                    Interlocked.Increment(ref _cancelledCount);
                    item.Tcs.TrySetCanceled(oce.CancellationToken);
                }
                catch (Exception ex)
                {
                    item.Tcs.TrySetException(ex);
                }
                finally
                {
                    lock (_fairnessLock)
                    {
                        var remaining = Interlocked.Decrement(ref _activeReadCount);
                        if (remaining == 0)
                        {
                            _readsDrainedEvent.Set();
                        }
                    }

                    // Notify mutation lane that a read turn completed for fair scheduling
                    _mutationLane?.OnReadCompleted();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Normal shutdown
        }
        catch (Exception e)
        {
            // Boundary: a worker thread must never take the process down —
            // the pool loses one lane but the server stays alive.
            Debug.WriteLine($"inbrisk read-pool worker exited: {e}");
        }
    }

    private void WaitForReadPermission(CancellationToken ct)
    {
        while (!_disposed)
        {
            bool shouldWait;
            lock (_fairnessLock)
            {
                shouldWait = _readPausedForMutation && !_readWindowForced;
            }

            if (!shouldWait)
                break;

            try
            {
                _readAllowedEvent.Wait(20, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Called by SerialMutationLane when a mutation is queued/pending to prevent continuous reads
    /// from starving the mutation.
    /// </summary>
    internal void PauseReadsForMutation()
    {
        lock (_fairnessLock)
        {
            _readPausedForMutation = true;
            if (!_readWindowForced)
            {
                _readAllowedEvent.Reset();
            }
        }
    }

    /// <summary>
    /// Called by SerialMutationLane when mutation completes to resume read traffic.
    /// </summary>
    internal void ResumeReadsAfterMutation()
    {
        lock (_fairnessLock)
        {
            _readPausedForMutation = false;
            _readAllowedEvent.Set();
        }
    }

    /// <summary>
    /// Called by SerialMutationLane when consecutive mutation threshold is reached
    /// to grant waiting reads a fair turn, preventing permanent read starvation.
    /// </summary>
    internal void OpenFairReadWindow()
    {
        lock (_fairnessLock)
        {
            _readWindowForced = true;
            _readAllowedEvent.Set();
        }
    }

    /// <summary>
    /// Closes the fair read window once waiting reads have made progress.
    /// </summary>
    internal void CloseFairReadWindow()
    {
        lock (_fairnessLock)
        {
            _readWindowForced = false;
            if (_readPausedForMutation)
            {
                _readAllowedEvent.Reset();
            }
        }
    }

    /// <summary>
    /// Waits until all currently active reads have drained. Used by SerialMutationLane before mutating.
    /// </summary>
    internal async Task WaitForActiveReadsDrainedAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _activeReadCount) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (sw.Elapsed >= timeout)
                throw new TimeoutException($"Timed out after {timeout.TotalMilliseconds}ms waiting for active reads to drain.");

            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a read action on one of the dedicated MTA worker threads.
    /// </summary>
    public async Task<T> RunAsync<T>(
        Func<IUIAutomation?, T> work,
        int? timeoutMs = null,
        CancellationToken ct = default,
        string? intentName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new ReadWorkItem(u => work(u), tcs, ct, intentName);

        Interlocked.Increment(ref _pendingReaderCount);

        try
        {
            // Bounded queue backpressure: Wait briefly to add to queue, throws InbriskException if full
            if (!_queue.TryAdd(item, 100, ct))
            {
                throw new InbriskException(ErrorCode.Busy,
                    $"UIA read pool queue saturated (capacity: {_maxQueueCapacity})");
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _pendingReaderCount);
            throw;
        }

        using var reg = ct.CanBeCanceled ? ct.UnsafeRegister(static state =>
        {
            var s = (TaskCompletionSource<object?>)state!;
            s.TrySetCanceled();
        }, tcs) : default;

        try
        {
            var timeout = timeoutMs.HasValue ? TimeSpan.FromMilliseconds(timeoutMs.Value) : Timeout.InfiniteTimeSpan;
            object? result;
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                result = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            else
            {
                result = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }

            return (T)result!;
        }
        catch (TimeoutException)
        {
            throw new InbriskException(ErrorCode.Timeout,
                $"UIA read timed out after {timeoutMs}ms");
        }
        finally
        {
            Interlocked.Decrement(ref _pendingReaderCount);
        }
    }

    /// <summary>
    /// Runs a read action without return value.
    /// </summary>
    public Task RunAsync(
        Action<IUIAutomation?> work,
        int? timeoutMs = null,
        CancellationToken ct = default,
        string? intentName = null) =>
        RunAsync<object?>(u =>
        {
            work(u);
            return null;
        }, timeoutMs, ct, intentName);

    /// <summary>
    /// Gathers results from parallel reads across items, guaranteeing that the returned
    /// output list preserves strict input index order regardless of execution completion order.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> GatherAsync<TInput, TResult>(
        IEnumerable<TInput> items,
        Func<IUIAutomation?, TInput, TResult> work,
        CancellationToken ct = default,
        int? timeoutMs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var inputArray = items.ToArray();
        if (inputArray.Length == 0)
            return Array.Empty<TResult>();

        var results = new TResult[inputArray.Length];
        var tasks = new Task[inputArray.Length];

        for (var i = 0; i < inputArray.Length; i++)
        {
            var index = i;
            var input = inputArray[i];
            tasks[i] = RunAsync(uia =>
            {
                var res = work(uia, input);
                results[index] = res;
                return res;
            }, timeoutMs, ct, intentName: $"Gather[{index}]");
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Gathers results from parallel read delegates, guaranteeing deterministic output ordering.
    /// </summary>
    public async Task<IReadOnlyList<TResult>> GatherAsync<TResult>(
        IEnumerable<Func<IUIAutomation?, TResult>> workItems,
        CancellationToken ct = default,
        int? timeoutMs = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workArray = workItems.ToArray();
        if (workArray.Length == 0)
            return Array.Empty<TResult>();

        var results = new TResult[workArray.Length];
        var tasks = new Task[workArray.Length];

        for (var i = 0; i < workArray.Length; i++)
        {
            var index = i;
            var work = workArray[i];
            tasks[i] = RunAsync(uia =>
            {
                var res = work(uia);
                results[index] = res;
                return res;
            }, timeoutMs, ct, intentName: $"Gather[{index}]");
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _queue.CompleteAdding();
            while (_queue.TryTake(out var item))
            {
                Interlocked.Increment(ref _cancelledCount);
                item.Tcs.TrySetCanceled(item.CancellationToken.IsCancellationRequested ? item.CancellationToken : default);
            }
            _readAllowedEvent.Set();
            _readsDrainedEvent.Set();
            _readAllowedEvent.Dispose();
            _readsDrainedEvent.Dispose();
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
