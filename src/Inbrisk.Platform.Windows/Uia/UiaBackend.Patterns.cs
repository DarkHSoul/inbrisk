using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;
using Interop.UIAutomationClient;

namespace Inbrisk.Platform.Windows.Uia;

public sealed partial class UiaBackend : IElementBackend
{
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
        _pool.Remove(element.Handle.BackendRef); // confirmed dead — poison the fingerprint too
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
}
