namespace Inbrisk.Core;

public enum IndicatorState { Disconnected, ConnectedIdle, Active, EmergencyStopped }

/// <summary>
/// Central activity tracking for the screen-control indicator — nothing in
/// the tool layer touches the indicator directly. Every action/perception
/// entry point takes a ref-counted lease; the state machine applies an idle
/// grace period so rapid consecutive operations read as ONE continuous
/// automation session instead of flickering between animated and static.
///
/// States:
///   Disconnected     — no client attached (no indicator)
///   ConnectedIdle    — attached, not working (static translucent lime smoke)
///   Active           — at least one lease held or inside the grace window
///                      (same smoke fading in/out — breathing)
///   EmergencyStopped — panic pressed; overrides everything (static red
///                      smoke, never the animated "AI is working" look)
/// </summary>
public sealed class ComputerControlActivityService : IDisposable
{
    private readonly object _gate = new();
    private readonly Timer? _idleTimer;
    private int _leases;
    private int _clients;
    private IDisposable? _manualLease; // SetConnected(true) holds one slot
    private bool _emergency;
    private bool _idlePending; // grace timer armed
    private IndicatorState _state = IndicatorState.Disconnected;

    /// <summary>How long Active persists after the last lease ends —
    /// bridges the gap between rapid consecutive actions.</summary>
    public int IdleGraceMs { get; set; }

    public event Action<IndicatorState>? StateChanged;

    public ComputerControlActivityService(int idleGraceMs = 700)
    {
        IdleGraceMs = idleGraceMs;
        _idleTimer = new Timer(_ => OnIdleTimer(), null,
            Timeout.Infinite, Timeout.Infinite);
    }

    public IndicatorState State { get { lock (_gate) return _state; } }
    public int ActiveLeases { get { lock (_gate) return _leases; } }
    /// <summary>Attached client sessions — the indicator is visible only
    /// while this is &gt; 0 (or a manual SetConnected(true) slot is held).</summary>
    public int ActiveClients { get { lock (_gate) return _clients; } }

    /// <summary>A bounded unit of computer interaction. Always Dispose —
    /// refcounted: the border stays animated until EVERY lease ends and the
    /// grace period expires.</summary>
    public IDisposable BeginActivity()
    {
        lock (_gate)
        {
            _leases++;
            _idlePending = false;
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        Evaluate();
        return new Lease(this);
    }

    /// <summary>An MCP client/session attached — refcounted: the indicator
    /// stays up until EVERY session's lease is disposed. The lease is
    /// idempotent, so double-dispose on abnormal teardown paths can never
    /// push the count negative or kill another session's indicator.
    /// 0 sessions → Disconnected (no overlay — the smoke means "a client is
    /// attached", never "a process is running").</summary>
    public IDisposable AttachClient()
    {
        lock (_gate) _clients++;
        Evaluate();
        return new ClientLease(this);
    }

    private void DetachClient()
    {
        lock (_gate) _clients = Math.Max(0, _clients - 1);
        Evaluate();
    }

    /// <summary>Simple connected/not-connected setter for tests and
    /// one-shot hosts — implemented on top of the session refcount: 'true'
    /// holds a manual slot until 'false' releases it, so repeated calls in
    /// either direction stay idempotent.</summary>
    public void SetConnected(bool connected)
    {
        lock (_gate) // reentrant — AttachClient/Dispose lock the same gate
        {
            if (connected && _manualLease == null)
                _manualLease = AttachClient();
            else if (!connected && _manualLease != null)
            {
                var l = _manualLease; _manualLease = null;
                l.Dispose();
            }
        }
    }

    /// <summary>Emergency stop overrides activity — releases take effect
    /// visually only when the panic state also clears.</summary>
    public void SetEmergency(bool stopped)
    {
        lock (_gate) _emergency = stopped;
        Evaluate();
    }

    private void EndActivity()
    {
        lock (_gate)
        {
            _leases = Math.Max(0, _leases - 1);
            if (_leases == 0 && !_idlePending && IdleGraceMs > 0)
            {
                _idlePending = true;
                _idleTimer?.Change(IdleGraceMs, Timeout.Infinite);
            }
        }
        Evaluate(); // stays Active during grace; timer does the real transition
    }

    private void OnIdleTimer()
    {
        lock (_gate)
        {
            _idlePending = false;
            if (_leases != 0) return; // new activity arrived during grace
        }
        Evaluate();
    }

    private IndicatorState Resolve()
    {
        // Disconnected wins over everything — with zero attached clients
        // nothing may be rendered, not even the emergency haze. Emergency
        // persistence is carried by the stop-marker file and re-applied on
        // the next session's attach, not by a clientless overlay.
        if (_clients == 0) return IndicatorState.Disconnected;
        if (_emergency) return IndicatorState.EmergencyStopped;
        if (_leases > 0 || _idlePending) return IndicatorState.Active;
        return IndicatorState.ConnectedIdle;
    }

    private void Evaluate()
    {
        IndicatorState next;
        lock (_gate)
        {
            next = Resolve();
            if (next == _state) return;
            _state = next;
        }
        try { StateChanged?.Invoke(next); } catch { /* indicator must never take us down */ }
    }

    public void Dispose() => _idleTimer?.Dispose();

    private sealed class Lease(ComputerControlActivityService owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.EndActivity();
        }
    }

    private sealed class ClientLease(ComputerControlActivityService owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                owner.DetachClient();
        }
    }
}
