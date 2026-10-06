using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>
/// Session-lifetime holder for IVirtualDesktopManager COM instance.
/// Option A: Controlled MTA Owner Architecture.
/// All COM object creation, method calls, failure invalidation/recreation,
/// and disposal execute strictly on a single dedicated MTA lane.
/// Callers from arbitrary threads (STA or MTA) have their calls marshaled
/// to this lane without cross-apartment affinity errors.
/// </summary>
public sealed class VirtualDesktopManagerHolder : IDisposable
{
    private sealed record WorkItem(Func<IVirtualDesktopManager?, object?> Action, TaskCompletionSource<object?> Tcs);

    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly Thread _mtaThread;
    private readonly object _lock = new();
    private IVirtualDesktopManager? _vdm;
    private MtaBoundVirtualDesktopManagerProxy? _activeProxy;
    private Func<IVirtualDesktopManager?> _factory;
    private int _creationCount;
    private volatile bool _disposed;

    public int CreationCount => Volatile.Read(ref _creationCount);
    public int MtaThreadId => _mtaThread.ManagedThreadId;
    public int CreationThreadId { get; private set; }
    public int LastExecutionThreadId { get; private set; }
    public ApartmentState LastApartmentState { get; private set; }
    public int DisposalThreadId { get; private set; }

    public VirtualDesktopManagerHolder(Func<IVirtualDesktopManager?>? factory = null)
    {
        _factory = factory ?? (() => (IVirtualDesktopManager)new VirtualDesktopManagerCom());
        _mtaThread = new Thread(ProcessQueue)
        {
            IsBackground = true,
            Name = "Inbrisk.VirtualDesktopManager.MtaLane"
        };
        _mtaThread.SetApartmentState(ApartmentState.MTA);
        _mtaThread.Start();
    }

    private void ProcessQueue()
    {
        // Boundary: bare MTA worker thread — nothing may escape or the
        // process dies; per-item faults are already routed to each Tcs.
        try
        {
            ProcessQueueCore();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"inbrisk VDM MTA lane exited: {e}");
        }
    }

    private void ProcessQueueCore()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                LastExecutionThreadId = Thread.CurrentThread.ManagedThreadId;
                LastApartmentState = Thread.CurrentThread.GetApartmentState();

                if (_vdm == null)
                {
                    CreationThreadId = Thread.CurrentThread.ManagedThreadId;
                    _vdm = _factory();
                    if (_vdm != null)
                    {
                        Interlocked.Increment(ref _creationCount);
                    }
                }

                var result = item.Action(_vdm);
                item.Tcs.TrySetResult(result);
            }
            catch (COMException comEx)
            {
                ReleaseVdmCore();
                try
                {
                    CreationThreadId = Thread.CurrentThread.ManagedThreadId;
                    _vdm = _factory();
                    if (_vdm != null)
                    {
                        Interlocked.Increment(ref _creationCount);
                        var retryResult = item.Action(_vdm);
                        item.Tcs.TrySetResult(retryResult);
                        continue;
                    }
                }
                catch (Exception retryEx)
                {
                    item.Tcs.TrySetException(retryEx);
                    continue;
                }
                item.Tcs.TrySetException(comEx);
            }
            catch (Exception ex)
            {
                item.Tcs.TrySetException(ex);
            }
        }

        ReleaseVdmCore();
    }

    private void ReleaseVdmCore()
    {
        DisposalThreadId = Thread.CurrentThread.ManagedThreadId;
        if (_vdm != null)
        {
            try
            {
                if (Marshal.IsComObject(_vdm))
                    Marshal.ReleaseComObject(_vdm);
            }
            catch { }
            _vdm = null;
        }
    }

    public T Execute<T>(Func<IVirtualDesktopManager, T> action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Thread.CurrentThread.ManagedThreadId == _mtaThread.ManagedThreadId)
        {
            LastExecutionThreadId = Thread.CurrentThread.ManagedThreadId;
            LastApartmentState = Thread.CurrentThread.GetApartmentState();
            if (_vdm == null)
            {
                CreationThreadId = Thread.CurrentThread.ManagedThreadId;
                _vdm = _factory();
                if (_vdm != null)
                    Interlocked.Increment(ref _creationCount);
            }
            return action(_vdm!);
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(v => action(v!), tcs));
        return (T)tcs.Task.GetAwaiter().GetResult()!;
    }

    public IVirtualDesktopManager? GetOrCreate(Func<IVirtualDesktopManager?> factory)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = Volatile.Read(ref _activeProxy);
        if (existing != null)
            return existing;

        lock (_lock)
        {
            if (_activeProxy != null)
                return _activeProxy;

            _factory = factory;
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(v => v, tcs));
        var vdm = tcs.Task.GetAwaiter().GetResult();
        if (vdm == null) return null;

        lock (_lock)
        {
            if (_activeProxy == null)
            {
                _activeProxy = new MtaBoundVirtualDesktopManagerProxy(this);
            }
            return _activeProxy;
        }
    }

    public void Invalidate()
    {
        if (_disposed) return;
        lock (_lock)
        {
            _activeProxy = null;
        }

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new WorkItem(_ =>
        {
            ReleaseVdmCore();
            return null;
        }, tcs));
        try { tcs.Task.Wait(500); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _activeProxy = null;
        }
        _queue.CompleteAdding();
        try { _mtaThread.Join(500); } catch { }
    }

    private sealed class MtaBoundVirtualDesktopManagerProxy : IVirtualDesktopManager
    {
        private readonly VirtualDesktopManagerHolder _holder;

        public MtaBoundVirtualDesktopManagerProxy(VirtualDesktopManagerHolder holder)
        {
            _holder = holder;
        }

        public int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow)
            => _holder.Execute(vdm => vdm.IsWindowOnCurrentVirtualDesktop(topLevelWindow));

        public Guid GetWindowDesktopId(IntPtr topLevelWindow)
            => _holder.Execute(vdm => vdm.GetWindowDesktopId(topLevelWindow));

        public void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId)
        {
            var id = desktopId;
            _holder.Execute(vdm => { vdm.MoveWindowToDesktop(topLevelWindow, ref id); return 0; });
            desktopId = id;
        }
    }
}
