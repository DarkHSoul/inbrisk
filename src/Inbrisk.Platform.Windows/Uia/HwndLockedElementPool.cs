using System.Collections.Concurrent;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// Fast re-resolution layer: elementId → (owning hwnd, last bounds, control
/// type, name, automationId, runtimeId, access tick). The _live map holds the
/// COM handles; this pool holds only value fingerprints, so a ReResolve after
/// live-map eviction (LRU overflow, single-handle stale drop) can locate the
/// element with ONE provider-side runtimeId (or exact-bounds) match instead of
/// re-walking the ancestry path level by level.
///
/// Safety contract: a lookup only pays off when the owning window is still
/// alive (Win32 IsWindow — a local syscall, sub-ms, no COM) and the resolved
/// candidate proves identity via runtimeId match or exact bounds + signature
/// match. Any mismatch falls through to the normal ancestry walk / stale
/// path — the pool never fabricates an element.
///
/// Entries drop on the same InvalidateWindow(hwnd) events as the live map:
/// window destroy/show/structure-change can reassign runtime ids and bounds,
/// so a fingerprint must not validate against a recycled identity.
/// </summary>
internal sealed class HwndLockedElementPool
{
    /// <summary>UIA_RuntimeIdPropertyId — the provider-side identity key for
    /// the one-call FindFirst re-resolution. Kept here (not UiaIds) because
    /// only the pool uses it as a *condition*; as an element property it is
    /// read via GetRuntimeId(), never through a cache request.</summary>
    internal const int RuntimeIdPropertyId = 30000;

    /// <summary>Re-resolution fingerprint for one minted element id.</summary>
    internal sealed record Entry(
        long Hwnd,
        RectPx Bounds,
        int ControlType,
        string? Name,
        string? AutomationId,
        int[]? RuntimeId)
    {
        public long Tick;
    }

    /// <summary>Why a TryGet consult did not produce a usable entry — feeds
    /// the `pool` field of the uia.reresolve perf event.</summary>
    internal enum MissReason { NoEntry, HwndChanged, DeadHwnd }

    private readonly ConcurrentDictionary<string, Entry> _map = new();
    private long _clock;
    private long _hits, _misses, _invalidated;

    /// <summary>Value-only entries are a few dozen bytes — the pool outlives
    /// the live map's 4096-handle cap so re-resolution survives bulk LRU
    /// eviction of a large Inspect.</summary>
    public int MaxEntries { get; set; } = 8192;

    public int Count => _map.Count;
    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long Invalidated => Interlocked.Read(ref _invalidated);

    public void NoteHit() => Interlocked.Increment(ref _hits);
    public void NoteMiss() => Interlocked.Increment(ref _misses);

    /// <summary>Record an element's re-resolution fingerprint. Called from
    /// ToUiElement next to LivePut for every minted id.</summary>
    public void Put(string id, long hwnd, RectPx bounds, int controlType,
        string? name, string? automationId, int[]? runtimeId)
    {
        _map[id] = new Entry(hwnd, bounds, controlType, name, automationId,
            runtimeId) { Tick = Interlocked.Increment(ref _clock) };
        if (_map.Count > MaxEntries) EvictOverflow();
    }

    private void EvictOverflow()
    {
        // amortized — same shape as the live map's EvictLiveOverflow
        var n = _map.Count - MaxEntries + MaxEntries / 4;
        if (n <= 0) return;
        foreach (var k in _map.OrderBy(kv => kv.Value.Tick)
                     .Take(n).Select(kv => kv.Key).ToList())
            _map.TryRemove(k, out _);
    }

    /// <summary>Sub-ms gate: entry exists, belongs to this hwnd, and the
    /// owning window is still alive (IsWindow is a local syscall — no COM,
    /// no dispatch). false + missReason means "use the normal resolve path".</summary>
    public bool TryGet(string id, long hwnd, out Entry? entry,
        out MissReason miss)
    {
        entry = null;
        if (!_map.TryGetValue(id, out var e))
        {
            miss = MissReason.NoEntry;
            return false;
        }
        if (e.Hwnd != hwnd)
        {
            // recipe points at a different window than the fingerprint was
            // minted under (e.g. hwnd recycled across re-open) — the entry
            // cannot validate against this resolve; drop it
            _map.TryRemove(id, out _);
            miss = MissReason.HwndChanged;
            return false;
        }
        if (!NativeMethods.IsWindow(new IntPtr(e.Hwnd)))
        {
            _map.TryRemove(id, out _);
            miss = MissReason.DeadHwnd;
            return false;
        }
        e.Tick = Interlocked.Increment(ref _clock);
        entry = e;
        miss = MissReason.NoEntry; // unused on success
        return true;
    }

    /// <summary>Drop one fingerprint — a confirmed-dead element or a failed
    /// validation must not linger and validate against a recycled id.</summary>
    public void Remove(string id) => _map.TryRemove(id, out _);

    /// <summary>Same trigger as the live map: window destroy/show/structure
    /// change can reassign runtime ids and bounds — drop the fingerprints so
    /// a re-resolve cannot validate against a stale identity.</summary>
    public void InvalidateWindow(long hwnd)
    {
        foreach (var kv in _map)
            if (kv.Value.Hwnd == hwnd && _map.TryRemove(kv.Key, out _))
                Interlocked.Increment(ref _invalidated);
    }

    /// <summary>Desktop-wide structural reset — mirrors _live.Clear().</summary>
    public void Clear() => _map.Clear();
}
