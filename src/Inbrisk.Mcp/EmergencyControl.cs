using System.Diagnostics;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Input;

namespace Inbrisk.Mcp;

public enum ComputerControlState { Active, Paused, EmergencyStopped }
public enum HumanInterventionMode { Off, PauseOnKeyboard, PauseOnMouse, PauseOnAnyInput }

/// <summary>
/// Process-wide emergency control. One process per machine owns the panic
/// hotkey (the "authority"); every other Inbrisk process delegates to it via
/// the shared stop-marker file — so an MCP host that spawns several server
/// processes still gets one consistent kill switch. Cross-process sync:
/// authority writes/deletes the marker on panic/resume; peers poll it.
/// </summary>
public sealed class EmergencyControl : IDisposable
{
    public static EmergencyControl Process { get; } = new();

    // scoped by chord + marker path: two processes delegate to each other
    // only when they share the exact same emergency configuration — a peer
    // watching a different marker file could otherwise stay Active while
    // the authority's panic goes unseen
    private string OwnerMutexName => EmergencyGate.OwnerMutexName(PanicHotkey);

    private readonly object _gate = new();
    private readonly HashSet<IInputService> _inputs = new();
    private CancellationTokenSource _epoch = new();
    private GlobalHotkeyService? _hotkeys;
    private Mutex? _ownerMutex;
    private Timer? _markerWatch;
    private ComputerControlState _state = ComputerControlState.EmergencyStopped;
    private bool _stoppedByPanic;
    private bool _peerAuthority;   // another inbrisk process owns the hotkey
    private bool _disposed;
    private readonly string _telemetryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "inbrisk", "mcp-emergency.jsonl");
    /// <summary>Stop state survives reconnect/process restart — a new MCP
    /// session must never bypass a panic stop.</summary>
    public string StopMarkerPath { get; } = EmergencyGate.MarkerPath;

    public string PanicHotkey { get; private set; } = "Ctrl+Alt+Pause";
    public string ResumeHotkey { get; private set; } = "Ctrl+Alt+Shift+Pause";
    public bool PanicAvailable => _hotkeys?.PanicAvailable == true;
    public bool ResumeAvailable => _hotkeys?.ResumeAvailable == true;
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
        if (_hotkeys != null) return;
        PanicHotkey = panic ?? "Ctrl+Alt+Pause";
        ResumeHotkey = resume ?? "Ctrl+Alt+Shift+Pause";
        try
        {
            _hotkeys = new GlobalHotkeyService(PanicHotkey, ResumeHotkey,
                () => ApplyStop("panic hotkey"), () => ApplyResume("local resume hotkey"), Log);
            _hotkeys.Start();

            // Authority: whoever holds the panic chord. If our registration
            // failed, check whether a peer already owns it — if so, panic
            // still works (they write the marker, we watch it). If the chord
            // is held by a foreign app, no authority exists → stay stopped.
            if (_hotkeys.PanicAvailable)
                BecomeAuthority();
            else
                lock (_gate) _peerAuthority = DetectPeerAuthority();

            var stoppedMarker = File.Exists(StopMarkerPath);
            lock (_gate)
            {
                _stoppedByPanic = stoppedMarker;
                if ((PanicAvailable || _peerAuthority) && !stoppedMarker)
                    _state = ComputerControlState.Active;
            }
            FireStateChanged();
            if (stoppedMarker)
                Log("Emergency stop marker restored — control remains stopped");
            if (!PanicAvailable)
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
            if (_state != ComputerControlState.Active ||
                !(PanicAvailable || _peerAuthority))
                return null;
            // belt-and-suspenders: marker may have appeared between watcher ticks
            if (File.Exists(StopMarkerPath)) return null;
            return _epoch.Token;
        }
    }

    public bool IsLocalResumeKey(string? key, IReadOnlyList<string>? modifiers)
    {
        if (key == null) return false;
        var resume = _hotkeys?.Resume;
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
        GlobalHotkeyService? svc = null;
        try
        {
            svc = new GlobalHotkeyService(PanicHotkey, ResumeHotkey,
                () => ApplyStop("panic hotkey"),
                () => ApplyResume("local resume hotkey"), Log);
            svc.Start();
        }
        catch { /* construction/start failure → stay stopped, retry next tick */ }
        if (svc == null) return;

        lock (_gate)
        {
            if (_disposed || _stoppedByPanic)
            {
                // raced with a real panic — abandon the new service
                try { svc.Dispose(); } catch { }
                return;
            }
            var old = _hotkeys;
            _hotkeys = svc;
            try { old?.Dispose(); } catch { }
            if (svc.PanicAvailable)
            {
                _peerAuthority = false;
                BecomeAuthority();
            }
            else
            {
                _peerAuthority = DetectPeerAuthority();
            }
            if ((PanicAvailable || _peerAuthority) &&
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
            if (!_stoppedByPanic || !(PanicAvailable || _peerAuthority) || _disposed) return;
            _epoch.Dispose();
            _epoch = new CancellationTokenSource();
            _stoppedByPanic = false;
            _state = ComputerControlState.Active;
            try { File.Delete(StopMarkerPath); }
            catch (Exception e) { Log($"stop marker delete failed: {e.Message}"); }
            foreach (var input in _inputs)
                try { input.ClearEmergency(); } catch { }
        }
        FireStateChanged();
        Log($"Active ({source})");
    }

    /// <summary>
    /// Invoked strictly by local user UI (e.g. system tray click) — never via MCP.
    /// </summary>
    public void TriggerLocalPanic(string source = "local user UI") => ApplyStop(source);

    /// <summary>
    /// Invoked strictly by local user UI (e.g. system tray click) — never via MCP.
    /// </summary>
    public void TriggerLocalResume(string source = "local user UI") => ApplyResume(source);

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
        _hotkeys?.Dispose();
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
