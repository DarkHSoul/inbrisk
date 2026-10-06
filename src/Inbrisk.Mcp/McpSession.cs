using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Runtime;
using Inbrisk.Runtime.Adapters;
using Inbrisk.Sdk;

namespace Inbrisk.Mcp;

/// <summary>
/// One MCP stdio session = one process = one client. Owns the runtime, the
/// observation builder, the frame registry that coordinate actions validate
/// against, and the session's capture monitor. Dispose → input release +
/// capture teardown. The low-level Windows services live in the shared
/// InbriskRuntime; everything session-scoped (last observation, frames,
/// monitor, budget policy, recent refs) lives here.
/// </summary>
public sealed class McpSession : IDisposable
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..10];
    public InbriskRuntime Rt { get; }
    public EmergencyControl Control { get; }
    private readonly IDisposable _inputRegistration;
    private readonly IDisposable _clientLease;
    public ObservationBuilder Obs { get; }
    public ActionResolver Resolver { get; }
    public CancellationTokenSource SessionCts { get; } = new();

    /// <summary>Frames this session has minted (observe + screenshots) —
    /// the registry coordinate actions are validated against.</summary>
    public Dictionary<long, FrameRef> Frames { get; } = new();
    /// <summary>Observation ids this session minted — a coordinate action may
    /// only reference one of these (frame race guard).</summary>
    public HashSet<long> ValidObsIds { get; } = new();
    public Dictionary<long, long> FrameObservations { get; } = new();
    public AgentObservation? LastObservation { get; private set; }
    public StepOutcome? PrevOutcome { get; set; }
    private long? _scopeHwnd;
    public long? ScopeHwnd
    {
        get
        {
            if (_scopeHwnd.HasValue && Rt?.WindowService?.GetWindow(_scopeHwnd.Value) == null)
                _scopeHwnd = null;
            return _scopeHwnd;
        }
        set => _scopeHwnd = value;
    }
    /// <summary>Live automation runs — runId → bindings + progress so a
    /// paused/checkpointed plan can be resumed by appending steps.</summary>
    public Dictionary<string, RunState> Runs { get; } = new();
    /// <summary>Default screenshot policy for computer_observe — sessions may
    /// differ; nothing process-global is mutated.</summary>
    public VisualAttachPolicy AttachPolicy { get; set; } = VisualAttachPolicy.Auto;
    public TaskRecipeStore RecipeStore { get; } = new();
    public ApplicationProfileStore ProfileStore { get; } = new();
    public DesktopStateMemory Memory { get; } = new();
    public ApplicationAdapterRegistry Adapters { get; } = new();
    public IDesktopArbiter Arbiter { get; set; } = DesktopArbiter.Shared;
    public SessionTelemetry Telemetry { get; } = new();
    public RequestDeduplicator Deduplicator { get; } = new();
    public DynamicToolset.DynamicToolsetManager DynamicToolset { get; } = new();

    public List<SessionWindowProvenance> TrackedWindows { get; } = new();
    public DateTimeOffset LastAppsQueryTimestamp { get; set; } = DateTimeOffset.MinValue;
    public string? LastAppsQueryName { get; set; }
    public DateTimeOffset LastLaunchTimestamp { get; set; } = DateTimeOffset.MinValue;
    public long LastLaunchedHwnd { get; set; } = 0;

    public PreLaunchSnapshot CapturePreLaunchSnapshot()
    {
        var existingHwnds = new HashSet<long>();
        var existingPids = new Dictionary<int, DateTimeOffset?>();

        try
        {
            var wins = Rt?.Windows() ?? Array.Empty<Inbrisk.Core.WindowInfo>();
            foreach (var w in wins)
            {
                existingHwnds.Add(w.Hwnd);
                if (w.Pid > 0 && !existingPids.ContainsKey(w.Pid))
                {
                    existingPids[w.Pid] = null;
                }
            }
        }
        catch { }

        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    existingPids[p.Id] = p.StartTime;
                }
                catch
                {
                    if (!existingPids.ContainsKey(p.Id))
                        existingPids[p.Id] = null;
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch { }

        return new PreLaunchSnapshot(DateTimeOffset.UtcNow, existingHwnds, existingPids);
    }

    public void RecordWindowLaunch(
        long hwnd,
        int pid,
        string appIdentity,
        bool agentOwned,
        string? launchRunId = null,
        string? title = null,
        bool alreadyRunning = false,
        LifecycleIntent intent = LifecycleIntent.Unknown)
    {
        DateTimeOffset? startTime = null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            startTime = p.StartTime;
        }
        catch { }

        var prov = new SessionWindowProvenance(
            LaunchRunId: launchRunId,
            Hwnd: hwnd,
            Pid: pid,
            ProcessStartTime: startTime,
            LaunchTimestamp: DateTimeOffset.UtcNow,
            AppIdentity: appIdentity,
            Title: title ?? appIdentity,
            AgentOwned: agentOwned,
            AlreadyRunning: alreadyRunning,
            ClosedOrStale: false,
            Intent: intent
        );

        lock (TrackedWindows)
        {
            TrackedWindows.RemoveAll(w => w.Hwnd == hwnd);
            TrackedWindows.Add(prov);
        }
    }

    public void SetLifecycleIntent(long hwnd, LifecycleIntent intent)
    {
        lock (TrackedWindows)
        {
            foreach (var w in TrackedWindows.Where(x => x.Hwnd == hwnd))
            {
                w.Intent = intent;
            }
        }
        Rt.Provenance.SetLifecycleIntent(hwnd, intent);
    }

    public void MarkWindowClosed(long hwnd)
    {
        lock (TrackedWindows)
        {
            foreach (var w in TrackedWindows.Where(x => x.Hwnd == hwnd))
            {
                w.ClosedOrStale = true;
            }
        }
    }

    /// <summary>
    /// Long-lived session-scoped find-result cache.
    /// Preserves entries across individual MCP tool requests in the session.
    /// Invalidated by MutationVersion, scoped UI events, and 400ms TTL.
    /// </summary>
    public SessionFindCache FindCache { get; }

    /// <summary>
    /// Session-scoped short-lived snapshot cache for computer_inspect results.
    /// </summary>
    public SessionSnapshotCache SnapshotCache { get; }

    /// <summary>
    /// Session-scoped metadata cache for HWND -> process identity (PID, process name, elevation).
    /// </summary>
    public HwndMetadataCache HwndMetadata { get; }

    /// <summary>
    /// Session-scoped monitor topology cache (monitors and virtual desktop geometry).
    /// </summary>
    public MonitorTopologyCache MonitorCache { get; }

    /// <summary>
    /// Session-lifetime holder for IVirtualDesktopManager instance.
    /// </summary>
    public VirtualDesktopManagerHolder VdmHolder { get; }

    public int FindCacheEntryCount => FindCache.EntryCount;
    public int SnapshotCacheEntryCount => SnapshotCache.EntryCount;
    public int HwndMetadataEntryCount => HwndMetadata.EntryCount;
    public int FindInFlightCount => FindCache.InFlightCount;
    public int SnapshotInFlightCount => SnapshotCache.InFlightCount;
    public int FindActiveOwnedProducerCount => FindCache.ActiveOwnedProducerCount;
    public int SnapshotActiveOwnedProducerCount => SnapshotCache.ActiveOwnedProducerCount;
    public int FindDetachedProducerCount => FindCache.DetachedProducerCount;
    public int SnapshotDetachedProducerCount => SnapshotCache.DetachedProducerCount;

    /// <summary>
    /// Long-lived session-scoped negative-result (not-found diagnosis) cache.
    /// Preserves diagnostic results for unchanged negative queries without running UIA sweeps.
    /// Invalidated by MutationVersion and scope-matched UI events.
    /// </summary>
    public Dictionary<string, (TargetDiagnosis Diag, DateTimeOffset At, HashSet<long> Hwnds, HashSet<int> Pids, long Ver)> NegativeFindCache { get; } = new();

    private ChangeMonitor? _monitor;
    private long _scopeHwndForMonitor;
    private readonly List<string> _recentRefs = new();

    public McpSession(EmergencyControl control, string? telemetryPath = null, bool startEvents = true, IDesktopArbiter? arbiter = null)
    {
        Control = control;
        if (arbiter != null) Arbiter = arbiter;
        FindCache = new SessionFindCache(() => Rt?.MutationVersion ?? 0, telemetry: Telemetry.FindCache);
        SnapshotCache = new SessionSnapshotCache(() => Rt?.MutationVersion ?? 0, telemetry: Telemetry.SnapshotCache);
        HwndMetadata = new HwndMetadataCache(telemetry: Telemetry.HwndMetadata);
        MonitorCache = new MonitorTopologyCache(telemetry: Telemetry.MonitorTopology);
        VdmHolder = new VirtualDesktopManagerHolder();

        // AutoConfirm: the MCP client's explicit tool call IS the
        // confirmation — the client is the brain. Deny-classified actions
        // (elevated targets, password fields, deny-listed processes,
        // kill-switch) still refuse regardless.
        Rt = new InbriskRuntime(new InbriskOptions(AutoConfirm: true,
            StartEvents: startEvents,
            TelemetryPath: telemetryPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "inbrisk", "mcp-telemetry.jsonl"),
            HwndMetadataCache: HwndMetadata,
            MonitorTopologyCache: MonitorCache,
            VdmHolder: VdmHolder));
        Rt.Event += OnEvent;
        var p = Rt.Parts;
        _inputRegistration = control.RegisterInput(p.Input);
        // screen-control indicator: the session refcounted-lease pattern
        // means the smoke exists only while a client is attached —
        // Disconnected renders nothing, whatever the process state
        _clientLease = Rt.Activity.AttachClient();
        Rt.Activity.SetEmergency(control.State == ComputerControlState.EmergencyStopped);
        control.StateChanged += OnControlState;
        Obs = new ObservationBuilder(p.Windows, h => Rt.ObserveScene(h),
            Rt.CaptureRaw, p.EventBuffer, null, (h, ct) => Rt.ObserveSceneAsync(h, ct: ct));
        Resolver = new ActionResolver(p.Registry, p.Executor, p.Waits,
            p.Windows, new AutoVerifier(p.Registry, p.Backends, p.Windows,
                p.EventBuffer));
        if (Arbiter is DesktopArbiter da && da.OnInputCleanup == null)
            da.OnInputCleanup = () => { try { Rt.Parts.Input.SweepAll(); } catch { } };

        if (Rt.ReadScheduler is { } sched)
        {
            sched.OnReadQueued = () => Telemetry.IncUiaReadQueued();
            sched.OnReadDequeued = () => Telemetry.DecUiaReadQueued();
            sched.OnReadActiveStarted = () => Telemetry.IncUiaReadActive();
            sched.OnReadActiveFinished = () => Telemetry.DecUiaReadActive();
            sched.OnReadQueueWaitRecorded = ms => Telemetry.AddUiaReadQueueWaitMs(ms);
            sched.OnReadExecutionRecorded = ms => Telemetry.AddUiaReadExecutionMs(ms);
            sched.OnReadTimeout = () => Telemetry.IncUiaReadTimeout();
            sched.OnReadCancelled = () => Telemetry.IncUiaReadCancelled();
            sched.OnLanePid = pid => Telemetry.SetUiaReadLanePid(pid);
            sched.OnWriteQueued = () => Telemetry.IncUiaWriteQueued();
            sched.OnWriteDequeued = () => Telemetry.DecUiaWriteQueued();
            sched.OnWriteQueueWaitRecorded = ms => Telemetry.AddUiaWriteQueueWaitMs(ms);
            sched.OnWriteExecutionRecorded = ms => Telemetry.AddUiaWriteExecutionMs(ms);
            sched.OnPoisonedWorker = () => Telemetry.IncUiaPoisonedWorkers();
            sched.OnWorkerReplaced = () => Telemetry.IncUiaWorkerReplacements();
            sched.OnWorkerAbandoned = () => Telemetry.IncUiaAbandonedWorkers();
            sched.OnWorkerReplacementRejected = () => Telemetry.IncUiaWorkerReplacementRejected();
            sched.OnSyncOverAsyncFallback = () => Telemetry.IncSyncOverAsyncFallback();
        }
    }

    /// <summary>Session monitor for wait_for_change/stable — created lazily
    /// against the current scope window, with automatic foreground or desktop fallback.</summary>
    public ChangeMonitor MonitorFor(long hwnd)
    {
        if (hwnd == 0 || Rt?.WindowService?.GetWindow(hwnd) == null)
        {
            var fg = Rt.ForegroundWindow()?.Hwnd ?? 0;
            if (fg != 0 && Rt?.WindowService?.GetWindow(fg) != null)
                hwnd = fg;
        }

        if (_monitor == null || _scopeHwndForMonitor != hwnd || !_monitor.Session.IsRunning)
        {
            _monitor?.Dispose();
            try
            {
                var target = (hwnd != 0 && Rt?.WindowService?.GetWindow(hwnd) != null)
                    ? (CaptureTarget)new CaptureTarget.Window(hwnd)
                    : (CaptureTarget)new CaptureTarget.FullDesktop();
                _monitor = Rt.Monitor(target);
                _scopeHwndForMonitor = hwnd;
            }
            catch
            {
                _monitor = Rt.Monitor(new CaptureTarget.FullDesktop());
                _scopeHwndForMonitor = 0;
            }
        }
        return _monitor;
    }

    private void OnControlState(ComputerControlState s)
    {
        Rt.Activity.SetEmergency(s == ComputerControlState.EmergencyStopped);
        if (s == ComputerControlState.EmergencyStopped)
            Rt.PurgePendingWork();
    }

    private readonly Dictionary<long, AgentObservation> _observationHistory = new();

    public AgentObservation? GetObservation(long id) =>
        _observationHistory.TryGetValue(id, out var obs) ? obs : null;

    /// <summary>Build the canonical observation, keep its frames in the
    /// session registry for later coordinate validation.</summary>
    public BuiltObservation Observe(long? hwndHint, ObservationBudget budget,
        VisualAttachPolicy policy, long? baseSnapshotId = null)
    {
        using var _act = Rt.Activity.BeginActivity(); // observing = using the computer
        var baseObs = baseSnapshotId.HasValue ? GetObservation(baseSnapshotId.Value) : null;
        var built = Obs.Build(hwndHint ?? ScopeHwnd, budget, policy, _monitor,
            PrevOutcome, _recentRefs, baseSnapshot: baseObs);
        foreach (var kv in built.Frames)
        {
            Frames[kv.Key] = kv.Value;
            FrameObservations[kv.Key] = built.Observation.ObservationId;
        }
        ValidObsIds.Add(built.Observation.ObservationId);
        if (ValidObsIds.Count > 24) ValidObsIds.Remove(ValidObsIds.Min());
        _observationHistory[built.Observation.ObservationId] = built.Observation;
        while (_observationHistory.Count > 24)
        {
            var oldest = _observationHistory.Keys.Min();
            _observationHistory.Remove(oldest);
        }
        LastObservation = built.Observation;
        ScopeHwnd = hwndHint ?? ScopeHwnd ?? built.Observation.ActiveWindow?.Hwnd;
        if (built.Observation.ActiveWindow is { } aw)
        {
            Memory.RecordObservation(aw.Hwnd, aw.Process, aw.Title, built.Observation.Elements);
        }
        TrimFrames();
        return built;
    }

    public async Task<BuiltObservation> ObserveAsync(long? hwndHint, ObservationBudget budget,
        VisualAttachPolicy policy, long? baseSnapshotId = null, CancellationToken ct = default)
    {
        using var _act = Rt.Activity.BeginActivity();
        var baseObs = baseSnapshotId.HasValue ? GetObservation(baseSnapshotId.Value) : null;
        var built = await Obs.BuildAsync(hwndHint ?? ScopeHwnd, budget, policy, _monitor,
            PrevOutcome, _recentRefs, baseSnapshot: baseObs, ct: ct).ConfigureAwait(false);
        foreach (var kv in built.Frames)
        {
            Frames[kv.Key] = kv.Value;
            FrameObservations[kv.Key] = built.Observation.ObservationId;
        }
        ValidObsIds.Add(built.Observation.ObservationId);
        if (ValidObsIds.Count > 24) ValidObsIds.Remove(ValidObsIds.Min());
        _observationHistory[built.Observation.ObservationId] = built.Observation;
        while (_observationHistory.Count > 24)
        {
            var oldest = _observationHistory.Keys.Min();
            _observationHistory.Remove(oldest);
        }
        LastObservation = built.Observation;
        ScopeHwnd = hwndHint ?? ScopeHwnd ?? built.Observation.ActiveWindow?.Hwnd;
        if (built.Observation.ActiveWindow is { } aw)
        {
            Memory.RecordObservation(aw.Hwnd, aw.Process, aw.Title, built.Observation.Elements);
        }
        TrimFrames();
        return built;
    }

    /// <summary>Screenshots are the heaviest context item — a session-level
    /// counter (tools are request-scoped, so this can't live on them)
    /// detects screenshot-polling while an app loads.</summary>
    private readonly List<DateTimeOffset> _shotTimes = new();

    /// <summary>Record a screenshot; returns how many were taken in the
    /// last minute (including this one).</summary>
    public int NoteScreenshot()
    {
        var now = DateTimeOffset.UtcNow;
        _shotTimes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        _shotTimes.Add(now);
        return _shotTimes.Count;
    }

    /// <summary>Mint a standalone screenshot frame into the session registry —
    /// a coordinate click can later reference this frameId.</summary>
    public (FrameRef Ref, long ObsId) MintFrame(RawFrame raw, long? hwnd)
    {
        using var _act = Rt.Activity.BeginActivity();
        var meta = new ObsFrameRef(FrameIds.Next(), raw.Width, raw.Height,
            raw.Transform.SourceRect, VisualAttach.WindowFrame, raw.ToPng(),
            hwnd, DateTimeOffset.Now,
            hwnd is { } h ? Rt.Window(h)?.Bounds : null);
        var fref = new FrameRef(raw, meta);
        Frames[meta.FrameId] = fref;
        // screenshots mint their own observation id so a coordinate click
        // can name the exact image it derived from
        var obsId = FrameIds.Next();
        FrameObservations[meta.FrameId] = obsId;
        ValidObsIds.Add(obsId);
        TrimFrames();
        return (fref, obsId);
    }

    private void TrimFrames()
    {
        foreach (var id in Frames
            .Where(kv => (DateTimeOffset.Now - kv.Value.Meta.At).TotalSeconds > 15)
            .Select(kv => kv.Key).ToArray())
        {
            Frames.Remove(id);
            FrameObservations.Remove(id);
        }
        foreach (var id in Frames.OrderBy(kv => kv.Value.Meta.At)
            .Take(Math.Max(0, Frames.Count - 16)).Select(kv => kv.Key).ToArray())
        {
            Frames.Remove(id);
            FrameObservations.Remove(id);
        }
        ValidObsIds.RemoveWhere(id => id != LastObservation?.ObservationId &&
            !FrameObservations.ContainsValue(id));
    }

    public void NoteElementRef(string? elementId)
    {
        if (elementId == null) return;
        _recentRefs.Add(elementId);
        if (_recentRefs.Count > 8) _recentRefs.RemoveAt(0);
    }

    /// <summary>
    /// Invalidate all cached frame IDs, element references, and observation history.
    /// Called when control transitions back from a human user to guarantee no stale
    /// handles or pre-human coordinates are used.
    /// </summary>
    public void InvalidateStaleHandles()
    {
        ValidObsIds.Clear();
        Frames.Clear();
        FrameObservations.Clear();
        _observationHistory.Clear();
        _recentRefs.Clear();
    }

    private void OnEvent(ObservedEvent e)
    {
        if (e.Kind is EventKind.StructureChanged or EventKind.NameChanged or EventKind.WindowClosed)
        {
            FindCache.InvalidateScope(e.Hwnd, e.Pid);
            SnapshotCache.InvalidateScope(e.Hwnd);
        }
        else if (e.Kind is EventKind.WindowOpened or EventKind.WindowShown)
        {
            SnapshotCache.InvalidateScope(e.Hwnd);
        }

        if (e.Kind == EventKind.WindowClosed && e.Hwnd.HasValue)
        {
            HwndMetadata.InvalidateHwnd(e.Hwnd.Value);
        }

        if (e.Kind == EventKind.ForegroundChanged && e.Hwnd.HasValue)
        {
            var win = Rt?.Window(e.Hwnd.Value);
            DynamicToolset.UpdateForegroundContext(win?.ProcessName, win?.Title, e.At);
        }
    }

    private int _disposed;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { SessionCts.Cancel(); } catch { }
        try { Rt.Event -= OnEvent; } catch { }
        try { DynamicToolset.Dispose(); } catch { }
        try { FindCache.Dispose(); } catch { }
        try { SnapshotCache.Dispose(); } catch { }
        try { HwndMetadata.Dispose(); } catch { }
        try { MonitorCache.Dispose(); } catch { }
        try { VdmHolder.Dispose(); } catch { }
        try { Rt.PurgePendingWork(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        try { Rt.TargetHighlight.OnSessionDisconnected(SessionId); } catch { }
        try { Rt.Activity.ClearSessionActivity(SessionId); } catch { }
        try { Rt.Parts.Input.ReleaseAll(); } catch { }
        try { _inputRegistration.Dispose(); } catch { }
        try { Control.StateChanged -= OnControlState; } catch { }
        try { _clientLease.Dispose(); } catch { }
        try { Rt.Dispose(); } catch { }
        try { SessionCts.Dispose(); } catch { }
    }
}

/// <summary>Live plan-run state: element bindings persist across the
/// calls that resume a paused/checkpointed run.</summary>
public sealed class RunState
{
    public required string RunId { get; init; }
    public long? ScopeHwnd { get; set; }
    public long? BaselineHwnd { get; set; }
    public List<UiElement> Baseline { get; set; } = [];
    /// <summary>Timestamp of the last real baseline snapshot.</summary>
    public DateTimeOffset BaselineAt { get; set; }
    /// <summary>Run start — used as the delta reference while the baseline
    /// is deferred (read-only plans skip the pre-run subtree snapshot).</summary>
    public DateTimeOffset RunStartedAt { get; set; }
    /// <summary>True when the plan skipped the baseline snapshot — Delta()
    /// then derives output from the event stream or pays the snapshot only
    /// when something actually changed.</summary>
    public bool BaselineDeferred { get; set; }
    public Dictionary<string, string> Bindings { get; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<Dictionary<string, object?>> Collected { get; } = [];
    public string? LastElementId { get; set; }
    public int TotalExecuted { get; set; }
    /// <summary>Report verbosity for this run — set from the last call's
    /// resolved `detail` (slim caps delta/availableElements output).</summary>
    public bool Slim { get; set; }

    // --- Onuncu Sıçrama: Human Takeover & Seamless Resume State ---
    /// <summary>Original or current plan steps stored so resume can run remaining steps without re-submitting.</summary>
    public InbriskTools.RunStep[]? PlannedSteps { get; set; }
    /// <summary>The step index at which the plan paused (0-indexed).</summary>
    public int PausedStepIndex { get; set; } = -1;
    /// <summary>Status when paused: e.g. "PausedForHuman", "TargetNotFound", "UnexpectedModalOpened".</summary>
    public string? PauseStatus { get; set; }
    /// <summary>Human or diagnostic explanation for why the run paused.</summary>
    public string? PauseReason { get; set; }
    /// <summary>Human takeover flag: indicates the user has taken over control.</summary>
    public bool IsPausedForHuman { get; set; }
    /// <summary>Flag set by computer_pause_run requesting a pause at the next safe step boundary.</summary>
    public bool SafePointPauseRequested { get; set; }
    /// <summary>Foreground HWND when pause occurred (used to detect if user changed windows).</summary>
    public long? PrePauseForegroundHwnd { get; set; }
    /// <summary>Foreground window title when pause occurred.</summary>
    public string? PrePauseForegroundTitle { get; set; }
    /// <summary>Timestamp when paused.</summary>
    public DateTimeOffset? PausedAt { get; set; }
    /// <summary>Report of what changed while human had control (populated upon resume).</summary>
    public Dictionary<string, object?>? HumanChanges { get; set; }
}

/// <summary>Process-wide frame id counter — shared with ObservationBuilder's
/// internal sequence so screenshot mints never collide with observe frames.</summary>
internal static class FrameIds
{
    private static long _seq = 1_000_000_000; // well above builder ids
    public static long Next() => Interlocked.Increment(ref _seq);
}

/// <summary>
/// Explicit window provenance tracked within an McpSession for Section C1.
/// Distinguishes agent-opened applications from pre-existing user applications.
/// </summary>
public sealed record SessionWindowProvenance(
    string? LaunchRunId,
    long Hwnd,
    int Pid,
    DateTimeOffset? ProcessStartTime,
    DateTimeOffset LaunchTimestamp,
    string AppIdentity,
    string Title,
    bool AgentOwned,
    bool AlreadyRunning,
    bool ClosedOrStale,
    LifecycleIntent Intent = LifecycleIntent.Unknown
)
{
    public bool ClosedOrStale { get; set; } = ClosedOrStale;
    public LifecycleIntent Intent { get; set; } = Intent;
}

