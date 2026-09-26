using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed record WaitResult(
    bool Success,
    string Reason,
    TimeSpan Elapsed,
    UiElement? MatchedElement = null,
    bool InterruptedByDialog = false,
    long? DialogHwnd = null,
    string? DialogTitle = null);

public sealed record WaitCondition(
    string? Name = null,
    string? NameContains = null,
    Role? Role = null,
    string? ExpectedState = null,
    string? ExpectedValue = null,
    bool Gone = false,
    int? MinCount = null,
    string? ScopeElementId = null,
    long? Hwnd = null,
    int? Pid = null,
    bool StopOnUnexpectedDialog = true,
    IReadOnlyList<RectPx>? IgnoreRegions = null);

/// <summary>
/// wait_for_* primitives driven by events first, polling as backstop:
/// - WaitForElement: find retry loop boosted by structure/name events
/// - ForCondition: semantic expectations (appearance, state, value, gone, minCount, unexpected dialog guards)
/// - WaitForStable: consecutive frame samples must stay quiet for quietMs (with ignoreRegions support)
/// - WaitForEvent: predicate over the semantic event stream
/// - WaitForGone: element stops being found / goes stale
/// </summary>
public sealed class WaitService
{
    private readonly Func<FindSpec, IReadOnlyList<UiElement>> _find;
    private readonly ICaptureService _capture;
    private readonly IEventSource _events;
    private readonly ElementRegistry _registry;
    private readonly IWindowService? _windows;

    public WaitService(Func<FindSpec, IReadOnlyList<UiElement>> find,
        ICaptureService capture, IEventSource events, ElementRegistry registry,
        IWindowService? windows = null)
    {
        _find = find;
        _capture = capture;
        _events = events;
        _registry = registry;
        _windows = windows;
    }

    public WaitResult ForElement(FindSpec spec, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.element");
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        // event-driven fast path: a structure/name/value change anywhere near
        // the scope re-checks immediately; the 200ms tick stays as the bounded
        // fallback for providers that under-report.
        var wake = new ManualResetEventSlim();
        void H(ObservedEvent e)
        {
            if (spec.Hwnd == null || e.Hwnd is null or 0 || e.Hwnd == spec.Hwnd)
                wake.Set();
        }
        _events.Event += H;
        try
        {
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                // existence-only: the backend may stop at the first native
                // match (FindFirst) — ambiguity/selection semantics do not
                // apply to a wait, only Count > 0 is consumed below
                var found = _find(spec with { FirstOnly = true });
                PerfTrace.Count("wait.findEnumerations");
                if (found.Count > 0)
                {
                    _registry.Register(found);
                    return new WaitResult(true, $"found {found[0].Id}", sw.Elapsed, MatchedElement: found[0]);
                }
                wake.Reset();
                var woke = WaitHandle.WaitAny(
                    [wake.WaitHandle, ct.WaitHandle], 200) == 0;
                PerfTrace.Count(woke ? "wait.eventWakeups" : "wait.pollWakeups");
            }
            return new WaitResult(false, "timeout", sw.Elapsed);
        }
        finally { _events.Event -= H; }
    }

    /// <summary>Check if an unexpected modal dialog or alert opened.</summary>
    public (long Hwnd, string Title)? CheckUnexpectedDialog(long? scopedHwnd)
    {
        if (_windows == null) return null;
        var fg = _windows.GetForegroundWindow();
        if (fg == null) return null;
        if (scopedHwnd != null && scopedHwnd > 0 && fg.Hwnd == scopedHwnd.Value) return null;

        var title = fg.Title ?? "";
        var isDialogTitle = title.Contains("Dialog", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Hata", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Uyarı", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Confirm", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Onayla", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Notice", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Alert", StringComparison.OrdinalIgnoreCase);

        if (isDialogTitle)
            return (fg.Hwnd, title.Length > 0 ? title : "Dialog");

        if (scopedHwnd != null && scopedHwnd > 0 && fg.Hwnd != scopedHwnd.Value)
        {
            try
            {
                var buttons = _find(new FindSpec(Hwnd: fg.Hwnd, Role: Role.Button, MaxResults: 8));
                var hasDialogButtons = buttons.Any(b =>
                    b.Name is "OK" or "Cancel" or "Tamam" or "İptal" or "Yes" or "No" or "Evet" or "Hayır" or "Close" or "Kapat");
                if (hasDialogButtons)
                    return (fg.Hwnd, title.Length > 0 ? title : "Dialog");
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Wait for task-scoped expectation: appearance, property match (state/value),
    /// disappearance (gone), element counts, while guarding against unexpected modal dialogs.
    /// </summary>
    public WaitResult ForCondition(WaitCondition cond, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.condition");
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);

        var wake = new ManualResetEventSlim();
        void H(ObservedEvent e)
        {
            if (cond.Hwnd == null || e.Hwnd is null or 0 || e.Hwnd == cond.Hwnd)
                wake.Set();
        }
        _events.Event += H;
        try
        {
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();

                // Check for unexpected modal dialog
                if (cond.StopOnUnexpectedDialog)
                {
                    var diag = CheckUnexpectedDialog(cond.Hwnd);
                    if (diag != null)
                    {
                        return new WaitResult(false,
                            $"interrupted_by_dialog: unexpected dialog '{diag.Value.Title}' (0x{diag.Value.Hwnd:X}) appeared",
                            sw.Elapsed,
                            InterruptedByDialog: true,
                            DialogHwnd: diag.Value.Hwnd,
                            DialogTitle: diag.Value.Title);
                    }
                }

                if (cond.Gone)
                {
                    if (!string.IsNullOrEmpty(cond.ScopeElementId))
                    {
                        if (_registry.EnsureAlive(cond.ScopeElementId) == null)
                            return new WaitResult(true, "element gone", sw.Elapsed);
                    }
                    else
                    {
                        var found = _find(new FindSpec(
                            Hwnd: cond.Hwnd,
                            Pid: cond.Pid,
                            Role: cond.Role,
                            Name: cond.Name,
                            FirstOnly: true));
                        if (found.Count == 0)
                            return new WaitResult(true, "element gone", sw.Elapsed);
                    }
                }
                else
                {
                    var spec = new FindSpec(
                        Hwnd: cond.Hwnd,
                        Pid: cond.Pid,
                        Role: cond.Role,
                        Name: cond.Name,
                        ScopeElementId: cond.ScopeElementId,
                        MaxResults: cond.MinCount ?? 20);

                    var candidates = _find(spec);
                    if (cond.NameContains != null)
                    {
                        candidates = candidates.Where(e =>
                            e.Name?.Contains(cond.NameContains, StringComparison.OrdinalIgnoreCase) == true).ToList();
                    }

                    if (cond.ExpectedState != null)
                    {
                        candidates = candidates.Where(e =>
                        {
                            var exp = cond.ExpectedState.ToLowerInvariant();
                            if (e.Props.TryGetValue("state", out var st) && st?.ToString()?.Contains(exp, StringComparison.OrdinalIgnoreCase) == true)
                                return true;
                            if (e.Props.TryGetValue(exp, out var bVal) && bVal is true or 1)
                                return true;
                            return false;
                        }).ToList();
                    }

                    if (cond.ExpectedValue != null)
                    {
                        candidates = candidates.Where(e =>
                            e.Props.TryGetValue("value", out var v) &&
                            v?.ToString()?.Contains(cond.ExpectedValue, StringComparison.OrdinalIgnoreCase) == true).ToList();
                    }

                    var min = cond.MinCount ?? 1;
                    if (candidates.Count >= min)
                    {
                        _registry.Register(candidates);
                        return new WaitResult(true,
                            $"condition met: found {candidates.Count} match{(candidates.Count > 1 ? "es" : "")} ({candidates[0].Id})",
                            sw.Elapsed,
                            MatchedElement: candidates[0]);
                    }
                }

                wake.Reset();
                var woke = WaitHandle.WaitAny([wake.WaitHandle, ct.WaitHandle], 200) == 0;
                PerfTrace.Count(woke ? "wait.eventWakeups" : "wait.pollWakeups");
            }

            return new WaitResult(false, cond.Gone ? "timeout: element still present" : "timeout: condition not met", sw.Elapsed);
        }
        finally
        {
            _events.Event -= H;
        }
    }

    /// <summary>Wait until a ChangeMonitor reports change started
    /// (first diff above threshold or semantic event).</summary>
    public WaitResult ForChange(ChangeMonitor monitor, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.change");
        var sw = Stopwatch.StartNew();
        if (monitor.Stability.State == StabilityState.Changing)
            return new WaitResult(true, "already changing", sw.Elapsed);
        var hit = new ManualResetEventSlim();
        void H(StabilityInfo s) { if (s.State == StabilityState.Changing) hit.Set(); }
        monitor.StabilityChanged += H;
        try
        {
            var ok = hit.Wait(timeoutMs, ct);
            PerfTrace.Count(ok ? "wait.eventWakeups" : "wait.pollWakeups");
            return ok
                ? new WaitResult(true, "change detected", sw.Elapsed)
                : new WaitResult(false, "timeout waiting for change", sw.Elapsed);
        }
        finally { monitor.StabilityChanged -= H; }
    }

    /// <summary>Wait until a ChangeMonitor reports Stable (quiet for its
    /// configured quietMs) — the post-action "screen settled" primitive.</summary>
    public WaitResult ForStable(ChangeMonitor monitor, int timeoutMs = 15000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.stable");
        var sw = Stopwatch.StartNew();
        var hit = new ManualResetEventSlim();
        void H(StabilityInfo s) { if (s.State == StabilityState.Stable) hit.Set(); }
        monitor.StabilityChanged += H;
        try
        {
            if (monitor.Stability.State == StabilityState.Stable)
                return new WaitResult(true, "already stable", sw.Elapsed);
            var ok = hit.Wait(timeoutMs, ct);
            PerfTrace.Count(ok ? "wait.eventWakeups" : "wait.pollWakeups");
            return ok
                ? new WaitResult(true, $"stable ({monitor.Stability.StableFor.TotalMilliseconds:F0}ms quiet)", sw.Elapsed)
                : new WaitResult(false, "timeout waiting for stability", sw.Elapsed);
        }
        finally { monitor.StabilityChanged -= H; }
    }

    /// <summary>Screen is "stable" when consecutive small samples show no
    /// change for quietMs within timeoutMs. Supports ignoring noisy regions (progress bars, clocks).</summary>
    public WaitResult ForStable(RectPx region, int quietMs = 1200, int timeoutMs = 15000,
        IReadOnlyList<RectPx>? ignoreRegions = null,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.stable.region");
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        var quietSince = DateTime.Now;
        var prev = _capture.Sample(region);

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            PerfTrace.Count("wait.pollWakeups");
            ct.WaitHandle.WaitOne(150);
            var cur = _capture.Sample(region);
            PerfTrace.Count("wait.captureSamples");
            var diff = CaptureDiff(prev, cur, region, ignoreRegions);
            if (diff > 0.005)
            {
                quietSince = DateTime.Now;
                prev = cur;
            }
            else if ((DateTime.Now - quietSince).TotalMilliseconds >= quietMs)
            {
                return new WaitResult(true, $"stable for {quietMs}ms", sw.Elapsed);
            }
        }
        return new WaitResult(false, "timeout waiting for stability", sw.Elapsed);
    }

    public WaitResult ForEvent(Func<ObservedEvent, bool> predicate, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.event");
        var sw = Stopwatch.StartNew();
        var hit = new ManualResetEventSlim();
        void Handler(ObservedEvent e) { if (predicate(e)) hit.Set(); }
        _events.Event += Handler;
        try
        {
            var ok = hit.Wait(timeoutMs, ct);
            PerfTrace.Count(ok ? "wait.eventWakeups" : "wait.pollWakeups");
            return ok
                ? new WaitResult(true, "event observed", sw.Elapsed)
                : new WaitResult(false, "timeout", sw.Elapsed);
        }
        finally { _events.Event -= Handler; }
    }

    public WaitResult ForGone(string elementId, int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.gone");
        var sw = Stopwatch.StartNew();
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        var wake = new ManualResetEventSlim();
        void H(ObservedEvent e)
        {
            if (e.Kind is EventKind.StructureChanged or EventKind.WindowClosed
                or EventKind.WindowOpened) wake.Set();
        }
        _events.Event += H;
        try
        {
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (_registry.EnsureAlive(elementId) == null)
                    return new WaitResult(true, "element gone", sw.Elapsed);
                wake.Reset();
                var woke = WaitHandle.WaitAny(
                    [wake.WaitHandle, ct.WaitHandle], 200) == 0;
                PerfTrace.Count(woke ? "wait.eventWakeups" : "wait.pollWakeups");
            }
            return new WaitResult(false, "still present", sw.Elapsed);
        }
        finally { _events.Event -= H; }
    }

    private static double CaptureDiff(byte[] a, byte[] b, RectPx region = default,
        IReadOnlyList<RectPx>? ignoreRegions = null)
    {
        if (a.Length != b.Length || a.Length == 0) return 1.0;
        var totalPixels = a.Length / 3;
        if (totalPixels == 0) return 1.0;

        var diff = 0;
        var considered = 0;
        const int scale = 8;
        var w = Math.Max(1, region.Width / scale);

        for (var i = 0; i < a.Length; i += 3)
        {
            if (ignoreRegions != null && ignoreRegions.Count > 0 && region.Width > 0 && region.Height > 0)
            {
                var pixelIdx = i / 3;
                var py = pixelIdx / w;
                var px = pixelIdx % w;
                var deskX = region.X + px * scale;
                var deskY = region.Y + py * scale;

                var ignored = false;
                for (var r = 0; r < ignoreRegions.Count; r++)
                {
                    var ir = ignoreRegions[r];
                    if (deskX >= ir.X && deskX < ir.X + ir.Width &&
                        deskY >= ir.Y && deskY < ir.Y + ir.Height)
                    {
                        ignored = true;
                        break;
                    }
                }
                if (ignored) continue;
            }

            considered++;
            if (Math.Abs(a[i] - b[i]) > 12) diff++;
        }

        if (considered == 0) return 0.0;
        return (double)diff / considered;
    }
}
