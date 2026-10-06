using Inbrisk.Core;
using Inbrisk.Platform.Windows.Events;
using Inbrisk.Platform.Windows.Input;
using Inbrisk.Runtime;
using Inbrisk.Sdk;

namespace Inbrisk.Mcp;

/// <summary>Auto-dismiss policy requested for a run's reflex engine —
/// the MCP-side name for the contract's AutoDismissMode. Kept as a local
/// enum so the tool surface stays stable even if the Runtime enum shifts
/// again; the adapter maps it (ReflexDismissMode → AutoDismissMode).</summary>
public enum ReflexDismissMode
{
    /// <summary>Log/observe only — the engine never touches the modal.</summary>
    Off,
    /// <summary>Save-confirm prompts commit (click Save).</summary>
    Save,
    /// <summary>Save-confirm prompts discard (click Don't Save).</summary>
    Discard,
    /// <summary>Save-confirm prompts cancel (keep work, abort the close).
    /// Safe default — never commits and never loses data.</summary>
    CloseOnly,
}

/// <summary>A single modal interception recorded by the reflex engine —
/// surfaced on run results as reflex.modals[].</summary>
public sealed record ReflexInterception(
    string? Title,
    string? Disposition,
    string? ActionTaken,
    long Ms,
    IReadOnlyList<string>? ButtonsSeen = null,
    string? Reason = null,
    long Hwnd = 0);

/// <summary>Set once a modal forced the plan to stop — e.g. a security
/// surface the policy refuses to touch, or a confirm with no safe
/// button. Produced by the reflex engine; RunPlanCore maps it to the
/// InterruptedByDialog run error.</summary>
public sealed record ReflexAbort(
    string? Title,
    string? Disposition,
    string Reason,
    IReadOnlyList<string>? ButtonsSeen = null,
    long ModalHwnd = 0);

/// <summary>
/// Seam over the Runtime modal-reflex engine (Inbrisk.Runtime.ReflexEngine:
/// Arm/Disarm/Paused/Abort/Log + ManualResetEventSlim PauseGate). The run
/// loop consumes this interface only — Arm-while-armed re-targets
/// interception at the run's current main window and clears a latched
/// Abort; the engine must never swallow EmergencyStopped (RunPlanCore
/// re-checks the epoch before honouring Abort and waits the gate on the
/// run's linked cancellation token).
/// </summary>
public interface IReflexEngine : IDisposable
{
    /// <summary>True while interception is armed.</summary>
    bool Armed { get; }
    /// <summary>The hwnd interception is currently scoped to (0 when disarmed).</summary>
    long ArmedHwnd { get; }
    /// <summary>(Re)arm interception against the run's current main window.</summary>
    void Arm(long mainHwnd, int mainPid, ReflexDismissMode mode);
    /// <summary>Stop interception. Idempotent.</summary>
    void Disarm();
    /// <summary>A modal is being inspected/dismissed — the run should
    /// wait on <see cref="WaitPausedAsync"/> instead of stepping forward.</summary>
    bool Paused { get; }
    /// <summary>Non-null once a modal forced plan abort — takes precedence
    /// over <see cref="Paused"/> at the between-step gate.</summary>
    ReflexAbort? Abort { get; }
    /// <summary>Session-cumulative interception log; runs slice it by
    /// offset (RunState.ReflexLogBaseline).</summary>
    IReadOnlyList<ReflexInterception> Log { get; }
    /// <summary>Wait while a modal is being handled. Returns true when the
    /// pause cleared; false or OperationCanceledException on
    /// abort/timeout — callers bound it (~30s) via ct.</summary>
    Task<bool> WaitPausedAsync(CancellationToken ct);
}

/// <summary>
/// Inert reflex engine — arms/disarms no-ops, never pauses, never aborts.
/// Fallback when the real engine cannot be constructed (event source
/// absent, platform unavailable) so a run never dies on reflex setup.
/// </summary>
public sealed class NullReflexEngine : IReflexEngine
{
    public bool Armed { get; private set; }
    public long ArmedHwnd { get; private set; }

    public void Arm(long mainHwnd, int mainPid, ReflexDismissMode mode)
    {
        Armed = true;
        ArmedHwnd = mainHwnd;
    }

    public void Disarm()
    {
        Armed = false;
        ArmedHwnd = 0;
    }

    public bool Paused => false;
    public ReflexAbort? Abort => null;
    public IReadOnlyList<ReflexInterception> Log => Array.Empty<ReflexInterception>();
    public Task<bool> WaitPausedAsync(CancellationToken ct) => Task.FromResult(true);
    public void Dispose() { }
}

/// <summary>
/// IReflexEngine over the real Inbrisk.Runtime.ReflexEngine. Owns the
/// platform ModalInterruptInterceptor (which watches the shared
/// WinEventService stream — via a shim over InbriskRuntime.Event, the
/// only public event surface) and drives its Start/Stop alongside the
/// engine's Arm/Disarm: the engine filters sightings to the armed
/// window's pid + owner chain, so interceptor arming tracks engine arming.
/// </summary>
public sealed class RuntimeReflexEngine : IReflexEngine
{
    /// <summary>IModalInterruptSource over the platform interceptor —
    /// maps the interceptor's own ModalInterrupt record onto the Core
    /// contract shape the engine consumes.</summary>
    private sealed class InterruptSourceAdapter : IModalInterruptSource
    {
        private readonly ModalInterruptInterceptor _inner;
        public event Action<global::Inbrisk.Core.ModalInterrupt>? ModalDetected;

        public InterruptSourceAdapter(ModalInterruptInterceptor inner)
        {
            _inner = inner;
            _inner.Interrupted += OnInterrupted;
        }

        private void OnInterrupted(global::Inbrisk.Platform.Windows.Events.ModalInterrupt m)
        {
            try
            {
                ModalDetected?.Invoke(new global::Inbrisk.Core.ModalInterrupt(
                    m.Hwnd, m.Title, m.ClassName, m.Pid, m.OwnerHwnd,
                    m.At, m.Detection));
            }
            catch { /* event fan-out must never fault the pump thread */ }
        }
    }

    /// <summary>IEventSource over InbriskRuntime.Event — the runtime only
    /// exposes the event, not the CoalescingEventSource object. Start is
    /// a no-op: the runtime owns the pump's lifetime.</summary>
    private sealed class EventSourceShim : IEventSource
    {
        private readonly InbriskRuntime _rt;
        public event Action<ObservedEvent>? Event;

        public EventSourceShim(InbriskRuntime rt)
        {
            _rt = rt;
            _rt.Event += OnEvent;
        }

        private void OnEvent(ObservedEvent e)
        {
            try { Event?.Invoke(e); }
            catch { /* never fault the source */ }
        }

        public void Start() { }
        public void Dispose()
        {
            try { _rt.Event -= OnEvent; } catch { }
        }
    }

    private readonly ReflexEngine _engine;
    private readonly ModalInterruptInterceptor _interceptor;
    private readonly EventSourceShim _shim;
    private long _armedHwnd;

    public RuntimeReflexEngine(InbriskRuntime rt)
    {
        ArgumentNullException.ThrowIfNull(rt);
        var parts = rt.Parts;
        _shim = new EventSourceShim(rt);
        _interceptor = new ModalInterruptInterceptor(_shim, parts.Windows);
        // silent input: a dedicated ISilentInputService — message-only
        // (BM_CLICK/WM_CLOSE), never SendInput, matching the executor's.
        _engine = new ReflexEngine(
            parts.Windows,
            new SilentInputService(),
            new DelegateReflexPolicy(ReflexPolicy.Decide, ReflexPolicy.IsDangerous),
            source: new InterruptSourceAdapter(_interceptor),
            events: parts.EventBuffer);
    }

    public bool Armed => _engine.Armed;
    public long ArmedHwnd => _armedHwnd;
    public bool Paused => _engine.Paused;

    public ReflexAbort? Abort => _engine.Abort is { } a
        ? new ReflexAbort(a.Title, a.Disposition.ToString(), a.Reason,
            a.ButtonsSeen, a.ModalHwnd)
        : null;

    public IReadOnlyList<ReflexInterception> Log =>
        _engine.Log.Select(m => new ReflexInterception(
            m.Title,
            m.Disposition.ToString(),
            m.Success ? m.Action.ToString() : $"{m.Action} failed",
            (long)m.ElapsedMs,
            Reason: m.Reason,
            Hwnd: m.Hwnd)).ToArray();

    public void Arm(long mainHwnd, int mainPid, ReflexDismissMode mode)
    {
        // engine first: an interrupt delivered between the two calls is
        // only useful if the engine is already armed — arming the source
        // first would let a sighting land on a disarmed engine and be
        // dropped permanently.
        _engine.Arm(mainHwnd, mainPid, Map(mode));
        _interceptor.Start(mainHwnd, mainPid > 0 ? mainPid : null);
        _armedHwnd = mainHwnd;
    }

    public void Disarm()
    {
        _interceptor.Stop();
        _engine.Disarm();
        _armedHwnd = 0;
    }

    /// <summary>The engine's pause gate is a ManualResetEventSlim — the
    /// wait is synchronous, so it's lifted onto the pool. ct still bounds
    /// it (~30s per modal at the call site) and panic cancels through.</summary>
    public Task<bool> WaitPausedAsync(CancellationToken ct) =>
        Task.Run(() =>
        {
            _engine.WaitWhilePaused(ct);
            return !_engine.Paused;
        }, ct);

    public void Dispose()
    {
        try { _engine.Dispose(); } catch { }
        try { _interceptor.Dispose(); } catch { }
        try { _shim.Dispose(); } catch { }
    }

    private static AutoDismissMode Map(ReflexDismissMode m) => m switch
    {
        ReflexDismissMode.Off => AutoDismissMode.Off,
        ReflexDismissMode.Save => AutoDismissMode.Save,
        ReflexDismissMode.Discard => AutoDismissMode.Discard,
        _ => AutoDismissMode.CloseOnly,
    };
}

public static class ReflexEngines
{
    /// <summary>One reflex engine per session, built from the runtime's
    /// window service + a dedicated silent-input service + the shared
    /// event stream. Any construction fault degrades to the Null engine —
    /// reflex must never take a run down with it.</summary>
    public static IReflexEngine CreateDefault(InbriskRuntime rt)
    {
        try { return new RuntimeReflexEngine(rt); }
        catch { return new NullReflexEngine(); }
    }
}
