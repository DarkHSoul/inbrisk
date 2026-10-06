using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed record CompletionTimeline(
    double t_start,
    double? t_firstChange,
    double? t_stable);

public sealed class WaitTelemetry
{
    private long _eventWakeCount;
    private long _fallbackPollCount;
    private long _propertyCheckCount;
    private long _relevantEvents;
    private long _ignoredEvents;

    public long EventWakeCount => Interlocked.Read(ref _eventWakeCount);
    public long FallbackPollCount => Interlocked.Read(ref _fallbackPollCount);
    public long PropertyCheckCount => Interlocked.Read(ref _propertyCheckCount);
    public long RelevantEvents => Interlocked.Read(ref _relevantEvents);
    public long IgnoredEvents => Interlocked.Read(ref _ignoredEvents);

    public void IncEventWake() => Interlocked.Increment(ref _eventWakeCount);
    public void IncFallbackPoll() => Interlocked.Increment(ref _fallbackPollCount);
    public void IncPropertyCheck() => Interlocked.Increment(ref _propertyCheckCount);
    public void IncRelevantEvent() => Interlocked.Increment(ref _relevantEvents);
    public void IncIgnoredEvent() => Interlocked.Increment(ref _ignoredEvents);

    public void Reset()
    {
        Interlocked.Exchange(ref _eventWakeCount, 0);
        Interlocked.Exchange(ref _fallbackPollCount, 0);
        Interlocked.Exchange(ref _propertyCheckCount, 0);
        Interlocked.Exchange(ref _relevantEvents, 0);
        Interlocked.Exchange(ref _ignoredEvents, 0);
    }
}

public sealed record WaitResult(
    bool Success,
    string Reason,
    TimeSpan Elapsed,
    UiElement? MatchedElement = null,
    bool InterruptedByDialog = false,
    long? DialogHwnd = null,
    string? DialogTitle = null,
    CompletionTimeline? Timeline = null,
    WaitTelemetry? TelemetrySnapshot = null);

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
    private readonly IScopedSubscriptionManager? _subscriptionManager;
    private int _activeTrackers;

    public WaitTelemetry Telemetry { get; } = new();
    public int ActiveTrackers => Volatile.Read(ref _activeTrackers);

    internal EventWaitTracker CreateTracker(Func<ObservedEvent, bool> filter)
    {
        Interlocked.Increment(ref _activeTrackers);
        return new EventWaitTracker(_events, filter, Telemetry, () => Interlocked.Decrement(ref _activeTrackers));
    }

    public WaitService(Func<FindSpec, IReadOnlyList<UiElement>> find,
        ICaptureService capture, IEventSource events, ElementRegistry registry,
        IWindowService? windows = null,
        IScopedSubscriptionManager? subscriptionManager = null)
    {
        _find = find;
        _capture = capture;
        _events = events;
        _registry = registry;
        _windows = windows;
        _subscriptionManager = subscriptionManager;
    }

    internal sealed class EventWaitTracker : IDisposable
    {
        private readonly object _lock = new();
        private readonly Func<ObservedEvent, bool> _filter;
        private readonly Action<ObservedEvent> _handler;
        private readonly IEventSource? _source;
        private readonly WaitTelemetry _telemetry;
        private readonly Action? _onDispose;
        private long _generation;
        private bool _disposed;

        public long CurrentGeneration
        {
            get { lock (_lock) return _generation; }
        }

        public EventWaitTracker(IEventSource? source, Func<ObservedEvent, bool> filter, WaitTelemetry telemetry, Action? onDispose = null)
        {
            _source = source;
            _filter = filter;
            _telemetry = telemetry;
            _onDispose = onDispose;
            _handler = OnEvent;
            if (_source != null)
                _source.Event += _handler;
        }

        private void OnEvent(ObservedEvent e)
        {
            if (_filter(e))
            {
                _telemetry.IncRelevantEvent();
                lock (_lock)
                {
                    if (_disposed) return;
                    _generation++;
                    Monitor.PulseAll(_lock);
                }
            }
            else
            {
                _telemetry.IncIgnoredEvent();
            }
        }

        public bool WaitForNext(long baselineGeneration, int timeoutMs, CancellationToken ct = default)
        {
            if (timeoutMs < 0) timeoutMs = 0;
            var sw = Stopwatch.StartNew();
            lock (_lock)
            {
                if (_disposed) return false;
                while (_generation <= baselineGeneration)
                {
                    if (_disposed) return false;
                    ct.ThrowIfCancellationRequested();
                    var remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0) return false;
                    var waitSlice = Math.Min(remaining, 50);
                    Monitor.Wait(_lock, waitSlice);
                }
                return !_disposed;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Monitor.PulseAll(_lock);
            }
            if (_source != null)
            {
                _source.Event -= _handler;
            }
            _onDispose?.Invoke();
        }
    }

    public WaitResult ForElement(FindSpec spec, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.element");
        var sw = Stopwatch.StartNew();
        var deadlineMs = timeoutMs;
        double startMs = 0;
        double? firstChangeMs = null;

        using var tracker = CreateTracker(
            e => spec.Hwnd == null || e.Hwnd is null or 0 || e.Hwnd == spec.Hwnd);
        var baseline = tracker.CurrentGeneration;

        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            ct.ThrowIfCancellationRequested();
            Telemetry.IncPropertyCheck();
            var found = _find(spec with { FirstOnly = true });
            PerfTrace.Count("wait.findEnumerations");
            if (found.Count > 0)
            {
                _registry.Register(found);
                var elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                return new WaitResult(true, $"found {found[0].Id}", sw.Elapsed,
                    MatchedElement: found[0],
                    Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                    TelemetrySnapshot: Telemetry);
            }

            var remaining = Math.Max(0, (int)(deadlineMs - sw.ElapsedMilliseconds));
            var slice = Math.Min(remaining, 200);
            if (slice <= 0) break;

            var wokeByEvent = tracker.WaitForNext(baseline, slice, ct);
            if (wokeByEvent)
            {
                Thread.Sleep(2);
                baseline = tracker.CurrentGeneration;
                Telemetry.IncEventWake();
                PerfTrace.Count("wait.eventWakeups");
                if (!firstChangeMs.HasValue) firstChangeMs = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                Telemetry.IncFallbackPoll();
                PerfTrace.Count("wait.pollWakeups");
            }
        }
        return new WaitResult(false, "timeout", sw.Elapsed,
            Timeline: new CompletionTimeline(startMs, firstChangeMs, null),
            TelemetrySnapshot: Telemetry);
    }

    /// <summary>Check if an unexpected modal dialog or alert opened.</summary>
    public (long Hwnd, string Title)? CheckUnexpectedDialog(long? scopedHwnd)
    {
        if (_windows == null) return null;

        // 1. Direct modal blocker check from WindowService
        if (scopedHwnd != null && scopedHwnd > 0)
        {
            var blocker = _windows.GetActiveBlockingPopup(scopedHwnd);
            if (blocker != null && blocker.Hwnd != scopedHwnd.Value)
                return (blocker.Hwnd, blocker.Title.Length > 0 ? blocker.Title : "Modal Dialog");
        }

        var fg = _windows.GetForegroundWindow();
        if (fg == null) return null;
        if (scopedHwnd != null && scopedHwnd > 0 && fg.Hwnd == scopedHwnd.Value) return null;
        if (_windows.IsWindowProtected(fg.Hwnd, out _)) return null;

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
                // Normal top-level windows have 'Close' titlebar buttons — exclude generic Close
                // and look for actual dialog confirmation actions (OK/Cancel, Yes/No, etc.)
                var dialogButtonCount = buttons.Count(b =>
                    b.Name is "OK" or "Cancel" or "Tamam" or "İptal" or "Yes" or "No" or "Evet" or "Hayır" or "Retry" or "Yeniden Dene");
                if (dialogButtonCount >= 1 && (isDialogTitle || dialogButtonCount >= 2))
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
        var deadlineMs = timeoutMs;
        double startMs = 0;
        double? firstChangeMs = null;

        var candidateWindowEvent = true; // initial iteration checks dialog
        var lastDialogCheck = DateTimeOffset.MinValue;

        ISubscriptionLease? lease = null;
        if (_subscriptionManager != null && cond.Hwnd.HasValue && cond.Hwnd.Value != 0)
        {
            lease = _subscriptionManager.Acquire(cond.Hwnd.Value, cond.Pid,
                UiaEventKinds.StructureChanged | UiaEventKinds.PropertyChanged | UiaEventKinds.WindowOpened | UiaEventKinds.WindowClosed);
        }

        try
        {
            using var tracker = CreateTracker(e =>
            {
                if (e.Kind is EventKind.WindowOpened or EventKind.WindowShown or EventKind.ForegroundChanged)
                    candidateWindowEvent = true;
                return cond.Hwnd == null || e.Hwnd is null or 0 || e.Hwnd == cond.Hwnd;
            });
            var baseline = tracker.CurrentGeneration;

        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            ct.ThrowIfCancellationRequested();

            // Check for unexpected modal dialog - gated by candidate window events or 1000ms fallback
            if (cond.StopOnUnexpectedDialog)
            {
                var now = DateTimeOffset.UtcNow;
                if (candidateWindowEvent || (now - lastDialogCheck).TotalMilliseconds >= 1000)
                {
                    candidateWindowEvent = false;
                    lastDialogCheck = now;
                    var diag = CheckUnexpectedDialog(cond.Hwnd);
                    if (diag != null)
                    {
                        var elapsedMs = sw.Elapsed.TotalMilliseconds;
                        if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                        return new WaitResult(false,
                            $"interrupted_by_dialog: unexpected dialog '{diag.Value.Title}' (0x{diag.Value.Hwnd:X}) appeared",
                            sw.Elapsed,
                            InterruptedByDialog: true,
                            DialogHwnd: diag.Value.Hwnd,
                            DialogTitle: diag.Value.Title,
                            Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                            TelemetrySnapshot: Telemetry);
                    }
                }
            }

            Telemetry.IncPropertyCheck();
            if (cond.Gone)
            {
                if (!string.IsNullOrEmpty(cond.ScopeElementId))
                {
                    if (_registry.EnsureAlive(cond.ScopeElementId) == null)
                    {
                        var elapsedMs = sw.Elapsed.TotalMilliseconds;
                        if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                        return new WaitResult(true, "element gone", sw.Elapsed,
                            Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                            TelemetrySnapshot: Telemetry);
                    }
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
                    {
                        var elapsedMs = sw.Elapsed.TotalMilliseconds;
                        if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                        return new WaitResult(true, "element gone", sw.Elapsed,
                            Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                            TelemetrySnapshot: Telemetry);
                    }
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
                    var elapsedMs = sw.Elapsed.TotalMilliseconds;
                    if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                    return new WaitResult(true,
                        $"condition met: found {candidates.Count} match{(candidates.Count > 1 ? "es" : "")} ({candidates[0].Id})",
                        sw.Elapsed,
                        MatchedElement: candidates[0],
                        Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                        TelemetrySnapshot: Telemetry);
                }
            }

            var remaining = Math.Max(0, (int)(deadlineMs - sw.ElapsedMilliseconds));
            var slice = Math.Min(remaining, 200);
            if (slice <= 0) break;

            var wokeByEvent = tracker.WaitForNext(baseline, slice, ct);
            if (wokeByEvent)
            {
                Thread.Sleep(2);
                baseline = tracker.CurrentGeneration;
                Telemetry.IncEventWake();
                PerfTrace.Count("wait.eventWakeups");
                if (!firstChangeMs.HasValue) firstChangeMs = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                Telemetry.IncFallbackPoll();
                PerfTrace.Count("wait.pollWakeups");
            }
        }

            return new WaitResult(false, cond.Gone ? "timeout: element still present" : "timeout: condition not met", sw.Elapsed,
                Timeline: new CompletionTimeline(startMs, firstChangeMs, null),
                TelemetrySnapshot: Telemetry);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    /// <summary>Wait until a ChangeMonitor reports change started
    /// (first diff above threshold or semantic event).</summary>
    public WaitResult ForChange(ChangeMonitor monitor, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.change");
        var sw = Stopwatch.StartNew();
        double startMs = 0;
        if (monitor.Stability.State == StabilityState.Changing)
            return new WaitResult(true, "already changing", sw.Elapsed,
                Timeline: new CompletionTimeline(startMs, 0, null),
                TelemetrySnapshot: Telemetry);
        var hit = new ManualResetEventSlim();
        void H(StabilityInfo s) { if (s.State == StabilityState.Changing) hit.Set(); }
        monitor.StabilityChanged += H;
        try
        {
            var ok = hit.Wait(timeoutMs, ct);
            PerfTrace.Count(ok ? "wait.eventWakeups" : "wait.pollWakeups");
            if (ok) Telemetry.IncEventWake(); else Telemetry.IncFallbackPoll();
            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            return ok
                ? new WaitResult(true, "change detected", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, elapsedMs, null),
                    TelemetrySnapshot: Telemetry)
                : new WaitResult(false, "timeout waiting for change", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, null, null),
                    TelemetrySnapshot: Telemetry);
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
        double startMs = 0;
        var hit = new ManualResetEventSlim();
        void H(StabilityInfo s) { if (s.State == StabilityState.Stable) hit.Set(); }
        monitor.StabilityChanged += H;
        try
        {
            if (monitor.Stability.State == StabilityState.Stable)
                return new WaitResult(true, "already stable", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, 0, sw.Elapsed.TotalMilliseconds),
                    TelemetrySnapshot: Telemetry);
            var ok = hit.Wait(timeoutMs, ct);
            PerfTrace.Count(ok ? "wait.eventWakeups" : "wait.pollWakeups");
            if (ok) Telemetry.IncEventWake(); else Telemetry.IncFallbackPoll();
            var elapsedMs = sw.Elapsed.TotalMilliseconds;
            return ok
                ? new WaitResult(true, $"stable ({monitor.Stability.StableFor.TotalMilliseconds:F0}ms quiet)", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, null, elapsedMs),
                    TelemetrySnapshot: Telemetry)
                : new WaitResult(false, "timeout waiting for stability", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, null, null),
                    TelemetrySnapshot: Telemetry);
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
        double startMs = 0;
        double? firstChangeMs = null;

        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            PerfTrace.Count("wait.pollWakeups");
            Telemetry.IncFallbackPoll();
            ct.WaitHandle.WaitOne(150);
            var cur = _capture.Sample(region);
            PerfTrace.Count("wait.captureSamples");
            var diff = CaptureDiff(prev, cur, region, ignoreRegions);
            if (diff > 0.005)
            {
                if (!firstChangeMs.HasValue) firstChangeMs = sw.Elapsed.TotalMilliseconds;
                quietSince = DateTime.Now;
                prev = cur;
            }
            else if ((DateTime.Now - quietSince).TotalMilliseconds >= quietMs)
            {
                var elapsedMs = sw.Elapsed.TotalMilliseconds;
                return new WaitResult(true, $"stable for {quietMs}ms", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                    TelemetrySnapshot: Telemetry);
            }
        }
        return new WaitResult(false, "timeout waiting for stability", sw.Elapsed,
            Timeline: new CompletionTimeline(startMs, firstChangeMs, null),
            TelemetrySnapshot: Telemetry);
    }

    public WaitResult ForEvent(Func<ObservedEvent, bool> predicate, int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.event");
        var sw = Stopwatch.StartNew();
        double startMs = 0;
        using var tracker = CreateTracker(predicate);
        var baseline = tracker.CurrentGeneration;
        var ok = tracker.WaitForNext(baseline, timeoutMs, ct);
        if (ok)
        {
            Telemetry.IncEventWake();
            PerfTrace.Count("wait.eventWakeups");
        }
        else
        {
            Telemetry.IncFallbackPoll();
            PerfTrace.Count("wait.pollWakeups");
        }
        var elapsedMs = sw.Elapsed.TotalMilliseconds;
        return ok
            ? new WaitResult(true, "event observed", sw.Elapsed,
                Timeline: new CompletionTimeline(startMs, elapsedMs, elapsedMs),
                TelemetrySnapshot: Telemetry)
            : new WaitResult(false, "timeout", sw.Elapsed,
                Timeline: new CompletionTimeline(startMs, null, null),
                TelemetrySnapshot: Telemetry);
    }

    public WaitResult ForGone(string elementId, int timeoutMs = 8000,
        CancellationToken ct = default)
    {
        using var _w = PerfTrace.Stage("wait.gone");
        var sw = Stopwatch.StartNew();
        var deadlineMs = timeoutMs;
        double startMs = 0;
        double? firstChangeMs = null;

        using var tracker = CreateTracker(
            e => e.Kind is EventKind.StructureChanged or EventKind.WindowClosed or EventKind.WindowOpened);
        var baseline = tracker.CurrentGeneration;

        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            ct.ThrowIfCancellationRequested();
            Telemetry.IncPropertyCheck();
            if (_registry.EnsureAlive(elementId) == null)
            {
                var elapsedMs = sw.Elapsed.TotalMilliseconds;
                if (!firstChangeMs.HasValue) firstChangeMs = elapsedMs;
                return new WaitResult(true, "element gone", sw.Elapsed,
                    Timeline: new CompletionTimeline(startMs, firstChangeMs, elapsedMs),
                    TelemetrySnapshot: Telemetry);
            }

            var remaining = Math.Max(0, (int)(deadlineMs - sw.ElapsedMilliseconds));
            var slice = Math.Min(remaining, 200);
            if (slice <= 0) break;

            var wokeByEvent = tracker.WaitForNext(baseline, slice, ct);
            if (wokeByEvent)
            {
                Thread.Sleep(2);
                baseline = tracker.CurrentGeneration;
                Telemetry.IncEventWake();
                PerfTrace.Count("wait.eventWakeups");
                if (!firstChangeMs.HasValue) firstChangeMs = sw.Elapsed.TotalMilliseconds;
            }
            else
            {
                Telemetry.IncFallbackPoll();
                PerfTrace.Count("wait.pollWakeups");
            }
        }
        return new WaitResult(false, "still present", sw.Elapsed,
            Timeline: new CompletionTimeline(startMs, firstChangeMs, null),
            TelemetrySnapshot: Telemetry);
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
