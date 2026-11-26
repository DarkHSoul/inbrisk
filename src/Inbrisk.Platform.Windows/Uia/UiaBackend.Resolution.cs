using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

public sealed partial class UiaBackend : IElementBackend
{
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
        var result = ReResolveCore(uia, handle.BackendRef, recipe, hwnd,
            out var poolOutcome, out var poolMs);
        var (live, cachedReads, comOps) = UiaPerf.TakeReads();
        UiaPerf.Write(new
        {
            kind = "uia.reresolve",
            at = DateTimeOffset.Now,
            run = PerfTrace.CurrentId,
            elementId = handle.BackendRef,
            hwnd,
            resolved = result != null,
            pool = poolOutcome,
            poolMs = Math.Round(poolMs, 2),
            poolHits = _pool.Hits,
            poolMisses = _pool.Misses,
            poolSize = _pool.Count,
            crossProcessPropertyReads = live,
            cachedReads,
            comOps,
            comCalls = live + comOps,
            uiaTimeMs = t.ElapsedMilliseconds,
        });
        return result;
    };

    private UiElement? ReResolveCore(IUIAutomation uia, string elementId,
        ReResolveRecipe recipe, long hwnd,
        out string poolOutcome, out double poolMs)
    {
        poolOutcome = "skip:noRoot";
        poolMs = 0;
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

        // ---- hwnd-locked pool fast path ---------------------------------
        // When the live map dropped this element's handle (LRU overflow or a
        // dead-handle eviction) but the fingerprint survives, ONE provider-
        // side runtimeId (or exact-bounds) FindFirst replaces the per-level
        // children walk below. Validation failures fall through to the walk —
        // the pool never fabricates an element.
        {
            var pt = Stopwatch.StartNew();
            if (_pool.TryGet(elementId, hwnd, out var pe,
                    out var miss) && pe != null)
            {
                var fast = TryPoolResolve(uia, root, pe, recipe);
                poolMs = pt.Elapsed.TotalMilliseconds;
                if (fast != null)
                {
                    _pool.NoteHit();
                    _pool.Remove(elementId); // consumed — the re-minted id replaces it
                    poolOutcome = fast.Value.Via;
                    return fast.Value.Element;
                }
                _pool.NoteMiss();
                _pool.Remove(elementId); // fingerprint failed validation
                poolOutcome = "miss:notFound";
            }
            else
            {
                poolMs = pt.Elapsed.TotalMilliseconds;
                poolOutcome = miss switch
                {
                    HwndLockedElementPool.MissReason.HwndChanged =>
                        "miss:hwndChanged",
                    HwndLockedElementPool.MissReason.DeadHwnd =>
                        "miss:deadHwnd",
                    _ => "miss:noEntry",
                };
            }
        }

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

    /// <summary>This UIA build may reject RuntimeId / BoundingRectangle as
    /// search-condition properties — same tri-state pattern as
    /// _ancestorCacheSupported so the failure is paid once, not per resolve.</summary>
    private static int _runtimeIdCondSupported = -1; // -1 unknown, 0 no, 1 yes
    private static int _boundsCondSupported = -1;

    /// <summary>Pool-hit resolution: locate the element under the window root
    /// with ONE provider-side condition instead of the ancestry walk.
    /// Path A (runtimeId property condition) is authoritative — the provider
    /// itself performs the identity match. Path B (exact bounds + control
    /// type) covers providers that reassign runtime ids without tripping a
    /// structure event; it is corroborated by name/automationId before the
    /// candidate is trusted. Either way the winning element is refreshed
    /// through the full cache request so ToUiElement converts it locally.</summary>
    private (UiElement Element, string Via)? TryPoolResolve(IUIAutomation uia,
        IUIAutomationElement root, HwndLockedElementPool.Entry pe,
        ReResolveRecipe recipe)
    {
        IUIAutomationElement? el = null;
        var via = "hit:runtimeId";

        // Path A — runtimeId is the authoritative element identity. The
        // condition makes the PROVIDER do the match in the same cross-process
        // call that returns the element, so a non-null result is already
        // validated (spec: "runtimeId match …").
        if (pe.RuntimeId is { Length: > 0 } rt && _runtimeIdCondSupported != 0)
        {
            try
            {
                var cond = uia.CreatePropertyCondition(
                    HwndLockedElementPool.RuntimeIdPropertyId, rt);
                UiaPerf.ComCall();
                el = root.FindFirst(TreeScope.TreeScope_Descendants, cond);
                _runtimeIdCondSupported = 1;
            }
            catch { _runtimeIdCondSupported = 0; el = null; }
        }

        // Path B — runtime ids are provider-volatile: an element rebuilt
        // without a structure event keeps neither its COM handle nor its
        // runtime id. An exact bounds+controlType match is the fallback
        // identity; name/aid corroboration happens after the refresh below.
        if (el == null && !pe.Bounds.IsEmpty && _boundsCondSupported != 0)
        {
            try
            {
                var cond = uia.CreateAndConditionFromArray(new[]
                {
                    uia.CreatePropertyCondition(
                        UiaIds.ControlTypeProperty, pe.ControlType),
                    uia.CreatePropertyCondition(
                        UiaIds.BoundingRectangleProperty,
                        new double[] { pe.Bounds.X, pe.Bounds.Y,
                            pe.Bounds.Width, pe.Bounds.Height }),
                });
                UiaPerf.ComCall();
                el = root.FindFirst(TreeScope.TreeScope_Descendants, cond);
                _boundsCondSupported = 1;
                via = "hit:bounds";
            }
            catch { _boundsCondSupported = 0; el = null; }
        }
        if (el == null) return null;

        // one cache-update call covers every property the conversion needs
        var finalEl = el;
        var cached = false;
        if (TryCreateCache(uia, out _) is { } fc)
        {
            try
            {
                UiaPerf.ComCall();
                var u = el.BuildUpdatedCache(fc);
                if (u != null) { finalEl = u; cached = true; }
            }
            catch { }
        }

        // bounds matched a rectangle, not an identity — require the signature
        // (name AND automationId) to corroborate before trusting the
        // candidate; a same-rect different element must fall through to the
        // ancestry walk rather than be acted on.
        if (via == "hit:bounds")
        {
            var (_, bn, ba) = Sig(finalEl, cached);
            if (!string.Equals(bn ?? "", pe.Name ?? "", StringComparison.Ordinal) ||
                !string.Equals(ba ?? "", pe.AutomationId ?? "", StringComparison.Ordinal))
                return null;
        }

        var uie = ToUiElement(uia, finalEl, recipe.AncestryPath.ToList(),
            recipe.Hwnd, recipe.OwnerTitle, cached, allowOffscreen: true);
        return uie == null ? null : (uie, via);
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
            if (cached)
            {
                // the state is in the cache request — a local read instead of
                // a cross-process LegacyIAccessible.State call per element
                try
                {
                    UiaPerf.CachedRead();
                    if (el.GetCachedPropertyValue(LegacyIAccessibleStatePropertyId) is int ls)
                        props["selected"] = (ls & 0x2) != 0;
                }
                catch { }
            }
            else
            {
                try { UiaPerf.LiveRead(); props["selected"] = (l2.CurrentState & 0x2) != 0; } catch { }
            }
        }

        var owner = ownerHwnd ?? (hwnd == IntPtr.Zero ? null : hwnd.ToInt64());
        var recipe = new ReResolveRecipe(pid == 0 ? null : pid, owner, ownerTitle,
            MapControlType(controlType), name, aid, path, bounds);
        var handle = new ElementHandle(BackendId.Uia, id, recipe);

        LivePut(id, el, owner);
        // fingerprint for the hwnd-locked re-resolution pool — GetRuntimeId
        // is local on a cached element; on the uncached fallback it is one
        // extra read that pays for O(1) re-resolves over the element's whole
        // registration life. Entries with no owning window can't be locked.
        if (owner is { } poolHwnd)
            _pool.Put(id, poolHwnd, bounds, controlType, name, aid,
                RuntimeIdOf(el, cached));
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
