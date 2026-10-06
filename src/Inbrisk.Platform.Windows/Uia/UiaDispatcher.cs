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
        Func<IUIAutomation, object?> Fn,
        TaskCompletionSource<object?> Done,
        CancellationToken Ct,
        long Generation,
        long DeadlineTicks,
        string? IntentName);

    private readonly object _gate = new();
    private BlockingCollection<WorkItem> _queue = new();
    private Thread? _thread;
    private volatile bool _poisoned;
    private volatile bool _disposed;
    private long _generation = 1;

    public int DefaultTimeoutMs { get; set; } = 10000;
    public long CurrentGeneration => Volatile.Read(ref _generation);
    public int QueuedCount => _queue.Count;

    public async Task<T> RunAsync<T>(Func<IUIAutomation, T> fn, int? timeoutMs = null,
        CancellationToken ct = default, string? intentName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timeout = timeoutMs ?? DefaultTimeoutMs;
        var deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeout * (Stopwatch.Frequency / 1000.0));
        lock (_gate)
        {
            if (_poisoned) Restart();
            EnsureThread();
            _queue.Add(new WorkItem(u => fn(u), tcs, ct, _generation, deadlineTicks, intentName));
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
            lock (_gate) _poisoned = true;
            throw new InbriskException(ErrorCode.Timeout,
                $"UIA call timed out after {timeout}ms (target app unresponsive?)");
        }
    }

    public T Run<T>(Func<IUIAutomation, T> fn, int? timeoutMs = null,
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
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
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
            item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
        _thread = null;
        _poisoned = false;
        EnsureThread();
    }

    private void Loop()
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
            uia = (IUIAutomation)new CUIAutomationClass();
        }

        var queue = _queue; // capture current queue; restarts swap the field
        foreach (var item in queue.GetConsumingEnumerable())
        {
            // 1. Generation check: if generation changed (purge or restart), drop without executing
            if (item.Generation != Volatile.Read(ref _generation))
            {
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                continue;
            }

            // 2. Cancellation and deadline check before touching COM
            if (item.Ct.IsCancellationRequested || Stopwatch.GetTimestamp() > item.DeadlineTicks)
            {
                item.Done.TrySetCanceled(item.Ct.IsCancellationRequested ? item.Ct : default);
                continue;
            }

            try
            {
                // 3. Re-check cancellation right before dispatching
                if (item.Ct.IsCancellationRequested)
                {
                    item.Done.TrySetCanceled(item.Ct);
                    continue;
                }
                item.Done.TrySetResult(item.Fn(uia));
            }
            catch (Exception e)
            {
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
