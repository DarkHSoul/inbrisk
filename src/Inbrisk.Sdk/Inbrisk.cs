using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Capture;
using Inbrisk.Platform.Windows.Events;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Platform.Windows.Input;
using Inbrisk.Platform.Windows.Ocr;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;

namespace Inbrisk.Sdk;

public sealed record InbriskOptions(
    bool AutoConfirm = false,
    string? TelemetryPath = null,
    bool StartEvents = false,
    /// <summary>Per-monitor screen-control indicator border. Default on;
    /// INBRISK_INDICATOR=off disables. Never controllable via MCP.</summary>
    bool Indicator = true,
    /// <summary>Floating Activity Pill / HUD overlay. Default on.</summary>
    bool Hud = true,
    /// <summary>How long the Active border persists after the last action —
    /// bridges rapid consecutive operations into one session.</summary>
    int IndicatorIdleGraceMs = 700,
    /// <summary>Indicator colors; null = read settings.json, then defaults.</summary>
    GlowPalette? IndicatorPalette = null,
    HwndMetadataCache? HwndMetadataCache = null,
    MonitorTopologyCache? MonitorTopologyCache = null,
    VirtualDesktopManagerHolder? VdmHolder = null,
    ApplicationCatalogService? CatalogService = null,
    LaunchResolutionCache? LaunchCache = null,
    ITargetHighlightService? TargetHighlight = null);

/// <summary>
/// The Unified Computer API. Composition root: wires platform services into
/// the runtime and exposes the high-level surface used by CLI, tests, and
/// later the agent layer / MCP wrapper.
/// </summary>
public sealed class InbriskRuntime : IDisposable
{
    private readonly UiaDispatcher _uiaDispatch;
    private readonly UiaReadScheduler _uiaReadScheduler;
    private readonly WindowService _windows;
    private readonly IntegrityService _integrity;
    private readonly SendInputService _input;
    private readonly CaptureService _capture;
    private readonly ClipboardService _clipboard;
    private readonly OcrService _ocr;
    private readonly UiaBackend _uiaBackend;
    private readonly CoalescingEventSource _events;
    private readonly ElementRegistry _registry;
    private readonly ElementRecipeStore _store;
    private readonly Executor _executor;
    private readonly WaitService _wait;
    private readonly SafetyPolicy _policy = new();
    private readonly JsonlTelemetrySink? _telemetry;
    private readonly ComputerControlActivityService _activity;
    private readonly ScreenIndicatorService _indicator;
    private readonly ActivityHudService _hud;
    private readonly ITargetHighlightService _targetHighlight;
    private readonly IProcessProvenanceService _provenance;
    private readonly AppService _apps;
    private readonly UiaEventSubscriptionManager _subscriptionManager;
    private readonly List<ChangeMonitor> _monitors = new();
    private readonly RecentEventBuffer _eventBuffer = new();
    private readonly IElementBackend[] _agentBackends;

    public UiaEventSubscriptionManager SubscriptionManager => _subscriptionManager;
    private ObservationBuilder? _obsBuilder;
    private IVisionBackend? _vision;
    private IGroundingBackend? _grounding;

    public SafetyPolicy Policy => _policy;
    public IProcessProvenanceService Provenance => _provenance;
    public UiaReadScheduler ReadScheduler => _uiaReadScheduler;
    public RecentEventBuffer EventBuffer => _eventBuffer;

    /// <summary>Computer-control activity for the screen indicator —
    /// BeginActivity marks the machine as actively used.</summary>
    public ComputerControlActivityService Activity => _activity;
    /// <summary>The per-monitor border overlay (tests/diagnostics).</summary>
    public ScreenIndicatorService Indicator => _indicator;
    /// <summary>The floating activity pill HUD overlay.</summary>
    public ActivityHudService Hud => _hud;
    /// <summary>Transient focus/target window highlight service.</summary>
    public ITargetHighlightService TargetHighlight => _targetHighlight;

    public event Action<ObservedEvent>? Event
    {
        add => _events.Event += value;
        remove => _events.Event -= value;
    }

    /// <summary>Raw (pre-coalescing) event counters for diagnostics.</summary>
    public (long Raw, long Emitted, double ReductionRatio) EventStats =>
        (_events.RawCount, _events.EmittedCount, _events.ReductionRatio);

    public InbriskRuntime(InbriskOptions? options = null)
    {
        var opt = options ?? new InbriskOptions();
        _policy.AutoConfirm = opt.AutoConfirm;

        _uiaDispatch = new UiaDispatcher();
        _uiaReadScheduler = new UiaReadScheduler();
        _windows = new WindowService(opt.HwndMetadataCache, opt.MonitorTopologyCache, opt.VdmHolder);
        _provenance = new ProcessProvenanceService(_windows);
        try
        {
            foreach (var w in _windows.ListWindows())
                _provenance.RegisterInitial(w.Pid, w.ProcessName ?? "", w.Hwnd);
        }
        catch { }
        _integrity = new IntegrityService();
        _input = new SendInputService();
        _capture = new CaptureService(_windows);
        _clipboard = new ClipboardService();
        _ocr = new OcrService();
        _uiaBackend = new UiaBackend(_uiaDispatch, _windows, _uiaReadScheduler);
        _events = new CoalescingEventSource(new CompositeEventSource(
            new WinEventService(),
            new UiaEventService(_uiaDispatch)));
        _agentBackends = new IElementBackend[]
        {
            _uiaBackend,
            new VisionElementBackend(BackendId.Vision),
            new VisionElementBackend(BackendId.Ocr),
        };
        _store = new ElementRecipeStore();
        _registry = new ElementRegistry(_agentBackends, _store);

        if (opt.TelemetryPath != null)
            _telemetry = new JsonlTelemetrySink(opt.TelemetryPath);

        _activity = new ComputerControlActivityService(opt.IndicatorIdleGraceMs);
        var indicatorOff = Environment.GetEnvironmentVariable("INBRISK_INDICATOR")
            is "0" or "off" or "false" or "disabled";
        var userSettings = UserSettings.Load();
        _indicator = new ScreenIndicatorService(
            enabled: opt.Indicator && userSettings.PerimeterEnabled && !indicatorOff,
            palette: opt.IndicatorPalette ?? PaletteFromSettings());

        var hudOff = Environment.GetEnvironmentVariable("INBRISK_HUD")
            is "0" or "off" or "false" or "disabled";
        _hud = new ActivityHudService(
            enabled: opt.Hud && userSettings.HudEnabled && !hudOff,
            animationsEnabled: userSettings.AnimationsEnabled,
            hudDockGapDip: userSettings.HudDockGapDip);

        _activity.StateChanged += state =>
        {
            _indicator.SetState(state);
            HudActivityPolicy.Apply(_hud, state);
            if (state == IndicatorState.EmergencyStopped)
                _targetHighlight.SetEmergency(true);
            else
                _targetHighlight.SetEmergency(false);
        };

        _targetHighlight = opt.TargetHighlight ?? new TargetHighlightService();

        _indicator.Start();
        _hud.Start();

        // Phase F Round 2: Scoped UIA Subscription Manager
        _subscriptionManager = new UiaEventSubscriptionManager(_uiaDispatch, getGeneration: () => _eventBuffer.CurrentGeneration);
        _subscriptionManager.Event += e =>
        {
            _eventBuffer.Add(e);
            if (e is { Kind: EventKind.WindowClosed, Hwnd: { } h })
                _registry.InvalidateWindow(h);
        };

        // Readiness probe: a window counts as usable only when its UIA
        // root is reachable — runs on the dedicated UIA dispatcher.
        _apps = new AppService(_windows,
            uiaProbe: hwnd => _uiaDispatch.Run(uia =>
            {
                try
                {
                    var el = uia.ElementFromHandle(new IntPtr(hwnd));
                    _ = el?.CurrentControlType;
                    return el != null;
                }
                catch { return false; }
            }, timeoutMs: 2000),
            isDeniedProcess: name => _policy.DenyProcessNames.Contains(name),
            eventBuffer: _eventBuffer,
            telemetry: _subscriptionManager.Telemetry,
            catalogService: opt.CatalogService,
            launchCache: opt.LaunchCache);

        _executor = new Executor(_windows, _integrity, _input, _capture,
            _agentBackends, _registry, _policy, _telemetry, _activity);
        _wait = new WaitService(s => Find(s), _capture, _events, _registry, _windows, _subscriptionManager);

        _events.Event += e =>
        {
            _eventBuffer.Add(e);
            if (e is { Kind: EventKind.WindowClosed, Hwnd: { } h })
                _registry.InvalidateWindow(h);
            lock (_monitors)
                foreach (var m in _monitors) m.FeedSemantic(e);
        };
        if (opt.StartEvents) _events.Start();
    }

    // ---- topology ----
    public IReadOnlyList<WindowInfo> Windows() => _windows?.ListWindows() ?? [];
    public WindowInfo? Window(long hwnd) => _windows?.GetWindow(hwnd);
    public WindowInfo? ForegroundWindow() => _windows?.GetForegroundWindow();
    public IWindowService WindowService => _windows;
    public WindowInfo? GetModalPopup(long hwnd) => _windows?.GetModalPopup(hwnd);
    public bool IsWindowEnabled(long hwnd) => _windows?.IsWindowEnabled(hwnd) ?? false;
    public IReadOnlyList<WindowInfo> FindSystemDialogs() => _windows?.FindSystemDialogs() ?? [];
    public bool IsWindowProtected(long hwnd, out string? reason)
    {
        if (_windows != null) return _windows.IsWindowProtected(hwnd, out reason);
        reason = null;
        return false;
    }
    /// <summary>Graceful window close (WM_CLOSE) — surfaces save prompts.</summary>
    public bool CloseWindow(long hwnd) => _windows?.CloseWindow(hwnd) ?? false;
    public IReadOnlyList<MonitorInfo> Monitors() => _windows?.GetMonitors() ?? [];
    public RectPx VirtualDesktop() => _windows?.GetVirtualDesktopBounds() ?? new RectPx(0, 0, 1920, 1080);

    /// <summary>
    /// Resolve a window by handle or title. Handles accept decimal,
    /// "0x"-prefixed hex and bare hex — every interpretation that names a
    /// live window is a candidate; exactly one live match wins, several is
    /// an ambiguity error. Titles match exact → prefix → substring, first
    /// decisive tier only — a substring that hits multiple windows is an
    /// ambiguity error, never a silent pick of the wrong one.
    /// </summary>
    public long ResolveWindow(string titleOrHwnd)
    {
        var s = titleOrHwnd.Trim();
        var hwndHits = HwndCandidates(s)
            .Where(h => _windows.GetWindow(h) != null).Distinct().ToList();
        if (hwndHits.Count == 1) return hwndHits[0];
        if (hwndHits.Count > 1)
            throw new InbriskException(ErrorCode.Unsupported,
                $"window handle '{s}' is ambiguous: " +
                string.Join(", ", hwndHits.Select(h => $"0x{h:X}")));
        var wins = _windows.ListWindows();
        foreach (var tier in new Func<WindowInfo, bool>[]
        {
            w => string.Equals(w.Title, s, StringComparison.OrdinalIgnoreCase),
            w => w.Title.StartsWith(s, StringComparison.OrdinalIgnoreCase),
            w => w.Title.Contains(s, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = wins.Where(tier).ToList();
            if (hits.Count == 1) return hits[0].Hwnd;
            if (hits.Count > 1)
                throw new InbriskException(ErrorCode.Unsupported,
                    $"window title '{s}' matches {hits.Count} windows: " +
                    string.Join("; ", hits.Select(x =>
                        $"0x{x.Hwnd:X} '{x.Title}' {x.ProcessName}")) +
                    " — pass a handle or use --pid instead");
        }
        throw new InbriskException(ErrorCode.NotFound,
            $"no window matching '{s}'");
    }

    /// <summary>Every numeric interpretation of s that could be a window
    /// handle: "0x" hex, decimal, or bare hex (when it differs).</summary>
    private static IEnumerable<long> HwndCandidates(string s)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                    null, out var h)) yield return h;
            yield break;
        }
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer,
                null, out var d)) yield return d;
        if (long.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                null, out var x) && x != d) yield return x;
    }

    /// <summary>Top-level window owned by a process — largest visible
    /// window on the current desktop wins; ambiguous multi-window processes
    /// pick the biggest (the document area).</summary>
    public long ResolvePidWindow(int pid)
    {
        var wins = _windows.ListWindows()
            .Where(w => w.Pid == pid && w.OnCurrentVirtualDesktop &&
                        w.State != WindowState.Minimized)
            .ToList();
        if (wins.Count == 0)
            throw new InbriskException(ErrorCode.NotFound,
                $"no visible window owned by pid {pid}");
        return wins.OrderByDescending(w => (long)w.Bounds.Width * w.Bounds.Height)
            .First().Hwnd;
    }

    // ---- perception ----

    /// <summary>Per-window semantic snapshot cache — same invalidation
    /// discipline as the find cache: reuse only while no semantic event
    /// attributable to the window (or unattributable) arrived, bounded by a
    /// 400ms TTL for providers that under-report. Returned elements are the
    /// same registry instances, so action-time liveness/ReResolve guards
    /// still apply — a cached element can never silently execute stale.</summary>
    private readonly Dictionary<long, (IReadOnlyList<UiElement> Els,
        InspectOptions Opt, DateTimeOffset At, HashSet<int> Pids, long Ver)>
        _inspectCache = new();

    /// <summary>Actions performed through the executor — a mutation
    /// invalidates semantic snapshot caches even if the corresponding UIA
    /// event has not been delivered yet.</summary>
    public long MutationVersion => _executor.MutationVersion;

    public bool IsInspectCacheFresh(long hwnd, InspectOptions? options = null)
    {
        var opt = options ?? new InspectOptions();
        var now = DateTimeOffset.UtcNow;
        return _inspectCache.TryGetValue(hwnd, out var hit) &&
            hit.Opt == opt &&
            hit.Ver == MutationVersion &&
            (now - hit.At).TotalMilliseconds < 5000 &&
            !_eventBuffer.Snapshot(100).Any(e => e.At >= hit.At &&
                (e.Hwnd == hwnd ||
                 (e.Pid is { } ep && hit.Pids.Contains(ep)) ||
                 (hit.Pids.Count == 0 && (e.Hwnd is null or 0 && e.Pid == null))));
    }

    public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions? options = null)
    {
        using var _ = _activity.BeginActivity();
        var opt = options ?? new InspectOptions();
        var now = DateTimeOffset.UtcNow;
        if (_inspectCache.TryGetValue(hwnd, out var hit) &&
            hit.Opt == opt &&
            hit.Ver == MutationVersion &&
            (now - hit.At).TotalMilliseconds < 5000 &&
            !_eventBuffer.Snapshot(100).Any(e => e.At >= hit.At &&
                (e.Hwnd == hwnd ||
                 (e.Pid is { } ep && hit.Pids.Contains(ep)) ||
                 (hit.Pids.Count == 0 && (e.Hwnd is null or 0 && e.Pid == null)))))
        {
            PerfTrace.Count("inspect.cacheHit");
            return hit.Els;
        }
        var els = _uiaBackend.Inspect(hwnd, opt);
        _registry.Register(els);
        var pids = new HashSet<int>(els.Select(e => e.Pid ?? 0));
        _inspectCache[hwnd] = (els, opt, DateTimeOffset.UtcNow, pids,
            MutationVersion);
        if (_inspectCache.Count > 16) _inspectCache.Clear();
        PerfTrace.Count("inspect.cacheMiss");
        return els;
    }

    public async Task<IReadOnlyList<UiElement>> InspectAsync(long hwnd, InspectOptions? options = null, CancellationToken ct = default)
    {
        using var _ = _activity.BeginActivity();
        var opt = options ?? new InspectOptions();
        var now = DateTimeOffset.UtcNow;
        if (_inspectCache.TryGetValue(hwnd, out var hit) &&
            hit.Opt == opt &&
            hit.Ver == MutationVersion &&
            (now - hit.At).TotalMilliseconds < 5000 &&
            !_eventBuffer.Snapshot(100).Any(e => e.At >= hit.At &&
                (e.Hwnd == hwnd ||
                 (e.Pid is { } ep && hit.Pids.Contains(ep)) ||
                 (hit.Pids.Count == 0 && (e.Hwnd is null or 0 && e.Pid == null)))))
        {
            PerfTrace.Count("inspect.cacheHit");
            return hit.Els;
        }
        var els = await _uiaBackend.InspectAsync(hwnd, opt, ct).ConfigureAwait(false);
        _registry.Register(els);
        var pids = new HashSet<int>(els.Select(e => e.Pid ?? 0));
        _inspectCache[hwnd] = (els, opt, DateTimeOffset.UtcNow, pids,
            MutationVersion);
        if (_inspectCache.Count > 16) _inspectCache.Clear();
        PerfTrace.Count("inspect.cacheMiss");
        return els;
    }

    /// <summary>Purge all pending queued work in the UIA dispatcher immediately.</summary>
    public void PurgePendingWork() => _uiaDispatch.PurgeQueue();

    public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default)
    {
        using var _ = _activity.BeginActivity();
        var els = _executor.Find(spec, ct);
        _registry.Register(els);
        return els;
    }

    public async Task<IReadOnlyList<UiElement>> FindAsync(FindSpec spec, CancellationToken ct = default)
    {
        using var _ = _activity.BeginActivity();
        var els = await _executor.FindAsync(spec, ct).ConfigureAwait(false);
        _registry.Register(els);
        return els;
    }

    public UiElement? Element(string id) => _registry.Get(id);

    public Observation Observe(long? hwnd = null, bool includeShot = false)
    {
        var windows = _windows.ListWindows();
        var fg = _windows.GetForegroundWindow();
        var target = hwnd ?? fg?.Hwnd;
        var elements = target is { } t
            ? Inspect(t, new InspectOptions())
            : (IReadOnlyList<UiElement>)Array.Empty<UiElement>();
        FrameTransform? ft = null;
        if (includeShot && target is { } tt)
            ft = _capture.CaptureWindow(tt).Transform;
        return new Observation(fg, windows, elements, ft,
            Array.Empty<ObservedEvent>(), DateTimeOffset.Now);
    }

    public async Task<Observation> ObserveAsync(long? hwnd = null, bool includeShot = false, CancellationToken ct = default)
    {
        var windows = _windows.ListWindows();
        var fg = _windows.GetForegroundWindow();
        var target = hwnd ?? fg?.Hwnd;
        var elements = target is { } t
            ? await InspectAsync(t, new InspectOptions(), ct).ConfigureAwait(false)
            : (IReadOnlyList<UiElement>)Array.Empty<UiElement>();
        FrameTransform? ft = null;
        if (includeShot && target is { } tt)
            ft = _capture.CaptureWindow(tt).Transform;
        return new Observation(fg, windows, elements, ft,
            Array.Empty<ObservedEvent>(), DateTimeOffset.Now);
    }

    /// <summary>
    /// Merged scene: UIA elements + OCR text + vision elements, deduplicated.
    /// Vision/OCR results are mapped to desktop space via the capture
    /// transform before merging.
    /// </summary>
    public IReadOnlyList<UiElement> ObserveScene(long hwnd,
        bool includeOcr = true, VisionQuery? visionQuery = null)
    {
        using var _ = _activity.BeginActivity();
        IReadOnlyList<UiElement> uia;
        RawFrame? frame = null;

        if (includeOcr || _vision != null || _grounding != null)
        {
            var uiaTask = Task.Run(() =>
            {
                using (PerfTrace.Stage("observe.uia"))
                    return Inspect(hwnd);
            });
            var frameTask = Task.Run(() => CaptureRawTimed(hwnd));
            Task.WaitAll(uiaTask, frameTask);
            uia = uiaTask.Result;
            frame = frameTask.Result;
        }
        else
        {
            using (PerfTrace.Stage("observe.uia"))
                uia = Inspect(hwnd);
        }

        IReadOnlyList<TextSpan> ocr;
        using (PerfTrace.Stage("observe.ocr"))
            ocr = includeOcr && frame != null
                ? _ocr.Recognize(frame)
                : (IReadOnlyList<TextSpan>)Array.Empty<TextSpan>();

        var vision = new List<UiElement>();
        if (_vision != null && frame != null)
        {
            using (PerfTrace.Stage("observe.vision"))
            {
                var vr = _vision.Analyze(frame, visionQuery ?? new VisionQuery());
                _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
                    "vision", Backend: vr.BackendName, DurationMs: vr.Duration.TotalMilliseconds,
                    ElementCount: vr.Elements.Count));
                foreach (var ve in vr.Elements)
                    vision.Add(GroundingService.ToDesktop(ve, frame.Transform));
            }
        }

        using (PerfTrace.Stage("observe.merge"))
        {
            var merged = SceneMerger.Merge(uia, ocr, vision);
            _registry.Register(merged);
            return merged;
        }
    }

    public async Task<IReadOnlyList<UiElement>> ObserveSceneAsync(long hwnd,
        bool includeOcr = true, VisionQuery? visionQuery = null, CancellationToken ct = default)
    {
        using var _ = _activity.BeginActivity();
        IReadOnlyList<UiElement> uia;
        RawFrame? frame = null;

        if (includeOcr || _vision != null || _grounding != null)
        {
            var uiaTask = InspectAsync(hwnd, ct: ct);
            var frameTask = Task.Run(() => CaptureRawTimed(hwnd), ct);
            await Task.WhenAll(uiaTask, frameTask).ConfigureAwait(false);
            uia = await uiaTask.ConfigureAwait(false);
            frame = await frameTask.ConfigureAwait(false);
        }
        else
        {
            using (PerfTrace.Stage("observe.uia"))
                uia = await InspectAsync(hwnd, ct: ct).ConfigureAwait(false);
        }

        IReadOnlyList<TextSpan> ocr;
        using (PerfTrace.Stage("observe.ocr"))
            ocr = includeOcr && frame != null
                ? _ocr.Recognize(frame)
                : (IReadOnlyList<TextSpan>)Array.Empty<TextSpan>();

        var vision = new List<UiElement>();
        if (_vision != null && frame != null)
        {
            using (PerfTrace.Stage("observe.vision"))
            {
                var vr = _vision.Analyze(frame, visionQuery ?? new VisionQuery());
                _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
                    "vision", Backend: vr.BackendName, DurationMs: vr.Duration.TotalMilliseconds,
                    ElementCount: vr.Elements.Count));
                foreach (var ve in vr.Elements)
                    vision.Add(GroundingService.ToDesktop(ve, frame.Transform));
            }
        }

        using (PerfTrace.Stage("observe.merge"))
        {
            var merged = SceneMerger.Merge(uia, ocr, vision);
            _registry.Register(merged);
            return merged;
        }
    }

    private RawFrame? CaptureRawTimed(long hwnd)
    {
        try
        {
            using var _c = PerfTrace.Stage("observe.capture");
            return CaptureRaw(new CaptureTarget.Window(hwnd));
        }
        catch (Exception e)
        {
            _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
                "observe.capture", Target: $"capture failed: {e.Message}"));
            return null;
        }
    }

    // ---- capture ----
    public byte[] Shot(string outPath, long? hwnd = null, RectPx? region = null)
    {
        using var _ = _activity.BeginActivity();
        var r = region ?? (hwnd is { } h
            ? (_windows.GetWindow(h)?.Bounds ?? _windows.GetVirtualDesktopBounds())
            : _windows.GetVirtualDesktopBounds());
        var frame = _capture.Capture(r);
        File.WriteAllBytes(outPath, frame.PngData);
        return frame.PngData;
    }

    /// <summary>One-shot raw frame of a capture target (desktop region).
    /// Window targets stamp SourceHwnd into the transform so image-space
    /// actions can raise the owning window before injecting input.</summary>
    public RawFrame CaptureRaw(CaptureTarget target)
    {
        using var _ = _activity.BeginActivity();
        var raw = _capture.CaptureRaw(CaptureService.ResolveRect(target, _windows));
        return target is CaptureTarget.Window w
            ? raw with { Transform = raw.Transform with { SourceHwnd = w.Hwnd } }
            : raw;
    }

    /// <summary>Start a continuous capture session (WGC primary, GDI fallback).</summary>
    public ICaptureSession StartCapture(CaptureTarget target)
    {
        var s = _capture.CreateSession(target);
        s.Start();
        return s;
    }

    /// <summary>Start a session wrapped in the change/stability pipeline;
    /// semantic events are fed in automatically.</summary>
    public ChangeMonitor Monitor(CaptureTarget target, int quietMs = 800)
    {
        var m = new ChangeMonitor(StartCapture(target), _telemetry, quietMs);
        lock (_monitors) _monitors.Add(m);
        return m;
    }

    /// <summary>Current frame of an existing session cropped to a desktop rect —
    /// carries a corrected FrameTransform.</summary>
    public RawFrame CropToRegion(RawFrame frame, RectPx desktopRect) =>
        frame.CropDesktop(desktopRect);

    // ---- action ----
    public ActionResult Perform(ActionIntent intent) => _executor.Perform(intent);

    public ActionResult Click(string elementId) =>
        Perform(new ActionIntent(ActionKind.Click, TargetRef.Element(elementId)));

    public ActionResult ClickAt(int x, int y) =>
        Perform(new ActionIntent(ActionKind.Click, TargetRef.At(x, y)));

    public ActionResult Invoke(string elementId) =>
        Perform(new ActionIntent(ActionKind.Invoke, TargetRef.Element(elementId)));

    public ActionResult SetValue(string elementId, string text) =>
        Perform(new ActionIntent(ActionKind.SetValue, TargetRef.Element(elementId),
            new Dictionary<string, object?> { ["text"] = text }));

    public ActionResult Toggle(string elementId) =>
        Perform(new ActionIntent(ActionKind.Toggle, TargetRef.Element(elementId)));

    public ActionResult Type(string elementId, string text) =>
        Perform(new ActionIntent(ActionKind.TypeText, TargetRef.Element(elementId),
            new Dictionary<string, object?> { ["text"] = text }));

    public ActionResult Focus(long hwnd) =>
        Perform(new ActionIntent(ActionKind.FocusWindow, TargetRef.Window(hwnd)));

    /// <summary>Vision element → desktop point via its frame transform, then
    /// SendInput click. The only vision→input path.</summary>
    public ActionResult ClickVision(VisionElement element, FrameTransform frameTransform)
    {
        var el = GroundingService.ToDesktop(element, frameTransform);
        _registry.Register([el]);
        return Click(el.Id);
    }

    /// <summary>Locate a described target in a frame and click it.</summary>
    public ActionResult? GroundClick(RawFrame frame, string targetDescription)
    {
        if (_grounding == null) return null;
        var vr = _grounding.Locate(frame, targetDescription);
        var first = vr.Elements.FirstOrDefault();
        if (first == null) return null;
        return ClickVision(first, frame.Transform);
    }

    // ---- vision / ocr ----
    public void SetVisionBackend(IVisionBackend? backend) => _vision = backend;
    public void SetGroundingBackend(IGroundingBackend? backend) => _grounding = backend;
    public IReadOnlyList<TextSpan> Ocr(RawFrame frame) => _ocr.Recognize(frame);
    public bool OcrAvailable => _ocr.Available;
    public string OcrLanguage => _ocr.Language;

    // ---- guards ----
    public bool SecureDesktopActive() => _integrity.IsSecureDesktopActive();
    public IntegrityRelation IntegrityOf(long hwnd) => _integrity.CheckTarget(hwnd);

    // ---- clipboard ----
    public string? ClipboardRead() => _clipboard.ReadText();
    public void ClipboardWrite(string text) => _clipboard.WriteText(text);

    // ---- launch ----
    /// <summary>Resolve + launch + bounded readiness wait for a Windows
    /// application. This is a mutating computer operation: the activity
    /// indicator turns Active, the kill switch denies, and safety policy
    /// screens the resolved target.</summary>
    public LaunchResult Launch(LaunchSpec spec, CancellationToken ct = default)
    {
        using var _ = _activity.BeginActivity();
        if (_policy.KillSwitch || EmergencyGate.IsStopped)
            return new LaunchResult(false, null, spec.App ?? spec.Path
                ?? spec.Aumid ?? spec.Uri, null, null, null, null, "Failed",
                0, 0, "EmergencyStopped",
                "computer control stopped by the local emergency hotkey; only the local user can resume");
        var r = _apps.Launch(spec, ct);
        var spawnedPid = r.Pid;
        if (spawnedPid.HasValue)
        {
            var procName = (r.Hwnd.HasValue ? _windows.GetWindow(r.Hwnd.Value)?.ProcessName : null)
                ?? r.ResolvedName
                ?? (r.ResolvedIdentifier != null ? Path.GetFileNameWithoutExtension(r.ResolvedIdentifier) : null)
                ?? "app";
            if (r.LaunchState == "AlreadyRunning")
                _provenance.MarkUsedProcess(spawnedPid.Value);
            else
                _provenance.RegisterAgentLaunch(spawnedPid.Value, procName, r.Hwnd, spec.App ?? spec.Executable ?? spec.Path);
        }
        _executor.NoteMutation(); // a new/changed top-level window invalidates cached queries
        _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
            "launch", spec.App ?? spec.Path ?? spec.Aumid ?? spec.Uri ?? "?",
            DurationMs: r.LaunchMs + r.ReadyMs,
            ElementCount: r.Success ? 1 : 0));
        return r;
    }

    // ---- waits ----
    public WaitResult WaitForElement(FindSpec spec, int timeoutMs = 10000) =>
        _wait.ForElement(spec, timeoutMs);
    public WaitResult WaitForStable(RectPx region, int quietMs = 1200, int timeoutMs = 15000) =>
        _wait.ForStable(region, quietMs, timeoutMs);
    public WaitResult WaitForStable(ChangeMonitor monitor, int timeoutMs = 15000) =>
        _wait.ForStable(monitor, timeoutMs);
    public WaitResult WaitForChange(ChangeMonitor monitor, int timeoutMs = 10000) =>
        _wait.ForChange(monitor, timeoutMs);
    public WaitResult WaitForEvent(Func<ObservedEvent, bool> predicate, int timeoutMs = 10000) =>
        _wait.ForEvent(predicate, timeoutMs);
    public WaitResult WaitForGone(string elementId, int timeoutMs = 8000) =>
        _wait.ForGone(elementId, timeoutMs);

    public void StartEvents() => _events.Start();

    /// <summary>The shared runtime services an agentic/MCP layer builds on.
    /// Session state stays per-caller; these low-level services are shared
    /// by design.</summary>
    public sealed record RuntimeParts(IWindowService Windows,
        ElementRegistry Registry, Executor Executor, WaitService Waits,
        RecentEventBuffer EventBuffer, IReadOnlyList<IElementBackend> Backends,
        SafetyPolicy Policy, IInputService Input, IAppService Apps);

    public RuntimeParts Parts => new(_windows, _registry, _executor, _wait,
        _eventBuffer, _agentBackends, _policy, _input, _apps);

    // ---- agent bridge (M3) -------------------------------------------------

    /// <summary>Build a canonical observation for the agent/model boundary.
    /// Elements come from the merged UIA+OCR+Vision scene. A persistent
    /// builder keeps frame ids and the delta baseline stable across calls.</summary>
    public BuiltObservation ObserveAgent(long? hwndHint = null,
        ObservationBudget? budget = null,
        VisualAttachPolicy screenshotPolicy = VisualAttachPolicy.Auto,
        ChangeMonitor? monitor = null, StepOutcome? prevOutcome = null)
        => (_obsBuilder ??= new ObservationBuilder(_windows, h => ObserveScene(h),
                CaptureRaw, _eventBuffer, _telemetry))
            .Build(hwndHint, budget ?? ObservationBudget.Default,
                screenshotPolicy, monitor, prevOutcome);

    /// <summary>Create a provider-independent agent orchestrator. The adapter
    /// is supplied per run — swap it to change the model. Each orchestrator
    /// gets a fresh AgentRunContext per run: isolated confirmer, budget,
    /// pause/cancel state and history.</summary>
    public AgentOrchestrator CreateAgent(AgentOptions? opts = null) =>
        new(new ObservationBuilder(_windows, h => ObserveScene(h),
                CaptureRaw, _eventBuffer, _telemetry),
            new ActionResolver(_registry, _executor, _wait, _windows,
                new AutoVerifier(_registry, _agentBackends, _windows, _eventBuffer)),
            h => Monitor(new CaptureTarget.Window(h)),
            _registry, _input, _policy, opts, _telemetry);

    /// <summary>Convenience: run a goal to completion (blocking).</summary>
    public AgentResult RunGoal(string goal, IModelAdapter adapter,
        AgentOptions? opts = null, long? hwndHint = null)
    {
        StartEvents(); // observations include the semantic event stream
        return CreateAgent(opts).Run(goal, adapter, hwndHint);
    }

    /// <summary>User-configured glow colors (settings.json) or null → defaults.</summary>
    private static GlowPalette? PaletteFromSettings()
    {
        var s = UserSettings.Load();
        var idle = UserSettings.ParseColor(s.IdleColor);
        var em = UserSettings.ParseColor(s.EmergencyColor);
        if (idle == null && em == null) return null;
        var d = GlowPalette.Default;
        return GlowPalette.FromColors(
            idle ?? (d.R, d.G, d.B), em ?? (d.EmR, d.EmG, d.EmB));
    }

    public void PostActivity(string text, string? targetApp = null, long? hwndHint = null)
        => _hud.SetActivity(text, targetApp, hwndHint);

    public void PostSuccess(string? text = null)
        => _hud.SetSuccess(text);

    public void PostFailure(string text)
        => _hud.SetFailure(text);

    public void PostHumanTakeover(string reason, string? nextStep = null)
        => _hud.SetHumanTakeover(reason, nextStep);

    public void SetHudEnabled(bool enabled)
        => _hud.SetEnabled(enabled);

    public void Dispose()
    {
        _store?.Flush();
        lock (_monitors)
        {
            foreach (var m in _monitors) m.Dispose();
            _monitors.Clear();
        }
        try { _input.ReleaseAll(); } catch { }
        _activity.SetConnected(false);
        _targetHighlight.Dispose();
        _hud.Dispose();
        _indicator.Dispose();
        _events.Dispose();
        _subscriptionManager.Dispose();
        _uiaReadScheduler.Dispose();
        _uiaDispatch.Dispose();
        _apps.Dispose();
        _telemetry?.Dispose();
        _activity.Dispose();
    }
}
