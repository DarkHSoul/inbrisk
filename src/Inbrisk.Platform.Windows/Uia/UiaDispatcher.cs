using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// All UIA COM calls run on a single dedicated MTA thread. Cross-process UIA
/// calls can block indefinitely when a target app hangs, so every call is
/// awaited with a timeout; a timed-out call poisons the dispatcher, which
/// restarts on a fresh thread (the stuck COM call is abandoned, never joined).
/// </summary>
public sealed class UiaDispatcher : IDisposable
{
    private sealed record WorkItem(
        Func<IUIAutomation, CancellationToken, object?> Fn,
        TaskCompletionSource<object?> Done,
        CancellationToken Ct,
        long Generation,
        long DeadlineTicks,
        string? IntentName)
    {
        /// <summary>
        /// Cancelled the moment the caller stops waiting (timeout, cancellation,
        /// purge) while this item may still be executing on an abandoned lane.
        /// The lane checks <see cref="IsAbandoned"/> before applying the result,
        /// so a late completion can never reply; cooperative callees observe
        /// <see cref="AbandonToken"/> and skip their side effects entirely.
        /// </summary>
        private readonly CancellationTokenSource _abandoned = new();
        public CancellationToken AbandonToken => _abandoned.Token;
        public bool IsAbandoned => _abandoned.IsCancellationRequested;
        public void Abandon() => _abandoned.Cancel();
    }

    private readonly object _gate = new();
    private BlockingCollection<WorkItem> _queue = new();
    private Thread? _thread;
    private volatile bool _poisoned;
    private volatile bool _disposed;
    private long _generation = 1;

    public int DefaultTimeoutMs { get; set; } = 10000;
    public long CurrentGeneration => Volatile.Read(ref _generation);
    public int QueuedCount => _queue.Count;

    public Task<T> RunAsync<T>(Func<IUIAutomation, T> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null) =>
        RunAsyncCore<T>((u, _) => fn(u), timeoutMs, ct, intentName);

    /// <summary>
    /// Abandon-aware variant: <paramref name="fn"/> receives a token that is
    /// cancelled if the caller's wait expires while the work is still running
    /// on the abandoned lane — check it before applying side effects.
    /// </summary>
    public Task<T> RunAsync<T>(Func<IUIAutomation, CancellationToken, T> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null) =>
        RunAsyncCore<T>(fn, timeoutMs, ct, intentName);

    private async Task<T> RunAsyncCore<T>(Func<IUIAutomation, CancellationToken, T> fn,
        int? timeoutMs, CancellationToken ct, string? intentName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = timeoutMs ?? DefaultTimeoutMs;
        var deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeout * (Stopwatch.Frequency / 1000.0));
        WorkItem item;
        lock (_gate)
        {
            if (_poisoned) Restart();
            EnsureThread();
            item = new WorkItem((u, aband) => fn(u, aband), tcs, ct, _generation, deadlineTicks, intentName);
            _queue.Add(item);
        }

        using var reg = ct.CanBeCanceled ? ct.UnsafeRegister(static state =>
        {
            var s = (TaskCompletionSource<object?>)state!;
            s.TrySetCanceled();
        }, tcs) : default;

        try
        {
            var res = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeout), ct).ConfigureAwait(false);
            return (T)res!;
        }
        catch (TimeoutException)
        {
            // The abandoned thread may still complete this item late — mark it
            // inert so the late result cannot reply and cooperative side effects
            // are skipped; the lane itself is poisoned and restarts.
            item.Abandon();
            lock (_gate) _poisoned = true;
            throw new InbriskException(ErrorCode.Timeout,
                $"UIA call timed out after {timeout}ms (target app unresponsive?)");
        }
        catch (OperationCanceledException)
        {
            item.Abandon();
            throw;
        }
    }

    public T Run<T>(Func<IUIAutomation, T> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null) =>
        RunAsync(fn, timeoutMs, ct, intentName).GetAwaiter().GetResult();

    /// <summary>Abandon-aware variant: the delegate also receives the item's abandon token.</summary>
    public T Run<T>(Func<IUIAutomation, CancellationToken, T> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null) =>
        RunAsync(fn, timeoutMs, ct, intentName).GetAwaiter().GetResult();

    /// <summary>Fire-and-forget variant that still respects the dispatch thread.</summary>
    public void Run(Action<IUIAutomation> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null) =>
        Run<object?>(u => { fn(u); return null; }, timeoutMs, ct, intentName);

    /// <summary>
    /// Purge any pending work items in the queue immediately (e.g. on emergency stop
    /// or request cancellation). Work items waiting to run are cancelled without executing COM calls.
    /// </summary>
    public void PurgeQueue()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            while (_queue.TryTake(out var item))
            {
                item.Abandon();
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
            }
        }
    }

    private void EnsureThread()
    {
        if (_thread is { IsAlive: true }) return;
        _thread = new Thread(Loop) { IsBackground = true, Name = "inbrisk-uia" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Restart()
    {
        // The old thread may be stuck inside a COM call; abandon it and its
        // queue, spin a fresh one. Items still queued are lost — callers retry.
        Interlocked.Increment(ref _generation);
        var old = _queue;
        _queue = new BlockingCollection<WorkItem>();
        while (old.TryTake(out var item))
        {
            item.Abandon();
            item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
        }
        // Close the abandoned queue so the stuck thread exits once its in-flight
        // COM call returns, instead of parking forever in GetConsumingEnumerable.
        old.CompleteAdding();
        _thread = null;
        _poisoned = false;
        EnsureThread();
    }

    private void Loop()
    {
        // Boundary: this is a bare worker thread — nothing may escape or the
        // process dies. The COM-provisioning fallback and per-item dispatch
        // already catch; this outer catch is the last resort.
        try
        {
            LoopCore();
        }
        catch (Exception e)
        {
            Debug.WriteLine($"inbrisk-uia dispatcher loop exited: {e}");
        }
    }

    private void LoopCore()
    {
        Inbrisk.Platform.Windows.Native.DesktopBridge.TrySwitchCurrentThread();
        IUIAutomation uia;
        try
        {
            // UIA3 (Windows 8+). Falls back to legacy CUIAutomation on old OSes.
            uia = (IUIAutomation)new CUIAutomation8Class();
        }
        catch
        {
            try { uia = (IUIAutomation)new CUIAutomationClass(); }
            catch
            {
                Debug.WriteLine("inbrisk-uia: no UIA automation available — dispatcher lane exiting");
                return; // no UIA at all — exit the lane quietly
            }
        }

        var queue = _queue; // capture current queue; restarts swap the field
        foreach (var item in queue.GetConsumingEnumerable())
        {
            // 1. Generation check: if generation changed (purge or restart), drop without executing
            if (item.Generation != Volatile.Read(ref _generation))
            {
                item.Abandon();
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                continue;
            }

            // 2. Cancellation, abandon and deadline check before touching COM
            if (item.Ct.IsCancellationRequested || item.IsAbandoned || Stopwatch.GetTimestamp() > item.DeadlineTicks)
            {
                item.Abandon();
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                continue;
            }

            try
            {
                // 3. Re-check cancellation right before dispatching
                if (item.Ct.IsCancellationRequested)
                {
                    item.Abandon();
                    item.Done.TrySetCanceled(item.Ct);
                    continue;
                }
                var result = item.Fn(uia, item.AbandonToken);
                // 4. Late-completion guard: the caller timed out (or cancelled)
                // while this item was executing on the abandoned lane — its
                // result is inert and must never reach the caller's task.
                if (item.IsAbandoned) continue;
                item.Done.TrySetResult(result);
            }
            catch (Exception e)
            {
                if (item.IsAbandoned) continue; // late fault on abandoned work — drop it
                item.Done.TrySetException(e);
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        PurgeQueue();
        lock (_gate) _queue.CompleteAdding();
    }
}
