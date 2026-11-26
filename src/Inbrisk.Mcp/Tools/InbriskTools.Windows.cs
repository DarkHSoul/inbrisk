using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

public sealed partial class InbriskTools
{
    [McpServerTool(Name = "computer_windows"), Description(
        "List top-level windows with orthogonal states (hwnd, title, app, rect, flags). " +
        "Flags report independent dimensions: focus ([foreground]), visibility ([shown]), " +
        "placement ([normal], [maximized], [minimized]), and monitor assignment ([monitor:0 primary], [monitor:1 secondary]). " +
        "POLICY: Do not focus or activate a window merely because it is not foreground. If a target window is [shown], " +
        "not [minimized], and assigned to a visible monitor, prefer non-disruptive inspection (computer_inspect, computer_find, " +
        "or computer_screenshot with monitor index) without stealing user focus. " +
        "Windows matching the input-blocking signature are marked [INPUT-BLOCKING]; modal popups/dialogs and blocked windows are explicitly flagged.")]
    public CallToolResult Windows(
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return _s.GhostCoordinator.WindowsAsync(ct).GetAwaiter().GetResult();
        }

        if (_s.Deduplicator.TryDeduplicateRead("computer_windows", "", 0, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedWin)
            return cachedWin;

        if (_s.PrevOutcome?.Kind == OutcomeKind.Verified)
        {
            _s.Telemetry.IncPostVerifiedObservation();
            PerfTrace.Count("postVerifiedObservation");
        }
        if (_s.LastLaunchedHwnd != 0 && (DateTimeOffset.UtcNow - _s.LastLaunchTimestamp).TotalSeconds < 5)
        {
            _s.Telemetry.IncLaunchFollowupDiscovery();
            PerfTrace.Count("launchFollowupDiscovery");
        }
        var prevCount = WindowSnapshot.EnumerationCount;
        var wins = WindowSnapshot.GetWindows();
        if (WindowSnapshot.EnumerationCount == prevCount)
        {
            _s.Telemetry.IncWindowSnapshotReuses();
        }

        var monitors = _s.Rt.WindowService.GetMonitors();
        var blockers = InputHealth.FindBlockingWindows()
            .Select(b => b.Hwnd).ToHashSet();
        var sb = new StringBuilder(UntrustedHeader("win32") + "\n");
        foreach (var w in wins)
        {
            var flags = new List<string>();
            if (w.IsForeground) flags.Add("[foreground]");
            flags.Add("[shown]");
            flags.Add(w.State switch
            {
                WindowState.Minimized => "[minimized]",
                WindowState.Maximized => "[maximized]",
                _ => "[normal]"
            });

            if (w.MonitorIndex >= 0 && w.MonitorIndex < monitors.Count)
            {
                var mon = monitors[w.MonitorIndex];
                flags.Add(mon.IsPrimary ? $"[monitor:{mon.Index} primary]" : $"[monitor:{mon.Index} secondary]");
            }
            else
            {
                flags.Add($"[monitor:{w.MonitorIndex}]");
            }

            if (w.IsElevated) flags.Add("[ELEVATED/UIPI: observation-only]");
            if (blockers.Contains(w.Hwnd)) flags.Add("[INPUT-BLOCKING]");
            if (w.IsModalPopup) flags.Add("[MODAL/DIALOG]");
            if (w.ModalPopupHwnd.HasValue) flags.Add($"[BLOCKED by modal 0x{w.ModalPopupHwnd.Value:X}]");
            if (!w.IsEnabled) flags.Add("[DISABLED/UNRESPONSIVE]");
            if (_s.Rt.WindowService.IsWindowProtected(w.Hwnd, out _))
                flags.Add("[PROTECTED]");
            else if (_s.Rt.Provenance.CanAgentClose(w.Hwnd, out _))
                flags.Add("[AGENT-OWNED: safe-to-close]");
            else
                flags.Add("[USER-OWNED: do-not-close]");

            var flagStr = flags.Count > 0 ? "  " + string.Join(" ", flags) : "";
            sb.AppendLine($"0x{w.Hwnd:X}  \"{w.Title}\"  app={w.ProcessName}  " +
                $"rect=({w.Bounds.X},{w.Bounds.Y} {w.Bounds.Width}x{w.Bounds.Height})" +
                flagStr);
        }
        var finalResult = Text(sb.ToString());
        _s.Deduplicator.RecordRead("computer_windows", "", 0, _s.Rt.MutationVersion, finalResult);
        return finalResult;
    }

    private bool IsWindowAgentOwned(WindowInfo win)
    {
        lock (_s.TrackedWindows)
        {
            var tracked = _s.TrackedWindows.FirstOrDefault(tw => tw.Hwnd == win.Hwnd && !tw.ClosedOrStale);
            if (tracked != null)
            {
                // PID-reuse guard: the recorded pid may now belong to a
                // foreign process (ours exited, pid recycled). When a start
                // time was recorded, a same-pid claim must re-prove the same
                // process instance before inheriting agent-owned status; a
                // null recorded start time is unverifiable and keeps the
                // hwnd-based claim (the reaper's "close allowed" tier).
                if (win.Pid == tracked.Pid && tracked.Pid > 0 &&
                    tracked.ProcessStartTime != null &&
                    !PidStillMatchesLaunch(tracked))
                {
                    tracked.ClosedOrStale = true;
                    return false;
                }
                return tracked.AgentOwned;
            }

            // Across HWND replacement: if window belongs to ApplicationFrameHost or matches launched identity/title
            var replacement = _s.TrackedWindows.FirstOrDefault(tw => tw.AgentOwned && !tw.ClosedOrStale &&
                ((tw.Pid == win.Pid && tw.Pid > 0 && PidStillMatchesLaunch(tw) &&
                  !string.Equals(win.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) ||
                 (string.Equals(win.ProcessName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
                  (!string.IsNullOrEmpty(tw.AppIdentity) && win.Title.Contains(tw.AppIdentity, StringComparison.OrdinalIgnoreCase) ||
                   !string.IsNullOrEmpty(tw.Title) && win.Title.Contains(tw.Title, StringComparison.OrdinalIgnoreCase)))));

            if (replacement != null)
            {
                // Adopt replaced HWND into tracked windows
                _s.TrackedWindows.Add(replacement with { Hwnd = win.Hwnd, Title = win.Title });
                return true;
            }
        }

        // Untracked-but-relevant window: this is an OWNERSHIP probe (used for
        // selection/adopt/cleanup hints), NOT the close policy gate — every
        // close site checks IsWindowProtected separately, so do not channel
        // CanAgentClose (protection + ownership merged) through this predicate.
        // Same strict evidence CanAgentClose accepts for ownership:
        //   1. the exact hwnd named by a verified agent launch, or
        //   2. a window whose live PID is a VERIFIED agent launch (a
        //      replacement window of a process the agent spawned — pre-existing
        //      apps with the same name are never matched).
        if (_s.Rt.Provenance.GetStrictWindowOwnership(win.Hwnd) == StrictWindowOwnership.Agent)
            return true;
        if (_s.Rt.Provenance.TryGetVerifiedAgentProcessForHwnd(win.Hwnd, out var hwndOwner) &&
            hwndOwner != null)
        {
            return true;
        }
        return false;
    }

    /// <summary>PID-reuse guard for tracked-window adoption: the recorded
    /// pid is still the process we launched only when its live start time
    /// equals the one captured at launch. False when the pid is dead,
    /// unreadable, or the start time differs — or none was recorded (a bare
    /// pid match alone is not enough evidence to adopt a foreign window).</summary>
    private static bool PidStillMatchesLaunch(SessionWindowProvenance tw)
    {
        if (tw.ProcessStartTime == null) return false;
        try
        {
            using var p = Process.GetProcessById(tw.Pid);
            return !p.HasExited &&
                p.StartTime.ToUniversalTime() == tw.ProcessStartTime.Value.UtcDateTime;
        }
        catch { return false; }
    }

    private LifecycleIntent GetTrackedLifecycleIntent(long hwnd)
    {
        lock (_s.TrackedWindows)
        {
            var tw = _s.TrackedWindows.FirstOrDefault(w => w.Hwnd == hwnd && !w.ClosedOrStale);
            if (tw != null) return tw.Intent;
        }
        var prov = _s.Rt.Provenance.GetProvenanceForHwnd(hwnd);
        return prov?.Intent ?? LifecycleIntent.Unknown;
    }

    private object BuildCandidateWindow(WindowInfo w) => new
    {
        hwnd = $"0x{w.Hwnd:X}",
        pid = w.Pid,
        title = w.Title,
        process = w.ProcessName,
        owned = IsWindowAgentOwned(w),
        retryArgs = IsWindowAgentOwned(w) ? new { hwnd = $"0x{w.Hwnd:X}" } : null
    };

    private sealed class CloseScope : IAsyncDisposable
    {
        public IDisposable? GlobalLease { get; }
        public List<IAsyncDisposable> ProcessBarriers { get; }
        public bool IsForeground { get; }

        public CloseScope(IDisposable? globalLease, List<IAsyncDisposable> processBarriers, bool isForeground)
        {
            GlobalLease = globalLease;
            ProcessBarriers = processBarriers;
            IsForeground = isForeground;
        }

        public async ValueTask DisposeAsync()
        {
            for (int i = ProcessBarriers.Count - 1; i >= 0; i--)
            {
                try { await ProcessBarriers[i].DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Debug.WriteLine($"close scope barrier dispose failed: {ex.Message}"); }
            }
            try { GlobalLease?.Dispose(); }
            catch (Exception ex) { Debug.WriteLine($"close scope lease dispose failed: {ex.Message}"); }
        }
    }

    private async Task<CloseScope> EnterCloseScopeAsync(IEnumerable<WindowInfo> targets, CancellationToken ct)
    {
        var targetList = targets.Where(t => t != null).ToList();

        // Round 6: ALL top-level close operations are unconditionally serialized under Global Desktop Gate (Rank 1)
        // to prevent TOCTOU races where a background window becomes foreground or triggers modal/activation transitions.
        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        IDisposable? globalLease = null;
        if (arbiter != null)
        {
            globalLease = await arbiter.AcquireAsync(
                ownerId: _s?.SessionId ?? "session",
                kind: LeaseKind.PhysicalInput,
                description: "Close window global gate",
                timeout: TimeSpan.FromSeconds(5),
                ct: ct).ConfigureAwait(false);
        }

        var barriers = new List<IAsyncDisposable>();
        try
        {
            if (_s?.Rt?.ReadScheduler != null)
            {
                var pids = targetList.Select(w => w.Pid).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
                foreach (var pid in pids)
                {
                    barriers.Add(await _s.Rt.ReadScheduler.EnterMutationBarrierAsync(pid, ct).ConfigureAwait(false));
                }
            }
        }
        catch
        {
            globalLease?.Dispose();
            throw;
        }

        return new CloseScope(globalLease, barriers, isForeground: true);
    }

    [McpServerTool(Name = "computer_close_window"), Description(
        "Gracefully close a window (posts WM_CLOSE — save prompts appear " +
        "normally, the app stays in control). Target by hwnd, process name, " +
        "title substring, list of hwnds, owned: true (closes purely ephemeral helper windows opened by the agent), " +
        "or lastOwned: true (closes the most recent agent-owned window). Respects the conservative Application Lifecycle Policy: " +
        "windows the agent opened may be closed; pre-existing user windows are refused unless force:true is passed. " +
        "Protected windows and processes (the agent host, terminals, IDEs, shell, anything in the host process tree) " +
        "can never be closed — force does not override them. force:true may additionally hard-kill the owning process, " +
        "but only when it is a single-purpose process that owns no other windows and is not a shared/protected host. " +
        "Do NOT automatically close windows merely because you opened them — " +
        "leave user-facing results open (Notepad documents, browser tabs, Calculator, Explorer folders). 'When in doubt, leave it open.'")]
    public async Task<CallToolResult> CloseWindow(
        [Description("window handle — decimal or 0x-prefixed")] string? hwnd = null,
        [Description("batch list of window handles to close in one call")] string[]? hwnds = null,
        [Description("process name of the window to close, e.g. \"mspaint\"")] string? process = null,
        [Description("substring of the window title")] string? titleContains = null,
        [Description("semantic target object, e.g. {\"process\": \"notepad.exe\", \"hwnd\": \"0x...\"}")] TargetSpec? target = null,
        [Description("close all open windows owned by the agent in this session (replaces guessed close loops)")] bool? owned = null,
        [Description("close the most recent agent-owned window in this session")] bool? lastOwned = null,
        [Description("close all windows opened during this session by the agent in a single turn (legacy alias for owned: true)")] bool closeAllAgentWindows = false,
        [Description("override lifecycle policy to close a pre-existing user window if explicitly requested by the user (default false)")] bool force = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        var normArgs = $"{hwnd}|{string.Join(",", hwnds ?? Array.Empty<string>())}|{process}|{titleContains}|{owned}|{lastOwned}|{closeAllAgentWindows}|{force}";
        if (_s.Deduplicator.TryDeduplicateMutation(operationId, "computer_close_window", normArgs, _s.Telemetry, out var conflictError) is { } deduped)
            return deduped;
        if (conflictError != null)
            return conflictError;

        // P1/F13: window closing mutates the desktop — it is gated by the
        // emergency-stop epoch like every other mutating tool. A latched panic
        // must refuse closes instead of silently bypassing the gate.
        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);
        ct = linked.Token;

        hwnd ??= target?.Hwnd ?? (target?.Window != null && (target.Window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || long.TryParse(target.Window, out _)) ? target.Window : null);
        process ??= target?.Process;
        titleContains ??= target?.Name ?? target?.NameContains ?? (hwnd == null ? target?.Window : null);

        var wins = _s.Rt.Windows();

        if (owned == true || closeAllAgentWindows)
        {
            var combinedWins = wins.ToList();
            lock (_s.TrackedWindows)
            {
                foreach (var tw in _s.TrackedWindows.Where(tw => tw.AgentOwned && !tw.ClosedOrStale))
                {
                    if (!combinedWins.Any(w => w.Hwnd == tw.Hwnd))
                    {
                        combinedWins.Add(new WindowInfo(tw.Hwnd, tw.Pid, tw.Title, tw.AppIdentity, new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0));
                    }
                }
            }

            var closableWindows = combinedWins
                .Where(x => !_s.Rt.WindowService.IsWindowProtected(x.Hwnd, out _))
                .Where(x => IsWindowAgentOwned(x))
                .Where(x =>
                {
                    if (closeAllAgentWindows || force) return true;
                    // For automatic cleanup (owned: true): preserve Reusable, UserUseful, and TaskArtifact resources
                    var intent = GetTrackedLifecycleIntent(x.Hwnd);
                    if (intent is LifecycleIntent.Reusable or LifecycleIntent.UserUseful or LifecycleIntent.TaskArtifact)
                        return false;
                    return true;
                })
                .ToList();

            await using var ownedScope = await EnterCloseScopeAsync(closableWindows, ct).ConfigureAwait(false);

            var closedList = new List<object>();
            int closedCount = 0;
            var ownedTargetHwnds = closableWindows.Select(x => x.Hwnd).ToHashSet();
            foreach (var win in closableWindows)
            {
                var wasClosed = _s.Rt.CloseWindow(win.Hwnd);
                var winModal = _s.Rt.WindowService.GetModalPopup(win.Hwnd);
                var winHasModal = winModal != null && winModal.Hwnd != win.Hwnd;
                if (!wasClosed && _s.Rt.Window(win.Hwnd) == null && !winHasModal)
                {
                    wasClosed = true;
                }
                string? killRefusal = null;
                string? killRefusalKind = null;
                if (!wasClosed && force && !winHasModal)
                {
                    if (ForceKillRefusal(win, wins, ownedTargetHwnds) is { } refusal)
                    {
                        killRefusalKind = refusal.Kind;
                        killRefusal = refusal.Reason;
                    }
                    else
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(win.Pid);
                            proc.Kill();
                            wasClosed = proc.WaitForExit(1000) || proc.HasExited;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"computer_close_window owned-batch force-kill pid={win.Pid} failed: {ex.Message}");
                        }
                    }
                }
                if (wasClosed)
                {
                    closedCount++;
                    _s.MarkWindowClosed(win.Hwnd);
                }
                closedList.Add(new
                {
                    hwnd = $"0x{win.Hwnd:X}",
                    process = win.ProcessName,
                    title = win.Title,
                    closed = wasClosed,
                    hasModal = winHasModal,
                    modalPopup = winHasModal ? $"0x{winModal!.Hwnd:X} \"{winModal.Title}\"" : null,
                    error = killRefusalKind,
                    detail = killRefusal
                });
            }

            var batchRes = Text(JsonSerializer.Serialize(new
            {
                success = true,
                batch = true,
                closedCount,
                totalFound = closableWindows.Count,
                windows = closedList,
                notification = $"Closed {closedCount} windows opened by the agent."
            }, J));
            _s.Deduplicator.RecordMutation(operationId, "computer_close_window", normArgs, batchRes);
            return batchRes;
        }

        if (lastOwned == true)
        {
            SessionWindowProvenance? lastProv = null;
            lock (_s.TrackedWindows)
            {
                lastProv = _s.TrackedWindows.LastOrDefault(tw => tw.AgentOwned && !tw.ClosedOrStale);
            }

            WindowInfo? lastWin = null;
            if (lastProv != null)
            {
                lastWin = wins.FirstOrDefault(x => x.Hwnd == lastProv.Hwnd);
                if (lastWin == null)
                {
                    lastWin = new WindowInfo(lastProv.Hwnd, lastProv.Pid, lastProv.Title, lastProv.AppIdentity, new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
                }
            }
            if (lastWin == null)
            {
                var agentWins = wins.Where(x => IsWindowAgentOwned(x)).ToList();
                if (agentWins.Count > 0)
                    lastWin = agentWins[^1];
            }

            if (lastWin == null)
            {
                _s.Telemetry.IncCloseRetry();
                Inbrisk.Core.PerfTrace.Count("closeRetry");
                return Text(JsonSerializer.Serialize(new
                {
                    error = "TargetNotFound",
                    detail = "No open agent-owned window found to close via lastOwned.",
                    candidates = wins.Take(10).Select(BuildCandidateWindow).ToList()
                }, J));
            }

            hwnd = $"0x{lastWin.Hwnd:X}";
        }

        if (hwnds is { Length: > 0 })
        {
            var batchHwndSet = hwnds.Select(ParseHwnd).Where(h => h.HasValue).Select(h => h!.Value).ToHashSet();
            var targetWins = hwnds.Select(ParseHwnd).Where(h => h.HasValue).Select(h => wins.FirstOrDefault(x => x.Hwnd == h.Value)).Where(x => x != null).ToList();
            await using var batchScope = await EnterCloseScopeAsync(targetWins!, ct).ConfigureAwait(false);

            var batchList = new List<object>();
            int batchClosed = 0;
            foreach (var hStr in hwnds)
            {
                if (ParseHwnd(hStr) is not { } parsedHwnd)
                {
                    batchList.Add(new { hwnd = hStr, closed = false, error = "MalformedHwnd" });
                    continue;
                }
                var win = wins.FirstOrDefault(x => x.Hwnd == parsedHwnd);
                if (win == null)
                {
                    batchList.Add(new { hwnd = $"0x{parsedHwnd:X}", closed = false, error = "TargetNotFound" });
                    continue;
                }
                if (_s.Rt.WindowService.IsWindowProtected(win.Hwnd, out var pr) ||
                    Inbrisk.Platform.Windows.Topology.WindowService.IsProcessProtected(win.ProcessName, out pr))
                {
                    batchList.Add(new { hwnd = $"0x{win.Hwnd:X}", process = win.ProcessName, closed = false, error = "ProtectedWindow", detail = pr });
                    continue;
                }
                // P1/F13: the explicit-hwnd path previously skipped the
                // Application Lifecycle Policy entirely — a bare hwnd could
                // close any user window. force:true is the explicit override.
                if (!force && !IsWindowAgentOwned(win))
                {
                    _s.Rt.Provenance.CanAgentClose(win.Hwnd, out var lifecycleDeny);
                    batchList.Add(new { hwnd = $"0x{win.Hwnd:X}", process = win.ProcessName, closed = false, error = "PolicyDenied", detail = $"DENIED: {lifecycleDeny} Pass force:true only if the user explicitly requested closing this window." });
                    continue;
                }
                var c = _s.Rt.CloseWindow(win.Hwnd);
                var winModal = _s.Rt.WindowService.GetModalPopup(win.Hwnd);
                var winHasModal = winModal != null && winModal.Hwnd != win.Hwnd;
                string? killRefusal = null;
                string? killRefusalKind = null;
                if (!c && force && !winHasModal)
                {
                    if (ForceKillRefusal(win, wins, batchHwndSet) is { } refusal)
                    {
                        killRefusalKind = refusal.Kind;
                        killRefusal = refusal.Reason;
                    }
                    else
                    {
                        try
                        {
                            using var proc = Process.GetProcessById(win.Pid);
                            proc.Kill();
                            c = proc.WaitForExit(1000) || proc.HasExited;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"computer_close_window hwnds-batch force-kill pid={win.Pid} failed: {ex.Message}");
                        }
                    }
                }
                if (c)
                {
                    batchClosed++;
                    _s.MarkWindowClosed(win.Hwnd);
                }
                batchList.Add(new
                {
                    hwnd = $"0x{win.Hwnd:X}",
                    process = win.ProcessName,
                    title = win.Title,
                    closed = c,
                    hasModal = winHasModal,
                    modalPopup = winHasModal ? $"0x{winModal!.Hwnd:X} \"{winModal.Title}\"" : null,
                    error = killRefusalKind,
                    detail = killRefusal
                });
            }

            var hwndsRes = Text(JsonSerializer.Serialize(new
            {
                success = true,
                batch = true,
                closedCount = batchClosed,
                totalRequested = hwnds.Length,
                windows = batchList,
                notification = $"Closed {batchClosed} of {hwnds.Length} windows."
            }, J));
            _s.Deduplicator.RecordMutation(operationId, "computer_close_window", normArgs, hwndsRes);
            return hwndsRes;
        }

        if (hwnd == null && string.IsNullOrWhiteSpace(process) && string.IsNullOrWhiteSpace(titleContains) && target == null)
        {
            _s.Telemetry.IncCloseRetry();
            Inbrisk.Core.PerfTrace.Count("closeRetry");
            return Text(JsonSerializer.Serialize(new
            {
                error = "Malformed",
                detail = "Untargeted close is not permitted. Untargeted close will never select the foreground window. Specify hwnd, hwnds, process, titleContains, owned:true, or lastOwned:true.",
                candidates = wins.Take(10).Select(BuildCandidateWindow).ToList()
            }, J));
        }

        WindowInfo? w = null;
        if (ParseHwnd(hwnd) is { } h)
        {
            w = wins.FirstOrDefault(x => x.Hwnd == h);
            if (w == null)
            {
                lock (_s.TrackedWindows)
                {
                    var tw = _s.TrackedWindows.FirstOrDefault(x => x.Hwnd == h && !x.ClosedOrStale);
                    if (tw != null)
                    {
                        w = new WindowInfo(tw.Hwnd, tw.Pid, tw.Title, tw.AppIdentity, new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
                    }
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(process))
        {
            var hits = wins
                .Select(x => (Window: x, Score: ScoreProcessMatch(x.ProcessName, process)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Window)
                .ToList();
            var closableHits = hits.Where(x => !_s.Rt.WindowService.IsWindowProtected(x.Hwnd, out _)).ToList();
            if (closableHits.Count > 0) hits = closableHits;

            if (hits.Count > 1)
            {
                var agentHits = hits.Where(x => IsWindowAgentOwned(x)).ToList();
                if (agentHits.Count == 1)
                    w = agentHits[0];
                else
                {
                    _s.Telemetry.IncCloseRetry();
                    Inbrisk.Core.PerfTrace.Count("closeRetry");
                    return Text(JsonSerializer.Serialize(new
                    {
                        error = "AmbiguousTarget",
                        detail = $"{hits.Count} windows owned by '{process}' — pick one by hwnd",
                        candidates = hits.Select(BuildCandidateWindow).ToList()
                    }, J));
                }
            }
            else
            {
                w = hits.FirstOrDefault();
            }
        }
        else if (!string.IsNullOrWhiteSpace(titleContains))
        {
            var hits = wins.Where(x => x.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)).ToList();
            var closableHits = hits.Where(x => !_s.Rt.WindowService.IsWindowProtected(x.Hwnd, out _)).ToList();
            if (closableHits.Count > 0) hits = closableHits;

            if (hits.Count > 1)
            {
                var agentHits = hits.Where(x => IsWindowAgentOwned(x)).ToList();
                if (agentHits.Count == 1)
                    w = agentHits[0];
                else
                {
                    _s.Telemetry.IncCloseRetry();
                    Inbrisk.Core.PerfTrace.Count("closeRetry");
                    return Text(JsonSerializer.Serialize(new
                    {
                        error = "AmbiguousTarget",
                        detail = $"{hits.Count} windows match '{titleContains}' — pick one by hwnd",
                        candidates = hits.Select(BuildCandidateWindow).ToList()
                    }, J));
                }
            }
            else
            {
                w = hits.FirstOrDefault();
            }
        }

        if (w == null)
        {
            _s.Telemetry.IncCloseRetry();
            Inbrisk.Core.PerfTrace.Count("closeRetry");
            return Text(JsonSerializer.Serialize(new
            {
                error = "TargetNotFound",
                detail = "no matching window found",
                candidates = wins.Take(10).Select(BuildCandidateWindow).ToList()
            }, J));
        }

        if (_s.Rt.WindowService.IsWindowProtected(w.Hwnd, out var protectReason) ||
            Inbrisk.Platform.Windows.Topology.WindowService.IsProcessProtected(w.ProcessName, out protectReason))
        {
            return Text(JsonSerializer.Serialize(new
            {
                error = "ProtectedWindow",
                hwnd = $"0x{w.Hwnd:X}",
                title = w.Title,
                process = w.ProcessName,
                detail = $"Refusing to close window: {protectReason}. Automated closure would terminate the agent or server connection."
            }, J));
        }

        // P1/F13: Application Lifecycle Policy — the explicit-hwnd/process/title
        // paths previously skipped CanAgentClose entirely, so any user window
        // could be closed by bare handle. force:true is the documented explicit
        // override ("Pre-existing user applications cannot be closed unless
        // force:true is explicitly set").
        if (!force && !IsWindowAgentOwned(w))
        {
            _s.Rt.Provenance.CanAgentClose(w.Hwnd, out var lifecycleDeny);
            return Text(JsonSerializer.Serialize(new
            {
                error = "PolicyDenied",
                hwnd = $"0x{w.Hwnd:X}",
                title = w.Title,
                process = w.ProcessName,
                detail = $"DENIED: {lifecycleDeny} Pass force:true only if the user explicitly requested closing this window."
            }, J));
        }

        if (w.IsElevated && Inbrisk.Platform.Windows.Topology.IntegrityService.ProcessIntegrityLevel(Environment.ProcessId) < 0x3000)
        {
            return Text(JsonSerializer.Serialize(new
            {
                error = "ElevatedUipiBlocked",
                hwnd = $"0x{w.Hwnd:X}",
                title = w.Title,
                process = w.ProcessName,
                isElevated = true,
                detail = $"Target window '{w.ProcessName}' (0x{w.Hwnd:X}) is running ELEVATED with High/System integrity while Inbrisk is running as standard user. Windows User Interface Privilege Isolation (UIPI) blocks WM_CLOSE, input injection, and termination across integrity levels. Run Inbrisk as Administrator or close this window manually."
            }, J));
        }

        await using var singleScope = await EnterCloseScopeAsync(new[] { w }, ct).ConfigureAwait(false);

        var closed = _s.Rt.CloseWindow(w.Hwnd);
        var modal = _s.Rt.WindowService.GetModalPopup(w.Hwnd);
        var hasModal = modal != null && modal.Hwnd != w.Hwnd;

        if (!closed && _s.Rt.Window(w.Hwnd) == null && !hasModal)
        {
            closed = true;
        }

        if (!closed && force)
        {
            if (hasModal)
            {
                return Text(JsonSerializer.Serialize(new
                {
                    success = false,
                    closed = false,
                    error = "UnsavedDataPromptOpen",
                    hwnd = $"0x{w.Hwnd:X}",
                    title = w.Title,
                    process = w.ProcessName,
                    hasUnsavedDataPrompt = true,
                    modalPopup = $"0x{modal!.Hwnd:X} \"{modal.Title}\"",
                    next = "dismiss_modal",
                    detail = $"WM_CLOSE posted to '{w.ProcessName}', but window remains open because an unsaved changes confirmation dialog appeared (0x{modal.Hwnd:X} \"{modal.Title}\"). Force-kill was prevented to avoid data loss. Resolve or interact with the save prompt."
                }, J));
            }

            // P1/F13: force may only hard-kill a single-purpose process — never
            // the agent host/ancestors, never a shared multi-window host, and
            // never a process owning top-level windows outside this request.
            if (ForceKillRefusal(w, wins, new HashSet<long> { w.Hwnd }) is { } killRefusal)
            {
                return Text(JsonSerializer.Serialize(new
                {
                    success = false,
                    closed = false,
                    error = killRefusal.Kind,
                    hwnd = $"0x{w.Hwnd:X}",
                    title = w.Title,
                    process = w.ProcessName,
                    pid = w.Pid,
                    detail = killRefusal.Reason
                }, J));
            }

            try
            {
                using var proc = Process.GetProcessById(w.Pid);
                proc.Kill();
                closed = proc.WaitForExit(1500) || proc.HasExited;
            }
            catch (Exception ex)
            {
                return Text(JsonSerializer.Serialize(new
                {
                    error = "ForceKillFailed",
                    hwnd = $"0x{w.Hwnd:X}",
                    title = w.Title,
                    process = w.ProcessName,
                    detail = $"WM_CLOSE failed and force-kill was attempted, but failed: {ex.Message} (process may be elevated or system-protected)."
                }, J));
            }
        }

        if (closed)
        {
            _s.MarkWindowClosed(w.Hwnd);
            var successResult = Text(JsonSerializer.Serialize(new
            {
                success = true,
                closed = true,
                hwnd = $"0x{w.Hwnd:X}",
                title = w.Title,
                process = w.ProcessName,
                notification = $"Closed {w.ProcessName} — no longer needed for this task.",
                detail = $"Closed '{w.ProcessName}' (0x{w.Hwnd:X}) — application was opened by the agent and is no longer needed for this task."
            }, J));
            _s.Deduplicator.RecordMutation(operationId, "computer_close_window", normArgs, successResult);
            return successResult;
        }
        else
        {
            _s.Telemetry.IncCloseRetry();
            Inbrisk.Core.PerfTrace.Count("closeRetry");
            // P1/F13: surface WHY the window stayed open — a blocking dialog,
            // a disabled (modally-blocked) window, or an unresponsive app —
            // so the caller can act on the dialog instead of escalating.
            var blocker = modal == null ? _s.Rt.WindowService.GetActiveBlockingPopup(w.Hwnd) : null;
            if (blocker != null && blocker.Hwnd == w.Hwnd) blocker = null;
            var windowEnabled = _s.Rt.WindowService.IsWindowEnabled(w.Hwnd);
            return Text(JsonSerializer.Serialize(new
            {
                success = false,
                closed = false,
                hwnd = $"0x{w.Hwnd:X}",
                title = w.Title,
                process = w.ProcessName,
                hasUnsavedDataPrompt = hasModal,
                modalPopup = hasModal ? $"0x{modal!.Hwnd:X} \"{modal.Title}\"" : null,
                blockingPopup = blocker != null ? $"0x{blocker.Hwnd:X} \"{blocker.Title}\" ({blocker.ProcessName})" : null,
                windowEnabled,
                detail = hasModal
                    ? $"WM_CLOSE posted to '{w.ProcessName}', but window remains open because an unsaved changes confirmation dialog appeared (0x{modal!.Hwnd:X} \"{modal.Title}\"). To protect user data, the window was not force-killed. Inspect or interact with the dialog."
                    : blocker != null
                        ? $"WM_CLOSE posted to '{w.ProcessName}', but window remains open — a blocking dialog is active (0x{blocker.Hwnd:X} \"{blocker.Title}\"). Resolve or interact with the dialog, then retry."
                        : $"WM_CLOSE posted but window still exists{(windowEnabled ? "" : " and is disabled (a modal dialog is likely blocking it)")} — it may be showing a save prompt (observe it) or the app hung. Force-kill is refused for shared/protected processes.",
                candidates = wins.Take(10).Select(BuildCandidateWindow).ToList()
            }, J));
        }
    }

    /// <summary>P1/F13: Process.Kill is a last resort reserved for single-purpose
    /// processes. Returns a refusal (error kind + human-readable reason) whenever
    /// the target pid is the agent host, a terminal/IDE ancestor, a known shared
    /// multi-window host, or owns visible top-level windows outside the current
    /// close batch — killing it would end the AI session or destroy unrelated
    /// user windows. Returns null when a force-kill is permitted.</summary>
    private (string Kind, string Reason)? ForceKillRefusal(
        WindowInfo w, IReadOnlyList<WindowInfo> wins, IReadOnlyCollection<long> batchTargetHwnds)
    {
        if (w.Pid > 0 &&
            WindowService.IsAgentHostOrAncestorPid(w.Pid, out var ancestorReason))
        {
            return ("ProtectedProcess",
                $"Force-kill refused: {ancestorReason}. Terminating it would end the AI host session or a shared terminal. " +
                "Close windows with WM_CLOSE (no force) instead.");
        }

        // Host-tree descendant: a process spawned beneath this runtime
        // (respawned server, helper, renamed host binary) carries no
        // protected name and may own an unprotected window — force must
        // never launder its termination. Agent-owned windows are exempt:
        // the lifecycle policy already authorizes their kill.
        if (w.Pid > 0 && !IsWindowAgentOwned(w) &&
            WindowService.IsDescendantOfAgentHost(w.Pid, out var descendantReason))
        {
            return ("ProtectedProcess",
                $"Force-kill refused: {descendantReason}. Terminating a process inside the agent host's process tree would end or degrade the AI host session. " +
                "Close windows with WM_CLOSE (no force) instead.");
        }

        if (WindowService.IsProcessProtected(w.ProcessName, out var nameReason) ||
            WindowService.IsSharedMultiWindowProcess(w.ProcessName, out nameReason))
        {
            return ("ProtectedProcess",
                $"Force-kill refused: {nameReason}. '{w.ProcessName}' is a shared/protected process — " +
                "killing it would close unrelated windows or end the AI host session. Close each window with WM_CLOSE instead.");
        }

        if (w.Pid > 0)
        {
            var others = wins
                .Where(x => x.Pid == w.Pid && x.Hwnd != w.Hwnd && !batchTargetHwnds.Contains(x.Hwnd))
                .ToList();
            if (others.Count > 0)
            {
                return ("SharedProcessKillRefused",
                    $"Force-kill refused: '{w.ProcessName}' (pid={w.Pid}) owns {others.Count + 1} top-level window(s) — " +
                    $"terminating the process would also close {others.Count} window(s) outside this request: " +
                    string.Join(", ", others.Take(5).Select(x => $"0x{x.Hwnd:X} \"{x.Title}\"")) +
                    ". Close each window individually with computer_close_window instead.");
            }
        }

        return null;
    }

    private static int ScoreProcessMatch(string? actual, string query)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(query)) return 0;
        var act = actual.Trim();
        var q = query.Trim();
        var normAct = act.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? act[..^4] : act;
        var normQ = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q[..^4] : q;

        // Exact match (100)
        if (string.Equals(act, q, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normAct, normQ, StringComparison.OrdinalIgnoreCase))
            return 100;

        // Common known aliases (95)
        if ((normQ.Equals("edge", StringComparison.OrdinalIgnoreCase) && normAct.Equals("msedge", StringComparison.OrdinalIgnoreCase)) ||
            (normQ.Equals("word", StringComparison.OrdinalIgnoreCase) && normAct.Equals("winword", StringComparison.OrdinalIgnoreCase)) ||
            (normQ.Equals("calc", StringComparison.OrdinalIgnoreCase) && (normAct.Equals("calculatorapp", StringComparison.OrdinalIgnoreCase) || normAct.Equals("calculator", StringComparison.OrdinalIgnoreCase))))
            return 95;

        // Starts-with prefix (80)
        if (normAct.StartsWith(normQ, StringComparison.OrdinalIgnoreCase))
            return 80;

        // Token / word boundary match (70 / 60)
        var tokens = normAct.Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            if (string.Equals(token, normQ, StringComparison.OrdinalIgnoreCase))
                return 70;
            if (token.StartsWith(normQ, StringComparison.OrdinalIgnoreCase))
                return 60;
        }

        // Substring match with lower score (40) — only for tokens length >= 4 to avoid false short positives
        if (normQ.Length >= 4 && normAct.IndexOf(normQ, StringComparison.OrdinalIgnoreCase) >= 0)
            return 40;

        return 0;
    }

    private static bool MatchesProcess(string? actual, string query) => ScoreProcessMatch(actual, query) > 0;

    private WindowInfo? FindBestWindowForProcess(string process)
    {
        var wins = _s.Rt.Windows();
        var fgHwnd = _s.Rt.ForegroundWindow()?.Hwnd;

        var byProc = wins
            .Select(w => (Window: w, Score: ScoreProcessMatch(w.ProcessName, process)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Window.Hwnd == fgHwnd ? 1 : 0)
            .ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Window.Title) ? 1 : 0)
            .Select(x => (WindowInfo?)x.Window)
            .FirstOrDefault();
        if (byProc != null) return byProc;

        // Host-managed apps report the host runtime as the process name of
        // every window they open (dotnet host → "dotnet", python host →
        // "python"). The app identity callers name — the managed assembly
        // ("Inbrisk.TestApp") — is not in any ProcessName; it lives in the
        // window title. Fall back to normalized title-identity matching so
        // process-scoped targets still resolve for those apps.
        var normQuery = NormalizeAppIdentity(process);
        if (normQuery.Length == 0) return null;
        return wins
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .Select(w => (Window: w, Score: ScoreAppIdentity(w.Title, normQuery)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Window.Hwnd == fgHwnd ? 1 : 0)
            .Select(x => (WindowInfo?)x.Window)
            .FirstOrDefault();
    }

    /// <summary>App identity normalization: letters/digits only, lowercased —
    /// "Inbrisk.TestApp", "Inbrisk-TestApp" and "InbriskTestApp" all collapse
    /// to the same token.</summary>
    private static string NormalizeAppIdentity(string s)
        => new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int ScoreAppIdentity(string title, string normQuery)
    {
        var normTitle = NormalizeAppIdentity(title);
        return normTitle.Length > 0 && normTitle.Contains(normQuery, StringComparison.Ordinal)
            ? 70
            : 0;
    }

    internal Dictionary<string, object?> BuildInitialAppMap(long hwnd, int? pid, string? title, string? appName = null)
    {
        var sw = Stopwatch.StartNew();
        var fg = _s.Rt.ForegroundWindow();
        var isFocused = fg?.Hwnd == hwnd;
        var modal = _s.Rt.WindowService.GetModalPopup(hwnd);

        const int maxActionables = 12;
        const int maxLandmarks = 6;
        const int maxNodesVisited = 50;
        var timeBudget = TimeSpan.FromMilliseconds(200);

        IReadOnlyList<UiElement> els = Array.Empty<UiElement>();
        bool truncated = false;
        int comPropertyReads = 0;

        try
        {
            var findSpec = new FindSpec(Hwnd: hwnd, MaxResults: maxNodesVisited);
            els = _s.Rt.Find(findSpec);
            comPropertyReads += els.Count * 2;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"BuildInitialAppMap find hwnd=0x{hwnd:X} failed: {ex.Message}");
        }

        if (sw.Elapsed > timeBudget || els.Count >= maxNodesVisited)
        {
            truncated = true;
        }

        var prioritized = els
            .Where(e => e.Actions.Count > 0 || e.Role is Inbrisk.Core.Role.Button or Inbrisk.Core.Role.Edit or Inbrisk.Core.Role.MenuItem or Inbrisk.Core.Role.TabItem or Inbrisk.Core.Role.CheckBox)
            .Take(maxActionables)
            .Select(e => new Dictionary<string, object?>
            {
                ["id"] = e.Id,
                ["role"] = e.Role.ToString(),
                ["name"] = e.Name,
                ["actions"] = e.Actions,
                ["bounds"] = $"({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})"
            })
            .ToList();

        var landmarks = els
            .Where(e => e.Role is Inbrisk.Core.Role.TitleBar or Inbrisk.Core.Role.Menu or Inbrisk.Core.Role.Toolbar or Inbrisk.Core.Role.Tab)
            .Select(e => e.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct()
            .Take(maxLandmarks)
            .ToList();

        // Incorporate persistent profile knowledge
        if (_s?.ProfileStore != null)
        {
            try
            {
                var queryName = appName ?? title;
                if (!string.IsNullOrEmpty(queryName))
                {
                    var id = new ApplicationIdentity(displayName: queryName);
                    var profile = _s.ProfileStore.GetProfile(id);
                    if (profile?.Landmarks != null)
                    {
                        foreach (var lmName in profile.Landmarks.Keys)
                        {
                            if (!landmarks.Contains(lmName, StringComparer.OrdinalIgnoreCase))
                            {
                                landmarks.Add(lmName);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BuildInitialAppMap profile landmark read failed: {ex.Message}");
            }
        }

        var map = new Dictionary<string, object?>
        {
            ["hwnd"] = $"0x{hwnd:X}",
            ["pid"] = pid,
            ["title"] = title,
            ["ready"] = true,
            ["focused"] = isFocused,
            ["landmarks"] = landmarks,
            ["firstActionables"] = prioritized,
            ["modal"] = (modal != null && modal.Hwnd != hwnd) ? new { hwnd = $"0x{modal.Hwnd:X}", title = modal.Title } : null
        };

        if (truncated)
        {
            map["truncated"] = true;
        }

        sw.Stop();
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(map);
        _s.Telemetry.RecordInitialMapMetrics(
            sw.Elapsed.TotalMilliseconds,
            els.Count,
            prioritized.Count,
            landmarks.Count,
            jsonBytes.Length,
            truncated,
            comPropertyReads
        );

        return map;
    }

    [McpServerTool(Name = "computer_launch"), Description(
        "Launch a Windows application by friendly name — the supported " +
        "replacement for shelling out to PowerShell/Start-Process. " +
        "DO NOT call computer_apps before launch solely to discover an app — call computer_launch directly. " +
        "Resolves through Windows app registration in a deterministic " +
        "pipeline: already-running window → Start Menu → App Paths → " +
        "packaged apps (AUMID) → executable on PATH → registered URI " +
        "scheme. With waitFor:\"window\" (default) it returns only after a " +
        "usable, UIA-reachable top-level window exists — not merely when a " +
        "process was spawned. Includes a compact initialMap with landmarks and firstActionables so " +
        "follow-up discovery calls are unnecessary. Idempotent: a running app is reused " +
        "(launchState=AlreadyRunning) unless newInstance:true. " +
        "A friendly name matching several DISTINCT apps returns AmbiguousApplication + structured candidates with exact retryArgs. " +
        "Heavy applications (e.g. Blender, IDEs, browsers) take time to load shaders, splash screens, and modules — computer_launch waits automatically (default 25000ms). Do NOT immediately re-launch or conclude failure if an app takes a moment to load; inspect or observe instead of looping launch.")]
    public async Task<CallToolResult> Launch(
        [Description("friendly app name (\"Spotify\", \"Notepad\", \"Calculator\") — preferred")] string? app = null,
        [Description("search installed apps and open the best match — alias of app (\"Unreal\" finds UnrealEditor)")] string? search = null,
        [Description("executable name or path (\"notepad.exe\")")] string? executable = null,
        [Description("explicit exe path")] string? path = null,
        [Description("packaged-app AUMID (PackageFamilyName!AppId)")] string? aumid = null,
        [Description("URI with a registered handler (\"spotify:\")")] string? uri = null,
        [Description("structured arguments — one element = one verbatim argument")] string[]? arguments = null,
        [Description("spawn a new instance instead of reusing a running window — default false")] bool? newInstance = null,
        [Description("window (default) | process | none")] string? waitFor = null,
        [Description("readiness timeout ms — default 25000")] int? timeoutMs = null,
        [Description("optional remote debugging port for Chrome DevTools Protocol / CDP (e.g. 9222)")] int? debugPort = null,
        [Description("optional post-launch continuation steps executed via canonical plan executor")] RunStep[]? then = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("optional specialist adapter to wire at launch — \"blender\" force-enables the bpy socket bridge injection; \"none\" disables auto-injection")] string? adapter = null,
        [Description("auto-inject the adapter bridge script when a supported app is detected (default true for Blender); false launches plain")] bool? bridge = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.LaunchAsync(
                app, search, executable, path, aumid, uri, arguments, workingDir: null, ct).ConfigureAwait(false);
        }

        var normArgs = $"{app}|{search}|{executable}|{path}|{aumid}|{uri}|{string.Join(",", arguments ?? Array.Empty<string>())}|{newInstance}|{waitFor}|{timeoutMs}|{debugPort}|{then?.Length}|{adapter}|{bridge}";
        if (_s.Deduplicator.TryDeduplicateMutation(operationId, "computer_launch", normArgs, _s.Telemetry, out var conflictError) is { } deduped)
            return deduped;
        if (conflictError != null)
            return conflictError;

        if (uri != null)
        {
            if (!DeepLinkSecurity.IsSafeDeepLink(uri, out var reason))
            {
                return Error(OutcomeKind.PolicyDenied, $"Unsafe deep link URI blocked: {reason}");
            }
        }

        if (DateTimeOffset.UtcNow - _s.LastAppsQueryTimestamp < TimeSpan.FromSeconds(5))
        {
            _s.Telemetry.IncAppsThenLaunchWithin5s();
            PerfTrace.Count("appsThenLaunchWithin5s");
        }

        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return Error(OutcomeKind.EmergencyStopped,
                StoppedDetail);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);
        var sw = Stopwatch.StartNew();

        var ids = new[]
        {
            app != null, search != null, executable != null, path != null,
            aumid != null, uri != null,
        }.Count(b => b);
        if (ids == 0)
            return Error(OutcomeKind.Malformed,
                "one of app|search|executable|path|aumid|uri is required");
        if (ids > 1)
            return Error(OutcomeKind.Malformed,
                "launch takes exactly one identifier: app|search|executable|path|aumid|uri");
        if (arguments is { Length: > 0 } && (aumid != null || uri != null))
            return Error(OutcomeKind.Malformed,
                "arguments only apply to executable launches");

        // Canonical lock ordering: Acquire GLOBAL DESKTOP GATE (Rank 1) BEFORE launch/spawn/activate.
        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        IInputLease? globalLease = null;
        try
        {
            globalLease = await arbiter.AcquireAsync(
                ownerId: _s?.SessionId ?? "session",
                kind: LeaseKind.PhysicalInput,
                description: $"Launch: {app ?? search ?? executable ?? path ?? aumid ?? uri}",
                timeout: TimeSpan.FromMilliseconds(timeoutMs ?? 25000),
                ct: linked.Token).ConfigureAwait(false);
        }
        catch (TimeoutException tex)
        {
            return Error(OutcomeKind.ConcurrencyConflict, $"Global desktop gate timed out: {tex.Message}");
        }

        try
        {
            var preSnapshot = _s.CapturePreLaunchSnapshot();
            var launchArguments = ApplyAdapterBridge(adapter, bridge,
                app ?? search, executable, path, aumid, uri,
                arguments, out var adapterBridge);
            LaunchResult r;
            try
            {
                r = _s.Rt.Launch(new LaunchSpec(app ?? search, executable, path,
                    aumid, uri, launchArguments, newInstance ?? false,
                    waitFor ?? "window", timeoutMs ?? 25000, debugPort), linked.Token);
            }
            catch (OperationCanceledException)
            {
                return Error(OutcomeKind.Cancelled, "launch cancelled");
            }

        if (!r.Success)
        {
            var kind = r.Error switch
            {
                "AmbiguousApplication" => OutcomeKind.AmbiguousApplication,
                "PolicyDenied" => OutcomeKind.PolicyDenied,
                "Timeout" => OutcomeKind.Timeout,
                "TargetNotFound" => OutcomeKind.TargetNotFound,
                "EmergencyStopped" => OutcomeKind.EmergencyStopped,
                _ => OutcomeKind.Failed,
            };
            var sb = new StringBuilder(r.ErrorDetail ?? r.Error);
            var candidatesList = new List<object>();
            if (r.Candidates is { Count: > 0 } cs)
            {
                sb.Append("\ncandidates:");
                foreach (var c in cs)
                {
                    sb.Append($"\n  {c.Name}  [{c.Method}]  {c.Identifier}");
                    candidatesList.Add(new
                    {
                        name = c.Name,
                        method = c.Method.ToString(),
                        identifier = c.Identifier,
                        retryArgs = new Dictionary<string, string> { [c.Method.ToString().ToLowerInvariant()] = c.Identifier }
                    });
                }
                sb.Append("\n→ pick one and call computer_launch again " +
                    "now with that identifier — do not narrate");
            }
            if (r.Error == "TargetNotFound" && (app ?? search) is { } q)
            {
                var near = ClosestApps(q, 3);
                if (near.Count > 0)
                {
                    sb.Append("\nclosest registered apps:");
                    foreach (var a in near)
                    {
                        sb.Append($"\n  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}");
                        candidatesList.Add(new
                        {
                            name = a.Name,
                            kind = a.Kind.ToString(),
                            launch = a.Launch,
                            retryArgs = new { app = a.Name }
                        });
                    }
                    sb.Append("\n→ retry computer_launch with one of these " +
                        "args now — do not narrate");
                }
            }
            // Emit the structured error ONCE — feeding a serialized blob into
            // Error()'s `detail` field double-encodes it (detail becomes a
            // stringified JSON document instead of the readable message).
            var errObj = new Dictionary<string, object?>
            {
                ["error"] = r.Error ?? kind.ToString(),
                ["detail"] = sb.ToString(),
                ["durationMs"] = sw.ElapsedMilliseconds,
            };
            if (candidatesList.Count > 0) errObj["candidates"] = candidatesList;
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock
                    { Text = JsonSerializer.Serialize(errObj, J) }],
            };
        }

        // record the window as session scope so a following
        // computer_run within:/wait_for can target it
        Dictionary<string, object?>? initialMap = null;
        long targetHwnd = r.Hwnd ?? 0;
        int targetPid = r.Pid ?? 0;

        if (targetHwnd == 0 && r.Success)
        {
            var win = _s.Rt.Windows().FirstOrDefault(w => r.Pid.HasValue && w.Pid == r.Pid.Value);
            if (win != null)
            {
                targetHwnd = win.Hwnd;
                targetPid = win.Pid;
            }
        }

        DateTimeOffset? finalStartTime = null;
        if (targetPid > 0)
        {
            try
            {
                using var targetProc = Process.GetProcessById(targetPid);
                finalStartTime = targetProc.StartTime;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"computer_launch pid={targetPid} start-time probe failed: {ex.Message}");
            }
        }

        var foundWin = _s.Rt.Windows().FirstOrDefault(w => (targetHwnd != 0 && w.Hwnd == targetHwnd) || (targetPid > 0 && w.Pid == targetPid));
        string? targetProcName = foundWin?.ProcessName;
        if (targetProcName == null && targetPid > 0)
        {
            try
            {
                using var targetP = Process.GetProcessById(targetPid);
                targetProcName = targetP.ProcessName;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"computer_launch pid={targetPid} name probe failed: {ex.Message}");
            }
        }

        bool isHostedProcess = string.Equals(targetProcName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ||
                               r.Method == LaunchMethod.Aumid ||
                               !string.IsNullOrEmpty(aumid);

        bool agentOwned = LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership(
            preSnapshot,
            finalHwnd: targetHwnd,
            finalPid: targetPid,
            finalPidStartTime: finalStartTime,
            spawnedPid: r.Pid,
            launchState: r.LaunchState,
            hasExplicitProcessSpawn: r.Pid.HasValue && r.Pid.Value > 0,
            isHostedProcess: isHostedProcess
        );
        bool alreadyRunning = !agentOwned || r.LaunchState == "AlreadyRunning";

        // Who owns the window the caller is about to interact with.
        // "agent" means THIS call created it (positive creation
        // evidence: new pid + new hwnd + start time after the pre-launch
        // snapshot). "user" covers every other case — most importantly a
        // reused ExistingInstance window, which may hold unsaved user
        // content. Without this field the caller had to re-derive it from
        // `method`/`launchState`, and the two are not equivalent: a
        // non-AlreadyRunning launch can still fail the evidence check.
        var ownership = agentOwned && r.LaunchState != "AlreadyRunning" ? "agent" : "user";
        var ownershipReason = ownership == "agent"
            ? "created by this launch call"
            : r.LaunchState == "AlreadyRunning" || r.Method == LaunchMethod.ExistingInstance
                ? $"reused a running window via {r.Method} — it predates this " +
                  "task and may hold unsaved user content; treat it as " +
                  "read-mostly, do not overwrite what is there, and do not close it"
                : "creation evidence was not conclusive — treated as a " +
                  "pre-existing user window";

        IAsyncDisposable? targetBarrier = null;
        if (targetPid > 0 && _s?.Rt?.ReadScheduler != null)
        {
            targetBarrier = await _s.Rt.ReadScheduler.EnterMutationBarrierAsync(targetPid, linked.Token).ConfigureAwait(false);
        }

        // Foreground activation and process spawn are complete: release Global Desktop Gate early
        // so unrelated physical tasks are not blocked during initial-map UIA tree inspection.
        globalLease?.Dispose();
        globalLease = null;

        try
        {
            if (targetHwnd != 0)
            {
                if (!GhostOn(ghost))
                {
                    _s.Rt.Focus(targetHwnd);
                }
                _s.ScopeHwnd = targetHwnd;
                _s.LastLaunchTimestamp = DateTimeOffset.UtcNow;
                _s.LastLaunchedHwnd = targetHwnd;

                var appNorm = (r.ResolvedName ?? app ?? search ?? executable ?? aumid ?? "").ToLowerInvariant();
                var intent = LifecycleIntent.Unknown;
                if (appNorm.Contains("calc")) intent = LifecycleIntent.Reusable;
                else if (appNorm.Contains("notepad") || appNorm.Contains("word") || appNorm.Contains("code")) intent = LifecycleIntent.TaskArtifact;
                else if (appNorm.Contains("explorer")) intent = LifecycleIntent.UserUseful;

                _s.RecordWindowLaunch(
                    launchRunId: null,
                    hwnd: targetHwnd,
                    pid: targetPid,
                    appIdentity: r.ResolvedName ?? (app ?? search ?? executable ?? path ?? aumid ?? uri ?? ""),
                    title: r.WindowTitle ?? "",
                    agentOwned: agentOwned,
                    alreadyRunning: alreadyRunning,
                    intent: intent
                );
                if (agentOwned && targetPid > 0)
                {
                    _s.Rt.Provenance.RegisterAgentLaunch(targetPid, targetProcName ?? r.ResolvedName ?? app ?? "app", targetHwnd, r.ResolvedName ?? app, intent);
                }
                initialMap = BuildInitialAppMap(targetHwnd, targetPid, r.WindowTitle, r.ResolvedName ?? app ?? search);
            }
            else
            {
                initialMap = new Dictionary<string, object?>
                {
                    ["hwnd"] = null,
                    ["pid"] = r.Pid,
                    ["title"] = r.WindowTitle,
                    ["landmarks"] = Array.Empty<string>(),
                    ["firstActionables"] = Array.Empty<object>()
                };
            }
        }
        finally
        {
            if (targetBarrier != null)
            {
                await targetBarrier.DisposeAsync().ConfigureAwait(false);
            }
        }

        string next = "none";
        bool nextObservationRequired = false;

        var hasModal = initialMap != null && initialMap.TryGetValue("modal", out var mVal) && mVal != null;
        var isTruncated = initialMap != null && initialMap.TryGetValue("truncated", out var tVal) && tVal is true;
        var actionableCount = 0;
        if (initialMap != null && initialMap.TryGetValue("firstActionables", out var faObj) && faObj is System.Collections.IEnumerable enumerable)
        {
            foreach (var _ in enumerable) actionableCount++;
        }

        if (hasModal)
        {
            next = "dismiss_modal";
            nextObservationRequired = true;
        }
        else if (isTruncated || initialMap == null || actionableCount == 0)
        {
            next = "inspect";
            nextObservationRequired = true;
        }
        else
        {
            next = "none";
            nextObservationRequired = false;
        }


        if (hasModal && then != null && then.Length > 0)
        {
            var pauseRes = Text(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["status"] = "Paused",
                ["pauseReason"] = "UnexpectedModal",
                ["app"] = r.ResolvedName,
                ["modal"] = initialMap?["modal"],
                ["note"] = "Launch completed but an unexpected modal appeared before continuation could run; execution paused for resolution.",
                ["resumable"] = true,
                ["provenance"] = Provenance("win32")
            }, J));
            _s.Deduplicator.RecordMutation(operationId, "computer_launch", normArgs, pauseRes);
            return pauseRes;
        }

        object? continuationOutcome = null;
        if (then != null && then.Length > 0)
        {
            var planResult = await RunPlanCore(then, null, Stopwatch.StartNew(), linked.Token, detail: "slim").ConfigureAwait(false);
            try
            {
                if (planResult.Content is [TextContentBlock { Text: { } rawJson }])
                {
                    continuationOutcome = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawJson, J);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"computer_launch continuation outcome parse failed: {ex.Message}");
            }
            if (continuationOutcome == null)
            {
                continuationOutcome = planResult.Content;
            }
        }

        var launchRes = Text(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "Verified",
            ["next"] = next,
            ["nextObservationRequired"] = nextObservationRequired,
            ["app"] = r.ResolvedName,
            ["method"] = r.Method?.ToString(),
            ["process"] = r.Pid is { } p
                ? new { pid = p } : null,
            ["window"] = r.Hwnd is { } wh
                ? new { hwnd = $"0x{wh:X}", title = r.WindowTitle } : null,
            ["initialMap"] = initialMap,
            ["continuation"] = continuationOutcome,
            ["launchState"] = r.LaunchState,
            ["alreadyRunning"] = r.LaunchState == "AlreadyRunning",
            ["ownership"] = ownership,
            ["ownershipReason"] = ownershipReason,
            // injected --python only takes effect when a new process was
            // actually spawned — a reused AlreadyRunning window never saw
            // the args, so don't claim the bridge is live there
            ["adapterBridge"] = r.LaunchState == "AlreadyRunning"
                ? null : adapterBridge,
            ["note"] = r.LaunchState == "AlreadyRunning"
                ? "already open — reused the running window (ownership: user)"
                : null,
            ["durationMs"] = r.LaunchMs + r.ReadyMs,
            ["launchMs"] = r.LaunchMs,
            ["readyMs"] = r.ReadyMs,
            ["provenance"] = Provenance("win32")
        }, J));
        _s.Deduplicator.RecordMutation(operationId, "computer_launch", normArgs, launchRes);
        return launchRes;
        }
        finally
        {
            globalLease?.Dispose();
        }
    }

    [McpServerTool(Name = "computer_apps"), Description(
        "SEARCH the launchable applications registered on this machine. " +
        "DO NOT call computer_apps before launch solely to discover an app; call computer_launch directly. " +
        "computer_launch resolves apps automatically and provides candidate retry arguments if ambiguous or not found. " +
        "To OPEN an app you usually skip this tool entirely: computer_launch{search:\"name\"} searches AND launches in one call. " +
        "Without name this tool returns counts only — the full list needs all:true and is huge; always prefer a name query.")]
    public CallToolResult Apps(
        [Description("case-insensitive name query — the app's name or a distinctive substring (\"unreal\", \"epic\"). Always pass this first")] string? name = null,
        [Description("installed|system|all — default all")] string? kind = null,
        [Description("list the whole catalog (hundreds of lines) — only when a name search is genuinely not possible")] bool? all = null,
        [Description("max entries listed — default 100 (slim) / 250 (full)")] int? limit = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        CancellationToken ct = default)
    {
        _s.LastAppsQueryTimestamp = DateTimeOffset.UtcNow;
        _s.LastAppsQueryName = name;
        if (BadDetail(detail) is { } bd) return bd;
        if (kind != null && kind is not ("installed" or "system" or "all"))
            return Error(OutcomeKind.Malformed,
                $"kind must be installed|system|all, got '{kind}'");
        var apps = _s.Rt.Parts.Apps.ListApps();
        if (kind is "installed" or "system")
            apps = apps.Where(a => a.Kind == kind).ToList();
        var pool = apps;

        // Search-first contract: no name → no dump. A bare call would
        // spend context on hundreds of entries the model doesn't need;
        // counts + the query hint cost one line instead.
        if (string.IsNullOrWhiteSpace(name) && all != true)
        {
            var inst = pool.Count(a => a.Kind == "installed");
            var sys = pool.Count(a => a.Kind == "system");
            return Text($"{apps.Count} app(s) registered " +
                $"({inst} installed, {sys} system) — " +
                "search first: computer_apps{name:\"<substring>\"}, or " +
                "open directly in one call: computer_launch{search:" +
                "\"<name>\"}. Full listing only via all:true.");
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var norm = NormStr(name);
            // Match name AND the launch identifier — package PFNs carry
            // canonical English ids, so "calculator" finds the app whose
            // localized display name is "Hesap Makinesi"
            apps = apps.Where(a => NormStr(a.Name).Contains(norm) ||
                NormStr(a.Launch).Contains(norm)).ToList();
            if (apps.Count == 0)
            {
                // unregistered executables — engines/dev tools live under
                // install roots with no Start Menu/App Path entry at all
                var exeHits = _s.Rt.Parts.Apps.FindExecutables(name);
                // fuzzy fallback over the catalog — never leave the model
                // at a dead end
                var near = pool.Select(a => (a, s: Sim(NormStr(a.Name), norm)))
                    .Where(x => x.s >= 0.30)
                    .OrderByDescending(x => x.s).Take(5).ToList();
                sb.AppendLine($"0 registered app(s) matching \"{name}\"");
                foreach (var x in exeHits)
                    sb.AppendLine($"  {Path.GetFileName(x)}  " +
                        $"[Executable/installed]  path:\"{x}\"");
                if (exeHits.Count > 0)
                    sb.AppendLine("  → call computer_launch{path:\"…\"} or " +
                        "{search:\"…\"} now — do not narrate");
                foreach (var (a, _) in near)
                    sb.AppendLine($"  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}" +
                        "  (closest)");
                if (exeHits.Count == 0 && near.Count == 0)
                    sb.AppendLine("  no close matches — try a shorter " +
                        "substring or computer_apps{kind:\"installed\"}");
                else if (exeHits.Count == 0)
                    sb.AppendLine("  → retry with a closer name or its " +
                        "launch arg — do not narrate");
                return Text(sb.ToString());
            }
        }
        var slim = Slim(detail);
        var cap = limit ?? (slim ? 100 : 250);
        sb.AppendLine($"{apps.Count} app(s)" +
            (kind is "installed" or "system" ? $" kind={kind}" : "") +
            (string.IsNullOrWhiteSpace(name)
                ? " — open any with computer_launch{search:\"…\"}:"
                : $" matching \"{name}\" — open with computer_launch{{search:\"…\"}} or the launch arg:"));
        foreach (var a in apps.Take(cap))
            sb.AppendLine($"  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}");
        if (apps.Count > cap)
            sb.AppendLine($"  …{apps.Count - cap} more — pass name to filter or limit to widen");
        _s.LastAppsQueryTimestamp = DateTimeOffset.UtcNow;
        return Text(sb.ToString());
    }

    private static string NormStr(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray())
            .ToLowerInvariant();

    /// <summary>Character-bigram Dice similarity — tolerates typos, case
    /// and spacing differences ("unrel" ≈ "unreal", "epic games" ≈
    /// "EpicGamesLauncher" partially). 0..1.</summary>
    private static double Sim(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
        var ba = new HashSet<int>();
        for (var i = 0; i + 1 < a.Length; i++) ba.Add((a[i] << 16) | a[i + 1]);
        var bb = new HashSet<int>();
        for (var i = 0; i + 1 < b.Length; i++) bb.Add((b[i] << 16) | b[i + 1]);
        var inter = ba.Count(x => bb.Contains(x));
        return 2.0 * inter / (ba.Count + bb.Count);
    }

    /// <summary>Fuzzy catalog suggestions for a failed friendly-name
    /// launch — up to <paramref name="n"/> closest app names, so a
    /// TargetNotFound error still hands the model a usable next step.</summary>
    private List<AppInfo> ClosestApps(string query, int n)
    {
        var norm = NormStr(query);
        return _s.Rt.Parts.Apps.ListApps()
            .Select(a => (a, s: Sim(NormStr(a.Name), norm)))
            .Where(x => x.s >= 0.30)
            .OrderByDescending(x => x.s).Take(n)
            .Select(x => x.a).ToList();
    }

    // ---------------- session process cleanup ----------------
    // Reaps ONLY processes this session provably spawned. Backed by
    // InbriskRuntime.ProcessTracker (Inbrisk.Core.SessionProcessTracker):
    // every pid inbrisk launches is Track()ed with its start-time + baseline
    // window count, and ReapAll does WM_CLOSE → ~2s grace → hard-kill only
    // when the pid's identity is verified, no windows remain, and no
    // protection veto applies. Untracked/user processes are never touched.

    [McpServerTool(Name = "computer_cleanup"), Description(
        "Reap leftover processes this session spawned that are still running: " +
        "posts WM_CLOSE for a graceful exit, waits a short grace period, then " +
        "kills only windowless processes whose spawn identity is verified. " +
        "NEVER touches untracked or user processes — ineligible pids are " +
        "reported in skipped[] with a reason. " +
        "Returns {pending, reaped, skipped:[{pid,reason}]}.")]
    public async Task<CallToolResult> Cleanup(
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        // Same mutating-op gate as computer_app_shutdown: cleanup mutates the
        // desktop, so it honours the latched emergency-stop epoch. The consent
        // path also mirrors app_shutdown — only session-spawned, tracker-held
        // pids are eligible; there is no force override for foreign processes.
        if (_s.Control.ActionToken() == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);

        return await ExecuteWithIdempotencyAsync(operationId, "computer_cleanup", "reap", async () =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var report = await Task.Run(
                    () => _s.Rt.ProcessTracker.ReapAll(_s.SessionId), ct);
                return Json(CleanupPayload(report));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Error(OutcomeKind.Failed, $"cleanup reap failed: {ex.Message}");
            }
        });
    }

    /// <summary>Shared {pending, reaped, skipped:[{pid,reason}]} projection
    /// of the tracker's ReapReport — "skipped" lists every tracked pid this
    /// pass did not terminate (already-exited, vetoed, or save-prompted).</summary>
    private static object CleanupPayload(ReapReport report) => new
    {
        pending = report.Attempted,
        reaped = report.Reaped,
        skipped = report.Outcomes
            .Where(o => o.Action is not ("closed" or "killed"))
            .Select(o => new { pid = o.Pid, process = o.ProcessName, reason = o.Reason ?? o.Action })
            .ToList(),
        pendingRemaining = report.PendingRemaining,
        elapsedMs = report.ElapsedMs,
    };

    /// <summary>Post-wraps a computer_do result with a "cleanup" node when
    /// cleanup:true was passed — reaps session-spawned leftovers and reports
    /// {pending, reaped, skipped:[{pid,reason}]}. Never reaps while the
    /// emergency stop is latched; leaves the original result untouched if its
    /// payload is not a JSON object.</summary>
    private CallToolResult WithCleanupReap(CallToolResult res)
    {
        try
        {
            object cleanup;
            if (_s.Control.ActionToken() == null)
            {
                cleanup = new { error = OutcomeKind.EmergencyStopped.ToString(), detail = StoppedDetail };
            }
            else
            {
                cleanup = CleanupPayload(_s.Rt.ProcessTracker.ReapAll(_s.SessionId));
            }

            var text = res.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (string.IsNullOrEmpty(text)) return res;
            if (System.Text.Json.Nodes.JsonNode.Parse(text) is not
                System.Text.Json.Nodes.JsonObject node) return res;
            node["cleanup"] = JsonSerializer.SerializeToNode(cleanup, J);
            return new CallToolResult
            {
                IsError = res.IsError,
                Content = [new TextContentBlock { Text = node.ToJsonString(J) }],
            };
        }
        catch { return res; }
    }

    [McpServerTool(Name = "computer_focus_window"), Description(
        "Bring a window to the foreground — verified against GetForegroundWindow. " +
        "Target by hwnd, process name, or title substring. Automatically unminimizes. " +
        "POLICY: Do not call this merely because a window is not foreground; if a window is [shown] " +
        "and not [minimized] on a visible or secondary monitor, prefer non-disruptive inspection " +
        "(computer_inspect, computer_find, computer_screenshot with monitor index) first to avoid stealing user focus.")]
    public Task<CallToolResult> FocusWindow(
        [Description("window handle — decimal or 0x-prefixed")] string? hwnd = null,
        [Description("process name of the window to focus, e.g. \"notepad\"")] string? process = null,
        [Description("substring of the window title")] string? titleContains = null,
        [Description("semantic target object, e.g. {\"process\": \"notepad.exe\", \"hwnd\": \"0x...\"}")] TargetSpec? target = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        var normArgs = $"{hwnd}|{process}|{titleContains}|{target?.Window}|{target?.Process}";
        return ExecuteWithIdempotencyAsync(operationId, "computer_focus_window", normArgs, () =>
        {
            hwnd ??= target?.Hwnd ?? (target?.Window != null && (target.Window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || long.TryParse(target.Window, out _)) ? target.Window : null);
            process ??= target?.Process;
            titleContains ??= target?.Name ?? target?.NameContains ?? (hwnd == null ? target?.Window : null);

            long? h = ParseHwnd(hwnd);
            if (h == null)
            {
                var wins = _s.Rt.Windows();
                if (!string.IsNullOrWhiteSpace(process))
                {
                    var hits = wins
                        .Select(x => (Window: x, Score: ScoreProcessMatch(x.ProcessName, process)))
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .Select(x => x.Window)
                        .ToList();
                    // P1/F13: several windows match — refuse to silently pick
                    // the foreground/first one; the model must disambiguate.
                    if (hits.Count > 1)
                    {
                        return Task.FromResult(Text(JsonSerializer.Serialize(new
                        {
                            error = "AmbiguousTarget",
                            detail = $"{hits.Count} windows owned by '{process}' — pick one by hwnd",
                            candidates = hits.Select(BuildCandidateWindow).ToList()
                        }, J)));
                    }
                    else if (hits.Count == 1)
                    {
                        h = hits[0].Hwnd;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(titleContains))
                {
                    var hits = wins.Where(x => x.Title.Contains(titleContains, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (hits.Count > 1)
                    {
                        return Task.FromResult(Text(JsonSerializer.Serialize(new
                        {
                            error = "AmbiguousTarget",
                            detail = $"{hits.Count} windows match '{titleContains}' — pick one by hwnd",
                            candidates = hits.Select(BuildCandidateWindow).ToList()
                        }, J)));
                    }
                    else if (hits.Count == 1)
                    {
                        h = hits[0].Hwnd;
                    }
                }
            }

            if (h == null) return Task.FromResult(Error(OutcomeKind.Malformed, "Target window not found. Specify hwnd, process, or titleContains."));
            return Act(new AgentAction(AgentActionKind.FocusWindow, Hwnd: h), ct);
        });
    }
}
