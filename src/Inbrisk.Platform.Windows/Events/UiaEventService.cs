using Inbrisk.Core;
using Inbrisk.Platform.Windows.Uia;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Events;

/// <summary>
/// UIA event handlers (focus changed, structure changed, window opened/closed,
/// property changed on Name/Value/ToggleState) registered against the desktop
/// root. Runs through the UiaDispatcher; callbacks push into the event stream.
/// </summary>
public sealed class UiaEventService : IEventSource
{
    private static readonly int[] WatchedProperties =
    {
        UiaIds.NameProperty,
        UiaIds.ValueValueProperty,
        UiaIds.ToggleStateProperty,
        UiaIds.ExpandCollapseStateProperty,
    };

    public event Action<ObservedEvent>? Event;

    private readonly UiaDispatcher _uia;
    private bool _started;

    public UiaEventService(UiaDispatcher dispatcher) => _uia = dispatcher;

    public void Start()
    {
        if (_started) return;
        _started = true;
        _uia.Run(uia =>
        {
            var root = uia.GetRootElement();

            try { uia.AddFocusChangedEventHandler(null, new FocusHandler(Raise)); } catch { }
            try
            {
                uia.AddStructureChangedEventHandler(root, TreeScope.TreeScope_Subtree,
                    null, new StructureHandler(Raise));
            }
            catch { }
            try
            {
                uia.AddAutomationEventHandler(UiaIds.WindowOpenedEvent, root,
                    TreeScope.TreeScope_Children, null,
                    new AutomationHandler(UiaIds.WindowOpenedEvent, Raise));
            }
            catch { }
            try
            {
                uia.AddAutomationEventHandler(UiaIds.WindowClosedEvent, root,
                    TreeScope.TreeScope_Subtree, null,
                    new AutomationHandler(UiaIds.WindowClosedEvent, Raise));
            }
            catch { }
            try
            {
                uia.AddPropertyChangedEventHandler(root, TreeScope.TreeScope_Subtree,
                    null, new PropHandler(Raise), WatchedProperties);
            }
            catch { }
            return true;
        });
    }

    // Per-subscriber delivery on UIA callback threads: one bad subscriber must
    // neither abort the remaining subscribers nor escape the COM callback —
    // an unhandled fault on a UIA event thread kills the process.
    private void Raise(ObservedEvent e)
    {
        var subs = Event;
        if (subs == null) return;
        foreach (Action<ObservedEvent> sub in subs.GetInvocationList())
        {
            try { sub(e); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk-uiaevent subscriber fault: {ex}");
            }
        }
    }

    private static ObservedEvent Ev(EventKind kind, IUIAutomationElement el, string? detail = null)
    {
        long? hwnd = null; int? pid = null; string? name = null;
        try { var h = el.CurrentNativeWindowHandle; if (h != IntPtr.Zero) hwnd = h.ToInt64(); } catch { }
        try { pid = el.CurrentProcessId; } catch { }
        try { name = el.CurrentName; } catch { }
        return new ObservedEvent(kind, DateTimeOffset.Now, hwnd, pid,
            Detail: detail ?? name);
    }

    private sealed class FocusHandler(Action<ObservedEvent> emit)
        : IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(IUIAutomationElement sender) =>
            emit(Ev(EventKind.FocusChanged, sender));
    }

    private sealed class StructureHandler(Action<ObservedEvent> emit)
        : IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(IUIAutomationElement sender,
            StructureChangeType changeType, int[] runtimeId) =>
            emit(Ev(EventKind.StructureChanged, sender, changeType.ToString()));
    }

    private sealed class AutomationHandler(int eventId, Action<ObservedEvent> emit)
        : IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(IUIAutomationElement sender, int id)
        {
            if (id != eventId) return;
            var kind = id == UiaIds.WindowOpenedEvent ? EventKind.WindowOpened : EventKind.WindowClosed;
            emit(Ev(kind, sender));
        }
    }

    private sealed class PropHandler(Action<ObservedEvent> emit)
        : IUIAutomationPropertyChangedEventHandler
    {
        public void HandlePropertyChangedEvent(IUIAutomationElement sender,
            int propertyId, object newValue)
        {
            var kind = propertyId == UiaIds.NameProperty ? EventKind.NameChanged
                : propertyId == UiaIds.ValueValueProperty ? EventKind.ValueChanged
                : EventKind.StateChanged;
            emit(Ev(kind, sender, $"prop={propertyId} → {newValue}"));
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        try { _uia.Run(uia => { uia.RemoveAllEventHandlers(); return true; }, 3000); }
        catch { /* best effort */ }
    }
}
