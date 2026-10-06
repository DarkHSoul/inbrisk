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
    private readonly Timer? _watchdogTimer;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ActivityToken> _activeTokens = new();
    private int _leases;
    private int _clients;
    private IDisposable? _manualLease; // SetConnected(true) holds one slot
    private bool _emergency;
    private bool _idlePending; // grace timer armed
    private IndicatorState _state = IndicatorState.Disconnected;

    /// <summary>How long Active persists after the last lease ends —
    /// bridges the gap between rapid consecutive actions (settle window ~300-800ms).</summary>
    public int IdleGraceMs { get; set; }

    /// <summary>Watchdog timeout after which orphaned tokens without activity are reaped.</summary>
    public int WatchdogTimeoutMs { get; set; } = 60_000;

    public event Action<IndicatorState>? StateChanged;

    public ComputerControlActivityService(int idleGraceMs = 500)
    {
        IdleGraceMs = idleGraceMs;
        _idleTimer = new Timer(_ => OnIdleTimer(), null,
            Timeout.Infinite, Timeout.Infinite);
        _watchdogTimer = new Timer(_ => CheckWatchdog(), null,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public IndicatorState State { get { lock (_gate) return _state; } }
    public int ActiveLeases { get { lock (_gate) return _leases; } }
    /// <summary>Perimeter visibility condition: strictly active work or emergency stop.</summary>
    public bool PerimeterVisible { get { lock (_gate) return _state is IndicatorState.Active or IndicatorState.EmergencyStopped; } }
    /// <summary>Attached client sessions.</summary>
    public int ActiveClients { get { lock (_gate) return _clients; } }
    /// <summary>All currently active operation activity tokens.</summary>
    public IReadOnlyCollection<ActivityToken> ActiveTokens => _activeTokens.Values.ToArray();

    /// <summary>A bounded unit of computer interaction with operation/session tracking.</summary>
    public ActivityToken BeginActivity(string? operationId, string? sessionId = null)
    {
        var token = new ActivityToken(this, Guid.NewGuid().ToString("N")[..8], operationId, sessionId);
        _activeTokens[token.TokenId] = token;
        lock (_gate)
        {
            _leases++;
            _idlePending = false;
            _idleTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        Evaluate();
        return token;
    }

    /// <summary>A bounded unit of computer interaction. Always Dispose —
    /// refcounted: the border stays animated until EVERY lease ends and the
    /// grace period expires.</summary>
    public IDisposable BeginActivity() => BeginActivity(null, null);

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

    internal void EndActivityToken(ActivityToken token)
    {
        _activeTokens.TryRemove(token.TokenId, out _);
        EndActivity();
    }

    /// <summary>Clears all activity tokens associated with a given session id (e.g. on session disconnect).</summary>
    public void ClearSessionActivity(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;
        foreach (var kvp in _activeTokens)
        {
            if (string.Equals(kvp.Value.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            {
                kvp.Value.Dispose();
            }
        }
    }

    private void CheckWatchdog()
    {
        // Boundary: thread-pool timer — an unhandled fault kills the process.
        try
        {
            var now = Environment.TickCount64;
            foreach (var kvp in _activeTokens)
            {
                var token = kvp.Value;
                if (now - token.LastActivityTick > WatchdogTimeoutMs)
                {
                    token.Dispose();
                }
            }
        }
        catch { /* watchdog is best-effort — next tick retries */ }
    }

    private void OnIdleTimer()
    {
        // Boundary: thread-pool timer — an unhandled fault kills the process.
        try
        {
            lock (_gate)
            {
                _idlePending = false;
                if (_leases != 0) return; // new activity arrived during grace
            }
            Evaluate();
        }
        catch { /* idle transition is best-effort — Evaluate already guards */ }
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

    public void Dispose()
    {
        _idleTimer?.Dispose();
        _watchdogTimer?.Dispose();
    }

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

/// <summary>
/// A traceable activity token representing an in-flight operation.
/// Reference counted so the perimeter wall stays visible until the final active token ends.
/// </summary>
public sealed class ActivityToken : IDisposable
{
    private readonly ComputerControlActivityService _owner;
    private int _disposed;

    public string TokenId { get; }
    public string? OperationId { get; }
    public string? SessionId { get; }
    public DateTimeOffset StartedAt { get; }
    public long LastActivityTick { get; private set; }

    internal ActivityToken(ComputerControlActivityService owner, string tokenId, string? operationId, string? sessionId)
    {
        _owner = owner;
        TokenId = tokenId;
        OperationId = operationId;
        SessionId = sessionId;
        StartedAt = DateTimeOffset.UtcNow;
        LastActivityTick = Environment.TickCount64;
    }

    public void Touch() => LastActivityTick = Environment.TickCount64;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _owner.EndActivityToken(this);
    }
}

/// <summary>
/// Transient focus/target window highlight service.
/// Tracks window-level activity tokens so a yellow target border appears only while
/// Inbrisk is actively targeting/operating on a window, and deterministically disappears
/// upon completion, failure, cancellation, timeout, exception, or session disconnect.
/// </summary>
public interface ITargetHighlightService : IDisposable
{
    IDisposable BeginWindowActivity(long hwnd, string? ownerId = null);
    bool IsWindowHighlighted(long hwnd);
    IReadOnlySet<long> GetHighlightedWindows();
    void ClearAll();
    void SetEmergency(bool stopped);
    void OnSessionDisconnected(string ownerId);
    void OnWindowDestroyed(long hwnd);
    int ActiveTokenCount(long hwnd);
}

