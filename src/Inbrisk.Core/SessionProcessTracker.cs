using System.Collections.Concurrent;
using System.Diagnostics;

namespace Inbrisk.Core;

/// <summary>One process spawned by an inbrisk tool call, tracked so the
/// session teardown reaper can close what it started.</summary>
public sealed record TrackedProcess(
    int Pid,
    /// <summary>Process name captured at Track() — a mismatch at reap time
    /// means the pid was reused by a different process.</summary>
    string? ProcessName,
    /// <summary>When inbrisk recorded the spawn (UTC).</summary>
    DateTimeOffset SpawnedAt,
    /// <summary>Process start time captured at Track() — the PID-reuse
    /// guard. null = unreadable (elevated/opaque process): identity is then
    /// unverifiable and the reaper will never hard-kill this entry.</summary>
    DateTimeOffset? ProcessStartUtc,
    /// <summary>Tool surface that launched it (ambient perf-trace kind:id,
    /// e.g. "tool:computer_launch", "computer_run:&lt;runId&gt;").</summary>
    string Tool,
    string? SessionId,
    /// <summary>Top-level window count observed at Track(). A process whose
    /// window count later grows beyond this hosts work we did not create —
    /// hard kill is vetoed (graceful WM_CLOSE is still allowed).</summary>
    int BaselineWindowCount);

/// <summary>Live state of a pid as seen by the reaper probe.
/// StartTimeUtc null = the process exists but its start time could not be
/// read (access denied) — identity unverifiable.</summary>
public sealed record ProcProbe(bool Alive, string? ProcessName,
    DateTimeOffset? StartTimeUtc);

/// <summary>Per-pid outcome of a reap pass.</summary>
/// <param name="Action">"closed" (graceful WM_CLOSE → exited),
/// "killed" (windowless verified leftover), "already-exited", or
/// "skipped" (Reason says why).</param>
public sealed record ReapOutcome(
    int Pid, string? ProcessName, string Tool,
    string Action, string? Reason);

/// <summary>Aggregate result of one <see cref="SessionProcessTracker.Reap"/>
/// pass — the surface a cleanup:true tool reports to the client.</summary>
public sealed record ReapReport(
    int Attempted,
    /// <summary>Processes this pass terminated (closed + killed).</summary>
    int Reaped,
    /// <summary>Tracked entries not terminated (already-exited + skipped).</summary>
    int Skipped,
    int PendingRemaining,
    IReadOnlyList<ReapOutcome> Outcomes,
    long ElapsedMs);

/// <summary>
/// Thread-safe registry of processes inbrisk spawned, with a conservative
/// reaper for session teardown.
///
/// Lives in Core because both recording (Inbrisk.Platform.Windows AppService)
/// and reaping (Inbrisk.Mcp McpSession / daemon) must reach it — the platform
/// project references Core only.
///
/// Reap order per tracked pid:
///   1. still alive?            — dead pids are untracked, never touched
///   2. same process?           — StartTimeUtc equality defeats PID reuse;
///                                an unreadable start time = unverifiable =
///                                graceful close allowed, Kill vetoed
///   3. not protected?          — agent-host/ancestor guard vetoes ANY touch;
///                                per-window IsWindowProtected vetoes WM_CLOSE
///                                for that window
///   4. WM_CLOSE, wait ~2s      — save prompts surface, never discarded
///   5. Kill only if still alive AND identity verified AND zero windows left
///      AND not protected AND windows did not multiply beyond the baseline
///
/// Never killed: pids we did not spawn, protected/host-ancestor processes,
/// shared multi-window hosts, processes with any remaining windows.
/// </summary>
public sealed class SessionProcessTracker
{
    private readonly ConcurrentDictionary<int, TrackedProcess> _tracked = new();
    private readonly IWindowService? _windows;
    /// <summary>pid → reason the process may not be touched at all (agent
    /// host/ancestor). null = safe to proceed.</summary>
    private readonly Func<int, string?>? _hostProcessGuard;
    /// <summary>(pid, processName) → reason a hard Kill is vetoed (protected
    /// process name, shared shell/UWP host, host ancestry). Does NOT veto a
    /// graceful WM_CLOSE of an individual window.</summary>
    private readonly Func<int, string?, string?>? _killGuard;
    private readonly object _reapGate = new();
    private long _reapedTotal;

    /// <summary>Process-wide fallback for launch paths with no runtime
    /// reference (static adapters like the CDP debug browser). InbriskRuntime
    /// installs its tracker here; the most recently constructed runtime wins —
    /// attribution for those spawns is best-effort.</summary>
    public static SessionProcessTracker? Ambient { get; set; }

    public SessionProcessTracker(IWindowService? windows = null,
        Func<int, string?>? hostProcessGuard = null,
        Func<int, string?, string?>? killGuard = null)
    {
        _windows = windows;
        _hostProcessGuard = hostProcessGuard;
        _killGuard = killGuard;
    }

    // ---- seams (tests / alternate platforms) ----

    /// <summary>Override the liveness/identity probe. Default uses
    /// System.Diagnostics.Process.</summary>
    public Func<int, ProcProbe?>? Probe { get; set; }
    /// <summary>Override the hard-terminate op. Default: Process.Kill.</summary>
    public Func<int, bool>? Terminate { get; set; }
    /// <summary>Override the post-close wait. Default: Process.WaitForExit.</summary>
    public Func<int, int, bool>? WaitExit { get; set; }
    /// <summary>Grace window between WM_CLOSE and the kill check.</summary>
    public int CloseGraceMs { get; set; } = 2000;

    /// <summary>Stamped onto every Track() — set once at session/runtime
    /// construction so ReapAll(sessionId) only reaps its own session's spawns
    /// even if a tracker instance were ever shared.</summary>
    public string? SessionId { get; set; }

    /// <summary>Tracked pids not yet reaped or untracked.</summary>
    public int PendingCount => _tracked.Count;
    /// <summary>Cumulative processes this tracker terminated.</summary>
    public long ReapedCount => Interlocked.Read(ref _reapedTotal);

    public IReadOnlyList<TrackedProcess> Snapshot() =>
        _tracked.Values.OrderBy(t => t.SpawnedAt).ToArray();

    /// <summary>Record a pid inbrisk just spawned. Safe to call for the same
    /// pid twice — the latest record wins. Never throws.</summary>
    public TrackedProcess? Track(int pid, string? processName = null,
        string tool = "unknown")
    {
        if (pid <= 0) return null;
        try
        {
            var probe = ProbeProcess(pid);
            var rec = new TrackedProcess(
                Pid: pid,
                ProcessName: processName ?? probe?.ProcessName,
                SpawnedAt: DateTimeOffset.UtcNow,
                ProcessStartUtc: probe?.StartTimeUtc,
                Tool: tool,
                SessionId: SessionId,
                BaselineWindowCount: WindowsOf(pid).Count);
            _tracked[pid] = rec;
            return rec;
        }
        catch { return null; }
    }

    /// <summary>Stop tracking a pid without touching the process (e.g. the
    /// session deliberately detached ownership).</summary>
    public bool Untrack(int pid) => _tracked.TryRemove(pid, out _);

    /// <summary>Reap every tracked pid — the cleanup:true tool surface.
    /// <paramref name="sessionId"/> non-null restricts to records stamped with
    /// that session; null reaps everything this tracker holds.</summary>
    public ReapReport ReapAll(string? sessionId = null) =>
        Reap(t => sessionId == null ||
            string.Equals(t.SessionId, sessionId, StringComparison.Ordinal));

    /// <summary>Reap all tracked pids matching <paramref name="filter"/>
    /// (null = all). Serialized — concurrent reaps never interleave closes.
    /// Never throws; per-pid failures land in the report.</summary>
    public ReapReport Reap(Func<TrackedProcess, bool>? filter = null)
    {
        var sw = Stopwatch.StartNew();
        var outcomes = new List<ReapOutcome>();
        lock (_reapGate)
        {
            foreach (var rec in _tracked.Values.ToArray())
            {
                bool match;
                try { match = filter?.Invoke(rec) ?? true; }
                catch { match = false; }
                if (!match) continue;
                outcomes.Add(ReapOne(rec));
            }
        }

        var report = new ReapReport(
            Attempted: outcomes.Count,
            Reaped: outcomes.Count(o => o.Action is "closed" or "killed"),
            Skipped: outcomes.Count(o => o.Action is not ("closed" or "killed")),
            PendingRemaining: _tracked.Count,
            Outcomes: outcomes,
            ElapsedMs: sw.ElapsedMilliseconds);
        EmitReaperEvent(report);
        return report;
    }

    // ------------------------------------------------------------------
    //  internals
    // ------------------------------------------------------------------

    private ReapOutcome ReapOne(TrackedProcess rec)
    {
        ReapOutcome Done(string action, string? reason) =>
            new(rec.Pid, rec.ProcessName, rec.Tool, action, reason);

        try
        {
            var probe = ProbeProcess(rec.Pid);
            if (probe is not { Alive: true })
            {
                _tracked.TryRemove(rec.Pid, out _);
                return Done("already-exited", null);
            }

            // PID-reuse guard: a live pid is OUR process only when its start
            // time still equals the one captured at spawn. Any mismatch (or a
            // changed process name) means the pid now belongs to a foreign
            // process — untrack it so it is never touched, now or later.
            if (rec.ProcessStartUtc != null && probe.StartTimeUtc != null &&
                rec.ProcessStartUtc != probe.StartTimeUtc)
            {
                _tracked.TryRemove(rec.Pid, out _);
                return Done("skipped", "pid-reused:start-time-mismatch");
            }
            if (rec.ProcessName != null && probe.ProcessName != null &&
                !string.Equals(rec.ProcessName, probe.ProcessName,
                    StringComparison.OrdinalIgnoreCase))
            {
                _tracked.TryRemove(rec.Pid, out _);
                return Done("skipped", "pid-reused:name-mismatch");
            }
            // unverifiable when we could not read the start time at spawn OR
            // cannot read it now — graceful close still allowed, Kill vetoed.
            var identityVerified =
                rec.ProcessStartUtc != null && probe.StartTimeUtc != null;

            var currentName = probe.ProcessName ?? rec.ProcessName;

            // Agent host / terminal-IDE ancestor: never touched at all.
            if (Guard(_hostProcessGuard, rec.Pid) is { } hostReason)
            {
                _tracked.TryRemove(rec.Pid, out _);
                return Done("skipped", hostReason);
            }

            // Graceful phase: WM_CLOSE every unprotected top-level window.
            var windows = WindowsOf(rec.Pid);
            var multiplied = rec.BaselineWindowCount > 0 &&
                windows.Count > rec.BaselineWindowCount;
            var protectedWindows = 0;
            var closePosted = 0;
            foreach (var hwnd in windows)
            {
                if (WindowVeto(hwnd) != null) { protectedWindows++; continue; }
                try
                {
                    if (_windows?.CloseWindow(hwnd) == true) closePosted++;
                }
                catch { }
            }

            var exited = WaitForExit(rec.Pid, Math.Max(0, CloseGraceMs));
            if (!exited)
            {
                var again = ProbeProcess(rec.Pid);
                exited = again is not { Alive: true };
            }
            if (exited)
            {
                _tracked.TryRemove(rec.Pid, out _);
                Interlocked.Increment(ref _reapedTotal);
                return Done("closed", closePosted > 0
                    ? $"wm-close x{closePosted}" +
                        (protectedWindows > 0 ? $" ({protectedWindows} protected left)" : "")
                    : "exited");
            }

            // Hard-kill phase — every veto below keeps the entry tracked so a
            // later reap (or the user) still owns the decision.
            var remaining = WindowsOf(rec.Pid);
            if (remaining.Count > 0)
                return Done("skipped",
                    $"windows-remain:{remaining.Count} (save prompt, dialog, or hung window — never force-killed)");
            if (!identityVerified)
                return Done("skipped", "identity-unverified:process-start-time-unreadable");
            if (Guard(_killGuard, rec.Pid, currentName) is { } killReason)
                return Done("skipped", killReason);
            if (multiplied)
                return Done("skipped", "windows-multiplied:hosts-windows-beyond-ours");

            // Final identity re-check: the probe above predates the
            // WM_CLOSE grace wait — the process may have exited and its pid
            // been recycled by a foreign process during it. A dead pid is
            // already "closed"; a mismatched start time must veto the kill.
            var final = ProbeProcess(rec.Pid);
            if (final is not { Alive: true })
            {
                _tracked.TryRemove(rec.Pid, out _);
                Interlocked.Increment(ref _reapedTotal);
                return Done("closed", "exited-during-kill-phase");
            }
            if (final.StartTimeUtc != rec.ProcessStartUtc)
                return Done("skipped", "pid-reused-during-reap");

            try
            {
                var terminated = Terminate != null
                    ? Terminate(rec.Pid)
                    : TerminateDefault(rec.Pid);
                if (terminated)
                {
                    _tracked.TryRemove(rec.Pid, out _);
                    Interlocked.Increment(ref _reapedTotal);
                    return Done("killed", "windowless-after-close");
                }
                return Done("skipped", "kill-failed");
            }
            catch (Exception e)
            {
                return Done("skipped", $"kill-failed:{e.GetType().Name}");
            }
        }
        catch (Exception e)
        {
            return Done("skipped", $"reap-error:{e.GetType().Name}");
        }
    }

    private ProcProbe? ProbeProcess(int pid)
    {
        if (Probe != null)
        {
            try { return Probe(pid); } catch { return null; }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return new ProcProbe(false, null, null);
            DateTimeOffset? start = null;
            string? name = null;
            try { start = new DateTimeOffset(p.StartTime.ToUniversalTime()); }
            catch { }
            try { name = p.ProcessName; } catch { }
            return new ProcProbe(true, name, start);
        }
        catch { return null; } // no such process / access denied on open
    }

    private bool WaitForExit(int pid, int ms)
    {
        if (WaitExit != null)
        {
            try { return WaitExit(pid, ms); } catch { }
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(ms);
        }
        catch
        {
            // WaitForExit can throw on odd states — fall back to a poll.
            var deadline = Environment.TickCount64 + ms;
            do
            {
                if (ProbeProcess(pid) is not { Alive: true }) return true;
                Thread.Sleep(100);
            } while (Environment.TickCount64 < deadline);
            return false;
        }
    }

    private bool TerminateDefault(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch { return false; }
    }

    private IReadOnlyList<long> WindowsOf(int pid)
    {
        if (_windows == null) return [];
        try
        {
            var list = _windows.ListWindows();
            var result = new List<long>();
            foreach (var w in list)
                if (w.Pid == pid) result.Add(w.Hwnd);
            return result;
        }
        catch { return []; }
    }

    /// <summary>null = close allowed. A throwing protection check vetoes the
    /// close — a wrong close is worse than a leftover window.</summary>
    private string? WindowVeto(long hwnd)
    {
        if (_windows == null) return "no-window-service";
        try
        {
            return _windows.IsWindowProtected(hwnd, out var reason)
                ? reason ?? "protected-window"
                : null;
        }
        catch { return "protection-check-failed"; }
    }

    private static string? Guard(Func<int, string?>? guard, int pid)
    {
        if (guard == null) return null;
        try { return guard(pid); } catch { return "protection-check-failed"; }
    }

    private static string? Guard(Func<int, string?, string?>? guard,
        int pid, string? name)
    {
        if (guard == null) return null;
        try { return guard(pid, name); } catch { return "protection-check-failed"; }
    }

    private void EmitReaperEvent(ReapReport report)
    {
        try
        {
            PerfLog.Write(new
            {
                kind = "session.reaper",
                at = DateTimeOffset.Now,
                sessionId = SessionId,
                attempted = report.Attempted,
                reaped = report.Reaped,
                skipped = report.Skipped,
                pending = report.PendingRemaining,
                elapsedMs = report.ElapsedMs,
                outcomes = report.Outcomes.Select(o => new
                {
                    pid = o.Pid,
                    process = o.ProcessName,
                    tool = o.Tool,
                    action = o.Action,
                    reason = o.Reason,
                }),
            });
        }
        catch { /* telemetry must never break teardown */ }
    }
}
