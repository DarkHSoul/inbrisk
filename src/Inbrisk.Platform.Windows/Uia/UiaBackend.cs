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
public sealed class UiaBackend : IElementBackend
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
    }

    /// <summary>Drop every live handle (desktop-wide structural reset).</summary>
    public void InvalidateAll() => _live.Clear();

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

    private void Walk(IUIAutomation uia, IUIAutomationElement el,
        InspectOptions opt, List<UiElement> results, List<AncestryStep> path,
        int depth, bool isRoot, long? ownerHwnd, string? ownerTitle,
        IUIAutomationCacheRequest? childrenCache, bool elCached)
    {
        if (results.Count >= opt.MaxElements) return;

        if (!isRoot)
        {
            if (depth > opt.MaxDepth) return;
            var converted = ToUiElement(uia, el, path.ToList(), ownerHwnd,
                ownerTitle, elCached, opt.IncludeOffscreen);
            if (converted != null) results.Add(converted);
        }

        // all children in ONE cached call — every child arrives with its
        // full property set already materialized instead of per-child
        // walker navigation + live reads. TrueCondition (not an
        // IsControlElement condition — it never matches in this interop)
        // keeps parity with ReResolve's raw-children assumption.
        IUIAutomationElementArray? kids = null;
        var kidsCached = false;
        var cond = uia.CreateTrueCondition();
        try
        {
            if (childrenCache != null)
            {
                UiaPerf.ComCall();
                kids = el.FindAllBuildCache(TreeScope.TreeScope_Children,
                    cond, childrenCache);
                kidsCached = true;
            }
        }
        catch (Exception e)
        {
            UiaPerf.Write(new { kind = "uia.inspectChildEnumFailed",
                depth, error = e.Message });
        }
        if (kids == null)
        {
            try { UiaPerf.ComCall(); kids = el.FindAll(TreeScope.TreeScope_Children, cond); }
            catch (Exception e2)
            {
                UiaPerf.Write(new { kind = "uia.inspectChildEnumFailed2",
                    depth, error = e2.Message });
            }
        }
        var len = kids?.Length ?? 0;

        // descend — count index among same-signature siblings for recipes
        var siblingCounts = new Dictionary<string, int>();
        for (var i = 0; i < len && results.Count < opt.MaxElements; i++)
        {
            var child = kids!.GetElement(i);
            var step = StepFor(child, siblingCounts, kidsCached);
            path.Add(step);
            Walk(uia, child, opt, results, path, depth + 1, isRoot: false,
                ownerHwnd, ownerTitle, childrenCache, kidsCached);
            path.RemoveAt(path.Count - 1);
        }
    }

    private static AncestryStep StepFor(IUIAutomationElement el,
        Dictionary<string, int> counts, bool cached = false)
    {
        var (role, name, aid) = Sig(el, cached);
        var key = $"{role}|{name}|{aid}";
        counts.TryGetValue(key, out var idx);
        counts[key] = idx + 1;
        return new AncestryStep(role, name, aid, idx);
    }

    private static (Role, string?, string?) Sig(IUIAutomationElement el,
        bool cached = false)
    {
        string? name = null, aid = null;
        var ct = 0;
        try
        {
            if (cached) { UiaPerf.CachedRead(); ct = el.CachedControlType; }
            else { UiaPerf.LiveRead(); ct = el.CurrentControlType; }
        }
        catch { }
        try
        {
            if (cached) { UiaPerf.CachedRead(); name = el.CachedName; }
            else { UiaPerf.LiveRead(); name = el.CurrentName; }
        }
        catch { }
        try
        {
            if (cached) { UiaPerf.CachedRead(); aid = el.CachedAutomationId; }
            else { UiaPerf.LiveRead(); aid = el.CurrentAutomationId; }
        }
        catch { }
        return (MapControlType(ct), string.IsNullOrEmpty(name) ? null : name,
            string.IsNullOrEmpty(aid) ? null : aid);
    }

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

    private Func<IUIAutomation?, IReadOnlyList<UiElement>> CreateFindWork(long callTicks, FindSpec spec, CancellationToken ct) => uia =>
    {
            if (uia == null) return (IReadOnlyList<UiElement>)Array.Empty<UiElement>();
            ct.ThrowIfCancellationRequested();
            // time spent queued behind other work on the UiaDispatcher —
            // surfaced as dispatchQueueMs in the uia.find event
            var queueMs = (Stopwatch.GetTimestamp() - callTicks)
                * 1000.0 / Stopwatch.Frequency;
            var total = Stopwatch.StartNew();
            UiaPerf.TakeReads();
            var t = Stopwatch.StartNew();
            var (roots, scopeType, globalScan) = ResolveRoots(uia, spec);
            var rootsMs = t.ElapsedMilliseconds;

            var results = new List<UiElement>();
            var cond = BuildCondition(uia, spec);
            var cache = TryCreateCache(uia, out var cacheProps);
            var ancMemo = new Dictionary<string, (AncestryStep Step, string? Parent)>();
            var sibMemo = new Dictionary<string,
                List<(string? Rt, Role R, string? N, string? A)>>();
            var ancStat = new AncStat();
            long enumMs = 0, ancMs = 0, convMs = 0;
            int enumerated = 0, realized = 0, enumCalls = 0;
            foreach (var root in roots)
            {
                // per-root owner identity — read once, not per candidate
                long? ownerHwnd = null; string? ownerTitle = null;
                try { UiaPerf.LiveRead(); var h = root.CurrentNativeWindowHandle; if (h != IntPtr.Zero) ownerHwnd = h.ToInt64(); } catch { }
                try { UiaPerf.LiveRead(); ownerTitle = root.CurrentName; } catch { }
                // existence-only fast path: FindFirst stops the provider
                // enumeration at the first native match. If that element is
                // rejected by client-side checks (offscreen/empty bounds),
                // fall back to the full FindAll below — a later match may
                // still pass, so skipping it would change correctness.
                if (spec.FirstOnly)
                {
                    t.Restart();
                    IUIAutomationElement? first = null;
                    try
                    {
                        enumCalls++;
                        UiaPerf.ComCall();
                        first = cache != null
                            ? root.FindFirstBuildCache(
                                TreeScope.TreeScope_Descendants, cond, cache)
                            : root.FindFirst(
                                TreeScope.TreeScope_Descendants, cond);
                    }
                    catch { }
                    enumMs += t.ElapsedMilliseconds;
                    if (first == null) continue; // no match under this root
                    var fa = BuildAncestry(uia, first, cache != null,
                        ancMemo, ancStat, sibMemo);
                    var fc = ToUiElement(uia, first, fa, ownerHwnd,
                        ownerTitle, cache != null, spec.IncludeOffscreen);
                    if (fc != null && (spec.Name == null ||
                        (fc.Name?.Contains(spec.Name,
                            StringComparison.OrdinalIgnoreCase) ?? false)))
                    {
                        enumerated = 1;
                        results.Add(fc);
                        goto done; // existence answered — skip enumeration
                    }
                    // first match rejected → full enumeration below
                }
                IUIAutomationElementArray? arr;
                t.Restart();
                try
                {
                    enumCalls++;
                    UiaPerf.ComCall();
                    arr = cache != null
                        ? root.FindAllBuildCache(
                            TreeScope.TreeScope_Descendants, cond, cache)
                        : root.FindAll(TreeScope.TreeScope_Descendants, cond);
                }
                catch { continue; }
                enumMs += t.ElapsedMilliseconds;

                // empty result under a virtualized container → realize its
                // targeted item or first item and re-enumerate
                if (arr.Length == 0 && TryRealize(root, spec))
                {
                    realized++;
                    t.Restart();
                    try
                    {
                        enumCalls++;
                        UiaPerf.ComCall();
                        arr = cache != null
                            ? root.FindAllBuildCache(
                                TreeScope.TreeScope_Descendants, cond, cache)
                            : root.FindAll(TreeScope.TreeScope_Descendants, cond);
                    }
                    catch { arr = null; }
                    enumMs += t.ElapsedMilliseconds;
                    if (arr == null) continue;
                }
                var len = arr.Length;
                enumerated += len;
                var cached = cache != null;
                for (var i = 0; i < len && results.Count < spec.MaxResults; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var el = arr.GetElement(i);
                    t.Restart();
                    var ancestry = BuildAncestry(uia, el, cached, ancMemo, ancStat, sibMemo);
                    ancMs += t.ElapsedMilliseconds;
                    t.Restart();
                    var converted = ToUiElement(uia, el, ancestry, ownerHwnd, ownerTitle, cached, spec.IncludeOffscreen);
                    convMs += t.ElapsedMilliseconds;
                    if (converted == null) continue;
                    if (spec.Name != null &&
                        !(converted.Name?.Contains(spec.Name, StringComparison.OrdinalIgnoreCase) ?? false))
                        continue; // contains-filter (condition used substring but be strict)
                    results.Add(converted);
                    // FirstOnly fell back to FindAll after its candidate was
                    // rejected — the first surviving match still answers it
                    if (spec.FirstOnly) goto done;
                }
            }
        done:
            var (live, cachedReads, comOps) = UiaPerf.TakeReads();
            // a desktop-global Descendants scan is the widest possible scope —
            // flagged so the audit can treat each one as a bug when a narrower
            // root (within:/hwnd/pid) was available
            var desktopGlobal = globalScan || scopeType == "desktop";
            UiaPerf.Write(new
            {
                kind = "uia.find",
                at = DateTimeOffset.Now,
                run = PerfTrace.CurrentId,
                scopeType,
                scopeHwnd = spec.Hwnd,
                scopePid = spec.Pid,
                scopeElementId = spec.ScopeElementId,
                treeScope = "Descendants",
                condition = CondDesc(spec),
                roots = roots.Count,
                method = spec.FirstOnly
                    ? cache != null ? "FindFirstBuildCache" : "FindFirst"
                    : cache != null ? "FindAllBuildCache" : "FindAll",
                enumCalls,
                cacheRequestProps = cacheProps,
                role = spec.Role?.ToString(),
                name = spec.Name,
                candidatesEnumerated = enumerated,
                candidatesConverted = results.Count,
                crossProcessPropertyReads = live,
                cachedReads,
                comOps,
                comCalls = live + enumCalls + comOps,
                cacheRequests = cache != null ? 1 : 0,
                desktopGlobal,
                virtualizedItemsRealized = realized,
                rootsMs,
                enumMs,
                ancestryMs = ancMs,
                ancLevels = ancStat.Levels,
                ancMemoHits = ancStat.Hits,
                ancRtNull = ancStat.RtNull,
                convertMs = convMs,
                dispatchQueueMs = Math.Round(queueMs, 2),
                uiaTimeMs = enumMs + ancMs + convMs + rootsMs,
                totalMs = total.ElapsedMilliseconds,
            });
            return (IReadOnlyList<UiElement>)results;
        };

    /// <summary>Compact machine-readable description of the pushed-down
    /// UIA condition — for per-query audit logging.</summary>
    private static string CondDesc(FindSpec s)
    {
        var parts = new List<string>();
        if (s.Role != null) parts.Add($"ct={s.Role}");
        if (s.AutomationId != null) parts.Add($"aid={s.AutomationId}");
        if (s.Name != null) parts.Add($"name~{s.Name}");
        if (s.ValueContains != null) parts.Add($"value~{s.ValueContains}");
        if (s.ValueEquals != null) parts.Add($"value={s.ValueEquals}");
        if (s.ClassName != null) parts.Add($"class={s.ClassName}");
        if (s.Enabled != null) parts.Add($"enabled={s.Enabled}");
        return parts.Count == 0 ? "true" : string.Join("&", parts);
    }

    /// <summary>Is this element the desktop root? Used to flag roots that
    /// fell back to the widest possible scope.</summary>
    private static bool IsDesktopRoot(IUIAutomation uia, IUIAutomationElement el)
    {
        try
        {
            UiaPerf.LiveRead();
            return el.CurrentNativeWindowHandle == IntPtr.Zero;
        }
        catch { return false; }
    }

    /// <summary>Container supports ItemContainerPattern → realize its targeted
    /// or first virtualized item so descendants materialize for re-enumeration.</summary>
    private static bool TryRealize(IUIAutomationElement root, FindSpec? spec = null)
    {
        try
        {
            UiaPerf.LiveRead();
            if (root.GetCurrentPattern(UiaIds.ItemContainerPattern)
                    is not IUIAutomationItemContainerPattern icp) return false;
            UiaPerf.LiveRead();
            IUIAutomationElement? item = null;
            if (!string.IsNullOrEmpty(spec?.Name))
            {
                UiaPerf.ComCall();
                item = icp.FindItemByProperty(null, UiaIds.NameProperty, spec.Name);
            }
            if (item == null && !string.IsNullOrEmpty(spec?.AutomationId))
            {
                UiaPerf.ComCall();
                item = icp.FindItemByProperty(null, UiaIds.AutomationIdProperty, spec.AutomationId);
            }
            if (item == null)
            {
                UiaPerf.ComCall();
                item = icp.FindItemByProperty(null, 0, null);
            }
            if (item == null) return false;
            UiaPerf.LiveRead();
            if (item.GetCurrentPattern(UiaIds.VirtualizedItemPattern)
                    is not IUIAutomationVirtualizedItemPattern vip) return false;
            UiaPerf.ComCall();
            vip.Realize();
            return true;
        }
        catch { return false; }
    }

    /// <returns>Roots + scope name + whether a desktop-global Descendants
    /// enumeration happened during resolution (the audit treats that as a
    /// bug whenever a narrower root was available).</returns>
    private (List<IUIAutomationElement> Roots, string Scope, bool GlobalScan) ResolveRoots(
        IUIAutomation uia, FindSpec spec)
    {
        // within:$element → search that element's subtree only. The live
        // element map holds it by id; a stale reference falls through to
        // the hwnd/pid scope (a strictly wider, still-correct superset).
        if (spec.ScopeElementId is { } sid &&
            LiveGet(sid) is { } scoped)
        {
            try
            {
                UiaPerf.LiveRead();
                _ = scoped.GetRuntimeId(); // alive probe
                return (new List<IUIAutomationElement> { scoped }, "element", false);
            }
            catch
            {
                // dead handle — evict it so every later touch also misses,
                // then fall through to hwnd/pid/global re-resolution
                _live.TryRemove(sid, out _);
            }
        }
        if (spec.Hwnd is { } h)
        {
            UiaPerf.ComCall();
            var el = uia.ElementFromHandle(new IntPtr(h));
            return (el != null ? new List<IUIAutomationElement> { el } : new(), "hwnd", false);
        }

        // pid/title scope → Win32 window list first (EnumWindows is local,
        // ~ms); UIA desktop scan only as the fallback for windows EnumWindows
        // skips (invisible/cloaked/untitled)
        if (spec.Pid != null || spec.WindowTitle != null)
        {
            var wins32 = _windows.ListWindows().Where(w =>
                (spec.Pid == null || w.Pid == spec.Pid) &&
                (spec.WindowTitle == null || w.Title.Contains(spec.WindowTitle,
                    StringComparison.OrdinalIgnoreCase))).ToList();
            var r = new List<IUIAutomationElement>();
            foreach (var w in wins32)
            {
                try
                {
                    UiaPerf.ComCall();
                    var el = uia.ElementFromHandle(new IntPtr(w.Hwnd));
                    if (el != null) r.Add(el);
                }
                catch { }
            }
            if (r.Count > 0)
                return (r, spec.Pid != null ? "process" : "windowTitle", false);
            // empty → fall through to the UIA-wide scan
        }

        UiaPerf.ComCall();
        var root = uia.GetRootElement();
        var roots = new List<IUIAutomationElement>();
        // Owned/child windows (e.g. dialogs with Owner) appear as descendants,
        // not as direct desktop children — search all Window-typed elements.
        // One cached Descendants fetch: name+pid for every top window arrive
        // in a single COM call instead of two live reads per window.
        var (wins, winsCached) = FindWindowElements(uia, root);
        var winsLen = wins?.Length ?? 0;
        for (var i = 0; i < winsLen; i++)
        {
            try
            {
                var w = wins!.GetElement(i);
                if (spec.WindowTitle != null)
                {
                    string? wn;
                    if (winsCached) { UiaPerf.CachedRead(); wn = w.CachedName; }
                    else { UiaPerf.LiveRead(); wn = w.CurrentName; }
                    if (!(wn?.Contains(spec.WindowTitle,
                            StringComparison.OrdinalIgnoreCase) ?? false))
                        continue;
                }
                if (spec.Pid is { } pid)
                {
                    int wp;
                    if (winsCached) { UiaPerf.CachedRead(); wp = w.CachedProcessId; }
                    else { UiaPerf.LiveRead(); wp = w.CurrentProcessId; }
                    if (wp != pid) continue;
                }
                roots.Add(w);
            }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return (roots, spec.Pid != null ? "process" :
            spec.WindowTitle != null ? "windowTitle" : "desktop", true);
    }

    /// <summary>Batch the property/pattern reads a conversion needs into one
    /// cache request — FindAllBuildCache returns elements whose properties,
    /// patterns and ancestor chain resolve locally. lightweight=true fetches
    /// only the name/pid needed for window-scope filtering.</summary>
    /// <summary>This UIA build rejects Element|Ancestors as a cache scope —
    /// remember the first rejection instead of paying one COM exception per
    /// Find call.</summary>
    private static int _ancestorCacheSupported = -1; // -1 unknown, 0 no, 1 yes

    private static IUIAutomationCacheRequest? TryCreateCache(IUIAutomation uia,
        out int cacheProps, bool parentScope = true)
    {
        cacheProps = 0;
        try
        {
            var c = uia.CreateCacheRequest();
            foreach (var p in new[]
            {
                UiaIds.BoundingRectangleProperty, UiaIds.ProcessIdProperty,
                UiaIds.ControlTypeProperty, UiaIds.LocalizedControlTypeProperty,
                UiaIds.NameProperty, UiaIds.HasKeyboardFocusProperty,
                UiaIds.IsEnabledProperty, UiaIds.AutomationIdProperty,
                UiaIds.ClassNameProperty, UiaIds.LabeledByProperty,
                UiaIds.IsPasswordProperty, UiaIds.NativeWindowHandleProperty,
                UiaIds.IsOffscreenProperty, UiaIds.ValueValueProperty,
                UiaIds.SelectionItemIsSelectedProperty,
                UiaIds.ExpandCollapseStateProperty, UiaIds.ToggleStateProperty,
            })
            {
                try { c.AddProperty(p); cacheProps++; }
                catch (Exception e)
                {
                    UiaPerf.Write(new { kind = "uia.cachePropFailed", id = p, error = e.Message });
                }
            }
            foreach (var (id, _) in UiaIds.PatternActions)
            {
                try { c.AddPattern(id); }
                catch (Exception e)
                {
                    UiaPerf.Write(new { kind = "uia.cachePatternFailed", id, error = e.Message });
                }
            }
            if (!parentScope)
            {
                // element-only cache — used for per-node children fetches
                // where the ancestry path is built by the DFS descent, so a
                // cached parent chain would be pure provider overhead
                c.TreeScope = TreeScope.TreeScope_Element;
            }
            else if (_ancestorCacheSupported == 1)
            {
                c.TreeScope = TreeScope.TreeScope_Element |
                              TreeScope.TreeScope_Ancestors;
            }
            else if (_ancestorCacheSupported == -1)
            {
                try
                {
                    c.TreeScope = TreeScope.TreeScope_Element |
                                  TreeScope.TreeScope_Ancestors;
                    _ancestorCacheSupported = 1;
                }
                catch (Exception e)
                {
                    UiaPerf.Write(new { kind = "uia.cacheScopeFailed", error = e.Message });
                    _ancestorCacheSupported = 0;
                }
            }
            if (_ancestorCacheSupported == 0)
            {
                // Element|Ancestors rejected, but Element|Parent is accepted —
                // each result's immediate parent (and its cached props) then
                // resolves locally, so the ancestry climb pays a live call
                // only per level beyond the first.
                try { c.TreeScope = TreeScope.TreeScope_Element |
                                    TreeScope.TreeScope_Parent; }
                catch { try { c.TreeScope = TreeScope.TreeScope_Element; } catch { } }
            }
            return c;
        }
        catch (Exception e)
        {
            UiaPerf.Write(new { kind = "uia.cacheFailed", error = e.Message });
            return null;
        }
    }

    private static IUIAutomationCondition BuildCondition(IUIAutomation uia, FindSpec spec)
    {
        var conds = new List<IUIAutomationCondition>();
        if (spec.Role is { } role && UnmapRole(role) is var ct && ct >= 0)
            conds.Add(uia.CreatePropertyCondition(UiaIds.ControlTypeProperty, ct));
        if (spec.AutomationId is { } aid)
            conds.Add(uia.CreatePropertyCondition(UiaIds.AutomationIdProperty, aid));
        if (spec.Name is { } name)
            conds.Add(uia.CreatePropertyConditionEx(UiaIds.NameProperty, name,
                PropertyConditionFlags.PropertyConditionFlags_IgnoreCase |
                PropertyConditionFlags.PropertyConditionFlags_MatchSubstring));
        const PropertyConditionFlags CI =
            PropertyConditionFlags.PropertyConditionFlags_IgnoreCase;
        if (spec.ValueContains is { } vc)
            conds.Add(uia.CreatePropertyConditionEx(UiaIds.ValueValueProperty, vc,
                CI | PropertyConditionFlags.PropertyConditionFlags_MatchSubstring));
        if (spec.ValueEquals is { } ve)
            conds.Add(uia.CreatePropertyConditionEx(UiaIds.ValueValueProperty, ve, CI));
        if (spec.ClassName is { } cn)
            conds.Add(uia.CreatePropertyConditionEx(UiaIds.ClassNameProperty, cn, CI));
        if (spec.Enabled is { } en)
            conds.Add(uia.CreatePropertyCondition(UiaIds.IsEnabledProperty, en ? 1 : 0));
        if (conds.Count == 0) return uia.CreateTrueCondition();
        return conds.Count == 1 ? conds[0] : uia.CreateAndConditionFromArray(conds.ToArray());
    }

    /// <summary>Parent-chain recipe for a flat (FindAll-produced) element.
    /// Three cost controls: (1) each level's parent is refreshed through a
    /// one-call Element|Parent cache so its props and grandparent resolve
    /// locally; (2) the sibling index comes from one batched children fetch
    /// instead of a per-sibling walker + triple property read; (3) `memo`
    /// shares already-walked chains between sibling candidates — a 50-item
    /// list pays the deep-window climb once, not 50 times.</summary>
    private sealed class AncStat { public int Levels, Hits, RtNull; }

    private List<AncestryStep> BuildAncestry(IUIAutomation uia,
        IUIAutomationElement el, bool cached,
        Dictionary<string, (AncestryStep Step, string? Parent)>? memo = null,
        AncStat? stat = null,
        Dictionary<string, List<(string? Rt, Role R, string? N, string? A)>>? sibMemo = null)
    {
        var walker = uia.ControlViewWalker;
        var pc = ParentCache(uia);
        var steps = new List<AncestryStep>();
        var cur = el;
        var curCached = cached;
        string? Rtk(IUIAutomationElement e, bool c) =>
            RuntimeIdOf(e, c) is { } rt ? string.Join(",", rt) : null;
        for (var guard = 0; guard < 64; guard++)
        {
            var curKey = Rtk(cur, curCached);
            if (curKey == null && stat != null) stat.RtNull++;
            // a previously-walked element's whole chain is in memo — replay it
            if (curKey != null && memo != null && memo.ContainsKey(curKey))
            {
                var k = curKey;
                while (memo.TryGetValue(k, out var node))
                {
                    steps.Add(node.Step);
                    k = node.Parent!;
                    if (k == null) break;
                }
                if (stat != null) stat.Hits++;
                break;
            }
            IUIAutomationElement? parent = null;
            // true when the parent arrived already cached (the find's
            // Element|Parent cache scope) — its props read locally and the
            // BuildUpdatedCache round trip is unnecessary.
            var parentCached = false;
            try
            {
                if (curCached)
                {
                    UiaPerf.CachedRead();
                    parent = cur.GetCachedParent();
                    parentCached = parent != null;
                }
                if (parent == null)
                {
                    UiaPerf.LiveRead();
                    parent = walker.GetParentElement(cur);
                }
            }
            catch { }
            if (parent == null) break;
            if (stat != null) stat.Levels++;
            // refresh the parent through a tiny Element|Parent cache — one COM
            // call covers its props AND prefetches the grandparent, so the
            // next level's GetCachedParent stays local too. Skipped when the
            // parent already arrived cached.
            if (!parentCached && pc != null)
            {
                try
                {
                    UiaPerf.ComCall();
                    var p2 = parent.BuildUpdatedCache(pc);
                    if (p2 != null) { parent = p2; parentCached = true; }
                }
                catch { }
            }
            var parentKey = Rtk(parent, parentCached);
            try
            {
                var (role, name, aid) = Sig(cur, curCached);
                var idx = 0;
                // sibling lists are memoized per parent — siblings under the
                // same parent share one FindAllBuildCache instead of paying
                // one enumeration per element
                List<(string? Rt, Role R, string? N, string? A)>? sibList = null;
                var sibHit = parentKey != null && sibMemo != null &&
                    sibMemo.TryGetValue(parentKey, out sibList);
                if (!sibHit)
                {
                    var siblings = FetchSiblings(uia, parent);
                    if (siblings != null)
                    {
                        sibList = new(siblings.Length);
                        for (var i = 0; i < siblings.Length; i++)
                        {
                            var sib = siblings.GetElement(i);
                            var (r2, n2, a2) = Sig(sib, true);
                            sibList.Add((Rtk(sib, true), r2, n2, a2));
                        }
                        if (parentKey != null && sibMemo != null)
                            sibMemo[parentKey] = sibList;
                    }
                }
                if (sibList != null)
                {
                    var curRt = Rtk(cur, curCached);
                    foreach (var (rt2, r, n, a) in sibList)
                    {
                        if (curRt != null && rt2 == curRt) break;
                        if (r == role && n == name && a == aid) idx++;
                    }
                }
                else
                {
                    UiaPerf.ComCall();
                    var sib = walker.GetFirstChildElement(parent);
                    while (sib != null)
                    {
                        UiaPerf.ComCall();
                        if (uia.CompareElements(sib, cur) != 0) break;
                        var (r, n, a) = Sig(sib, false);
                        if (r == role && n == name && a == aid) idx++;
                        UiaPerf.ComCall();
                        sib = walker.GetNextSiblingElement(sib);
                    }
                }
                var step = new AncestryStep(role, name, aid, idx);
                steps.Add(step);
                if (memo != null && curKey != null)
                    memo[curKey] = (step, parentKey);
            }
            catch { break; }
            cur = parent;
            curCached = parentCached;
            // stop at the window element (has own hwnd)
            try
            {
                if (parentCached)
                {
                    UiaPerf.CachedRead(); var ph = parent.CachedNativeWindowHandle;
                    UiaPerf.CachedRead(); var pct = parent.CachedControlType;
                    if (ph != IntPtr.Zero && pct == 50032 /* Window */) break;
                }
                else
                {
                    UiaPerf.LiveRead(); var ph = parent.CurrentNativeWindowHandle;
                    UiaPerf.LiveRead(); var pct = parent.CurrentControlType;
                    if (ph != IntPtr.Zero && pct == 50032 /* Window */) break;
                }
            }
            catch { break; }
        }
        steps.Reverse();
        return steps;
    }

    /// <summary>Element|Parent cache for ancestor-chain refresh — parents
    /// arrive cached so grandparent lookups stay local. Null when the
    /// provider rejects the scope (then ancestry falls back to live reads).</summary>
    /// <summary>Same UIA-build quirk as _ancestorCacheSupported — remember
    /// whether Element|Parent is accepted instead of retrying per level.</summary>
    private static int _parentCacheSupported = -1;

    private static IUIAutomationCacheRequest? ParentCache(IUIAutomation uia)
    {
        if (_parentCacheSupported == 0) return null;
        try
        {
            var c = uia.CreateCacheRequest();
            c.AddProperty(UiaIds.NameProperty);
            c.AddProperty(UiaIds.ControlTypeProperty);
            c.AddProperty(UiaIds.AutomationIdProperty);
            c.AddProperty(UiaIds.NativeWindowHandleProperty);
            c.TreeScope = TreeScope.TreeScope_Element | TreeScope.TreeScope_Parent;
            _parentCacheSupported = 1;
            return c;
        }
        catch
        {
            _parentCacheSupported = 0;
            return null;
        }
    }

    /// <summary>All top-level Window-typed elements under root with
    /// name/pid/hwnd prefetched — one FindAllBuildCache round trip
    /// replaces two live property reads per window. Falls back to plain
    /// FindAll for providers that reject the cache request.</summary>
    private static (IUIAutomationElementArray? Wins, bool Cached) FindWindowElements(
        IUIAutomation uia, IUIAutomationElement root)
    {
        var cond = uia.CreatePropertyCondition(
            UiaIds.ControlTypeProperty, 50032 /* Window */);
        try
        {
            var c = uia.CreateCacheRequest();
            c.AddProperty(UiaIds.NameProperty);
            c.AddProperty(UiaIds.ProcessIdProperty);
            c.AddProperty(UiaIds.NativeWindowHandleProperty);
            c.TreeScope = TreeScope.TreeScope_Element;
            UiaPerf.ComCall();
            var arr = root.FindAllBuildCache(
                TreeScope.TreeScope_Descendants, cond, c);
            if (arr != null) return (arr, true);
        }
        catch { }
        try
        {
            UiaPerf.ComCall();
            return (root.FindAll(TreeScope.TreeScope_Descendants, cond), false);
        }
        catch { return (null, false); }
    }

    /// <summary>One batched parent+children fetch (name/type/aid/runtimeId
    /// cached) — a single COM round trip replaces N sibling walker reads.
    /// TreeScope on the request doubles as the search scope for
    /// FindAllBuildCache: Element|Children returns the parent (skipped by
    /// the caller) plus its children, each with cached props.</summary>
    private static IUIAutomationElementArray? FetchSiblings(IUIAutomation uia,
        IUIAutomationElement parent)
    {
        try
        {
            var c = uia.CreateCacheRequest();
            c.AddProperty(UiaIds.NameProperty);
            c.AddProperty(UiaIds.ControlTypeProperty);
            c.AddProperty(UiaIds.AutomationIdProperty);
            // explicit Children search scope; Element cache scope gives each
            // returned element its own props (the parent itself is included
            // in results only when TreeScope_Element is in the search scope —
            // here it is not, so children only)
            c.TreeScope = TreeScope.TreeScope_Element;
            UiaPerf.ComCall();
            return parent.FindAllBuildCache(
                TreeScope.TreeScope_Children,
                uia.CreateTrueCondition(), c) is { } arr &&
                arr.Length > 0 ? arr : null;
        }
        catch { return null; }
    }

    /// <summary>GetRuntimeId is the one call that stays local on cached
    /// elements (runtime ids are stored in the element wrapper — the
    /// RuntimeId *property* is not valid in a cache request).</summary>
    private static int[]? RuntimeIdOf(IUIAutomationElement el, bool cached)
    {
        try
        {
            if (cached) UiaPerf.CachedRead(); else UiaPerf.LiveRead();
            return el.GetRuntimeId() as int[];
        }
        catch { return null; }
    }

    private static bool SameRuntimeId(IUIAutomationElement el, int[]? rt, bool cached)
    {
        if (rt == null) return false;
        var other = RuntimeIdOf(el, cached);
        return other != null && other.SequenceEqual(rt);
    }

    // ------------------------------------------------------------------
    //  Semantic actions
    // ------------------------------------------------------------------

    public Func<UiElement, ActionIntent, ActionResult?>? SyntheticPerformNative { get; set; }
    public Func<UiElement, string, PatternSupportState>? SyntheticProbePattern { get; set; }

    public PatternSupportState ProbePattern(UiElement element, string action)
    {
        if (SyntheticProbePattern != null) return SyntheticProbePattern(element, action);
        var el = Live(element);
        if (el == null) return PatternSupportState.Unknown;
        var pid = element.Pid ?? (element.Hwnd != null ? _windows.GetWindow(element.Hwnd.Value)?.Pid : null) ?? 0;
        try
        {
            var actionPatterns = UiaIds.PatternActions
                .Where(p => string.Equals(p.Action, action, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Id)
                .ToArray();

            if (actionPatterns.Length == 0)
            {
                element.RecordPatternProbe(action, supported: false, isTransientFailure: false);
                return PatternSupportState.Unsupported;
            }

            var callTicks = Stopwatch.GetTimestamp();
            bool found = false;
            _uia.Run(uia =>
            {
                var queueMs = (Stopwatch.GetTimestamp() - callTicks)
                    * 1000.0 / Stopwatch.Frequency;
                UiaPerf.TakeReads();
                var t = Stopwatch.StartNew();
                // ONE cache-update call fetches every candidate pattern —
                // replaces one GetCurrentPattern round trip per pattern id
                IUIAutomationElement ce = el;
                var cached = false;
                try
                {
                    var c = uia.CreateCacheRequest();
                    c.TreeScope = TreeScope.TreeScope_Element;
                    foreach (var p in actionPatterns)
                        try { c.AddPattern(p); } catch { }
                    UiaPerf.ComCall();
                    if (el.BuildUpdatedCache(c) is { } fresh)
                    { ce = fresh; cached = true; }
                }
                catch { }
                foreach (var patternId in actionPatterns)
                {
                    try
                    {
                        object? pat;
                        if (cached) { UiaPerf.CachedRead(); pat = ce.GetCachedPattern(patternId); }
                        else { UiaPerf.LiveRead(); pat = ce.GetCurrentPattern(patternId); }
                        if (pat != null)
                        {
                            found = true;
                            break;
                        }
                    }
                    catch (System.Runtime.InteropServices.COMException)
                        when (IsDead(el))
                    {
                        // dead element → propagate so the outer catch records
                        // a transient Unknown, not a false "Unsupported"
                        throw;
                    }
                    catch
                    {
                    }
                }
                var (live, cachedReads, comOps) = UiaPerf.TakeReads();
                UiaPerf.Write(new
                {
                    kind = "uia.probe",
                    at = DateTimeOffset.Now,
                    run = PerfTrace.CurrentId,
                    elementId = element.Id,
                    action,
                    found,
                    cached,
                    crossProcessPropertyReads = live,
                    cachedReads,
                    comOps,
                    comCalls = live + comOps,
                    dispatchQueueMs = Math.Round(queueMs, 2),
                    uiaTimeMs = t.ElapsedMilliseconds,
                });
                return true;
            }, 3000);

            if (found)
            {
                element.RecordPatternProbe(action, supported: true);
                return PatternSupportState.Supported;
            }
            else
            {
                element.RecordPatternProbe(action, supported: false, isTransientFailure: false);
                return PatternSupportState.Unsupported;
            }
        }
        catch
        {
            element.RecordPatternProbe(action, supported: false, isTransientFailure: true);
            return PatternSupportState.Unknown;
        }
    }

    /// <summary>Instrumented action work item — emits one uia.action JSONL
    /// event per dispatch with the COM-call split (live property reads vs
    /// boundary calls vs locally-cached reads) so Phase-2 batching deltas
    /// are measurable.</summary>
    private Func<IUIAutomation, string?> CreateActionWork(
        long callTicks, UiElement element, ActionIntent intent, CancellationToken ct) => uia =>
    {
        var queueMs = (Stopwatch.GetTimestamp() - callTicks)
            * 1000.0 / Stopwatch.Frequency;
        UiaPerf.TakeReads();
        var t = Stopwatch.StartNew();
        string? method = null; string? error = null;
        try { method = ExecuteNativeIntent(uia, element, intent, ct); }
        catch (System.Runtime.InteropServices.COMException e)
            // an element dying mid-action surfaces as a raw COM failure —
            // confirm with a live probe (this runs on the UIA thread) and
            // report the structured Stale error so the caller re-resolves
            when (Live(element) is not { } probe || IsDead(probe))
        {
            error = e.Message;
            throw Stale(element);
        }
        catch (Exception e) { error = e.Message; throw; }
        finally
        {
            var (live, cachedReads, comOps) = UiaPerf.TakeReads();
            UiaPerf.Write(new
            {
                kind = "uia.action",
                at = DateTimeOffset.Now,
                run = PerfTrace.CurrentId,
                elementId = element.Id,
                action = intent.Kind.ToString(),
                method,
                error,
                declined = method == null && error == null,
                crossProcessPropertyReads = live,
                cachedReads,
                comOps,
                comCalls = live + comOps,
                dispatchQueueMs = Math.Round(queueMs, 2),
                uiaTimeMs = t.ElapsedMilliseconds,
            });
        }
        return method;
    };

    public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default)
    {
        if (SyntheticPerformNative != null) return SyntheticPerformNative(element, intent);
        var sw = Stopwatch.StartNew();
        var pid = element.Pid ?? (element.Hwnd != null ? _windows.GetWindow(element.Hwnd.Value)?.Pid : null) ?? 0;
        _readScheduler?.NotifyMutationStarting(pid);
        var callTicks = Stopwatch.GetTimestamp();
        try
        {
            var method = _uia.Run(CreateActionWork(callTicks, element, intent, ct), ct: ct, intentName: intent.Kind.ToString());

            if (method == null) return null; // backend declines → caller falls back
            return new ActionResult(true, BackendId.Uia, method,
                new[] { new Attempt(BackendId.Uia, method, true, null, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed);
        }
        catch (InbriskException e)
        {
            return new ActionResult(false, BackendId.Uia, "UIA",
                new[] { new Attempt(BackendId.Uia, "UIA", false, e.Message, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed, e.Code, e.Message);
        }
        catch (Exception e)
        {
            return new ActionResult(false, BackendId.Uia, "UIA",
                new[] { new Attempt(BackendId.Uia, "UIA", false, e.Message, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Internal, e.Message);
        }
        finally
        {
            _readScheduler?.NotifyMutationCompleted(pid);
        }
    }

    public async Task<ActionResult?> PerformNativeAsync(UiElement element, ActionIntent intent, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var pid = element.Pid ?? (element.Hwnd != null ? _windows.GetWindow(element.Hwnd.Value)?.Pid : null) ?? 0;
        IAsyncDisposable? barrier = null;
        if (_readScheduler != null && pid > 0)
        {
            barrier = await _readScheduler.EnterMutationBarrierAsync(pid, ct).ConfigureAwait(false);
        }
        var callTicks = Stopwatch.GetTimestamp();
        try
        {
            var method = await _uia.RunAsync(CreateActionWork(callTicks, element, intent, ct), ct: ct, intentName: intent.Kind.ToString()).ConfigureAwait(false);

            if (method == null) return null; // backend declines → caller falls back
            return new ActionResult(true, BackendId.Uia, method,
                new[] { new Attempt(BackendId.Uia, method, true, null, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed);
        }
        catch (InbriskException e)
        {
            return new ActionResult(false, BackendId.Uia, "UIA",
                new[] { new Attempt(BackendId.Uia, "UIA", false, e.Message, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed, e.Code, e.Message);
        }
        catch (Exception e)
        {
            return new ActionResult(false, BackendId.Uia, "UIA",
                new[] { new Attempt(BackendId.Uia, "UIA", false, e.Message, sw.Elapsed) },
                VerifyResult.NotRequested, sw.Elapsed, ErrorCode.Internal, e.Message);
        }
        finally
        {
            if (barrier != null)
                await barrier.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Live cross-process probe — CurrentXxx on a dead element
    /// throws even when the handle arrived cached. Used to convert
    /// vanished-element COMExceptions into the structured Stale error.</summary>
    private static bool IsDead(IUIAutomationElement el)
    {
        try { UiaPerf.LiveRead(); _ = el.CurrentProcessId; return false; }
        catch { return true; }
    }

    private static int SafeInt(Func<int> f, int fallback = 0)
    { try { return f(); } catch { return fallback; } }

    private InbriskException Stale(UiElement element)
    {
        element.IsStale = true;
        _live.TryRemove(element.Handle.BackendRef, out _);
        return new InbriskException(ErrorCode.Stale, "element gone");
    }

    /// <summary>Intent-scoped cache: enabled/offscreen state (+read-only
    /// for SetValue) and exactly the patterns the action can use, plus
    /// ScrollItem for the offscreen pre-check. BuildUpdatedCache turns the
    /// whole pre-check + pattern fetch into ONE COM call.</summary>
    private static int _actionCacheSupported = -1; // as _ancestorCacheSupported

    private static IUIAutomationCacheRequest? TryCreateActionCache(
        IUIAutomation uia, ActionKind kind)
    {
        if (_actionCacheSupported == 0) return null;
        try
        {
            var c = uia.CreateCacheRequest();
            c.AddProperty(UiaIds.IsEnabledProperty);
            c.AddProperty(UiaIds.IsOffscreenProperty);
            if (kind == ActionKind.SetValue)
                c.AddProperty(UiaIds.ValueIsReadOnlyProperty);
            foreach (var p in ActionPatterns(kind))
                try { c.AddPattern(p); } catch { }
            c.TreeScope = TreeScope.TreeScope_Element;
            _actionCacheSupported = 1;
            return c;
        }
        catch
        {
            _actionCacheSupported = 0;
            return null;
        }
    }

    private static int[] ActionPatterns(ActionKind kind) => kind switch
    {
        ActionKind.Invoke or ActionKind.Click => new[]
            { UiaIds.InvokePattern, UiaIds.LegacyIAccessiblePattern,
              UiaIds.ScrollItemPattern },
        ActionKind.FocusElement => new[] { UiaIds.ScrollItemPattern },
        ActionKind.SetValue => new[] { UiaIds.ValuePattern,
            UiaIds.RangeValuePattern, UiaIds.ScrollItemPattern },
        ActionKind.Toggle => new[] { UiaIds.TogglePattern,
            UiaIds.ScrollItemPattern },
        ActionKind.Select => new[] { UiaIds.SelectionItemPattern,
            UiaIds.ScrollItemPattern },
        ActionKind.Expand or ActionKind.Collapse =>
            new[] { UiaIds.ExpandCollapsePattern, UiaIds.ScrollItemPattern },
        ActionKind.ScrollIntoView => new[] { UiaIds.ScrollItemPattern },
        _ => new[] { UiaIds.ScrollItemPattern },
    };

    private string? ExecuteNativeIntent(IUIAutomation uia, UiElement element, ActionIntent intent, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var el = Live(element);
        if (el == null) throw Stale(element);

        // ONE round trip replaces the live IsEnabled read plus each
        // GetCurrentPattern probe: BuildUpdatedCache fetches the state
        // props and this intent's patterns together. A dead handle throws
        // here (or on the live fallback below) — both convert to
        // ErrorCode.Stale so the caller re-resolves via the recipe instead
        // of surfacing a raw COM failure as Internal.
        IUIAutomationElement ce = el;
        var cached = false;
        try
        {
            if (TryCreateActionCache(uia, intent.Kind) is { } ac)
            {
                UiaPerf.ComCall();
                if (el.BuildUpdatedCache(ac) is { } fresh)
                { ce = fresh; cached = true; }
            }
        }
        catch (System.Runtime.InteropServices.COMException) when (IsDead(el))
        {
            throw Stale(element);
        }
        catch { /* cache rejected → live reads below */ }

        int enabled, offscreen;
        try
        {
            if (cached)
            {
                UiaPerf.CachedRead(); enabled = ce.CachedIsEnabled;
                UiaPerf.CachedRead(); offscreen = ce.CachedIsOffscreen;
            }
            else
            {
                UiaPerf.LiveRead(); enabled = ce.CurrentIsEnabled;
                UiaPerf.LiveRead(); offscreen = ce.CurrentIsOffscreen;
            }
        }
        catch (System.Runtime.InteropServices.COMException) when (IsDead(el))
        {
            throw Stale(element);
        }

        // disabled pre-check — same guard Click applies; focus is exempt
        // (UIA IsEnabled misreports on some focusable providers)
        if (enabled == 0 && intent.Kind != ActionKind.FocusElement)
            throw new InbriskException(ErrorCode.Disabled, "element is disabled");

        // offscreen pre-check for every element-targeted path including
        // focus: try ScrollItemPattern first so the action lands on a
        // visible element (mirrors the coordinate path's offscreen guard)
        if (offscreen != 0 && intent.Kind != ActionKind.ScrollIntoView &&
            Pattern<IUIAutomationScrollItemPattern>(ce, UiaIds.ScrollItemPattern, cached) is { } scr)
        {
            try { UiaPerf.ComCall(); scr.ScrollIntoView(); }
            catch (System.Runtime.InteropServices.COMException) when (IsDead(el))
            {
                throw Stale(element);
            }
            catch { /* best-effort — the action itself still runs */ }
        }

        ct.ThrowIfCancellationRequested();
        switch (intent.Kind)
        {
            case ActionKind.Invoke:
            case ActionKind.Click:
                if (Pattern<IUIAutomationInvokePattern>(ce, UiaIds.InvokePattern, cached) is { } inv)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    inv.Invoke();
                    element.RecordPatternProbe("invoke", true);
                    return "UIA.InvokePattern";
                }
                if (Pattern<IUIAutomationLegacyIAccessiblePattern>(ce, UiaIds.LegacyIAccessiblePattern, cached) is { } leg)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    leg.DoDefaultAction();
                    element.RecordPatternProbe("invoke", true);
                    return "UIA.LegacyIAccessible.DoDefaultAction";
                }
                element.RecordPatternProbe("invoke", false, isTransientFailure: false);
                return null; // → coordinate fallback
            case ActionKind.FocusElement:
                ct.ThrowIfCancellationRequested();
                UiaPerf.ComCall();
                try { ce.SetFocus(); }
                catch (System.Runtime.InteropServices.COMException) when (IsDead(el))
                {
                    throw Stale(element);
                }
                return "UIA.SetFocus";
            case ActionKind.SetValue:
                if (Pattern<IUIAutomationValuePattern>(ce, UiaIds.ValuePattern, cached) is { } val)
                {
                    ct.ThrowIfCancellationRequested();
                    var readOnly = cached
                        ? SafeInt(() =>
                        {
                            UiaPerf.CachedRead();
                            var v = ce.GetCachedPropertyValue(UiaIds.ValueIsReadOnlyProperty);
                            return v is int i ? i : v is true ? 1 : 0;
                        })
                        : SafeInt(() => { UiaPerf.LiveRead(); return val.CurrentIsReadOnly; });
                    if (readOnly != 0)
                        throw new InbriskException(ErrorCode.Disabled, "value is read-only");
                    UiaPerf.ComCall();
                    val.SetValue(intent.Args?.TryGetValue("text", out var t) == true ? t?.ToString() ?? "" : "");
                    return "UIA.ValuePattern";
                }
                if (Pattern<IUIAutomationRangeValuePattern>(ce, UiaIds.RangeValuePattern, cached) is { } rv)
                {
                    ct.ThrowIfCancellationRequested();
                    var num = System.Convert.ToDouble(intent.Args?["value"] ?? 0);
                    UiaPerf.ComCall();
                    rv.SetValue(num); return "UIA.RangeValuePattern";
                }
                return null;
            case ActionKind.Toggle:
                if (Pattern<IUIAutomationTogglePattern>(ce, UiaIds.TogglePattern, cached) is { } tog)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    tog.Toggle();
                    return "UIA.TogglePattern";
                }
                return null;
            case ActionKind.Select:
                if (Pattern<IUIAutomationSelectionItemPattern>(ce, UiaIds.SelectionItemPattern, cached) is { } sel)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    sel.Select();
                    return "UIA.SelectionItemPattern";
                }
                return null;
            case ActionKind.Expand:
                if (Pattern<IUIAutomationExpandCollapsePattern>(ce, UiaIds.ExpandCollapsePattern, cached) is { } ex)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    ex.Expand();
                    return "UIA.ExpandCollapsePattern";
                }
                return null;
            case ActionKind.Collapse:
                if (Pattern<IUIAutomationExpandCollapsePattern>(ce, UiaIds.ExpandCollapsePattern, cached) is { } col)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    col.Collapse();
                    return "UIA.ExpandCollapsePattern";
                }
                return null;
            case ActionKind.ScrollIntoView:
                if (Pattern<IUIAutomationScrollItemPattern>(ce, UiaIds.ScrollItemPattern, cached) is { } sc)
                {
                    ct.ThrowIfCancellationRequested();
                    UiaPerf.ComCall();
                    sc.ScrollIntoView();
                    return "UIA.ScrollItemPattern";
                }
                return null;
            default:
                return null;
        }
    }

    private static T? Pattern<T>(IUIAutomationElement el, int patternId,
        bool cached = false) where T : class
    {
        try
        {
            if (cached) { UiaPerf.CachedRead(); return el.GetCachedPattern(patternId) as T; }
            UiaPerf.LiveRead();
            return el.GetCurrentPattern(patternId) as T;
        }
        catch { return null; }
    }

    private IUIAutomationElement? Live(UiElement element) =>
        LiveGet(element.Handle.BackendRef);

    // ------------------------------------------------------------------
    //  Staleness / re-resolution
    // ------------------------------------------------------------------

    public Func<UiElement, bool>? SyntheticIsAlive { get; set; }

    public bool IsAlive(UiElement element)
    {
        if (SyntheticIsAlive != null) return SyntheticIsAlive(element);
        if (!element.IsStale && !_live.ContainsKey(element.Handle.BackendRef)) return false;
        try
        {
            // an id missing from the live map (evicted / invalidated) is
            // NOT alive — null-conditional probing would report true
            return _uia.Run(u =>
            {
                var el = Live(element);
                if (el == null) return false;
                UiaPerf.LiveRead();
                _ = el.GetRuntimeId();
                return true;
            }, 3000);
        }
        catch { element.IsStale = true; return false; }
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

    public async Task<UiElement?> ReResolveAsync(ElementHandle handle, CancellationToken ct = default)
    {
        var recipe = handle.Recipe;
        if (recipe.Hwnd is not { } hwnd) return null;
        var work = CreateReResolveWork(handle, ct);
        try
        {
            if (_readScheduler != null && recipe.Pid is > 0)
                return await _readScheduler.ScheduleReadAsync(recipe.Pid.Value,
                    work, 10000, ct, "ReResolve").ConfigureAwait(false);
            return await _uia.RunAsync(u => work(u), ct: ct, intentName: "ReResolve").ConfigureAwait(false);
        }
        catch { return null; }
    }

    private Func<IUIAutomation?, UiElement?> CreateReResolveWork(ElementHandle handle, CancellationToken ct) => uia =>
    {
        if (uia == null) return null;
        var recipe = handle.Recipe;
        if (recipe.Hwnd is not { } hwnd) return null;
        ct.ThrowIfCancellationRequested();
        UiaPerf.TakeReads();
        var t = Stopwatch.StartNew();
        var result = ReResolveCore(uia, recipe, hwnd);
        var (live, cachedReads, comOps) = UiaPerf.TakeReads();
        UiaPerf.Write(new
        {
            kind = "uia.reresolve",
            at = DateTimeOffset.Now,
            run = PerfTrace.CurrentId,
            elementId = handle.BackendRef,
            hwnd,
            resolved = result != null,
            crossProcessPropertyReads = live,
            cachedReads,
            comOps,
            comCalls = live + comOps,
            uiaTimeMs = t.ElapsedMilliseconds,
        });
        return result;
    };

    private UiElement? ReResolveCore(IUIAutomation uia,
        ReResolveRecipe recipe, long hwnd)
    {
        IUIAutomationElement? root = null;
        try { UiaPerf.ComCall(); root = uia.ElementFromHandle(new IntPtr(hwnd)); }
        catch { /* dead handle → fall through to title re-resolution */ }
        if (root == null && recipe.OwnerTitle != null)
        {
            // window was closed+reopened → hwnd changed; find by title+pid.
            // One cached Descendants fetch batches name+pid for every top
            // window into a single COM call (was: two live reads each).
            UiaPerf.ComCall();
            var (wins, winsCached) = FindWindowElements(uia, uia.GetRootElement());
            var winsLen = wins?.Length ?? 0;
            for (var i = 0; i < winsLen; i++)
            {
                var w = wins!.GetElement(i);
                try
                {
                    string? wn;
                    if (winsCached) { UiaPerf.CachedRead(); wn = w.CachedName; }
                    else { UiaPerf.LiveRead(); wn = w.CurrentName; }
                    if (wn?.Contains(recipe.OwnerTitle,
                            StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (recipe.Pid is { } p)
                    {
                        int wp;
                        if (winsCached) { UiaPerf.CachedRead(); wp = w.CachedProcessId; }
                        else { UiaPerf.LiveRead(); wp = w.CurrentProcessId; }
                        if (wp != p) continue;
                    }
                    root = w; break;
                }
                catch { }
            }
        }
        if (root == null) return (UiElement?)null;

        IUIAutomationElement? cur = root;
        // one cached children fetch per level — sig props for every
        // sibling arrive in a single cross-process call instead of
        // three live reads per child
        IUIAutomationCacheRequest? sigCache = null;
        try
        {
            sigCache = uia.CreateCacheRequest();
            sigCache.AddProperty(UiaIds.NameProperty);
            sigCache.AddProperty(UiaIds.ControlTypeProperty);
            sigCache.AddProperty(UiaIds.AutomationIdProperty);
            sigCache.TreeScope = TreeScope.TreeScope_Element;
        }
        catch { sigCache = null; }
        foreach (var step in recipe.AncestryPath)
        {
            // cached children fetch batches sibling sig props into
            // one call; providers that reject it fall back to plain
            // FindAll — a re-resolve must never die on a cache quirk
            IUIAutomationElementArray? children = null;
            var childrenCached = false;
            try
            {
                if (sigCache != null)
                {
                    UiaPerf.ComCall();
                    children = cur!.FindAllBuildCache(
                        TreeScope.TreeScope_Children,
                        uia.CreateTrueCondition(), sigCache);
                    childrenCached = true;
                }
            }
            catch { children = null; childrenCached = false; }
            if (children == null)
            {
                UiaPerf.ComCall();
                children = cur!.FindAll(TreeScope.TreeScope_Children,
                    uia.CreateTrueCondition());
            }
            IUIAutomationElement? next = null;
            var seen = 0;
            for (var i = 0; i < children.Length; i++)
            {
                var c = children.GetElement(i);
                var (r, n, a) = Sig(c, cached: childrenCached);
                if (r == step.Role && n == step.Name && a == step.AutomationId)
                {
                    if (seen == step.IndexAmongSiblings) { next = c; break; }
                    seen++;
                }
            }
            if (next == null) { cur = null; break; }
            cur = next;
        }

        if (cur == null) return (UiElement?)null;
        // one cache-update call covers every property the conversion
        // needs (patterns + state props included) instead of ~15
        // separate live reads
        var finalEl = cur;
        var cachedFinal = false;
        if (TryCreateCache(uia, out _) is { } fc)
        {
            try
            {
                UiaPerf.ComCall();
                var u = cur.BuildUpdatedCache(fc);
                if (u != null) { finalEl = u; cachedFinal = true; }
            }
            catch { }
        }
        // the walk consumed the full ancestry (last step = the element
        // itself) — the re-resolved element must keep the same full
        // path or a second re-resolve would stop at its parent
        return ToUiElement(uia, finalEl, recipe.AncestryPath.ToList(),
            recipe.Hwnd, recipe.OwnerTitle, cached: cachedFinal, allowOffscreen: true);
    }

    // ------------------------------------------------------------------
    //  Conversion + role map
    // ------------------------------------------------------------------

    private UiElement? ToUiElement(IUIAutomation uia, IUIAutomationElement el,
        List<AncestryStep> path, long? ownerHwnd, string? ownerTitle,
        bool cached = false, bool allowOffscreen = false)
    {
        tagRECT r;
        int isOffscreen = 0, controlType = 0, pid = 0;
        string? name = null, aid = null;
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            if (cached) { UiaPerf.CachedRead(); r = el.CachedBoundingRectangle; }
            else { UiaPerf.LiveRead(); r = el.CurrentBoundingRectangle; }
        }
        catch { return null; }
        try { if (cached) { UiaPerf.CachedRead(); isOffscreen = el.CachedIsOffscreen; } else { UiaPerf.LiveRead(); isOffscreen = el.CurrentIsOffscreen; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); controlType = el.CachedControlType; } else { UiaPerf.LiveRead(); controlType = el.CurrentControlType; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); name = el.CachedName; } else { UiaPerf.LiveRead(); name = el.CurrentName; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); aid = el.CachedAutomationId; } else { UiaPerf.LiveRead(); aid = el.CurrentAutomationId; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); pid = el.CachedProcessId; } else { UiaPerf.LiveRead(); pid = el.CurrentProcessId; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); hwnd = el.CachedNativeWindowHandle; } else { UiaPerf.LiveRead(); hwnd = el.CurrentNativeWindowHandle; } } catch { }

        var bounds = new RectPx(r.left, r.top, r.right - r.left, r.bottom - r.top);
        var isOff = isOffscreen != 0 || bounds.IsEmpty;
        if (isOff && !allowOffscreen) return null; // pruning: offscreen/empty

        // ids embed the minting pid — a later one-shot process can find the
        // persisted recipe file for exactly this element without ambiguity
        var id = $"uia_{Environment.ProcessId}_{Interlocked.Increment(ref _idCounter)}";
        int enabled = 1, focused = 0, password = 0;
        try { if (cached) { UiaPerf.CachedRead(); enabled = el.CachedIsEnabled; } else { UiaPerf.LiveRead(); enabled = el.CurrentIsEnabled; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); focused = el.CachedHasKeyboardFocus; } else { UiaPerf.LiveRead(); focused = el.CurrentHasKeyboardFocus; } } catch { }
        try { if (cached) { UiaPerf.CachedRead(); password = el.CachedIsPassword; } else { UiaPerf.LiveRead(); password = el.CurrentIsPassword; } } catch { }

        var props = new Dictionary<string, object?>
        {
            ["enabled"] = enabled != 0,
            ["focused"] = focused != 0,
            ["offscreen"] = isOff,
            ["localizedType"] = cached
                ? SafeStr(() => { UiaPerf.CachedRead(); return el.CachedLocalizedControlType; })
                : SafeStr(() => { UiaPerf.LiveRead(); return el.CurrentLocalizedControlType; }),
            ["className"] = cached
                ? SafeStr(() => { UiaPerf.CachedRead(); return el.CachedClassName; })
                : SafeStr(() => { UiaPerf.LiveRead(); return el.CurrentClassName; }),
        };
        string? labelledBy = null;
        try
        {
            IUIAutomationElement? label;
            if (cached) { UiaPerf.CachedRead(); label = el.CachedLabeledBy; }
            else { UiaPerf.LiveRead(); label = el.CurrentLabeledBy; }
            if (label != null)
            {
                // element-valued props arrive outside every cache scope —
                // the label's own props are never prefetched, so its name
                // is one live read, paid only for elements that have a label
                UiaPerf.LiveRead();
                labelledBy = label.CurrentName;
            }
        }
        catch { }
        if (labelledBy != null) props["labelledBy"] = labelledBy;
        if (password != 0) props["isPassword"] = true; // never expose value

        // state props for actionable patterns — pattern objects from the
        // cache still cross into the provider for *current* values, so read
        // the state through cached element properties instead
        if (password == 0 && Pattern<IUIAutomationValuePattern>(el, UiaIds.ValuePattern, cached) is { } vp)
            props["value"] = cached
                ? SafeStr(() => { UiaPerf.CachedRead(); return el.GetCachedPropertyValue(UiaIds.ValueValueProperty) as string; })
                : SafeStr(() => { UiaPerf.LiveRead(); return vp.CurrentValue; });
        if (Pattern<IUIAutomationTogglePattern>(el, UiaIds.TogglePattern, cached) is { } tog)
            props["toggleState"] = cached
                // GetCachedPropertyValue returns the raw enum int — cast
                // before ToString so the cached path yields the same
                // "ToggleState_*" names as CurrentToggleState
                ? SafeStr(() => { UiaPerf.CachedRead(); var v = el.GetCachedPropertyValue(UiaIds.ToggleStateProperty); return v is int ts ? ((ToggleState)ts).ToString() : v?.ToString(); })
                : tog.CurrentToggleState.ToString();
        if (Pattern<IUIAutomationExpandCollapsePattern>(el, UiaIds.ExpandCollapsePattern, cached) is { } ex)
            props["expandCollapseState"] = cached
                ? SafeStr(() => { UiaPerf.CachedRead(); var v = el.GetCachedPropertyValue(UiaIds.ExpandCollapseStateProperty); return v is int es ? ((ExpandCollapseState)es).ToString() : v?.ToString(); })
                : ex.CurrentExpandCollapseState.ToString();
        if (Pattern<IUIAutomationSelectionItemPattern>(el, UiaIds.SelectionItemPattern, cached) is { } si)
            props["selected"] = cached
                ? el.GetCachedPropertyValue(UiaIds.SelectionItemIsSelectedProperty) as bool? == true
                : si.CurrentIsSelected != 0;
        else if (Pattern<IUIAutomationLegacyIAccessiblePattern>(el, UiaIds.LegacyIAccessiblePattern, cached) is { } l2)
        { // STATE_SYSTEM_SELECTED bit — legacy fallback for list items
            try { UiaPerf.LiveRead(); props["selected"] = (l2.CurrentState & 0x2) != 0; } catch { }
        }

        var owner = ownerHwnd ?? (hwnd == IntPtr.Zero ? null : hwnd.ToInt64());
        var recipe = new ReResolveRecipe(pid == 0 ? null : pid, owner, ownerTitle,
            MapControlType(controlType), name, aid, path, bounds);
        var handle = new ElementHandle(BackendId.Uia, id, recipe);

        LivePut(id, el, owner);
        var uie = new UiElement(id, BackendId.Uia, MapControlType(controlType),
            string.IsNullOrEmpty(name) ? null : name, bounds,
            UiaIds.ActionsFor(uia, el, cached, enabled != 0), props, handle,
            pid == 0 ? null : pid, hwnd == IntPtr.Zero ? null : hwnd.ToInt64());
        uie.DefaultProbeFallback = a => ProbePattern(uie, a);
        return uie;
    }

    private static string? SafeStr(Func<string?> f) { try { return f(); } catch { return null; } }

    internal static Role MapControlType(int ct) => ct switch
    {
        50000 => Role.Button, 50001 => Role.Custom, 50002 => Role.CheckBox,
        50003 => Role.ComboBox, 50004 => Role.Edit, 50005 => Role.Hyperlink,
        50006 => Role.Image, 50007 => Role.ListItem, 50008 => Role.List,
        50009 => Role.Menu, 50010 => Role.Menu, 50011 => Role.MenuItem,
        50012 => Role.ProgressBar, 50013 => Role.RadioButton, 50014 => Role.ScrollBar,
        50015 => Role.Slider, 50016 => Role.Spinner, 50017 => Role.StatusBar,
        50018 => Role.Tab, 50019 => Role.TabItem, 50020 => Role.Text,
        50021 => Role.Toolbar, 50022 => Role.Text, 50023 => Role.Tree,
        50024 => Role.TreeItem, 50025 => Role.Custom, 50026 => Role.Group,
        50027 => Role.Custom, 50028 => Role.Table, 50029 => Role.DataItem,
        50030 => Role.Document, 50031 => Role.Button, 50032 => Role.Window,
        50033 => Role.Pane, 50034 => Role.Group, 50035 => Role.DataItem,
        50036 => Role.Table, 50037 => Role.TitleBar, 50038 => Role.Separator,
        50039 => Role.Custom, 50040 => Role.Pane,
        _ => Role.Unknown,
    };

    private static int UnmapRole(Role role) => role switch
    {
        Role.Button => 50000, Role.CheckBox => 50002, Role.ComboBox => 50003,
        Role.Edit => 50004, Role.Hyperlink => 50005, Role.Image => 50006,
        Role.ListItem => 50007, Role.List => 50008, Role.Menu => 50009,
        Role.MenuItem => 50011, Role.ProgressBar => 50012, Role.RadioButton => 50013,
        Role.ScrollBar => 50014, Role.Slider => 50015, Role.Spinner => 50016,
        Role.StatusBar => 50017, Role.Tab => 50018, Role.TabItem => 50019,
        Role.Text => 50020, Role.Toolbar => 50021, Role.Tree => 50023,
        Role.TreeItem => 50024, Role.Custom => 50025, Role.Group => 50026,
        Role.Table => 50036, Role.DataItem => 50029, Role.Document => 50030,
        Role.Window => 50032, Role.Pane => 50033, Role.TitleBar => 50037,
        Role.Separator => 50038,
        _ => -1,
    };
}
