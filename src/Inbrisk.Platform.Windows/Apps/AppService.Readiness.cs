using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Apps;

public sealed partial class AppService : IAppService, IDisposable
{
    /// <summary>Grace period after the spawned pid exits for bootstrapper
    /// children/publisher-related successors to appear before the launch is
    /// declared dead.</summary>
    private const long SpawnedDeadGraceMs = 5000;

    private (string? error, string? detail, string? state, int? pid,
        long? hwnd, string? title, string? procName)
        WaitReady(ResolvedApp app, int? spawnedPid, LaunchReadiness readiness,
            int timeoutMs, CancellationToken ct, Stopwatch total,
            string? requestedName = null, DateTime spawnUtc = default,
            string? relatedCompany = null)
    {
        if (readiness == LaunchReadiness.None)
            return (null, null, "Started", spawnedPid, null, null, null);

        using var _waitStage = PerfTrace.Stage("launch.windowWait");
        var deadline = total.ElapsedMilliseconds + timeoutMs;
        var rawHints = new List<string>(app.ExeHints) { app.DisplayName };
        if (!string.IsNullOrEmpty(requestedName))
            rawHints.Add(requestedName);

        foreach (var h in rawHints.ToList())
        {
            if (h.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            {
                var stripped = System.Text.RegularExpressions.Regex.Replace(h, @"[-_]?launcher", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!string.IsNullOrWhiteSpace(stripped)) rawHints.Add(stripped);
            }
            var parts = h.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                if (p.Length >= 3) rawHints.Add(p);
            }
        }

        var hints = rawHints.Select(Norm).Where(s => s.Length > 0).Distinct().ToList();
        var spawnedName = app.Method is LaunchMethod.ExplicitPath
            or LaunchMethod.AppPath or LaunchMethod.Executable
            ? Norm(Path.GetFileNameWithoutExtension(app.Identifier))
            : null;

        WindowInfo? lastSeenWin = null;
        var baselineGen = _eventBuffer?.CurrentGeneration ?? 0;
        var startTicks = Stopwatch.GetTimestamp();
        long timeToFirstWinMs = 0;
        long lastPollTicks = 0;

        // Launch family bookkeeping: a bootstrapper exe (League of Legends,
        // Epic launcher stubs) exits right after spawning the REAL app as a
        // child — or a differently-named same-publisher process. Union every
        // descendant ever observed plus publisher-correlated successors so
        // the handoff counts as this launch's process/window, not a failure.
        var familyPids = new List<int>();
        long? spawnedDeadAt = null;
        int? relatedPid = null;
        long familyProbeTicks = 0;
        void RefreshFamily()
        {
            var now = Environment.TickCount64;
            if (now - familyProbeTicks < 350) return;
            familyProbeTicks = now;
            familyPids.Clear();
            if (spawnedPid is { } sp)
            {
                familyPids.Add(sp);
                foreach (var d in DescendantsOf(sp)) familyPids.Add(d);
            }
            var rp = RelatedStartedAfter(spawnUtc == default
                    ? DateTime.UtcNow : spawnUtc, hints, relatedCompany);
            if (rp != null) { relatedPid = rp; familyPids.Add(rp.Value); }
        }

        while (total.ElapsedMilliseconds < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (readiness == LaunchReadiness.Process)
            {
                if (!ProcessAlive(spawnedPid) && spawnedPid.HasValue)
                    RefreshFamily();
                var familyAlive = familyPids
                    .FirstOrDefault(p => p != spawnedPid.GetValueOrDefault());
                if (ProcessAlive(spawnedPid) ||
                    ProcessByName(hints, spawnedName) ||
                    familyAlive > 0)
                    return (null, null, "ProcessStarted",
                        ProcessAlive(spawnedPid) ? spawnedPid
                            : (familyAlive > 0 ? familyAlive : spawnedPid),
                        null, null, null);
                ct.WaitHandle.WaitOne(Math.Min(150, (int)Math.Max(1, deadline - total.ElapsedMilliseconds)));
                continue;
            }

            // Early detection: process exited before becoming ready
            // Do not fail prematurely if spawnedPid exits for launcher stubs / AUMID / execution aliases.
            var isStubOrAlias = IsLauncherOrStub(app, spawnedName);
            if (spawnedPid.HasValue &&
                (isStubOrAlias || !ProcessAlive(spawnedPid)))
                RefreshFamily();
            if (!isStubOrAlias && spawnedPid.HasValue && !ProcessAlive(spawnedPid) && !ProcessByName(hints, spawnedName))
            {
                var winImmediate = MatchWindow(spawnedPid, hints, spawnedName,
                    app.DisplayName, requestedName, familyPids);
                var familyAliveNow = familyPids
                    .Any(p => p != spawnedPid.Value);
                if (winImmediate == null && !familyAliveNow)
                {
                    // Bootstrapper children can materialize a beat after the
                    // parent exits — hold a short grace before declaring the
                    // spawn dead (the plain "spawn died, nothing came up"
                    // Steam-game-without-Steam path still fails, just after
                    // the grace/deadline instead of instantly).
                    spawnedDeadAt ??= Environment.TickCount64;
                    if (Environment.TickCount64 - spawnedDeadAt.Value >=
                        SpawnedDeadGraceMs)
                    {
                        return ("Failed",
                            $"process {spawnedPid.Value} exited before becoming ready",
                            "Failed", spawnedPid, null, null, null);
                    }
                }
                else spawnedDeadAt = null;
            }

            bool eventWoke = false;
            if (_eventBuffer != null)
            {
                var remaining = (int)Math.Max(1, Math.Min(150, deadline - total.ElapsedMilliseconds));
                if (_eventBuffer.WaitForNextEvent(baselineGen, remaining, ct))
                {
                    baselineGen = _eventBuffer.CurrentGeneration;
                    var recents = _eventBuffer.Snapshot(10);
                    if (recents.Any(e => e.Kind is EventKind.WindowOpened or EventKind.WindowShown or EventKind.ForegroundChanged))
                    {
                        eventWoke = true;
                        Telemetry.IncLaunchEventWake();
                    }
                }
            }
            else
            {
                ct.WaitHandle.WaitOne(Math.Min(250, (int)Math.Max(1, deadline - total.ElapsedMilliseconds)));
            }

            var nowTicks = Environment.TickCount64;
            if (!eventWoke)
            {
                if (_eventBuffer != null && lastPollTicks != 0 &&
                    nowTicks - lastPollTicks < FallbackPollMs)
                    continue;
                Telemetry.IncLaunchFallbackPoll();
            }
            lastPollTicks = nowTicks;

            var win = MatchWindow(spawnedPid, hints, spawnedName,
                app.DisplayName, requestedName, familyPids);
            if (win != null)
            {
                Telemetry.IncLaunchCandidate();
                if (timeToFirstWinMs == 0)
                {
                    timeToFirstWinMs = (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
                    Telemetry.SetLaunchTimeToFirstWindowMs(timeToFirstWinMs);
                }

                if (IsSplashOrHelperWindow(win))
                {
                    Telemetry.IncLaunchCandidateRejected();
                }
                else
                {
                    lastSeenWin = win;
                    Telemetry.IncLaunchProbe();
                    var probeOk = false;
                    try { probeOk = _uiaProbe?.Invoke(win.Hwnd) ?? true; } catch { }
                    if (!IsHung(win.Hwnd) && probeOk)
                    {
                        var readyMs = (long)((Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency);
                        Telemetry.SetLaunchReadyMs(readyMs);
                        if (eventWoke) Telemetry.IncLaunchEventPath();
                        else Telemetry.IncLaunchFallbackPath();

                        return (null, null, "Ready", win.Pid, win.Hwnd, win.Title,
                            win.ProcessName);
                    }
                }
            }
        }

        // If a matching top-level window was observed at any point during wait,
        // treat it as Ready rather than timing out (heavy applications like Blender,
        // Visual Studio, or game engines may be compiling shaders or showing a splash screen)
        if (lastSeenWin != null)
        {
            return (null, null, "Ready", lastSeenWin.Pid, lastSeenWin.Hwnd,
                lastSeenWin.Title, lastSeenWin.ProcessName);
        }

        // Final check right at the deadline
        RefreshFamily();
        var finalWin = MatchWindow(spawnedPid, hints, spawnedName,
            app.DisplayName, requestedName, familyPids);
        if (finalWin != null)
        {
            return (null, null, "Ready", finalWin.Pid, finalWin.Hwnd,
                finalWin.Title, finalWin.ProcessName);
        }

        // A spawned process that died and left NO live descendants, no
        // publisher-correlated successor and no matching window was a dead
        // spawn (e.g. a game bootstrapper with its launcher absent) — report
        // the real failure, not a misleading "still initializing" timeout.
        if (spawnedPid.HasValue && !ProcessAlive(spawnedPid) &&
            !ProcessByName(hints, spawnedName) &&
            !familyPids.Any(p => p != spawnedPid.Value))
        {
            return ("Failed",
                $"process {spawnedPid.Value} exited before becoming ready",
                "Failed", spawnedPid, null, null, null);
        }

        // debugging telemetry: what windows were visible at the deadline
        var seen = SnapshotWindows()
            .Where(w => !w.Bounds.IsEmpty)
            .Select(w => $"{w.ProcessName} \"{w.Title}\" pid={w.Pid}")
            .Take(12);

        var isProcAlive = ProcessAlive(spawnedPid) || ProcessByName(hints, spawnedName);
        if (isProcAlive && readiness == LaunchReadiness.Window)
        {
            return ("Timeout",
                $"launched '{app.DisplayName}' via {app.Method} (pid={spawnedPid}) and process is actively running, but has no visible top-level window within {timeoutMs}ms (application may still be initializing heavy assets or minimized to system tray). Do not re-launch immediately; inspect or observe the application. Windows visible: {string.Join(" | ", seen)}",
                "TimedOut", spawnedPid, null, null, null);
        }

        return ("Timeout",
            $"launched '{app.DisplayName}' via {app.Method} but no " +
            $"{(readiness == LaunchReadiness.Window ? "usable window" : "process")} " +
            $"within {timeoutMs}ms — windows: {string.Join(" | ", seen)}",
            "TimedOut", spawnedPid, null, null, null);
    }

    private static bool IsSplashOrHelperWindow(WindowInfo win)
    {
        var t = win.Title?.ToLowerInvariant() ?? "";
        if (t.Contains("splash") || t.Contains("loading...") || t.Contains("initializing"))
            return true;
        if (win.Bounds.Width <= 10 && win.Bounds.Height <= 10)
            return true;
        return false;
    }

    private WindowInfo? MatchWindow(int? pid, List<string> hints,
        string? spawnedName, string displayName, string? requestedName = null,
        IReadOnlyCollection<int>? familyPids = null)
    {
        var wins = SnapshotWindows()
            .Where(w => !w.Bounds.IsEmpty && !string.IsNullOrEmpty(w.Title) &&
                        !IsShellSurface(w.Hwnd))
            .ToList();

        var norm = Norm(displayName);
        var reqNorm = requestedName != null ? Norm(requestedName) : null;

        bool MatchesFrameWindow(WindowInfo w)
        {
            if (NormProc(w.ProcessName) != "applicationframehost") return false;
            var winTitle = Norm(w.Title);
            if (string.IsNullOrEmpty(winTitle)) return false;

            // 1. Direct title match against norm or requested name
            if (winTitle.Contains(norm) || (norm.Length >= 4 && norm.Contains(winTitle))) return true;
            if (reqNorm != null && (winTitle.Contains(reqNorm) || (reqNorm.Length >= 4 && reqNorm.Contains(winTitle)))) return true;
            if (hints.Any(h => h.Length >= 3 && (winTitle.Contains(h) || h.Contains(winTitle)))) return true;

            // 2. Localized aliases matching against title
            var allKeys = new List<string> { norm };
            if (reqNorm != null) allKeys.Add(reqNorm);
            allKeys.AddRange(hints);

            foreach (var key in allKeys)
            {
                if (WellKnownLocalizedAliases.TryGetValue(key, out var aliases))
                {
                    if (aliases.Any(a => winTitle.Contains(Norm(a)) || Norm(a).Contains(winTitle)))
                        return true;
                }
            }

            foreach (var kvp in WellKnownLocalizedAliases)
            {
                var aliasKey = kvp.Key;
                if (allKeys.Any(k => k.Contains(aliasKey) || aliasKey.Contains(k)))
                {
                    if (kvp.Value.Any(a => winTitle.Contains(Norm(a)) || Norm(a).Contains(winTitle)))
                        return true;
                }
            }

            return false;
        }

        // The launch "family": the spawned pid plus any live descendant /
        // publisher-correlated successor — a bootstrapper's child owns the
        // window, not the exe we spawned.
        var pidSet = new HashSet<int>();
        if (pid is { } p) pidSet.Add(p);
        if (familyPids != null) pidSet.UnionWith(familyPids);
        if (pidSet.Count > 0)
        {
            var byPid = wins.Where(w => pidSet.Contains(w.Pid)).ToList();
            if (byPid.Count > 0) return BestWindow(byPid);

            // Exact UWP attribution or frame matching by title / localized aliases without relying on EnumChildWindows
            var hosted = wins
                .Where(w => NormProc(w.ProcessName) == "applicationframehost" &&
                            (pidSet.Contains(HostedProcessId(w.Hwnd)) ||
                             MatchesFrameWindow(w)))
                .ToList();
            if (hosted.Count > 0 &&
                BestWindow(hosted) is { } frame)
                return frame with { Pid = pid ?? frame.Pid };

            // spawned pid may be a launcher that exited or handed off —
            // fall through to name/title matching
        }
        var named = wins.Where(w =>
        {
            var pn = NormProc(w.ProcessName);
            // 1. Direct process match against spawned name, requested name, or hints
            if (spawnedName != null && pn == spawnedName) return true;
            if (reqNorm != null && (pn == reqNorm || pn.StartsWith(reqNorm) || reqNorm.StartsWith(pn))) return true;
            if (hints.Count > 0 && (hints.Contains(pn) || hints.Any(h => h.Length >= 4 && (h.StartsWith(pn) || pn.StartsWith(h))))) return true;
            if (pn == norm || (norm.Length >= 4 && (norm.StartsWith(pn) || pn.StartsWith(norm)))) return true;

            // 2. Title matching fallback. A process that embeds document/
            //    tab/chat titles (IDE, terminal, editor, the agent host) can
            //    contain the requested name mid-title without being the app
            //    — for those a word-boundary position match is required.
            var docHost = DocumentTitleHosts.Contains(pn);
            bool TitleHit(string normTerm)
                => normTerm.Length >= 3 && (docHost
                    ? TitleBoundaryMatch(w.Title, normTerm)
                    : Norm(w.Title).Contains(normTerm));
            if (TitleHit(norm) || (reqNorm != null && TitleHit(reqNorm))) return true;
            if (hints.Any(h => h.Length >= 4 && (TitleHit(h) || pn.Contains(h)))) return true;

            // 3. UWP / ApplicationFrameHost special handling without relying on EnumChildWindows
            if (pn == "applicationframehost")
            {
                if (MatchesFrameWindow(w)) return true;
            }

            // 4. Localized aliases fallback for regular windows — same
            //    document-host guard on the title side (process-name side
            //    stays a real match).
            var winTitle = Norm(w.Title);
            if (reqNorm != null && WellKnownLocalizedAliases.TryGetValue(reqNorm, out var rAliases) &&
                rAliases.Any(a => TitleHit(Norm(a)) || pn.Contains(Norm(a)))) return true;
            if (WellKnownLocalizedAliases.TryGetValue(norm, out var nAliases) &&
                nAliases.Any(a => TitleHit(Norm(a)) || pn.Contains(Norm(a)))) return true;

            return false;
        }).ToList();

        var best = BestWindow(named);
        if (best != null && pid.HasValue && NormProc(best.ProcessName) == "applicationframehost")
            return best with { Pid = pid.Value };
        return best;
    }

    private static WindowInfo? BestWindow(List<WindowInfo> wins)
        => wins
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.Bounds.Width * w.Bounds.Height)
            .ThenBy(w => w.Hwnd)
            .FirstOrDefault();

    private bool ProcessAlive(int? pid)
    {
        if (pid is not { } p) return false;
        if (ProcessAliveChecker != null) return ProcessAliveChecker(p);
        if (Spawner != null) return true;
        try { return !Process.GetProcessById(p).HasExited; }
        catch { return false; }
    }

    private bool ProcessByName(List<string> hints, string? spawnedName)
    {
        if (ProcessAliveChecker != null) return false;
        var names = ProcessNameSnapshot();
        if (names == null) return false;
        return (spawnedName != null && names.Contains(spawnedName)) ||
            names.Overlaps(hints);
    }

    /// <summary>Live descendant processes of <paramref name="rootPid"/>,
    /// resolved through the Toolhelp parent-chain — works even after
    /// <paramref name="rootPid"/> itself exited (a child's recorded parent
    /// pid is immutable), which is exactly the bootstrapper case: League of
    /// Legends' launcher exe dies while its RiotClientServices child keeps
    /// running. Returns an empty list when the chain has no survivors.</summary>
    internal IReadOnlyList<int> DescendantsOf(int rootPid)
    {
        if (DescendantProbe != null)
            return DescendantProbe(rootPid) ?? [];
        var parentOf = ProcessParentMap();
        var found = new List<int>();
        foreach (var pid in parentOf.Keys)
        {
            var cur = pid;
            var hops = 0;
            while (hops++ < 32 && parentOf.TryGetValue(cur, out var parent) &&
                   parent > 0)
            {
                if (parent == rootPid) { found.Add(pid); break; }
                cur = parent;
            }
        }
        return found;
    }

    /// <summary>First live descendant of <paramref name="rootPid"/>, or
    /// null. Callers re-probe while polling because bootstrapper children
    /// materialize a beat after the parent exits.</summary>
    private int? LiveDescendantPid(int rootPid)
        => DescendantsOf(rootPid).FirstOrDefault(p => p > 0) is { } d && d > 0
            ? d : null;

    /// <summary>pid → parentPid for every process in a fresh Toolhelp
    /// snapshot. A dead parent still appears as the VALUE — which is what
    /// lets descendants of an exited bootstrapper resolve.</summary>
    private static Dictionary<int, int> ProcessParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = Native.NativeMethods.CreateToolhelp32Snapshot(
            Native.NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new Native.NativeMethods.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<
                    Native.NativeMethods.PROCESSENTRY32W>()
            };
            if (Native.NativeMethods.Process32FirstW(snap, ref e))
            {
                do
                {
                    map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
                } while (Native.NativeMethods.Process32NextW(snap, ref e));
            }
        }
        catch { }
        finally { Native.NativeMethods.CloseHandle(snap); }
        return map;
    }

    /// <summary>A process that started AFTER the launch began and correlates
    /// to the target — same normalized process name as a hint, or (the
    /// bootstrapper-redirect case) the same PE publisher as the spawned exe
    /// (Riot Games launcher dying into RiotClientServices). Processes that
    /// merely pre-existed (a Steam that was already running) never qualify:
    /// their start time precedes <paramref name="sinceUtc"/>.</summary>
    private int? RelatedStartedAfter(DateTime sinceUtc,
        IReadOnlyList<string> nameHints, string? companyNorm)
    {
        if (RelatedProcessProbe != null)
            return RelatedProcessProbe(sinceUtc, nameHints, companyNorm);
        if (ProcessAliveChecker != null) return null; // synthetic test env
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    DateTime started;
                    try { started = p.StartTime.ToUniversalTime(); }
                    catch { continue; }
                    if (started < sinceUtc.AddSeconds(-2)) continue;
                    var pn = NormProc(p.ProcessName);
                    if (nameHints.Contains(pn)) return p.Id;
                    if (companyNorm == null) continue;
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (path == null) continue;
                        var company = FileVersionInfo.GetVersionInfo(path)
                            .CompanyName;
                        if (!string.IsNullOrWhiteSpace(company) &&
                            Norm(company) == companyNorm)
                            return p.Id;
                    }
                    catch { /* protected/foreign-bitness process */ }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Normalized publisher (CompanyName) of the resolved launch
    /// target — the correlation key that lets a bootstrapper's differently
    /// named successor (leagueoflegends.exe → RiotClientServices.exe, both
    /// "Riot Games, Inc.") count as the launched app. null when the target
    /// is not a local exe or its version info is unreadable.</summary>
    private static string? RelatedCompanyNorm(ResolvedApp app)
    {
        try
        {
            if (app.Method is not (LaunchMethod.ExplicitPath
                or LaunchMethod.AppPath or LaunchMethod.Executable
                or LaunchMethod.StartMenu))
                return null;
            var target = ResolveLaunchTarget(app);
            if (target == null ||
                !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(target))
                return null;
            var company = FileVersionInfo.GetVersionInfo(target).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : Norm(company);
        }
        catch { return null; }
    }

    /// <summary>Normalized process-name set, refreshed at most every 250 ms
    /// — GetProcesses() per 150 ms poll slice was the wait path's hot spot.</summary>
    private HashSet<string>? ProcessNameSnapshot()
    {
        var now = Environment.TickCount64;
        lock (_procNameLock)
        {
            if (_procNameSnapshot != null &&
                now - _procNameTicks < ProcNameTtlMs)
                return _procNameSnapshot;
            try
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var p in Process.GetProcesses())
                {
                    set.Add(NormProc(p.ProcessName));
                    p.Dispose();
                }
                _procNameSnapshot = set;
                _procNameTicks = now;
                return set;
            }
            catch { return null; }
        }
    }

    /// <summary>Top-level window list with a short TTL keyed to the
    /// event-buffer generation — any window event invalidates immediately,
    /// so bursts and repeated polls share one EnumWindows. Without an
    /// event source the cache is bypassed entirely (exact for tests).</summary>
    private IReadOnlyList<WindowInfo> SnapshotWindows()
    {
        if (_eventBuffer == null) return _windows.ListWindows();
        var gen = _eventBuffer.CurrentGeneration;
        var now = Environment.TickCount64;
        lock (_windowSnapshotLock)
        {
            if (_windowSnapshot != null &&
                gen == _windowSnapshotGen &&
                now - _windowSnapshotTicks < WindowSnapshotTtlMs)
                return _windowSnapshot;
            var wins = _windows.ListWindows();
            _windowSnapshot = wins;
            _windowSnapshotGen = gen;
            _windowSnapshotTicks = now;
            return wins;
        }
    }

    /// <summary>The real application pid hosted inside an
    /// ApplicationFrameHost frame window — the first descendant owned by a
    /// different process (the packaged app's content window). 0 when the
    /// frame hosts nothing.</summary>
    private static int HostedProcessId(long frameHwnd)
    {
        var frame = new IntPtr(frameHwnd);
        Native.NativeMethods.GetWindowThreadProcessId(frame, out var framePid);
        var hosted = 0;
        try
        {
            EnumChildWindows(frame, (child, _) =>
            {
                Native.NativeMethods.GetWindowThreadProcessId(child, out var pid);
                if (pid != 0 && pid != framePid) { hosted = pid; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return hosted;
    }

    private static bool DebugPortReachable(int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
            using var resp = http
                .GetAsync($"http://127.0.0.1:{port}/json/version")
                .GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static LaunchReadiness? ParseReadiness(string? waitFor)
        => (waitFor ?? "window").ToLowerInvariant() switch
        {
            "window" => LaunchReadiness.Window,
            "process" => LaunchReadiness.Process,
            "none" => LaunchReadiness.None,
            _ => null,
        };

    private static LaunchResult Fail(string error, string? detail,
        Stopwatch total, string? resolvedName = null, string? identifier = null,
        int? pid = null, string? state = null)
        => new(false, null, resolvedName, identifier, pid, null, null,
            state ?? "Failed", total.ElapsedMilliseconds, 0, error, detail);

    private static LaunchResult Done(LaunchMethod method, string name,
        string? procName, int? pid, long? hwnd, string? title, string state,
        Stopwatch total, long readyMs, int? spawnedPid = null,
        string? identifier = null)
        => new(true, method, name, identifier, pid ?? spawnedPid, hwnd, title,
            state, total.ElapsedMilliseconds - readyMs, readyMs, null);

    // ------------------------------------------------------------------
    //  interop
    // ------------------------------------------------------------------

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent,
        EnumChildProc lpEnumFunc, IntPtr lParam);

    private static bool IsHung(long hwnd)
    {
        try { return IsHungAppWindow(new IntPtr(hwnd)); }
        catch { return false; }
    }
}
