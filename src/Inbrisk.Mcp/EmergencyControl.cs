using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;

namespace Inbrisk.Mcp;

public enum ComputerControlState { Active, Paused, EmergencyStopped }

public interface IEmergencyHotkeyRegistrar : IDisposable
{
    bool PanicAvailable { get; }
    bool ResumeAvailable { get; }
    HotkeyChord? Resume { get; }
    void Start(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null);
    void TryReacquire(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null);
}

public sealed class RealEmergencyHotkeyRegistrar : IEmergencyHotkeyRegistrar
{
    private GlobalHotkeyService? _service;

    public bool PanicAvailable => _service?.PanicAvailable == true;
    public bool ResumeAvailable => _service?.ResumeAvailable == true;
    public HotkeyChord? Resume => _service?.Resume;

    public void Start(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null)
    {
        _service = new GlobalHotkeyService(panicHotkey, resumeHotkey, onPanic, onResume, log ?? (_ => { }));
        _service.Start();
    }

    public void TryReacquire(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null)
    {
        var svc = new GlobalHotkeyService(panicHotkey, resumeHotkey, onPanic, onResume, log ?? (_ => { }));
        svc.Start();
        var old = _service;
        _service = svc;
        try { old?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _service?.Dispose();
    }
}

public sealed class FakeEmergencyHotkeyRegistrar : IEmergencyHotkeyRegistrar
{
    public bool PanicAvailable { get; set; } = true;
    public bool ResumeAvailable { get; set; } = true;
    public HotkeyChord? Resume { get; set; }
    public bool StartCalled { get; private set; }

    public void Start(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null)
    {
        StartCalled = true;
        try { Resume = HotkeyChord.Parse(resumeHotkey); } catch { }
    }

    public void TryReacquire(string panicHotkey, string resumeHotkey, Action onPanic, Action onResume, Action<string>? log = null)
    {
        StartCalled = true;
    }

    public void Dispose() { }
}

/// <summary>
/// Headless fake physical input monitor for deterministic unit testing without OS hooks.
/// </summary>
public sealed class FakePhysicalInputMonitor : IPhysicalInputMonitor
{
    public event Action<PhysicalInputEvent>? InputDetected;
    public bool IsRunning { get; private set; }
    public int MouseMoveThresholdPx { get; set; } = 12;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool Disposed { get; private set; }

    public void Start()
    {
        if (Disposed) return;
        IsRunning = true;
        StartCount++;
    }

    public void Stop()
    {
        IsRunning = false;
        StopCount++;
    }

    public void SimulateInput(PhysicalInputEvent ev) => InputDetected?.Invoke(ev);

    public void SimulateKeyboard(uint vk = 0x41, uint flags = 0) =>
        SimulateInput(new PhysicalInputEvent(PhysicalInputKind.Keyboard, DateTimeOffset.UtcNow, VirtualKey: vk, Flags: flags));

    public void SimulateMouseMove(int x, int y, int deltaPx = 20, uint flags = 0) =>
        SimulateInput(new PhysicalInputEvent(PhysicalInputKind.MouseMove, DateTimeOffset.UtcNow, X: x, Y: y, DeltaPx: deltaPx, Flags: flags));

    public void SimulateMouseClick(int x = 100, int y = 100, uint flags = 0) =>
        SimulateInput(new PhysicalInputEvent(PhysicalInputKind.MouseButton, DateTimeOffset.UtcNow, X: x, Y: y, DeltaPx: 0, Flags: flags));

    public void SimulateMouseWheel(int x = 100, int y = 100, uint flags = 0) =>
        SimulateInput(new PhysicalInputEvent(PhysicalInputKind.MouseWheel, DateTimeOffset.UtcNow, X: x, Y: y, DeltaPx: 0, Flags: flags));

    public void Dispose()
    {
        Disposed = true;
        IsRunning = false;
    }
}

/// <summary>
/// Process-wide emergency control. One process per machine owns the panic
/// hotkey (the "authority"); every other Inbrisk process delegates to it via
/// the shared stop-marker file — so an MCP host that spawns several server
/// processes still gets one consistent kill switch. Cross-process sync:
/// authority writes/deletes the marker on panic/resume; peers poll it.
/// F18: peers NEVER resume just because the marker vanished — they mirror a
/// resume only while a live authority exists (its held lifetime event), so
/// deleting the marker and/or killing the authority cannot un-panic peers;
/// only the local resume gesture on a live authority clears a panic stop.
/// </summary>
public sealed class EmergencyControl : IDisposable
{
    public static EmergencyControl Process { get; internal set; } = new();

    /// <summary>Testing factory: provides an Active control instance without binding global OS hotkeys.</summary>
    public static EmergencyControl ForTests(IEmergencyHotkeyRegistrar? registrar = null, IPhysicalInputMonitor? inputMonitor = null) =>
        new(registrar ?? new FakeEmergencyHotkeyRegistrar(), testActive: true, inputMonitor: inputMonitor ?? new FakePhysicalInputMonitor());

    // scoped by chord + marker path: two processes delegate to each other
    // only when they share the exact same emergency configuration — a peer
    // watching a different marker file could otherwise stay Active while
    // the authority's panic goes unseen
    private string OwnerMutexName => EmergencyGate.OwnerMutexName(PanicHotkey);

    private readonly object _gate = new();
    private readonly HashSet<IInputService> _inputs = new();
    private readonly List<Func<bool>> _interventionSuppressors = new();
    private CancellationTokenSource _epoch = new();
    private readonly IEmergencyHotkeyRegistrar _registrar;
    private readonly IPhysicalInputMonitor _inputMonitor;
    private HumanInterventionMode _interventionMode = HumanInterventionMode.Off;
    private int _mouseMoveThresholdPx = 12;
    private bool _pausedByHumanIntervention;
    private string? _pauseReason;
    private PhysicalInputKind? _lastInterventionKind;
    private Mutex? _ownerMutex;
    private Timer? _markerWatch;
    // F18: while stopped, the authority parks a background thread holding a
    // named kernel mutex (EmergencyGate.StoppedMutexName). Mutex ownership
    // is thread-scoped — no other process can release or delete it — so
    // deleting the stop-marker file can no longer clear the panic state.
    private Thread? _stopMutexHolder;
    private ManualResetEventSlim? _stopMutexRelease;
    // F18: while this process is the panic authority it also holds a named
    // event open (EmergencyGate.AuthorityEventName) for its whole lifetime.
    // An open event handle tracks the process — no thread affinity, no
    // release by another process — so "event gone" unambiguously means the
    // authority died. Peers consult it as the resume gate: a vanished
    // marker with no live authority is NOT a resume.
    private EventWaitHandle? _authorityEvent;
    private bool _wedgeLogged;
    private ComputerControlState _state = ComputerControlState.EmergencyStopped;
    private bool _stoppedByPanic;
    private bool _peerAuthority;   // another inbrisk process owns the hotkey
    private bool _disposed;
    private readonly bool _testMode;
    private bool _hotkeysStarted;

    public EmergencyControl() : this(new RealEmergencyHotkeyRegistrar(), testActive: false, inputMonitor: new PhysicalInputMonitor())
    {
    }

    internal EmergencyControl(IEmergencyHotkeyRegistrar registrar, bool testActive = false, IPhysicalInputMonitor? inputMonitor = null)
    {
        _registrar = registrar;
        _inputMonitor = inputMonitor ?? new FakePhysicalInputMonitor();
        _inputMonitor.InputDetected += OnPhysicalInput;
        if (testActive)
        {
            _state = ComputerControlState.Active;
            _testMode = true;
        }
    }

    // F32: emergency-control transitions are part of the tamper-evident
    // audit trail — ACL'd ProgramData location, hash-chained lines, plus an
    // agent-readable mirror under the data dir.
    private readonly string _telemetryPath = AuditLog.Path("mcp-emergency.jsonl");
    private readonly string? _mirrorPath = AuditLog.MirrorPath("mcp-emergency.jsonl");
    /// <summary>Stop state survives reconnect/process restart — a new MCP
    /// session must never bypass a panic stop.</summary>
    public string StopMarkerPath { get; } = EmergencyGate.MarkerPath;

    public string PanicHotkey { get; private set; } = "Ctrl+Alt+Pause";
    public string ResumeHotkey { get; private set; } = "Ctrl+Alt+Shift+Pause";
    public bool PanicAvailable => _registrar.PanicAvailable;
    public bool ResumeAvailable => _registrar.ResumeAvailable;
    /// <summary>Panic coverage exists but is owned by a peer process.</summary>
    public bool PanicDelegated { get { lock (_gate) return _peerAuthority; } }
    public ComputerControlState State { get { lock (_gate) return _state; } }
    public HumanInterventionMode InterventionMode { get { lock (_gate) return _interventionMode; } }
    public int MouseMoveThresholdPx { get { lock (_gate) return _mouseMoveThresholdPx; } }
    public bool PausedByHumanIntervention { get { lock (_gate) return _pausedByHumanIntervention; } }
    public string? PauseReason { get { lock (_gate) return _pauseReason; } }
    public PhysicalInputKind? LastInterventionKind { get { lock (_gate) return _lastInterventionKind; } }
    /// <summary>The recorded panic authority (owner file) — for diagnostics
    /// surfaces like `inbrisk status`/`doctor`.</summary>
    public EmergencyGate.OwnerInfo? Authority => EmergencyGate.ReadOwner();
    /// <summary>Fired on any control-state transition — drives the screen
    /// indicator's emergency visual. Raised outside the lock.</summary>
    public event Action<ComputerControlState>? StateChanged;
    /// <summary>Fired when physical human input pauses active control. Raised outside the lock.</summary>
    public event Action<PhysicalInputEvent, string>? HumanInterventionDetected;

    /// <summary>
    /// Configures human-intervention auto-pause mode and mouse movement deadzone.
    /// Starts or stops the underlying <see cref="IPhysicalInputMonitor"/> accordingly.
    /// </summary>
    public void ConfigureHumanIntervention(HumanInterventionMode mode, int mouseMoveThresholdPx = 12)
    {
        var clamped = Math.Max(0, mouseMoveThresholdPx);
        bool shouldRun;
        lock (_gate)
        {
            if (_disposed) return;
            _interventionMode = mode;
            _mouseMoveThresholdPx = clamped;
            shouldRun = mode != HumanInterventionMode.Off;
        }

        _inputMonitor.MouseMoveThresholdPx = clamped;
        if (shouldRun)
            _inputMonitor.Start();
        else
            _inputMonitor.Stop();
    }

    /// <summary>
    /// Registers a suppression predicate (e.g. Ghost OS desktop mode) — while any registered
    /// predicate returns true, interactive-desktop physical input will NOT pause control.
    /// </summary>
    public IDisposable RegisterInterventionSuppression(Func<bool> isSuppressed)
    {
        lock (_gate) _interventionSuppressors.Add(isSuppressed);
        return new SuppressionRegistration(this, isSuppressed);
    }

    private void OnPhysicalInput(PhysicalInputEvent ev)
    {
        // Defense-in-depth: never react to injected events even if a custom monitor emits them.
        if (ev.Kind == PhysicalInputKind.Keyboard && PhysicalInputMonitor.IsKeyboardEventInjected(ev.Flags))
            return;
        if (ev.Kind != PhysicalInputKind.Keyboard && PhysicalInputMonitor.IsMouseEventInjected(ev.Flags))
            return;

        IInputService[] inputs;
        string reason;
        lock (_gate)
        {
            if (_disposed || _stoppedByPanic || _state != ComputerControlState.Active)
                return;

            var matches = _interventionMode switch
            {
                HumanInterventionMode.Off => false,
                HumanInterventionMode.PauseOnKeyboard => ev.Kind == PhysicalInputKind.Keyboard,
                HumanInterventionMode.PauseOnMouse => ev.Kind is PhysicalInputKind.MouseMove or PhysicalInputKind.MouseButton or PhysicalInputKind.MouseWheel,
                HumanInterventionMode.PauseOnAnyInput => true,
                _ => false
            };
            if (!matches)
                return;

            // Defense-in-depth deadzone check for MouseMove (clicks/wheel always count).
            if (ev.Kind == PhysicalInputKind.MouseMove && _mouseMoveThresholdPx > 0 && ev.DeltaPx < _mouseMoveThresholdPx)
                return;

            // Ghost OS / session-level suppression check.
            foreach (var suppressor in _interventionSuppressors)
            {
                try { if (suppressor()) return; }
                catch { /* fail-open to safe pause if predicate faults */ }
            }

            reason = ev.Kind == PhysicalInputKind.Keyboard
                ? "HumanKeyboardIntervention"
                : "HumanMouseIntervention";

            _state = ComputerControlState.Paused;
            _pausedByHumanIntervention = true;
            _pauseReason = reason;
            _lastInterventionKind = ev.Kind;
            _epoch.Cancel();
            inputs = _inputs.ToArray();
        }

        foreach (var input in inputs)
            try { input.ReleaseAll(); }
            catch (Exception e) { Log($"input release on human intervention failed: {e.Message}"); }

        FireStateChanged();
        try { HumanInterventionDetected?.Invoke(ev, reason); }
        catch { /* never break control */ }
        Log($"Paused ({reason})");
    }

    /// <summary>
    /// Resumes control from a human-intervention <see cref="ComputerControlState.Paused"/> state.
    /// NEVER clears <see cref="ComputerControlState.EmergencyStopped"/> — panic always requires
    /// local resume.
    /// </summary>
    public bool ResumeFromPause(string source = "computer_resume_run")
    {
        lock (_gate)
        {
            if (_disposed || _stoppedByPanic || _state != ComputerControlState.Paused)
                return false;
            _epoch.Dispose();
            _epoch = new CancellationTokenSource();
            _state = ComputerControlState.Active;
            _pausedByHumanIntervention = false;
            _pauseReason = null;
            _lastInterventionKind = null;
        }
        FireStateChanged();
        Log($"Active ({source})");
        return true;
    }

    private void FireStateChanged()
    {
        try { StateChanged?.Invoke(State); } catch { /* never break control */ }
    }

    public void StartHotkeys(string? panic = null, string? resume = null)
    {
        if (_hotkeysStarted) return;
        _hotkeysStarted = true;
        PanicHotkey = panic ?? "Ctrl+Alt+Pause";
        ResumeHotkey = resume ?? "Ctrl+Alt+Shift+Pause";
        try
        {
            _registrar.Start(PanicHotkey, ResumeHotkey,
                () => ApplyStop("panic hotkey"), () => ApplyResume("local resume hotkey"), Log);

            // Authority: whoever holds the panic chord. If our registration
            // failed, check whether a peer already owns it — if so, panic
            // still works (they write the marker, we watch it). If the chord
            // is held by a foreign app, no authority exists → stay stopped.
            if (_registrar.PanicAvailable)
                BecomeAuthority();
            else
                lock (_gate) _peerAuthority = DetectPeerAuthority();

            var stoppedMarker = EmergencyGate.IsStopped;
            lock (_gate)
            {
                _stoppedByPanic = stoppedMarker;
                if ((_registrar.PanicAvailable || _peerAuthority || _testMode) && !stoppedMarker)
                    _state = ComputerControlState.Active;
            }
            FireStateChanged();
            if (stoppedMarker)
                Log("Emergency stop marker restored — control remains stopped");
            if (!_registrar.PanicAvailable && !_testMode)
                Log(_peerAuthority
                    ? $"Panic hotkey {PanicHotkey} owned by peer Inbrisk process — delegated via stop marker"
                    : $"Emergency hotkey unavailable: {PanicHotkey}; computer control remains disabled " +
                      "(chord held by a non-Inbrisk process or stale owner record — `inbrisk doctor` shows details)");

            _markerWatch = new Timer(_ =>
            {
                try { SyncMarker(); } catch { /* never kill the watcher */ }
            }, null, 150, 150);
        }
        catch (Exception e)
        {
            Log($"Emergency hotkey setup failed: {e.Message}; computer control remains disabled");
        }
    }

    public IDisposable RegisterInput(IInputService input)
    {
        lock (_gate) _inputs.Add(input);
        return new InputRegistration(this, input);
    }

    public CancellationToken? ActionToken()
    {
        lock (_gate)
        {
            if (_state != ComputerControlState.Active)
                return null;
            if (_testMode)
                return _epoch.Token;
            if (!(_registrar.PanicAvailable || _peerAuthority))
                return null;
            // belt-and-suspenders: marker may have appeared between watcher
            // ticks; the held-mutex backstop covers a deleted marker (F18)
            if (EmergencyGate.IsStopped) return null;
            return _epoch.Token;
        }
    }

    public bool IsLocalResumeKey(string? key, IReadOnlyList<string>? modifiers)
    {
        if (key == null) return false;
        var resume = _registrar.Resume;
        if (resume == null) return false;
        try
        {
            if (modifiers == null)
                return HotkeyChord.Parse("Ctrl+" + key).VirtualKey == resume.Value.VirtualKey;
            var chord = HotkeyChord.Parse(string.Join("+", modifiers.Append(key)));
            return chord.VirtualKey == resume.Value.VirtualKey &&
                chord.Modifiers == resume.Value.Modifiers;
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>Peers mirror the authority's panic/resume. The stopped
    /// signal is marker-file OR held-mutex: a deleted file alone is NOT a
    /// resume — only the authority's local gesture releases the mutex
    /// (F18). If we are the authority and the marker vanished while the
    /// mutex is still held, rewrite it so late-joining processes stay
    /// stopped. A resume is mirrored only while a LIVE authority exists —
    /// its lifetime event stays open exactly as long as the process runs,
    /// so "signals gone + authority alive" means a local resume happened,
    /// while "signals gone + authority dead" means it was killed or the
    /// files tampered with — fail closed: stay stopped for a local
    /// gesture, never auto-resume.</summary>
    private void SyncMarker()
    {
        var markerExists = File.Exists(StopMarkerPath);
        var mutexHeld = EmergencyGate.StoppedMutexHeld() == true;
        if (mutexHeld && !markerExists)
        {
            lock (_gate)
            {
                if (_registrar.PanicAvailable && _stoppedByPanic && !_disposed)
                    try { File.WriteAllText(StopMarkerPath,
                        DateTimeOffset.UtcNow.ToString("O")); }
                    catch { /* marker heal is best-effort — the mutex still latches the stop */ }
            }
        }
        if (markerExists || mutexHeld)
            ApplyStop("peer panic marker", recheckMarker: true);
        else if (EmergencyGate.AuthorityAlive(PanicHotkey) == true)
            // Live authority holds no stop latch — it resumed through its
            // own local gesture; mirror it. The lifetime event can only be
            // held by the authority process itself, so this branch is
            // unreachable after the authority is killed (F18).
            ApplyResume("authority resume", fromAuthoritySync: true);
        else
        {
            // Marker/mutex gone with NO live authority: the previous
            // authority was killed while stopped or its files were deleted
            // after death — indistinguishable from a legit resume only to
            // something that cannot see process liveness. Stay stopped; a
            // wedged peer reclaims the chord below so a local gesture has
            // somewhere to land.
            bool report;
            lock (_gate) { report = _stoppedByPanic && !_wedgeLogged; _wedgeLogged |= report; }
            if (report)
                Log("Stop signals cleared with no live panic authority — control stays " +
                    $"stopped (possible tamper); local resume: {ResumeHotkey} or the tray icon");
        }

        // Reacquire attempts: a stopped peer retries only while no live
        // authority owns the chord — re-probe first, since the recorded
        // peer authority may have died (wedge case). Reclaiming the chord
        // while _stoppedByPanic re-latches the stop under the new
        // authority but never activates — a real panic still requires an
        // explicit local resume.
        if (Interlocked.Increment(ref _retryTicks) % 20 != 0) return;
        lock (_gate)
        {
            if (_disposed || PanicAvailable ||
                _state != ComputerControlState.EmergencyStopped) return;
            if (_peerAuthority)
                _peerAuthority = DetectPeerAuthority();
            if (_peerAuthority) return;
        }
        TryReacquireAuthority();
    }

    private int _retryTicks;

    /// <summary>This process holds the panic chord — publish ownership so
    /// peers and diagnostics can tell a live authority from a foreign chord
    /// holder. The mutex is acquired explicitly with WaitOne(0): the
    /// initiallyOwned flag is silently ignored when the named mutex already
    /// exists (e.g. created earlier by a probing peer), which previously
    /// left the mutex unowned and made every peer conclude "no authority"
    /// → permanent EmergencyStopped. The owner file is the primary channel;
    /// the mutex is the fallback for older binaries.</summary>
    private void BecomeAuthority()
    {
        try
        {
            // Lifetime event (F18): held open for the rest of this
            // process's life so peers can prove a live authority exists.
            // Created here, only ever opened (never created) by peers.
            _authorityEvent ??= new EventWaitHandle(false,
                EventResetMode.ManualReset,
                EmergencyGate.AuthorityEventName(PanicHotkey));
        }
        catch (Exception e) { Log($"authority event create failed: {e.Message}"); }
        try { _ownerMutex?.Dispose(); } catch { }
        _ownerMutex = new Mutex(initiallyOwned: false, OwnerMutexName);
        try { _ownerMutex.WaitOne(0); }
        catch (AbandonedMutexException) { /* abandoned = acquired */ }
        catch { /* ownership best-effort — owner file is primary */ }
        EmergencyGate.WriteOwner(PanicHotkey);
    }

    /// <summary>Another live Inbrisk process holds the panic chord? Owner
    /// record first (pid + start-time validated); the named mutex is the
    /// fallback — someone owning it means an authority exists even if the
    /// owner file is missing (older binary).</summary>
    private bool DetectPeerAuthority()
    {
        var owner = EmergencyGate.ReadOwner();
        // chord must match: an authority configured with a different panic
        // hotkey is not ours to delegate to
        if (owner != null && owner.PanicHotkey.Equals(PanicHotkey,
                StringComparison.OrdinalIgnoreCase) &&
            EmergencyGate.OwnerAlive(owner)) return true;
        // Lifetime event: a live authority holds it open for its whole
        // process life — robust where the owner mutex's thread-affinity
        // can be lost when the acquiring thread exits (F18).
        if (EmergencyGate.AuthorityAlive(PanicHotkey) == true) return true;
        try
        {
            _ownerMutex ??= new Mutex(false, OwnerMutexName);
            var free = _ownerMutex.WaitOne(0);
            if (free) _ownerMutex.ReleaseMutex();
            return !free;
        }
        catch (AbandonedMutexException)
        { try { _ownerMutex?.ReleaseMutex(); } catch { } return false; }
        catch { return false; }
    }

    /// <summary>Re-run hotkey registration + peer probing after the chord was
    /// unavailable at startup. Runs on the marker-watch timer thread.</summary>
    private void TryReacquireAuthority()
    {
        if (_testMode) return;
        try
        {
            _registrar.TryReacquire(PanicHotkey, ResumeHotkey,
                () => ApplyStop("panic hotkey"),
                () => ApplyResume("local resume hotkey"), Log);
        }
        catch { /* construction/start failure → stay stopped, retry next tick */ }

        bool stillStopped;
        lock (_gate)
        {
            if (_disposed) return;

            if (_registrar.PanicAvailable)
            {
                _peerAuthority = false;
                if (_stoppedByPanic)
                {
                    // Chord reclaimed while still panic-stopped (the previous
                    // authority was killed — F18 wedge): re-latch the stop
                    // under the new authority so peers keep mirroring it.
                    // Activation is still gated below — reclaiming the
                    // chord is never a resume by itself.
                    try { File.WriteAllText(StopMarkerPath,
                        DateTimeOffset.UtcNow.ToString("O")); }
                    catch { /* marker write best-effort; the held mutex is the durable latch */ }
                    TryHoldStoppedMutexLocked();
                }
                BecomeAuthority();
            }
            else
            {
                _peerAuthority = DetectPeerAuthority();
            }
            if (!_stoppedByPanic &&
                (_registrar.PanicAvailable || _peerAuthority) &&
                !EmergencyGate.IsStopped)
            {
                _epoch.Dispose();
                _epoch = new CancellationTokenSource();
                _state = ComputerControlState.Active;
            }
            stillStopped = _stoppedByPanic;
        }
        FireStateChanged();
        if (PanicAvailable || PanicDelegated)
            Log(stillStopped
                ? "Emergency hotkey reacquired — control remains stopped until a local resume"
                : "Emergency hotkey reacquired — computer control enabled");
    }

    /// <summary>Acquire and park the emergency-stop mutex on a background
    /// thread (authority only). The thread keeps the mutex owned until
    /// <see cref="ReleaseStoppedMutexLocked"/> signals resume — nothing
    /// outside this process can release it, which is what makes the stop
    /// survive deletion of the marker file.</summary>
    private void TryHoldStoppedMutexLocked()
    {
        if (_stopMutexHolder != null || _testMode ||
            !_registrar.PanicAvailable) return;
        var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            try
            {
                using var m = new Mutex(false, EmergencyGate.StoppedMutexName);
                try { m.WaitOne(); }
                catch (AbandonedMutexException) { /* abandoned = acquired */ }
                release.Wait();
                try { m.ReleaseMutex(); } catch { }
            }
            catch { /* hold is best-effort; the marker file remains the broadcast */ }
        })
        { IsBackground = true, Name = "inbrisk-estop-hold" };
        _stopMutexRelease = release;
        _stopMutexHolder = holder;
        holder.Start();
    }

    /// <summary>Signal the holder thread to release the mutex and wait for
    /// the release to complete, so a marker/mutex recheck immediately
    /// after this lock cannot observe a half-resumed state.</summary>
    private void ReleaseStoppedMutexLocked()
    {
        var holder = _stopMutexHolder;
        var release = _stopMutexRelease;
        _stopMutexHolder = null;
        _stopMutexRelease = null;
        if (holder == null) return;
        try { release?.Set(); } catch { }
        try { holder.Join(250); } catch { }
    }

    private void ApplyStop(string source, bool recheckMarker = false)
    {
        IInputService[] inputs;
        lock (_gate)
        {
            if (_stoppedByPanic || _disposed) return;
            // A watcher tick can observe a stale "stopped" signal (marker
            // still on disk / mutex still held) while a local resume is
            // mid-flight; re-verify under the lock so the resume gesture
            // is never silently overwritten back to stopped.
            if (recheckMarker && !EmergencyGate.IsStopped) return;
            _state = ComputerControlState.EmergencyStopped;
            _stoppedByPanic = true;
            _pausedByHumanIntervention = false;
            _pauseReason = null;
            _lastInterventionKind = null;
            _epoch.Cancel();
            TryHoldStoppedMutexLocked();
            inputs = _inputs.ToArray();
        }
        try { File.WriteAllText(StopMarkerPath, DateTimeOffset.UtcNow.ToString("O")); }
        catch (Exception e) { Log($"stop marker write failed: {e.Message}"); }
        // Hard input cleanup, bounded and high-priority: each input service
        // rejects new injected input, drains open transactions briefly,
        // force-releases every owned key/button and reconciles against real
        // keyboard/mouse state — panic never waits on a worker queue.
        foreach (var input in inputs)
            try { input.EmergencyCleanup(); }
            catch (Exception e) { Log($"input release failed: {e.Message}"); }
        FireStateChanged();
        Log($"EmergencyStopped ({source})");
    }

    private void ApplyResume(string source, bool fromAuthoritySync = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Authority sync only mirrors panic-stop clears; it must never
            // auto-resume a local human-intervention Paused state.
            if (fromAuthoritySync && !_stoppedByPanic) return;

            try { File.Delete(StopMarkerPath); }
            catch (Exception e) { Log($"stop marker delete failed: {e.Message}"); }
            // F18: release the held stop mutex under the same lock so peers
            // never observe "file gone, mutex still held" as a new stop.
            ReleaseStoppedMutexLocked();

            // SyncMarker invokes this every 150ms while no marker exists —
            // skip the teardown and broadcast unless we were actually stopped.
            if (_state == ComputerControlState.Active && !_stoppedByPanic) return;
            _epoch.Dispose();
            _epoch = new CancellationTokenSource();
            _stoppedByPanic = false;
            _pausedByHumanIntervention = false;
            _pauseReason = null;
            _lastInterventionKind = null;
            _wedgeLogged = false;
            _state = ComputerControlState.Active;
            foreach (var input in _inputs)
                try { input.ClearEmergency(); } catch { }
        }
        FireStateChanged();
        Log($"Active ({source})");
    }

    /// <summary>
    /// Invoked strictly by local user UI (e.g. control window or tray click) — never via MCP.
    /// </summary>
    public void TriggerLocalPanic(string source = "local user UI") => ApplyStop(source);

    /// <summary>
    /// Invoked strictly by local user UI (e.g. control window or tray click) — never via MCP.
    /// </summary>
    public void TriggerLocalResume(string source = "local user UI")
    {
        try { File.Delete(StopMarkerPath); } catch { }
        ApplyResume(source);
    }

    private void Log(string message)
    {
        Console.Error.WriteLine($"Inbrisk emergency control: {message}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_telemetryPath)!);
            var record = JsonSerializer.SerializeToNode(new { at = DateTimeOffset.UtcNow,
                category = "emergency_control", state = State.ToString(),
                message, panicHotkey = PanicHotkey, resumeHotkey = ResumeHotkey })!.AsObject();
            lock (_gate)
            {
                var line = AuditLog.ChainLine(record, AuditLog.LastChainHash(_telemetryPath));
                File.AppendAllText(_telemetryPath, line + "\n");
                try
                {
                    if (_mirrorPath != null)
                        File.AppendAllText(_mirrorPath, line + "\n");
                }
                catch { /* best-effort mirror */ }
            }
        }
        catch (Exception e) { Console.Error.WriteLine($"Inbrisk emergency telemetry failed: {e.Message}"); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _state = ComputerControlState.EmergencyStopped;
            _disposed = true;
            try { _epoch.Cancel(); } catch (ObjectDisposedException) { }
            ReleaseStoppedMutexLocked();
            foreach (var input in _inputs)
                try { input.ReleaseAll(); } catch { }
            _inputs.Clear();
        }
        _markerWatch?.Dispose();
        try { _inputMonitor.InputDetected -= OnPhysicalInput; } catch { }
        try { _inputMonitor.Dispose(); } catch { }
        _registrar.Dispose();
        try { _authorityEvent?.Dispose(); } catch { }
        try { _ownerMutex?.ReleaseMutex(); } catch { }
        try { _ownerMutex?.Dispose(); } catch { }
        EmergencyGate.ClearOwner(Environment.ProcessId);
        try { _epoch.Dispose(); } catch (ObjectDisposedException) { }
    }

    private sealed class InputRegistration(EmergencyControl owner, IInputService input) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._inputs.Remove(input); }
    }

    private sealed class SuppressionRegistration(EmergencyControl owner, Func<bool> predicate) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._interventionSuppressors.Remove(predicate); }
    }
}
