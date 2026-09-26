using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Events;

/// <summary>
/// SetWinEventHook on a dedicated thread with a Win32 message pump.
/// Out-of-context + skip-own-process. Delivers semantic desktop signals
/// (foreground, create/destroy/show, name/value/focus/state changes) that
/// power wait_for_* and cache invalidation without pixel polling.
/// </summary>
public sealed class WinEventService : IEventSource
{
    private static readonly uint[] HookedEvents =
    {
        NativeMethods.EVENT_SYSTEM_FOREGROUND,
        NativeMethods.EVENT_OBJECT_CREATE,
        NativeMethods.EVENT_OBJECT_DESTROY,
        NativeMethods.EVENT_OBJECT_SHOW,
        NativeMethods.EVENT_OBJECT_FOCUS,
        NativeMethods.EVENT_OBJECT_STATECHANGE,
        NativeMethods.EVENT_OBJECT_NAMECHANGE,
        NativeMethods.EVENT_OBJECT_VALUECHANGE,
        NativeMethods.EVENT_OBJECT_LOCATIONCHANGE,
    };

    public event Action<ObservedEvent>? Event;

    private readonly List<IntPtr> _hooks = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _stop;
    private NativeMethods.WinEventDelegate? _callback; // keep delegate alive

    public void Start()
    {
        if (_thread != null) return;
        _stop = false;
        _thread = new Thread(PumpLoop) { IsBackground = true, Name = "inbrisk-winevent" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void PumpLoop()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _callback = OnWinEvent; // must stay rooted for the hook's lifetime
        foreach (var ev in HookedEvents)
        {
            var h = NativeMethods.SetWinEventHook(ev, ev, IntPtr.Zero, _callback, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _hooks.Add(h);
        }

        while (!_stop && NativeMethods.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessageW(ref msg);
        }

        foreach (var h in _hooks) NativeMethods.UnhookWinEvent(h);
        _hooks.Clear();
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // idObject<0 = system objects (caret, cursor, sysmenu...) — drop.
        if (idObject < 0) return;
        if (hwnd == IntPtr.Zero) return;

        var kind = eventType switch
        {
            NativeMethods.EVENT_SYSTEM_FOREGROUND => EventKind.ForegroundChanged,
            NativeMethods.EVENT_OBJECT_CREATE => EventKind.WindowOpened,
            NativeMethods.EVENT_OBJECT_DESTROY => EventKind.WindowClosed,
            NativeMethods.EVENT_OBJECT_SHOW => EventKind.WindowShown,
            NativeMethods.EVENT_OBJECT_FOCUS => EventKind.FocusChanged,
            NativeMethods.EVENT_OBJECT_STATECHANGE => EventKind.StateChanged,
            NativeMethods.EVENT_OBJECT_NAMECHANGE => EventKind.NameChanged,
            NativeMethods.EVENT_OBJECT_VALUECHANGE => EventKind.ValueChanged,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE => EventKind.LocationChanged,
            _ => (EventKind?)null,
        };
        if (kind == null) return;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        Event?.Invoke(new ObservedEvent(kind.Value, DateTimeOffset.Now,
            hwnd.ToInt64(), pid == 0 ? null : pid,
            Detail: $"obj={idObject} child={idChild}"));
    }

    public void Dispose()
    {
        _stop = true;
        if (_threadId != 0)
            NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _thread = null;
    }
}
