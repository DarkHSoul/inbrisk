using System.Collections.Concurrent;
using System.Diagnostics;
using Inbrisk.Platform.Windows.Uia;

namespace Inbrisk.Mcp.Daemon;

/// <summary>
/// F14: detaches a session's per-pid UIA read lanes stalled by a hung
/// provider. The read scheduler bounds each read with a caller-side
/// timeout and marks the lane IsPoisoned on expiry, but a poisoned lane
/// keeps accepting work — every retry can abandon another worker
/// mid-COM-call until the session's worker pool is exhausted and reads
/// for ALL pids come back Busy.
///
/// A hung COM call cannot be cancelled, so once a lane has shown a stuck
/// in-flight read for longer than the fault threshold the watchdog
/// detaches it (ProcessReadLane.Dispose): workers skip the lane, queued
/// callers are unblocked with an error, new reads and mutation barriers
/// for that pid fail immediately instead of queueing behind the stuck
/// call, and other pids keep their workers. The disposed lane remains in
/// the scheduler as a faulted tombstone until the provider call (if ever)
/// returns and idle eviction retires it — a recovered provider is then
/// re-laned automatically on the next schedule.
/// </summary>
public sealed class SessionLaneWatchdog : IDisposable
{
    /// <summary>How long a lane may stay suspect before it is detached.
    /// Must comfortably exceed the 10s per-read deadline + 15s write
    /// drain the scheduler already enforces; 30s bounds worker loss to
    /// the per-lane concurrency cap.</summary>
    private static readonly TimeSpan FaultAfter = ReadSpanEnv("INBRISK_LANE_FAULT_MS", 30_000);
    private static readonly TimeSpan PollInterval = ReadSpanEnv("INBRISK_LANE_SCAN_MS", 5_000);
    private static readonly TimeSpan PidTrackingWindow = TimeSpan.FromMinutes(10);

    private readonly UiaReadScheduler _scheduler;
    private readonly string _sessionId;
    /// <summary>Pids that ever scheduled a read on this session — the only
    /// reliable feed of which lanes exist (lanes cannot be enumerated).</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> _seenPids = new();
    /// <summary>First timestamp a lane was observed suspect in its current
    /// episode — cleared when the lane drains, so a recovered lane gets a
    /// fresh window.</summary>
    private readonly ConcurrentDictionary<int, DateTimeOffset> _stuckSince = new();
    /// <summary>Pids this watchdog has detached (faulted tombstones).</summary>
    private readonly ConcurrentDictionary<int, byte> _faultedPids = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public IReadOnlyCollection<int> FaultedPids => _faultedPids.Keys.ToList();

    private SessionLaneWatchdog(UiaReadScheduler scheduler, string sessionId)
    {
        _scheduler = scheduler;
        _sessionId = sessionId;

        // Chain after the session's telemetry hook — OnLanePid is invoked
        // at schedule time and carries the lane's pid.
        var prev = scheduler.OnLanePid;
        scheduler.OnLanePid = pid =>
        {
            _seenPids[pid] = DateTimeOffset.UtcNow;
            prev?.Invoke(pid);
        };

        _loop = Task.Run(RunAsync);
    }

    /// <summary>Wires a watchdog onto the session's read scheduler;
    /// returns null when the session has none.</summary>
    public static SessionLaneWatchdog? Attach(McpSession session)
    {
        var scheduler = session?.Rt?.ReadScheduler;
        if (scheduler == null) return null;
        return new SessionLaneWatchdog(scheduler, session?.SessionId ?? "session");
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            // Fire-and-forget like the daemon listener: a scan fault must
            // never fault the task or the daemon — next tick retries.
            try { ScanOnce(); }
            catch (Exception e)
            {
                Debug.WriteLine($"inbrisk-daemon lane watchdog scan failed (session {_sessionId}): {e}");
            }
        }
    }

    private void ScanOnce()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _seenPids)
        {
            var pid = kvp.Key;
            if (now - kvp.Value > PidTrackingWindow)
            {
                _seenPids.TryRemove(pid, out _);
                _stuckSince.TryRemove(pid, out _);
                continue;
            }

            UiaReadScheduler.ProcessReadLane lane;
            try { lane = _scheduler.GetOrCreateLane(pid); }
            catch { continue; } // lane cap / scheduler disposed — teardown races

            // A detached tombstone stays in the scheduler until idle
            // eviction retires it; nothing further to enforce on it.
            if (lane.IsDisposed)
            {
                _stuckSince.TryRemove(pid, out _);
                continue;
            }

            var inFlight = Volatile.Read(ref lane.ActiveReads);

            // Suspect = a deadline-expired call still in-flight (poisoned)
            // or any in-flight read on a lane that has shown no dequeue or
            // completion for the whole fault window (e.g. a read stuck via
            // a cancellation path that never marks the lane poisoned).
            var suspect = inFlight > 0 &&
                (lane.IsPoisoned || (now - lane.LastActivity) > FaultAfter);

            if (!suspect)
            {
                _stuckSince.TryRemove(pid, out _);
                continue;
            }

            var since = _stuckSince.GetOrAdd(pid, now);
            if (now - since <= FaultAfter) continue;

            // Hard deadline expired and the in-flight call has not
            // returned: the provider is hung. Detach the lane — enqueues
            // throw, queued callers are cancelled, workers skip it.
            try { lane.Dispose(); }
            catch { continue; }

            _faultedPids[pid] = 1;
            _stuckSince.TryRemove(pid, out _);
            Debug.WriteLine(
                $"inbrisk-daemon detached faulted UIA read lane (session {_sessionId}): " +
                $"pid={pid} activeReads={inFlight} stuck>{(int)FaultAfter.TotalMilliseconds}ms");
        }
    }

    public void Dispose()
    {
        // The loop is fire-and-forget (it may be mid-scan); cancel and let
        // it unwind — a CTS dispose racing a live Delay can fault it.
        try { _cts.Cancel(); } catch { }
    }

    private static TimeSpan ReadSpanEnv(string name, int defaultMs)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.FromMilliseconds(defaultMs);
    }
}
