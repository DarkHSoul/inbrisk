namespace Inbrisk.Core.WorldState;

/// <summary>
/// Options for configuring event storm coalescing and version tracking.
/// </summary>
public sealed record StateVersionTrackerOptions(
    TimeSpan CoalesceWindow,
    TimeSpan MaxCoalesceDuration,
    int MaxCoalescedCount)
{
    public static StateVersionTrackerOptions Default => new(
        CoalesceWindow: TimeSpan.FromMilliseconds(50),
        MaxCoalesceDuration: TimeSpan.FromMilliseconds(200),
        MaxCoalescedCount: 5);
}

/// <summary>
/// Monotonically increasing stateVersion tracker per logical session.
/// Increments on relevant world changes (windows opened/closed, foreground/focus change, mutation side effects).
/// Does NOT increment for telemetry counters or read-only queries.
/// Implements bounded coalescing of rapid UI event storms into a single version increment.
/// </summary>
public sealed class StateVersionTracker
{
    private readonly object _sync = new();
    private readonly StateDeltaRing? _deltaRing;
    private readonly StateVersionTrackerOptions _options;

    private long _currentVersion;
    private bool _inCoalesceBatch;
    private int _coalescedCount;
    private DateTimeOffset _batchStartedAt;
    private DateTimeOffset _lastEventAt;

    public string SessionId { get; }
    public long CurrentVersion => Volatile.Read(ref _currentVersion);

    public event Action<StateChangeEntry>? OnStateChanged;

    public StateVersionTracker(
        string? sessionId = null,
        long initialVersion = 1,
        StateDeltaRing? deltaRing = null,
        StateVersionTrackerOptions? options = null)
    {
        SessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _currentVersion = initialVersion;
        _deltaRing = deltaRing;
        _options = options ?? StateVersionTrackerOptions.Default;
    }

    /// <summary>
    /// Explicit mutation side effects (e.g. click, typing, invoke, layout changes).
    /// Always increments version immediately and closes any in-flight event storm coalescing window.
    /// </summary>
    public long RecordMutation(
        string mutationKind,
        string? description = null,
        long? hwnd = null,
        string? elementId = null,
        IReadOnlyDictionary<string, object?>? details = null,
        DateTimeOffset? timestamp = null)
    {
        long newVersion;
        StateChangeEntry entry;
        var now = timestamp ?? DateTimeOffset.UtcNow;

        lock (_sync)
        {
            _currentVersion++;
            newVersion = _currentVersion;

            // Reset event storm batch upon explicit mutation
            _inCoalesceBatch = false;
            _coalescedCount = 0;

            entry = new StateChangeEntry(
                Version: newVersion,
                Timestamp: now,
                ChangeKind: $"Mutation:{mutationKind}",
                Description: description ?? mutationKind,
                Hwnd: hwnd,
                ElementId: elementId,
                Details: details);

            _deltaRing?.RecordChange(entry);
        }

        OnStateChanged?.Invoke(entry);
        return newVersion;
    }

    /// <summary>
    /// Records an observed UI event. Increments version if relevant.
    /// Rapid bursts within CoalesceWindow are coalesced into a single increment,
    /// bounded by MaxCoalescedCount and MaxCoalesceDuration.
    /// </summary>
    public long RecordEvent(ObservedEvent evt, DateTimeOffset? timestamp = null)
    {
        if (!IsRelevantWorldChange(evt))
        {
            // Telemetry or non-mutating query events do NOT increment version
            return CurrentVersion;
        }

        var now = timestamp ?? evt.At;
        StateChangeEntry? entryToNotify = null;
        long newVersion;

        lock (_sync)
        {
            bool shouldIncrement;

            if (!_inCoalesceBatch)
            {
                // First event in a burst -> increment and open coalescing batch
                shouldIncrement = true;
                _inCoalesceBatch = true;
                _coalescedCount = 1;
                _batchStartedAt = now;
                _lastEventAt = now;
            }
            else
            {
                // Check coalescing bounds:
                // 1. Time since last event exceeds quiet window
                // 2. Coalesced count reaches max limit
                // 3. Total duration of current burst exceeds max duration
                var timeSinceLast = now - _lastEventAt;
                var totalDuration = now - _batchStartedAt;

                if (timeSinceLast > _options.CoalesceWindow ||
                    _coalescedCount >= _options.MaxCoalescedCount ||
                    totalDuration > _options.MaxCoalesceDuration)
                {
                    // Bound reached: close previous batch and trigger new increment
                    shouldIncrement = true;
                    _coalescedCount = 1;
                    _batchStartedAt = now;
                    _lastEventAt = now;
                }
                else
                {
                    // Coalesce within bounds: do not increment version
                    shouldIncrement = false;
                    _coalescedCount++;
                    _lastEventAt = now;
                }
            }

            if (shouldIncrement)
            {
                _currentVersion++;
                newVersion = _currentVersion;

                entryToNotify = new StateChangeEntry(
                    Version: newVersion,
                    Timestamp: now,
                    ChangeKind: $"Event:{evt.Kind}",
                    Description: evt.Detail ?? evt.Kind.ToString(),
                    Hwnd: evt.Hwnd,
                    ElementId: evt.ElementId,
                    Details: null);

                _deltaRing?.RecordChange(entryToNotify);
            }
            else
            {
                newVersion = _currentVersion;
            }
        }

        if (entryToNotify != null)
        {
            OnStateChanged?.Invoke(entryToNotify);
        }

        return newVersion;
    }

    /// <summary>
    /// Explicit telemetry counter. Strictly does NOT increment stateVersion.
    /// </summary>
    public void RecordTelemetry(string counterName, double value = 1.0, IReadOnlyDictionary<string, object?>? tags = null)
    {
        // Design invariant: Telemetry is purely diagnostic and never affects world state version.
    }

    /// <summary>
    /// Read-only observation/inspect query. Strictly does NOT increment stateVersion.
    /// </summary>
    public void RecordReadOnlyQuery(string queryType, string? target = null)
    {
        // Design invariant: Read operations are passive observation and never mutate world state.
    }

    /// <summary>
    /// Filters events to determine whether they represent meaningful physical or semantic world changes.
    /// </summary>
    public static bool IsRelevantWorldChange(ObservedEvent evt)
    {
        return evt.Kind switch
        {
            EventKind.WindowOpened => true,
            EventKind.WindowClosed => true,
            EventKind.WindowShown => true,
            EventKind.ForegroundChanged => true,
            EventKind.FocusChanged => true,
            EventKind.StateChanged => true,
            EventKind.StructureChanged => true,
            EventKind.ValueChanged => true,
            EventKind.NameChanged => true,
            EventKind.LocationChanged => true,
            EventKind.PropertyChanged => true,
            EventKind.LiveRegionChanged => true,
            EventKind.Notification => false, // Pure notification message, non-state mutating
            _ => false
        };
    }
}
