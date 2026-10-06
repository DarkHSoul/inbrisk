namespace Inbrisk.Core.WorldState;

/// <summary>
/// Granular state change record stored in the bounded delta ring buffer.
/// </summary>
public sealed record StateChangeEntry(
    long Version,
    DateTimeOffset Timestamp,
    string ChangeKind,
    string? Description = null,
    long? Hwnd = null,
    string? ElementId = null,
    IReadOnlyDictionary<string, object?>? Details = null);

/// <summary>
/// Compact snapshot representation of desktop windows for state resynchronization.
/// </summary>
public sealed record CompactWindowSnapshot(
    long Hwnd,
    int Pid,
    string Title,
    bool IsForeground);

/// <summary>
/// Compact snapshot representation of significant elements for state resynchronization.
/// </summary>
public sealed record CompactElementSnapshot(
    string Id,
    string Role,
    string? Name,
    RectPx Bounds);

/// <summary>
/// Compact world snapshot returned when requested version has dropped out of the ring buffer.
/// </summary>
public sealed record CompactWorldSnapshot(
    long Version,
    DateTimeOffset Timestamp,
    IReadOnlyList<CompactWindowSnapshot> Windows,
    IReadOnlyList<CompactElementSnapshot> Elements,
    long? FocusedHwnd = null,
    string? FocusedElementId = null);

/// <summary>
/// Result of querying delta changes since a known stateVersion.
/// </summary>
public sealed record StateDeltaResult(
    bool RequiresResync,
    long CurrentVersion,
    long KnownVersion,
    IReadOnlyList<StateChangeEntry> Changes,
    CompactWorldSnapshot? CompactSnapshot = null);

/// <summary>
/// Bounded ring buffer storing recent state changes.
/// Enables incremental queries ("what changed since version N").
/// When requested version is older than ring retention, returns a resync-required marker with compact snapshot.
/// </summary>
public sealed class StateDeltaRing
{
    private readonly object _sync = new();
    private readonly StateChangeEntry?[] _buffer;
    private int _head;
    private int _count;
    private long _currentVersion;
    private long? _oldestVersion;

    public int Capacity { get; }
    public int Count { get { lock (_sync) return _count; } }
    public long CurrentVersion { get { lock (_sync) return _currentVersion; } }
    public long? OldestRetainedVersion { get { lock (_sync) return _oldestVersion; } }

    public StateDeltaRing(int capacity = 100)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        Capacity = capacity;
        _buffer = new StateChangeEntry?[capacity];
    }

    /// <summary>
    /// Records a new state change in the ring buffer.
    /// If capacity is exceeded, the oldest entry is overwritten.
    /// </summary>
    public void RecordChange(StateChangeEntry entry)
    {
        lock (_sync)
        {
            _buffer[_head] = entry;
            _head = (_head + 1) % Capacity;

            if (_count < Capacity)
            {
                _count++;
            }

            _currentVersion = entry.Version;

            // Compute oldest version in ring
            int oldestIndex = (_head - _count + Capacity) % Capacity;
            _oldestVersion = _buffer[oldestIndex]?.Version;
        }
    }

    /// <summary>
    /// Queries all changes that occurred strictly after knownVersion up to CurrentVersion.
    /// If knownVersion is older than the oldest retained version in the ring,
    /// returns RequiresResync = true along with a compact snapshot.
    /// </summary>
    public StateDeltaResult GetChangesSince(long knownVersion, Func<CompactWorldSnapshot>? snapshotProvider = null)
    {
        lock (_sync)
        {
            // Case 1: Exact match with current version -> no changes, no resync required
            if (knownVersion == _currentVersion)
            {
                return new StateDeltaResult(
                    RequiresResync: false,
                    CurrentVersion: _currentVersion,
                    KnownVersion: knownVersion,
                    Changes: Array.Empty<StateChangeEntry>());
            }

            // Case 2: Buffer is empty
            if (_count == 0)
            {
                // If knownVersion matches, ok; otherwise resync
                bool needsResync = knownVersion != _currentVersion;
                return new StateDeltaResult(
                    RequiresResync: needsResync,
                    CurrentVersion: _currentVersion,
                    KnownVersion: knownVersion,
                    Changes: Array.Empty<StateChangeEntry>(),
                    CompactSnapshot: needsResync ? (snapshotProvider?.Invoke() ?? CreateDefaultSnapshot(_currentVersion)) : null);
            }

            // Case 3: Future or invalid version requested
            if (knownVersion > _currentVersion)
            {
                return new StateDeltaResult(
                    RequiresResync: true,
                    CurrentVersion: _currentVersion,
                    KnownVersion: knownVersion,
                    Changes: Array.Empty<StateChangeEntry>(),
                    CompactSnapshot: snapshotProvider?.Invoke() ?? CreateDefaultSnapshot(_currentVersion));
            }

            // Case 4: Version has been overwritten / evicted from ring buffer
            var oldest = _oldestVersion ?? _currentVersion;
            if (knownVersion < oldest - 1)
            {
                return new StateDeltaResult(
                    RequiresResync: true,
                    CurrentVersion: _currentVersion,
                    KnownVersion: knownVersion,
                    Changes: Array.Empty<StateChangeEntry>(),
                    CompactSnapshot: snapshotProvider?.Invoke() ?? CreateDefaultSnapshot(_currentVersion));
            }

            // Case 5: Version is within retained window -> collect delta entries
            var changes = new List<StateChangeEntry>();
            int startIndex = (_head - _count + Capacity) % Capacity;

            for (int i = 0; i < _count; i++)
            {
                int idx = (startIndex + i) % Capacity;
                var item = _buffer[idx];
                if (item != null && item.Version > knownVersion && item.Version <= _currentVersion)
                {
                    changes.Add(item);
                }
            }

            return new StateDeltaResult(
                RequiresResync: false,
                CurrentVersion: _currentVersion,
                KnownVersion: knownVersion,
                Changes: changes);
        }
    }

    private static CompactWorldSnapshot CreateDefaultSnapshot(long version)
    {
        return new CompactWorldSnapshot(
            Version: version,
            Timestamp: DateTimeOffset.UtcNow,
            Windows: Array.Empty<CompactWindowSnapshot>(),
            Elements: Array.Empty<CompactElementSnapshot>());
    }
}
