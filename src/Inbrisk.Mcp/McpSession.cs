using Inbrisk.Core;
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
    public long? ScopeHwnd { get; set; }
    /// <summary>Live automation runs — runId → bindings + progress so a
    /// paused/checkpointed plan can be resumed by appending steps.</summary>
    public Dictionary<string, RunState> Runs { get; } = new();
    /// <summary>Default screenshot policy for computer_observe — sessions may
    /// differ; nothing process-global is mutated.</summary>
    public VisualAttachPolicy AttachPolicy { get; set; } = VisualAttachPolicy.Auto;
    public TaskRecipeStore RecipeStore { get; } = new();
    public DesktopStateMemory Memory { get; } = new();
    public ApplicationAdapterRegistry Adapters { get; } = new();
    public IDesktopArbiter Arbiter { get; } = DesktopArbiter.Shared;

    private ChangeMonitor? _monitor;
    private long _scopeHwndForMonitor;
    private readonly List<string> _recentRefs = new();

    public McpSession(EmergencyControl control, string? telemetryPath = null)
    {
        Control = control;
        // AutoConfirm: the MCP client's explicit tool call IS the
        // confirmation — the client is the brain. Deny-classified actions
        // (elevated targets, password fields, deny-listed processes,
        // kill-switch) still refuse regardless.
        Rt = new InbriskRuntime(new InbriskOptions(AutoConfirm: true,
            StartEvents: true,
            TelemetryPath: telemetryPath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "inbrisk", "mcp-telemetry.jsonl")));
        var p = Rt.Parts;
        _inputRegistration = control.RegisterInput(p.Input);
        // screen-control indicator: the session refcounted-lease pattern
        // means the smoke exists only while a client is attached —
        // Disconnected renders nothing, whatever the process state
        _clientLease = Rt.Activity.AttachClient();
        Rt.Activity.SetEmergency(control.State == ComputerControlState.EmergencyStopped);
        control.StateChanged += OnControlState;
        Obs = new ObservationBuilder(p.Windows, h => Rt.ObserveScene(h),
            Rt.CaptureRaw, p.EventBuffer, null);
        Resolver = new ActionResolver(p.Registry, p.Executor, p.Waits,
            p.Windows, new AutoVerifier(p.Registry, p.Backends, p.Windows,
                p.EventBuffer));
        if (DesktopArbiter.Shared.OnInputCleanup == null)
            DesktopArbiter.Shared.OnInputCleanup = () => { try { Rt.Parts.Input.SweepAll(); } catch { } };
    }

    /// <summary>Session monitor for wait_for_change/stable — created lazily
    /// against the current scope window.</summary>
    public ChangeMonitor MonitorFor(long hwnd)
    {
        if (_monitor == null || _scopeHwndForMonitor != hwnd || !_monitor.Session.IsRunning)
        {
            _monitor?.Dispose();
            _monitor = Rt.Monitor(new CaptureTarget.Window(hwnd));
            _scopeHwndForMonitor = hwnd;
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

    public void Dispose()
    {
        SessionCts.Cancel();
        try { Rt.PurgePendingWork(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        try { Rt.Parts.Input.ReleaseAll(); } catch { }
        _inputRegistration.Dispose();
        Control.StateChanged -= OnControlState;
        try { _clientLease.Dispose(); } catch { }
        try { Rt.Dispose(); } catch { }
        SessionCts.Dispose();
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
