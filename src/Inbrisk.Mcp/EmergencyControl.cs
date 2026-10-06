using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;

namespace Inbrisk.Mcp;

public enum ComputerControlState { Active, Paused, EmergencyStopped }
public enum HumanInterventionMode { Off, PauseOnKeyboard, PauseOnMouse, PauseOnAnyInput }

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
/// Process-wide emergency control. One process per machine owns the panic
/// hotkey (the "authority"); every other Inbrisk process delegates to it via
/// the shared stop-marker file — so an MCP host that spawns several server
/// processes still gets one consistent kill switch. Cross-process sync:
/// authority writes/deletes the marker on panic/resume; peers poll it.
/// </summary>
public sealed class EmergencyControl : IDisposable
{
    public static EmergencyControl Process { get; internal set; } = new();

    /// <summary>Testing factory: provides an Active control instance without binding global OS hotkeys.</summary>
    public static EmergencyControl ForTests(IEmergencyHotkeyRegistrar? registrar = null) => new(registrar ?? new FakeEmergencyHotkeyRegistrar(), testActive: true);

    // scoped by chord + marker path: two processes delegate to each other
    // only when they share the exact same emergency configuration — a peer
    // watching a different marker file could otherwise stay Active while
    // the authority's panic goes unseen
    private string OwnerMutexName => EmergencyGate.OwnerMutexName(PanicHotkey);

    private readonly object _gate = new();
    private readonly HashSet<IInputService> _inputs = new();
    private CancellationTokenSource _epoch = new();
    private readonly IEmergencyHotkeyRegistrar _registrar;
    private Mutex? _ownerMutex;
    private Timer? _markerWatch;
    private ComputerControlState _state = ComputerControlState.EmergencyStopped;
    private bool _stoppedByPanic;
    private bool _peerAuthority;   // another inbrisk process owns the hotkey
    private bool _disposed;
    private readonly bool _testMode;
    private bool _hotkeysStarted;

    public EmergencyControl() : this(new RealEmergencyHotkeyRegistrar(), testActive: false)
    {
    }

    internal EmergencyControl(IEmergencyHotkeyRegistrar registrar, bool testActive = false)
    {
        _registrar = registrar;
        if (testActive)
        {
            _state = ComputerControlState.Active;
            _testMode = true;
        }
    }

    private readonly string _telemetryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "inbrisk", "mcp-emergency.jsonl");
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
    /// <summary>The recorded panic authority (owner file) — for diagnostics
    /// surfaces like `inbrisk status`/`doctor`.</summary>
    public EmergencyGate.OwnerInfo? Authority => EmergencyGate.ReadOwner();
    /// <summary>Fired on any control-state transition — drives the screen
    /// indicator's emergency visual. Raised outside the lock.</summary>
    public event Action<ComputerControlState>? StateChanged;

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

            var stoppedMarker = File.Exists(StopMarkerPath);
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
            // belt-and-suspenders: marker may have appeared between watcher ticks
            if (File.Exists(StopMarkerPath)) return null;
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

    /// <summary>Peers mirror the authority's panic/resume through the marker.</summary>
    private void SyncMarker()
    {
        if (File.Exists(StopMarkerPath)) ApplyStop("peer panic marker");
        else ApplyResume("peer resume marker");

        // Recoverable stop: the panic chord was held by another process at
        // startup and no peer authority existed. That holder may have exited
        // since — retry acquisition periodically instead of staying stopped
        // forever. Never retries while _stoppedByPanic (a real panic must
        // require an explicit resume).
        if (Interlocked.Increment(ref _retryTicks) % 20 != 0) return;
        lock (_gate)
            if (_state != ComputerControlState.EmergencyStopped ||
                _stoppedByPanic || _disposed ||
                PanicAvailable || _peerAuthority) return;
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

        lock (_gate)
        {
            if (_disposed || _stoppedByPanic) return;

            if (_registrar.PanicAvailable)
            {
                _peerAuthority = false;
                BecomeAuthority();
            }
            else
            {
                _peerAuthority = DetectPeerAuthority();
            }
            if ((_registrar.PanicAvailable || _peerAuthority) &&
                !File.Exists(StopMarkerPath))
            {
                _epoch.Dispose();
                _epoch = new CancellationTokenSource();
                _state = ComputerControlState.Active;
            }
        }
        FireStateChanged();
        if (PanicAvailable || PanicDelegated)
            Log("Emergency hotkey reacquired — computer control enabled");
    }

    private void ApplyStop(string source)
    {
        IInputService[] inputs;
        lock (_gate)
        {
            if (_stoppedByPanic || _disposed) return;
            _state = ComputerControlState.EmergencyStopped;
            _stoppedByPanic = true;
            _epoch.Cancel();
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

    private void ApplyResume(string source)
    {
        lock (_gate)
        {
            try { File.Delete(StopMarkerPath); }
            catch (Exception e) { Log($"stop marker delete failed: {e.Message}"); }

            if (_disposed) return;
            // SyncMarker invokes this every 150ms while no marker exists —
            // skip the teardown and broadcast unless we were actually stopped.
            if (_state == ComputerControlState.Active && !_stoppedByPanic) return;
            _epoch.Dispose();
            _epoch = new CancellationTokenSource();
            _stoppedByPanic = false;
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
            lock (_gate)
                File.AppendAllText(_telemetryPath,
                    JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow,
                        category = "emergency_control", state = State.ToString(),
                        message, panicHotkey = PanicHotkey, resumeHotkey = ResumeHotkey }) + "\n");
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
            foreach (var input in _inputs)
                try { input.ReleaseAll(); } catch { }
            _inputs.Clear();
        }
        _markerWatch?.Dispose();
        _registrar.Dispose();
        try { _ownerMutex?.ReleaseMutex(); } catch { }
        try { _ownerMutex?.Dispose(); } catch { }
        EmergencyGate.ClearOwner(Environment.ProcessId);
        try { _epoch.Dispose(); } catch (ObjectDisposedException) { }
    }

    private sealed class InputRegistration(EmergencyControl owner, IInputService input) : IDisposable
    {
        public void Dispose() { lock (owner._gate) owner._inputs.Remove(input); }
    }
}
