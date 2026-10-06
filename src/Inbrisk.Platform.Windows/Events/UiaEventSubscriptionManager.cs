using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Uia;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Events;

/// <summary>
/// Phase F Round 2: Lifecycle owner for scoped UIA subscriptions.
/// - Scopes subscriptions to target HWND root (TreeScope.Subtree) rather than desktop root.
/// - Uses a compact CacheRequest so event callbacks avoid COM cross-process reads.
/// - Executes all native add/remove operations strictly on the dedicated UiaDispatcher MTA lane.
/// - Reference-counts equivalent subscriptions for safe reuse across concurrent consumers.
/// - Guarantees late-callback safety via monotonic subscription epoch tracking.
/// - Normalizes events and feeds them into the existing event pipeline.
/// </summary>
public sealed class UiaEventSubscriptionManager : IScopedSubscriptionManager, IEventSource
{
    internal sealed class NativeSubscriptionRecord
    {
        public required SubscriptionKey Key { get; init; }
        public int RefCount;
        public long Epoch = 1;
        public volatile bool IsActive;
        public IUIAutomationElement? TargetElement;
        public readonly List<object> Handlers = new();
        public readonly List<Action<IUIAutomation>> CleanupActions = new();
    }

    internal sealed class SubscriptionLease : ISubscriptionLease
    {
        private readonly UiaEventSubscriptionManager _mgr;
        internal readonly NativeSubscriptionRecord _record;
        private readonly long _leaseId;
        internal readonly long _epoch;
        private int _disposed;

        public SubscriptionKey Key => _record.Key;
        public long LeaseId => _leaseId;
        public long GenerationBaseline { get; }
        public bool IsActive => Volatile.Read(ref _disposed) == 0 && _record.IsActive && _record.Epoch == _epoch;

        public SubscriptionLease(UiaEventSubscriptionManager mgr, NativeSubscriptionRecord record,
            long leaseId, long epoch, long baseline)
        {
            _mgr = mgr;
            _record = record;
            _leaseId = leaseId;
            _epoch = epoch;
            GenerationBaseline = baseline;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _mgr.ReleaseLease(_record, _leaseId);
        }
    }

    private static readonly int[] DefaultWatchedProperties =
    {
        UiaIds.NameProperty,
        UiaIds.ValueValueProperty,
        UiaIds.ToggleStateProperty,
        UiaIds.ExpandCollapseStateProperty,
        UiaIds.IsEnabledProperty,
        UiaIds.IsOffscreenProperty,
    };

    private readonly UiaDispatcher _uia;
    private readonly Func<long>? _getGeneration;
    private readonly object _gate = new();
    private readonly Dictionary<SubscriptionKey, NativeSubscriptionRecord> _subscriptions = new();
    private readonly ConcurrentDictionary<EventCoalescingKey, (ObservedEvent Ev, long Ticks)> _recentCallbacks = new();
    private long _nextLeaseId = 1;
    private bool _disposed;

    public event Action<ObservedEvent>? Event;
    public ScopedSubscriptionTelemetry Telemetry { get; }

    public UiaEventSubscriptionManager(UiaDispatcher uia,
        ScopedSubscriptionTelemetry? telemetry = null,
        Func<long>? getGeneration = null)
    {
        _uia = uia;
        Telemetry = telemetry ?? new ScopedSubscriptionTelemetry();
        _getGeneration = getGeneration;
    }

    public ISubscriptionLease Acquire(long hwnd, int? pid, UiaEventKinds kinds, int[]? propertyIds = null, int scope = (int)TreeScope.TreeScope_Subtree)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var propSetKey = propertyIds is { Length: > 0 }
            ? string.Join(",", propertyIds.Distinct().OrderBy(x => x))
            : (kinds.HasFlag(UiaEventKinds.PropertyChanged) ? "default" : null);

        var key = new SubscriptionKey(hwnd, pid, kinds, propSetKey, scope);
        var baseline = _getGeneration?.Invoke() ?? 0;
        var leaseId = Interlocked.Increment(ref _nextLeaseId);

        NativeSubscriptionRecord record;
        bool isNew = false;
        long epoch;

        lock (_gate)
        {
            if (_subscriptions.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                Telemetry.IncScopedSubscriptionReuse();
                return new SubscriptionLease(this, existing, leaseId, existing.Epoch, baseline);
            }

            record = new NativeSubscriptionRecord
            {
                Key = key,
                RefCount = 1,
                IsActive = false,
                Epoch = 1
            };
            _subscriptions[key] = record;
            isNew = true;
            epoch = record.Epoch;
        }

        if (isNew)
        {
            try
            {
                InstallNative(record, propertyIds);
                record.IsActive = true;
                Telemetry.IncScopedSubscriptionAdd();
                Telemetry.IncActiveScopedSubscriptions();
                if (hwnd == 0)
                {
                    Telemetry.IncDesktopRootSubscription();
                }
            }
            catch
            {
                lock (_gate)
                {
                    _subscriptions.Remove(key);
                }
                throw;
            }
        }

        return new SubscriptionLease(this, record, leaseId, epoch, baseline);
    }

    private void ReleaseLease(NativeSubscriptionRecord record, long leaseId)
    {
        bool mustUninstall = false;
        lock (_gate)
        {
            record.RefCount--;
            if (record.RefCount <= 0)
            {
                record.IsActive = false;
                record.Epoch++;
                _subscriptions.Remove(record.Key);
                mustUninstall = true;
            }
        }

        if (mustUninstall)
        {
            try
            {
                UninstallNative(record);
            }
            catch { /* best effort cleanup */ }
            finally
            {
                Telemetry.IncScopedSubscriptionRemove();
                Telemetry.DecActiveScopedSubscriptions();
            }
        }
    }

    private void InstallNative(NativeSubscriptionRecord record, int[]? customProps)
    {
        _uia.Run(uia =>
        {
            IUIAutomationElement? root = null;
            if (record.Key.Hwnd != 0)
            {
                try
                {
                    root = uia.ElementFromHandle(new IntPtr(record.Key.Hwnd));
                }
                catch
                {
                    root = null;
                }
            }
            else
            {
                try
                {
                    root = uia.GetRootElement();
                }
                catch
                {
                    root = null;
                }
            }

            if (root == null)
            {
                // Target window or desktop root unavailable; skip native registration
                return false;
            }

            record.TargetElement = root;
            var treeScope = (TreeScope)record.Key.Scope;

            // Compact CacheRequest to eliminate cross-process property calls in callback
            var cache = uia.CreateCacheRequest();
            cache.TreeScope = treeScope;
            cache.AddProperty(UiaIds.ProcessIdProperty);
            cache.AddProperty(UiaIds.NativeWindowHandleProperty);
            cache.AddProperty(UiaIds.NameProperty);
            cache.AddProperty(UiaIds.AutomationIdProperty);
            cache.AddProperty(UiaIds.ControlTypeProperty);
            cache.AddProperty(UiaIds.IsEnabledProperty);
            cache.AddProperty(UiaIds.IsOffscreenProperty);

            var watchedProps = customProps is { Length: > 0 } ? customProps : DefaultWatchedProperties;
            foreach (var p in watchedProps)
            {
                try { cache.AddProperty(p); } catch { }
            }

            var epoch = record.Epoch;

            // 1. StructureChanged
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.StructureChanged))
            {
                var handler = new ScopedStructureHandler(this, record, epoch);
                record.Handlers.Add(handler);
                uia.AddStructureChangedEventHandler(root, treeScope, cache, handler);
                record.CleanupActions.Add(u =>
                {
                    try { u.RemoveStructureChangedEventHandler(root, handler); } catch { }
                });
            }

            // 2. PropertyChanged
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.PropertyChanged))
            {
                var handler = new ScopedPropertyHandler(this, record, epoch);
                record.Handlers.Add(handler);
                uia.AddPropertyChangedEventHandler(root, treeScope, cache, handler, watchedProps);
                record.CleanupActions.Add(u =>
                {
                    try { u.RemovePropertyChangedEventHandler(root, handler); } catch { }
                });
            }

            // 3. WindowOpened
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.WindowOpened))
            {
                var handler = new ScopedAutomationHandler(this, record, epoch, UiaIds.WindowOpenedEvent, EventKind.WindowOpened);
                record.Handlers.Add(handler);
                uia.AddAutomationEventHandler(UiaIds.WindowOpenedEvent, root, TreeScope.TreeScope_Children, cache, handler);
                record.CleanupActions.Add(u =>
                {
                    try { u.RemoveAutomationEventHandler(UiaIds.WindowOpenedEvent, root, handler); } catch { }
                });
            }

            // 4. WindowClosed
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.WindowClosed))
            {
                var handler = new ScopedAutomationHandler(this, record, epoch, UiaIds.WindowClosedEvent, EventKind.WindowClosed);
                record.Handlers.Add(handler);
                uia.AddAutomationEventHandler(UiaIds.WindowClosedEvent, root, treeScope, cache, handler);
                record.CleanupActions.Add(u =>
                {
                    try { u.RemoveAutomationEventHandler(UiaIds.WindowClosedEvent, root, handler); } catch { }
                });
            }

            // 5. Notification (where supported on Windows 10 1809+ / IUIAutomation5)
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.Notification))
            {
                if (uia is IUIAutomation5 uia5)
                {
                    try
                    {
                        var handler = new ScopedNotificationHandler(this, record, epoch);
                        record.Handlers.Add(handler);
                        uia5.AddNotificationEventHandler(root, treeScope, cache, handler);
                        record.CleanupActions.Add(u =>
                        {
                            if (u is IUIAutomation5 u5)
                            {
                                try { u5.RemoveNotificationEventHandler(root, handler); } catch { }
                            }
                        });
                    }
                    catch { /* Graceful degradation without fabricating fake Notification */ }
                }
            }

            // 6. LiveRegionChanged (where supported)
            if (record.Key.EventKinds.HasFlag(UiaEventKinds.LiveRegion))
            {
                try
                {
                    var handler = new ScopedAutomationHandler(this, record, epoch, UiaIds.LiveRegionChangedEvent, EventKind.LiveRegionChanged);
                    record.Handlers.Add(handler);
                    uia.AddAutomationEventHandler(UiaIds.LiveRegionChangedEvent, root, treeScope, cache, handler);
                    record.CleanupActions.Add(u =>
                    {
                        try { u.RemoveAutomationEventHandler(UiaIds.LiveRegionChangedEvent, root, handler); } catch { }
                    });
                }
                catch { /* Graceful degradation */ }
            }

            return true;
        });
    }

    private void UninstallNative(NativeSubscriptionRecord record)
    {
        _uia.Run(uia =>
        {
            foreach (var action in record.CleanupActions)
            {
                try { action(uia); } catch { }
            }
            record.CleanupActions.Clear();
            record.Handlers.Clear();
            record.TargetElement = null;
            return true;
        }, timeoutMs: 3000);
    }

    internal void RouteCallback(
        NativeSubscriptionRecord record,
        long epoch,
        EventKind kind,
        IUIAutomationElement sender,
        string? detail = null,
        UiaNotificationData? notificationData = null,
        int? propertyId = null)
    {
        // Boundary: this runs on a UIA COM callback thread — a routing fault
        // escaping the callback kills the process.
        try
        {
            RouteCallbackGuarded(record, epoch, kind, sender, detail, notificationData, propertyId);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"inbrisk UIA route-callback fault: {e}");
        }
    }

    private void RouteCallbackGuarded(
        NativeSubscriptionRecord record,
        long epoch,
        EventKind kind,
        IUIAutomationElement sender,
        string? detail = null,
        UiaNotificationData? notificationData = null,
        int? propertyId = null)
    {
        // Late callback safety: early check before reading properties
        if (!record.IsActive || record.Epoch != epoch)
        {
            Telemetry.IncLateCallbackIgnored();
            return;
        }

        long? hwnd = null;
        int? pid = null;
        string? name = null;
        string? autoId = null;

        // Routing reads from cache request with zero COM cross-process calls
        try
        {
            var h = sender.CachedNativeWindowHandle;
            if (h != IntPtr.Zero) hwnd = h.ToInt64();
        }
        catch
        {
            Telemetry.IncCallbackExtraComRead();
            try { var h = sender.CurrentNativeWindowHandle; if (h != IntPtr.Zero) hwnd = h.ToInt64(); } catch { }
        }

        try
        {
            pid = sender.CachedProcessId;
        }
        catch
        {
            Telemetry.IncCallbackExtraComRead();
            try { pid = sender.CurrentProcessId; } catch { }
        }

        try
        {
            name = sender.CachedName;
        }
        catch
        {
            Telemetry.IncCallbackExtraComRead();
            try { name = sender.CurrentName; } catch { }
        }

        try
        {
            autoId = sender.CachedAutomationId;
        }
        catch { }

        RouteCallbackCore(record, epoch, kind, hwnd, pid, name, autoId, detail, notificationData, propertyId);
    }

    internal void RouteCallbackCore(
        NativeSubscriptionRecord record,
        long epoch,
        EventKind kind,
        long? hwnd,
        int? pid,
        string? name,
        string? autoId,
        string? detail = null,
        UiaNotificationData? notificationData = null,
        int? propertyId = null,
        string? structureChangeType = null)
    {
        Telemetry.IncUiaEventCallback();

        // Late callback safety: expired subscription or mismatching epoch is ignored safely
        if (!record.IsActive || record.Epoch != epoch)
        {
            Telemetry.IncLateCallbackIgnored();
            return;
        }

        // Process isolation: if sender PID is known and does not match target subscription PID, reject
        if (record.Key.Pid.HasValue && pid.HasValue && pid.Value != record.Key.Pid.Value)
        {
            Telemetry.IncUiaEventIgnored();
            return;
        }

        // Window isolation & Zero-HWND Descendant Routing:
        // A UIA descendant is not guaranteed to have a useful non-zero native HWND.
        // Required routing rule:
        // if sender has useful HWND:
        //     use HWND + PID identity
        // else:
        //     use known subscription/root HWND + PID + subscription epoch/key
        // Zero HWND must NOT mean global; it remains scoped to the subscribed root HWND.
        long effectiveHwnd;
        if (hwnd.HasValue && hwnd.Value != 0)
        {
            if (record.Key.Hwnd != 0 && hwnd.Value != record.Key.Hwnd)
            {
                if (record.Key.Pid.HasValue && pid.HasValue && pid.Value != record.Key.Pid.Value)
                {
                    Telemetry.IncUiaEventIgnored();
                    return;
                }
            }
            effectiveHwnd = hwnd.Value;
        }
        else
        {
            effectiveHwnd = record.Key.Hwnd;
        }

        // Semantically safe Notification handling:
        // Meaningful notifications must NEVER be collapsed or erased by coalescing.
        // Direct dispatch ensures zero waiter loss.
        if (kind == EventKind.Notification)
        {
            var notifEv = new ObservedEvent(
                Kind: kind,
                At: DateTimeOffset.Now,
                Hwnd: effectiveHwnd != 0 ? effectiveHwnd : null,
                Pid: pid ?? record.Key.Pid,
                ElementId: autoId,
                Detail: detail ?? name,
                NotificationData: notificationData,
                PropertyId: propertyId);

            Telemetry.IncUiaEventRelevant();
            Raise(notifEv);
            return;
        }

        // Bounded Coalescing: coalesce high-churn same-window events within 15ms window
        // Coalescing key includes PropertyId and StructureChange type to prevent semantic collisions.
        var propId = propertyId ?? (kind switch {
            EventKind.NameChanged => UiaIds.NameProperty,
            EventKind.ValueChanged => UiaIds.ValueValueProperty,
            _ => null
        });
        if (!propId.HasValue && detail != null && detail.StartsWith("prop="))
        {
            var endIdx = detail.IndexOfAny([' ', '-', '>']);
            var numStr = endIdx > 5 ? detail[5..endIdx] : detail[5..];
            if (int.TryParse(numStr, out var parsed)) propId = parsed;
        }

        var coalescingKey = new EventCoalescingKey(
            Kind: kind,
            Hwnd: effectiveHwnd,
            ElementId: autoId ?? name ?? "",
            PropertyId: propId,
            StructureChange: kind == EventKind.StructureChanged ? (structureChangeType ?? detail) : null);

        var nowTicks = Stopwatch.GetTimestamp();
        var cutoffTicks = (long)(0.015 * Stopwatch.Frequency);

        if (_recentCallbacks.TryGetValue(coalescingKey, out var prev) && (nowTicks - prev.Ticks) < cutoffTicks)
        {
            Telemetry.IncUiaEventCoalesced();
            // Preserve latest state for correctness
            _recentCallbacks[coalescingKey] = (prev.Ev with
            {
                Detail = detail ?? prev.Ev.Detail,
                NotificationData = notificationData ?? prev.Ev.NotificationData,
                PropertyId = propId ?? prev.Ev.PropertyId
            }, nowTicks);
            return;
        }

        var ev = new ObservedEvent(
            Kind: kind,
            At: DateTimeOffset.Now,
            Hwnd: effectiveHwnd != 0 ? effectiveHwnd : null,
            Pid: pid ?? record.Key.Pid,
            ElementId: autoId,
            Detail: detail ?? name,
            NotificationData: notificationData,
            PropertyId: propId);

        _recentCallbacks[coalescingKey] = (ev, nowTicks);

        Telemetry.IncUiaEventRelevant();
        Raise(ev);
    }

    // Per-subscriber delivery on UIA callback threads: one bad subscriber must
    // neither abort the remaining subscribers nor escape the COM callback.
    private void Raise(ObservedEvent e)
    {
        var subs = Event;
        if (subs == null) return;
        foreach (Action<ObservedEvent> sub in subs.GetInvocationList())
        {
            try { sub(e); }
            catch (Exception ex)
            {
                Debug.WriteLine($"inbrisk UIA subscriber fault: {ex}");
            }
        }
    }

    internal void TestRouteCallback(
        ISubscriptionLease lease,
        EventKind kind,
        long? hwnd,
        int? pid,
        string? name,
        string? autoId,
        string? detail = null,
        UiaNotificationData? notificationData = null,
        int? propertyId = null)
    {
        if (lease is SubscriptionLease sl)
        {
            RouteCallbackCore(sl._record, sl._epoch, kind, hwnd, pid, name, autoId, detail, notificationData, propertyId);
        }
    }

    private sealed class ScopedNotificationHandler(UiaEventSubscriptionManager mgr, NativeSubscriptionRecord rec, long epoch)
        : IUIAutomationNotificationEventHandler
    {
        public void HandleNotificationEvent(IUIAutomationElement sender, NotificationKind notificationKind, NotificationProcessing notificationProcessing, string displayString, string activityId)
        {
            var data = new UiaNotificationData(
                (int)notificationKind,
                (int)notificationProcessing,
                displayString,
                activityId);
            mgr.RouteCallback(rec, epoch, EventKind.Notification, sender, displayString, data);
        }
    }

    private sealed class ScopedStructureHandler(UiaEventSubscriptionManager mgr, NativeSubscriptionRecord rec, long epoch)
        : IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(IUIAutomationElement sender, StructureChangeType changeType, int[] runtimeId)
        {
            mgr.RouteCallback(rec, epoch, EventKind.StructureChanged, sender, changeType.ToString());
        }
    }

    private sealed class ScopedPropertyHandler(UiaEventSubscriptionManager mgr, NativeSubscriptionRecord rec, long epoch)
        : IUIAutomationPropertyChangedEventHandler
    {
        public void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, object newValue)
        {
            var kind = propertyId == UiaIds.NameProperty ? EventKind.NameChanged
                : propertyId == UiaIds.ValueValueProperty ? EventKind.ValueChanged
                : EventKind.StateChanged;
            mgr.RouteCallback(rec, epoch, kind, sender, $"prop={propertyId} -> {newValue}", propertyId: propertyId);
        }
    }

    private sealed class ScopedAutomationHandler(UiaEventSubscriptionManager mgr, NativeSubscriptionRecord rec, long epoch, int eventId, EventKind kind)
        : IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(IUIAutomationElement sender, int id)
        {
            if (id != eventId) return;
            mgr.RouteCallback(rec, epoch, kind, sender);
        }
    }

    public void Start()
    {
        // On-demand manager starts when acquired
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<NativeSubscriptionRecord> toClean;
        lock (_gate)
        {
            toClean = _subscriptions.Values.ToList();
            _subscriptions.Clear();
        }

        foreach (var rec in toClean)
        {
            rec.IsActive = false;
            rec.Epoch++;
            try { UninstallNative(rec); } catch { }
            Telemetry.IncScopedSubscriptionRemove();
            Telemetry.DecActiveScopedSubscriptions();
        }
    }
}
