using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// The UIA perception/control backend. All COM access happens on the
/// UiaDispatcher's dedicated MTA thread under timeouts.
/// </summary>
public sealed partial class UiaBackend : IElementBackend
{
    private readonly UiaDispatcher _uia;
    private readonly IWindowService _windows;

    private readonly UiaReadScheduler? _readScheduler;

    /// <summary>Live element entry — the COM reference plus the owning
    /// window (for targeted invalidation) and an access stamp for LRU
    /// eviction.</summary>
    private sealed class LiveEntry(IUIAutomationElement el, long? hwnd)
    {
        public IUIAutomationElement El { get; } = el;
        public long? Hwnd { get; } = hwnd;
        public long Tick;
    }

    /// <summary>elementId → live COM element. Bounded LRU so a long session
    /// cannot accumulate handles forever; entries drop on window
    /// destroy/show/structure-change via InvalidateWindow. Using a dropped
    /// id fails fast with ErrorCode.Stale → recipe re-resolution, instead
    /// of acting on a dead handle.</summary>
    private readonly ConcurrentDictionary<string, LiveEntry> _live = new();

    private long _liveClock;
    private int _idCounter;

    /// <summary>elementId → re-resolution fingerprint (hwnd, bounds,
    /// runtimeId). Survives live-map eviction so ReResolve can re-locate an
    /// element with one provider-side match instead of the ancestry walk.</summary>
    private readonly HwndLockedElementPool _pool = new();

    /// <summary>UIA_LegacyIAccessibleStatePropertyId — element-level mirror
    /// of LegacyIAccessiblePattern.State; in the cache request it lets the
    /// "selected" bit resolve locally instead of one live pattern read per
    /// legacy element.</summary>
    private const int LegacyIAccessibleStatePropertyId = 30146;

    /// <summary>Live-map capacity. Generous enough that a full Inspect of a
    /// large window (≤ MaxElements) never self-evicts.</summary>
    public int MaxLiveElements { get; set; } = 4096;

    public int LiveCount => _live.Count;

    private IUIAutomationElement? LiveGet(string id)
    {
        if (!_live.TryGetValue(id, out var e)) return null;
        e.Tick = Interlocked.Increment(ref _liveClock);
        return e.El;
    }

    private void LivePut(string id, IUIAutomationElement el, long? hwnd)
    {
        _live[id] = new LiveEntry(el, hwnd)
            { Tick = Interlocked.Increment(ref _liveClock) };
        if (_live.Count > MaxLiveElements) EvictLiveOverflow();
    }

    private void EvictLiveOverflow()
    {
        // amortized eviction — drop the overflow plus a quarter of the cap
        // worth of least-recently-touched entries, not one per insert
        var n = _live.Count - MaxLiveElements + MaxLiveElements / 4;
        if (n <= 0) return;
        foreach (var k in _live.OrderBy(kv => kv.Value.Tick)
                     .Take(n).Select(kv => kv.Key).ToList())
            _live.TryRemove(k, out _);
    }

    /// <summary>Drop every live handle owned by a window — invoked on
    /// WinEvent destroy/show and UIA structure-change. The registry marks
    /// the UiElements stale; this drops the COM handles so the next touch
    /// returns ErrorCode.Stale → re-resolve instead of a dead-handle COM
    /// exception mid-action.</summary>
    public void InvalidateWindow(long hwnd)
    {
        foreach (var kv in _live)
            if (kv.Value.Hwnd == hwnd) _live.TryRemove(kv.Key, out _);
        _pool.InvalidateWindow(hwnd);
    }

    /// <summary>Drop every live handle (desktop-wide structural reset).</summary>
    public void InvalidateAll()
    {
        _live.Clear();
        _pool.Clear();
    }

    public BackendId Id => BackendId.Uia;

    public UiaBackend(UiaDispatcher dispatcher, IWindowService windows, UiaReadScheduler? readScheduler = null)
    {
        _uia = dispatcher;
        _windows = windows;
        _readScheduler = readScheduler;
    }

    // ------------------------------------------------------------------
    //  Inspect — pruned control-view tree walk
    // ------------------------------------------------------------------

    public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default)
    {
        var callTicks = Stopwatch.GetTimestamp();
        var pid = _windows.GetWindow(hwnd)?.Pid ?? 0;
        var work = CreateInspectWork(callTicks, hwnd, options, ct);

        if (_readScheduler != null && pid > 0)
            return _readScheduler.ScheduleRead(pid, work, 10000, ct, "Inspect");
        return _uia.Run(u => work(u), ct: ct, intentName: "Inspect");
    }

    public async Task<IReadOnlyList<UiElement>> InspectAsync(long hwnd, InspectOptions options, CancellationToken ct = default)
    {
        var callTicks = Stopwatch.GetTimestamp();
        var pid = _windows.GetWindow(hwnd)?.Pid ?? 0;
        var work = CreateInspectWork(callTicks, hwnd, options, ct);

        if (_readScheduler != null && pid > 0)
            return await _readScheduler.ScheduleReadAsync(pid, work, 10000, ct, "Inspect").ConfigureAwait(false);
        return await _uia.RunAsync(u => work(u), ct: ct, intentName: "Inspect").ConfigureAwait(false);
    }

    private Func<IUIAutomation?, IReadOnlyList<UiElement>> CreateInspectWork(long callTicks, long hwnd, InspectOptions options, CancellationToken ct) => uia =>
    {
        if (uia == null) return (IReadOnlyList<UiElement>)Array.Empty<UiElement>();
        ct.ThrowIfCancellationRequested();
        var queueMs = (Stopwatch.GetTimestamp() - callTicks)
            * 1000.0 / Stopwatch.Frequency;
        var sw = Stopwatch.StartNew();
        IUIAutomationElement? root = null;
        try { UiaPerf.ComCall(); root = uia.ElementFromHandle(new IntPtr(hwnd)); }
        catch { return (IReadOnlyList<UiElement>)Array.Empty<UiElement>(); }
        if (root == null) return (IReadOnlyList<UiElement>)Array.Empty<UiElement>();

        var results = new List<UiElement>();
        var ownerTitle = _windows.GetWindow(hwnd)?.Title;
        // Shared pipeline with Find: each node's children arrive via one
        // FindAllBuildCache — every child's props, patterns and state
        // resolve locally instead of ~15 live cross-process reads per
        // element. DFS is kept (not a flat Descendants fetch) because
        // the ancestry path then comes free from the descent and
        // depth/offscreen pruning stays exact.
        var childrenCache = TryCreateCache(uia, out _,
            parentScope: false);
        Walk(uia, root, options, results, new List<AncestryStep>(),
            depth: 0, isRoot: true, ownerHwnd: hwnd, ownerTitle,
            childrenCache, elCached: false);
        var (live, cachedReads, comOps) = UiaPerf.TakeReads();
        UiaPerf.Write(new
        {
            kind = "uia.inspect",
            at = DateTimeOffset.Now,
            run = PerfTrace.CurrentId,
            hwnd,
            elements = results.Count,
            crossProcessPropertyReads = live,
            cachedReads,
            comOps,
            comCalls = live + comOps,
            dispatchQueueMs = Math.Round(queueMs, 2),
            totalMs = sw.ElapsedMilliseconds,
        });
        return (IReadOnlyList<UiElement>)results;
    };

    public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default)
    {
        var callTicks = Stopwatch.GetTimestamp();
        int pid = spec.Pid ?? (spec.Hwnd != null ? _windows.GetWindow(spec.Hwnd.Value)?.Pid : null) ?? 0;
        var work = CreateFindWork(callTicks, spec, ct);

        if (_readScheduler != null && pid > 0)
            return _readScheduler.ScheduleRead(pid, work, 10000, ct, "Find");
        return _uia.Run(u => work(u), ct: ct, intentName: "Find");
    }

    public async Task<IReadOnlyList<UiElement>> FindAsync(FindSpec spec, CancellationToken ct = default)
    {
        var callTicks = Stopwatch.GetTimestamp();
        int pid = spec.Pid ?? (spec.Hwnd != null ? _windows.GetWindow(spec.Hwnd.Value)?.Pid : null) ?? 0;
        var work = CreateFindWork(callTicks, spec, ct);

        if (_readScheduler != null && pid > 0)
            return await _readScheduler.ScheduleReadAsync(pid, work, 10000, ct, "Find").ConfigureAwait(false);
        return await _uia.RunAsync(u => work(u), ct: ct, intentName: "Find").ConfigureAwait(false);
    }

    public void PurgePendingQueue() => _uia.PurgeQueue();

    public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default)
    {
        var recipe = handle.Recipe;
        if (recipe.Hwnd is not { } hwnd) return null;
        var work = CreateReResolveWork(handle, ct);
        try
        {
            // a read — goes through the per-process read lanes so a slow
            // resolve cannot serialize behind unrelated mutations
            if (_readScheduler != null && recipe.Pid is > 0)
                return _readScheduler.ScheduleRead(recipe.Pid.Value, work,
                    10000, ct, "ReResolve");
            return _uia.Run(u => work(u), ct: ct, intentName: "ReResolve");
        }
        catch { return null; }
    }
}
