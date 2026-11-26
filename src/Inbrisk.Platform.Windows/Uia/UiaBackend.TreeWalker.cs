using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

public sealed partial class UiaBackend : IElementBackend
{
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
            var childCached = kidsCached;
            if (!childCached && childrenCache != null)
            {
                try
                {
                    UiaPerf.ComCall();
                    if (child.BuildUpdatedCache(childrenCache) is { } cc)
                    { child = cc; childCached = true; }
                }
                catch { }
            }
            var step = StepFor(child, siblingCounts, childCached);
            path.Add(step);
            Walk(uia, child, opt, results, path, depth + 1, isRoot: false,
                ownerHwnd, ownerTitle, childrenCache, childCached);
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
            var (roots, scopeType, globalScan, rootsCached) = ResolveRoots(uia, spec);
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
                try
                {
                    IntPtr h;
                    if (rootsCached) { UiaPerf.CachedRead(); h = root.CachedNativeWindowHandle; }
                    else { UiaPerf.LiveRead(); h = root.CurrentNativeWindowHandle; }
                    if (h != IntPtr.Zero) ownerHwnd = h.ToInt64();
                }
                catch { }
                try
                {
                    if (rootsCached) { UiaPerf.CachedRead(); ownerTitle = root.CachedName; }
                    else { UiaPerf.LiveRead(); ownerTitle = root.CurrentName; }
                }
                catch { }
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
                if (arr.Length == 0 && TryRealize(root, spec, rootsCached))
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
    private static bool TryRealize(IUIAutomationElement root, FindSpec? spec = null, bool rootCached = false)
    {
        try
        {
            if (Pattern<IUIAutomationItemContainerPattern>(root, UiaIds.ItemContainerPattern, rootCached)
                    is not { } icp) return false;
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
    /// bug whenever a narrower root was available) + whether the roots
    /// arrived prefetched.</returns>
    private (List<IUIAutomationElement> Roots, string Scope, bool GlobalScan, bool RootsCached) ResolveRoots(
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
                return (new List<IUIAutomationElement> { scoped }, "element", false, false);
            }
            catch
            {
                // dead handle — evict it so every later touch also misses,
                // then fall through to hwnd/pid/global re-resolution
                _live.TryRemove(sid, out _);
                _pool.Remove(sid); // confirmed dead — drop the fingerprint too
            }
        }
        if (spec.Hwnd is { } h)
        {
            UiaPerf.ComCall();
            var el = uia.ElementFromHandle(new IntPtr(h));
            return (el != null ? new List<IUIAutomationElement> { el } : new(), "hwnd", false, false);
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
                return (r, spec.Pid != null ? "process" : "windowTitle", false, false);
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
            spec.WindowTitle != null ? "windowTitle" : "desktop", true, winsCached);
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
                LegacyIAccessibleStatePropertyId,
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
    /// name/pid/hwnd (+ItemContainerPattern for TryRealize) prefetched —
    /// one FindAllBuildCache round trip replaces two live property reads
    /// per window. Falls back to plain FindAll for providers that reject
    /// the cache request.</summary>
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
            try { c.AddPattern(UiaIds.ItemContainerPattern); } catch { }
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
}
