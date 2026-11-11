using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Windows-native application resolution + launch.
///
/// Friendly-name pipeline (deterministic order):
///   existing top-level window/process  (unless newInstance)
///   → Start Menu shortcuts (*.lnk)
///   → App Paths registry (HKLM/HKCU)
///   → packaged/Store apps (AppModel repository → AUMID)
///   → executable on PATH / per-user WindowsApps alias dir
///   → registered URI scheme (only when actually registered)
///
/// A friendly name that resolves to several DISTINCT applications is
/// ambiguous — launch refuses to guess. The same app reachable through
/// two mechanisms is not ambiguous (pipeline order picks the best).
///
/// Readiness is separate from spawn: launch returns only after the
/// requested signal — a real top-level window (UIA-reachable, not hung),
/// a live process, or nothing. No fixed sleeps; bounded polling.
/// </summary>
public sealed class AppService : IAppService, IDisposable
{
    /// <summary>Interpreters a launch must never target — computer_launch is
    /// an app launcher, not a shell. Blocking here means arguments can never
    /// be smuggled into `cmd /c`, `powershell -Command`, mshta, rundll32…</summary>
    internal static readonly HashSet<string> ShellHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wscript", "wscript.exe", "cscript", "cscript.exe", "mshta",
        "mshta.exe", "rundll32", "rundll32.exe", "regsvr32", "regsvr32.exe",
        "wmic", "wmic.exe", "msiexec", "msiexec.exe",
        "conhost", "conhost.exe", "bash", "bash.exe",
        "sh", "sh.exe", "zsh", "zsh.exe", "fish", "fish.exe",
        "nu", "nu.exe", "wt", "wt.exe",
        "windowsterminal", "windowsterminal.exe",
        "wsl", "wsl.exe", "curl", "curl.exe",
        "python", "python.exe", "pythonw", "pythonw.exe",
        "node", "node.exe",
    };

    internal static bool IsShellHost(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var stem = Path.GetFileNameWithoutExtension(name.Trim().Trim('"'));
        if (ShellHosts.Contains(stem) || ShellHosts.Contains(stem + ".exe"))
            return true;
        var n = Norm(stem);
        return n.Length > 0 &&
            System.Text.RegularExpressions.Regex.IsMatch(n, @"^pythonw?\d*$");
    }

    /// <summary>Optional consent hook consulted for unsigned executables
    /// when env/settings consent is absent — hosts with a local approval
    /// surface (the MCP ConfirmChannel) wire this; it must register a
    /// pending request on refusal and return true only for an approved,
    /// not-yet-consumed pending (one-shot).</summary>
    public Func<string, bool>? UnsignedBinaryConsent;

    private readonly IWindowService _windows;
    private readonly Func<long, bool>? _uiaProbe;
    private readonly Func<string, bool> _isDeniedProcess;
    /// <summary>Session reaper bookkeeping — every pid WE spawn is recorded
    /// here so session teardown can close it. Reused/existing instances are
    /// never tracked (the spawn call returns no pid for them).</summary>
    private readonly SessionProcessTracker? _processTracker;

    // test seams — production uses the real spawn/registry path
    internal Func<ResolvedApp, IReadOnlyList<string>?, int?>? Spawner;
    internal Func<IReadOnlyList<ResolvedApp>>? PackageEnumerator;
    internal Func<int, bool>? ProcessAliveChecker;
    /// <summary>Test seam for descendant discovery — production walks the
    /// real Toolhelp process table; tests inject pid→children.</summary>
    internal Func<int, IReadOnlyList<int>>? DescendantProbe;
    /// <summary>Test seam for the "related process started after launch"
    /// probe (publisher-correlated bootstrapper handoff).</summary>
    internal Func<DateTime, IReadOnlyList<string>, string?, int?>? RelatedProcessProbe;

    /// <summary>Provenance sink — the runtime installs its
    /// IProcessProvenanceService here (constructor arg or post-hoc
    /// assignment; <see cref="AmbientProvenance"/> is the process-wide
    /// fallback). When set, every pid a launch verifiably created is
    /// registered as agent-owned so a later close is never PolicyDenied for
    /// something THIS call spawned.</summary>
    public IProcessProvenanceService? Provenance { get; set; }
    /// <summary>Process-wide fallback provenance sink, mirroring
    /// SessionProcessTracker.Ambient — lets hosts wire launch ownership
    /// without touching the AppService constructor signature.</summary>
    public static IProcessProvenanceService? AmbientProvenance { get; set; }

    public sealed record ResolvedApp(LaunchMethod Method, string Identifier,
        string DisplayName, string[] ExeHints, int Score);

    private static readonly TimeSpan AppsCacheTtl = TimeSpan.FromMinutes(10);
    private IReadOnlyList<AppInfo>? _cachedApps;
    private DateTime _appsCacheTime = DateTime.MinValue;
    private readonly object _appsLock = new();

    private static readonly TimeSpan PackagesCacheTtl = TimeSpan.FromMinutes(10);
    private IReadOnlyList<ResolvedApp>? _cachedPackages;
    private DateTime _packagesCacheTime = DateTime.MinValue;
    private readonly object _packagesLock = new();

    // Launch-wait caches: a top-level-window snapshot invalidated by ANY
    // event-buffer generation bump (window events) plus a short TTL, and a
    // process-name set refreshed at most every 250ms — a full EnumWindows /
    // GetProcesses on every poll slice is what made waits expensive.
    private const long WindowSnapshotTtlMs = 120;
    private const long FallbackPollMs = 500;
    private IReadOnlyList<WindowInfo>? _windowSnapshot;
    private long _windowSnapshotGen = -1;
    private long _windowSnapshotTicks;
    private readonly object _windowSnapshotLock = new();

    private const long ProcNameTtlMs = 250;
    private HashSet<string>? _procNameSnapshot;
    private long _procNameTicks;
    private readonly object _procNameLock = new();

    public static int CatalogFullScanCount;
    public static int MemoryCacheHitCount;
    public static int FilesystemEntriesScanned;
    public static int ResolveCacheHitCount;
    public static int NegativeCacheHitCount;

    public static void ResetTelemetry()
    {
        CatalogFullScanCount = 0;
        MemoryCacheHitCount = 0;
        FilesystemEntriesScanned = 0;
        ResolveCacheHitCount = 0;
        NegativeCacheHitCount = 0;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk");
            var file = Path.Combine(dir, "app-catalog.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<ResolvedApp> Apps, DateTime Timestamp)> _resolveCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEventWaiter? _eventBuffer;
    public ScopedSubscriptionTelemetry Telemetry { get; }

    private readonly ApplicationCatalogService _catalogService;
    private readonly LaunchResolutionCache _launchCache;

    public ApplicationCatalogService CatalogService => _catalogService;
    public LaunchResolutionCache LaunchCache => _launchCache;
    public ApplicationCatalogTelemetry CatalogTelemetry => _catalogService.Telemetry;

    public AppService(IWindowService windows, Func<long, bool>? uiaProbe = null,
        Func<string, bool>? isDeniedProcess = null,
        IEventWaiter? eventBuffer = null,
        ScopedSubscriptionTelemetry? telemetry = null,
        ApplicationCatalogService? catalogService = null,
        LaunchResolutionCache? launchCache = null,
        string? storageDirectory = null,
        Func<DateTimeOffset>? clock = null,
        SessionProcessTracker? processTracker = null)
    {
        _windows = windows;
        _processTracker = processTracker;
        _uiaProbe = uiaProbe;
        _isDeniedProcess = isDeniedProcess ?? (_ => false);
        _eventBuffer = eventBuffer;
        Telemetry = telemetry ?? new ScopedSubscriptionTelemetry();

        _catalogService = catalogService ?? new ApplicationCatalogService(
            storageDirectory: storageDirectory,
            clock: clock,
            telemetry: new ApplicationCatalogTelemetry(),
            enumerator: EnumerateAuthoritativeCatalog,
            startMenuDirs: StartMenuDirs());

        _launchCache = launchCache ?? new LaunchResolutionCache(
            _catalogService,
            storageDirectory: storageDirectory,
            clock: clock,
            telemetry: _catalogService.Telemetry);

        // Background cold build: the first launch resolves from a warm
        // snapshot instead of paying a synchronous filesystem/registry/
        // WinRT scan. GetSnapshot is single-flight — a launch landing
        // mid-build awaits the same refresh, never a duplicate scan.
        _ = Task.Run(() =>
        {
            try { _catalogService.GetSnapshot(); }
            catch { /* warm-up is best-effort; resolution falls back */ }
        });
    }

    internal void InvalidateCache()
    {
        lock (_appsLock) _cachedApps = null;
        lock (_packagesLock) _cachedPackages = null;
        lock (_windowSnapshotLock) _windowSnapshot = null;
        lock (_procNameLock) _procNameSnapshot = null;
        _resolveCache.Clear();
        _catalogService.Invalidate();
        _launchCache.InvalidateAll();
    }

    public void Dispose()
    {
        _catalogService.Dispose();
        _launchCache.Dispose();
    }

    // ------------------------------------------------------------------
    //  public surface
    // ------------------------------------------------------------------

    public LaunchResult Launch(LaunchSpec spec, CancellationToken ct = default)
    {
        // Self-contained perf record: computer_launch is not wrapped by a
        // PerfTrace at the MCP layer, so begin one only when the launch is
        // not already inside a trace (e.g. a computer_run step).
        var ownTrace = PerfTrace.TryCurrent == null
            ? PerfTrace.Begin("computer_launch",
                spec.App ?? spec.Executable ?? spec.Path ?? spec.Aumid ??
                spec.Uri ?? "app")
            : null;
        var timings = new LaunchTimings();
        var wall = Stopwatch.StartNew();
        LaunchResult? result = null;
        try
        {
            result = LaunchCore(spec, ct, timings);
            return result;
        }
        finally
        {
            timings.TotalMs = wall.ElapsedMilliseconds;
            EmitLaunchPerf(spec, result, timings);
            ownTrace?.Dispose();
        }
    }

    /// <summary>Per-call launch stage timings — emitted as one "launch"
    /// record into perf-trace.jsonl so p50 before/after is measurable.</summary>
    private sealed class LaunchTimings
    {
        public long ResolveMs;
        public long SpawnMs;
        public long WindowWaitMs;
        public long TotalMs;
    }

    private void EmitLaunchPerf(LaunchSpec spec, LaunchResult? r,
        LaunchTimings t)
    {
        try
        {
            var cur = PerfTrace.TryCurrent;
            if (cur != null)
            {
                cur.Set("launch.resolveMs", t.ResolveMs);
                cur.Set("launch.spawnMs", t.SpawnMs);
                cur.Set("launch.windowWaitMs", t.WindowWaitMs);
                cur.Set("launch.totalMs", t.TotalMs);
                cur.Set("launch.state", r?.LaunchState);
                cur.Set("launch.method", r?.Method?.ToString());
            }
            PerfLog.Write(new
            {
                kind = "launch",
                at = DateTimeOffset.Now,
                app = r?.ResolvedName ?? spec.App ?? spec.Executable ??
                    spec.Path ?? spec.Aumid ?? spec.Uri,
                method = r?.Method?.ToString(),
                identifier = r?.ResolvedIdentifier,
                state = r?.LaunchState,
                success = r?.Success ?? false,
                error = r?.Error,
                pid = r?.Pid,
                reused = r?.LaunchState == "AlreadyRunning",
                resolveMs = t.ResolveMs,
                spawnMs = t.SpawnMs,
                windowWaitMs = t.WindowWaitMs,
                launchMs = r?.LaunchMs,
                readyMs = r?.ReadyMs,
                totalMs = t.TotalMs,
            });
        }
        catch { /* telemetry must never break a launch */ }
    }

    private LaunchResult LaunchCore(LaunchSpec spec, CancellationToken ct,
        LaunchTimings timings)
    {
        ct.ThrowIfCancellationRequested();
        var total = Stopwatch.StartNew();
        var readiness = ParseReadiness(spec.WaitFor);
        if (readiness == null)
            return Fail("Malformed", "waitFor must be window|process|none", total);

        if (spec.Arguments is { Count: > 0 } &&
            (spec.Aumid != null || spec.Uri != null))
            return Fail("Malformed",
                "arguments only apply to executable launches (app/executable/path)", total);

        // A requested CDP port that is ALREADY live means a debug-enabled
        // browser is running — instance reuse is safe. When the port is
        // dead, the running browser cannot serve DevTools, so reuse checks
        // below stay disabled and we spawn a dedicated debug instance.
        var debugReady = spec.DebugPort.HasValue &&
            DebugPortReachable(spec.DebugPort.Value);

        // ---- explicit identifiers resolve directly ----
        var resolved = ResolveDirect(spec, out var directErr);
        if (directErr != null)
        {
            var kind = directErr.Split(':', 2);
            return Fail(kind[0], kind.Length > 1 ? kind[1].Trim() : null,
                total);
        }
        ResolvedApp? chosen = null;

        if (resolved != null)
        {
            chosen = resolved;
        }
        else if (spec.App != null)
        {
            // a friendly name that itself denotes a shell is denied before
            // any resolution or running-instance reuse
            if (IsShellHost(spec.App) ||
                _isDeniedProcess(Norm(spec.App)))
                return Fail("PolicyDenied",
                    $"refusing to launch '{spec.App}' — computer_launch is " +
                    "not a shell; use executable UI applications", total);

            // ---- friendly name → existing instance first ----
            if (!spec.NewInstance && (!spec.DebugPort.HasValue || debugReady) &&
                FindRunning(spec.App, null) is { } existing)
            {
                var swProbe = Stopwatch.StartNew();
                Telemetry.IncLaunchProbe();
                bool isProbeReady = false;
                try
                {
                    isProbeReady = _uiaProbe?.Invoke(existing.Hwnd) ?? true;
                }
                catch { }
                swProbe.Stop();
                long readyMs = Math.Max(1, swProbe.ElapsedMilliseconds);
                Telemetry.SetLaunchReadyMs(readyMs);

                if (isProbeReady)
                {
                    return Done(LaunchMethod.ExistingInstance, spec.App,
                        existing.ProcessName, existing.Pid, existing.Hwnd,
                        existing.Title, "AlreadyRunning", total, readyMs: readyMs);
                }
                else
                {
                    return Fail("ProbeFailure", $"Existing window 0x{existing.Hwnd:X} failed UIA readiness probe", total,
                        resolvedName: spec.App, pid: existing.Pid, state: "NotReady");
                }
            }

            var (cachedBest, isAmbiguous, ambiguousCandidates) = _launchCache.GetOrResolve(
                spec.App,
                exactOnly: false,
                newInstance: spec.NewInstance,
                resolver: () =>
                {
                    var all = ResolveAll(spec.App);
                    var picked = PickBest(all, out var ambig);
                    return (picked, ambig != null, ambig);
                },
                arguments: spec.Arguments);

            if (isAmbiguous && ambiguousCandidates != null)
                return new LaunchResult(false, null, spec.App, null, null,
                    null, null, "Failed", total.ElapsedMilliseconds, 0,
                    "AmbiguousApplication",
                    $"'{spec.App}' matches several applications — refine " +
                    "(use executable:/path:/aumid: or a more specific name)",
                    ambiguousCandidates.Select(c => new AppCandidate(c.DisplayName,
                        c.Method, c.Identifier, c.Score)).ToList());

            if (cachedBest == null)
            {
                // "open github.com" gets routed here constantly — a URL is
                // not an app; point the model at the right tool instead of
                // a bare not-found.
                var looksLikeUrl = spec.App!.Contains("://") ||
                    Uri.TryCreate(spec.App, UriKind.Absolute, out var u) &&
                    u.Host.Contains('.') ||
                    System.Text.RegularExpressions.Regex.IsMatch(spec.App!,
                        @"^[\w\-]+(\.[\w\-]+)+(/|$)");
                return Fail("TargetNotFound",
                    $"no application resolvable as '{spec.App}' " +
                    "(checked running windows, Start Menu, App Paths, " +
                    "packaged apps, PATH)" +
                    (looksLikeUrl
                        ? " — that looks like a URL, not an app: use " +
                          "browser_browse{url} to open it in the browser"
                        : ""), total);
            }
            chosen = cachedBest;
        }
        else
        {
            return Fail("Malformed",
                "one of app/executable/path/aumid/uri is required", total);
        }

        // ---- safety: launch must never be a shell in disguise ----
        // This check precedes running-instance reuse on purpose: an already
        // running powershell/cmd window must not launder a denied target
        // into an "AlreadyRunning" success.
        foreach (var hint in chosen.ExeHints.Append(
                     Path.GetFileNameWithoutExtension(chosen.Identifier)))
            if (hint != null && (IsShellHost(hint) ||
                                 _isDeniedProcess(hint)))
                return Fail("PolicyDenied",
                    $"refusing to launch '{hint}' — computer_launch is not a " +
                    "shell; use executable UI applications", total);

        if (ResolveLaunchTarget(chosen) is { } launchTarget)
        {
            var identity = TargetIdentity(launchTarget);
            if (IsShellHost(launchTarget) || IsShellHost(identity))
                return Fail("PolicyDenied",
                    $"refusing to launch " +
                    $"'{identity ?? Path.GetFileName(launchTarget)}' — " +
                    "computer_launch is not a shell; use executable UI " +
                    "applications", total,
                    resolvedName: chosen.DisplayName,
                    identifier: chosen.Identifier);

            if (chosen.Method is LaunchMethod.ExplicitPath
                    or LaunchMethod.Executable &&
                LooksLikePe(launchTarget) &&
                !IsSignedBinary(launchTarget))
            {
                if (!LaunchPathGate.ConsentGranted(out var consentSrc) &&
                    !LaunchAllowListed(launchTarget) &&
                    !(UnsignedBinaryConsent?.Invoke(launchTarget) ?? false))
                    return Fail("ConfirmationRequired",
                        $"'{Path.GetFileName(launchTarget)}' is an unsigned " +
                        "binary outside the registered-app catalog — " +
                        "refusing to launch unverifiable code; set " +
                        $"{LaunchPathGate.AllowEnvVar}=1, " +
                        "\"allowScriptLaunch\": true in settings.json, or " +
                        "add the file name to \"allowLaunch\" in " +
                        "settings.json to permit unsigned launches", total,
                        resolvedName: chosen.DisplayName,
                        identifier: chosen.Identifier);
                try
                {
                    PerfLog.Write(new
                    {
                        kind = "launch_unsigned_consent",
                        at = DateTimeOffset.Now,
                        target = launchTarget,
                        via = consentSrc,
                        identifier = chosen.Identifier,
                    });
                }
                catch { /* telemetry must never break a launch */ }
            }
        }

        // ---- reuse a running instance for explicit targets too ----
        // titleMatch stays loose only for friendly names — an explicit
        // exe/path/aumid must match by PROCESS, so "Qwen - BRO3d" can
        // never pose as the app behind BRO3d.uproject
        if (!spec.NewInstance && (!spec.DebugPort.HasValue || debugReady) &&
            chosen.Method != LaunchMethod.Protocol &&
            FindRunning(chosen.DisplayName, chosen.ExeHints,
                titleMatch: spec.App != null) is { } running)
        {
            var swProbe = Stopwatch.StartNew();
            Telemetry.IncLaunchProbe();
            bool isProbeReady = false;
            try
            {
                isProbeReady = _uiaProbe?.Invoke(running.Hwnd) ?? true;
            }
            catch { }
            swProbe.Stop();
            long readyMs = Math.Max(1, swProbe.ElapsedMilliseconds);
            Telemetry.SetLaunchReadyMs(readyMs);

            if (isProbeReady)
            {
                return Done(LaunchMethod.ExistingInstance, chosen.DisplayName,
                    running.ProcessName, running.Pid, running.Hwnd, running.Title,
                    "AlreadyRunning", total, readyMs: readyMs);
            }
            else
            {
                return Fail("ProbeFailure", $"Existing window 0x{running.Hwnd:X} failed UIA readiness probe", total,
                    resolvedName: chosen.DisplayName, pid: running.Pid, state: "NotReady");
            }
        }

        // ---- spawn ----
        var spawnMs = total.ElapsedMilliseconds;
        timings.ResolveMs = spawnMs; // everything before spawn = resolve + reuse check
        int? spawnedPid;
        try
        {
            var effectiveArgs = spec.Arguments != null ? new List<string>(spec.Arguments) : new List<string>();
            if (spec.DebugPort.HasValue)
            {
                effectiveArgs.Add($"--remote-debugging-port={spec.DebugPort.Value}");
                effectiveArgs.Add("--remote-allow-origins=*");
                effectiveArgs.Add("--no-first-run");
                effectiveArgs.Add("--no-default-browser-check");
                effectiveArgs.Add("--profile-directory=Default");
                var profileDir = Path.Combine(Path.GetTempPath(), $"inbrisk_debug_profile_{spec.DebugPort.Value}");
                effectiveArgs.Add($"--user-data-dir={profileDir}");
            }
            var spawnSw = Stopwatch.StartNew();
            using (PerfTrace.Stage("launch.spawn"))
            {
                spawnedPid = (Spawner ?? SpawnReal)(chosen, effectiveArgs.Count > 0 ? effectiveArgs : null);
            }
            timings.SpawnMs = spawnSw.ElapsedMilliseconds;
            // Reaper bookkeeping: only a pid the spawn call itself returned is
            // tracked — pids matched during readiness (e.g. a URI handler that
            // was already running) are never attributed to us. Tracking even
            // covers launches whose readiness wait later fails/times out.
            if (spawnedPid is > 0)
            {
                try
                {
                    _processTracker?.Track(spawnedPid.Value,
                        tool: PerfTrace.TryCurrent is { } tr
                            ? $"{tr.Kind}:{tr.Id}"
                            : "computer_launch");
                }
                catch { /* bookkeeping must never break a launch */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return Fail("Failed",
                $"launch failed via {chosen.Method}: {e.Message}", total,
                resolvedName: chosen.DisplayName, identifier: chosen.Identifier);
        }

        // ---- readiness ----
        var launchStartUtc = DateTime.UtcNow;
        var relatedCompany = RelatedCompanyNorm(chosen);
        var waitSw = Stopwatch.StartNew();
        var ready = WaitReady(chosen, spawnedPid, readiness.Value,
            spec.TimeoutMs, ct, total, spec.App, launchStartUtc,
            relatedCompany);
        timings.WindowWaitMs = waitSw.ElapsedMilliseconds;
        if (ready.error != null)
            return Fail(ready.error, ready.detail, total,
                resolvedName: chosen.DisplayName,
                identifier: chosen.Identifier, pid: ready.pid,
                state: ready.state ?? "Started");
        // "ProcessStarted" proves a spawn, not a surviving process —
        // bootstrapper exes that need a missing launcher (a Steam game exe
        // with no Steam running) exit within a second of Verified. A short
        // settle + liveness check turns that silent loss into a real error.
        // A live DESCENDANT of the spawned pid or a publisher-correlated
        // successor that started after the spawn (leagueoflegends.exe →
        // RiotClientServices.exe) IS the app — the spawn was a bootstrapper
        // handoff, not a failure.
        if (readiness == LaunchReadiness.Process && spawnedPid is > 0 &&
            ProcessAliveChecker == null && Spawner == null)
        {
            var spawnedName = chosen.Method is LaunchMethod.ExplicitPath
                or LaunchMethod.AppPath or LaunchMethod.Executable
                ? Norm(Path.GetFileNameWithoutExtension(chosen.Identifier))
                : null;
            var nameHints = new List<string> { Norm(chosen.DisplayName) };
            nameHints.AddRange(chosen.ExeHints.Select(Norm)
                .Where(h => h.Length > 0));
            var settleUntil = Environment.TickCount64 + 1500;
            int? familyAlive;
            bool spawnAlive;
            do
            {
                ct.WaitHandle.WaitOne(300);
                spawnAlive = ProcessAlive(spawnedPid) ||
                    ProcessByName(nameHints, spawnedName);
                familyAlive = LiveDescendantPid(spawnedPid.Value)
                    ?? RelatedStartedAfter(launchStartUtc, nameHints,
                        relatedCompany);
            } while (!spawnAlive && familyAlive == null &&
                     Environment.TickCount64 < settleUntil);
            if (!spawnAlive && familyAlive == null)
                return Fail("Failed",
                    $"process {spawnedPid.Value} exited right after spawn — " +
                    "the target may need a launcher/runtime that is not " +
                    "running (e.g. Steam games need Steam; try a " +
                    "steam://rungameid/<appid> uri)", total,
                    resolvedName: chosen.DisplayName,
                    identifier: chosen.Identifier, pid: spawnedPid,
                    state: "ProcessExited");
            // The bootstrapper died but a live descendant/publisher
            // successor exists — the RESULT should carry the surviving pid
            // (spawnedPid itself stays accurate for reaper bookkeeping;
            // WaitReady may have returned the dead spawned pid when the
            // exit raced the readiness return).
            if (!ProcessAlive(spawnedPid) && familyAlive is { } fpv &&
                ready.pid == spawnedPid)
                ready = (ready.error, ready.detail, ready.state, fpv,
                    ready.hwnd, ready.title, ready.procName);
        }
        // Shell-launched targets (UseShellExecute + app-execution aliases like
        // Win11 Store notepad.exe) return null/no pid from Process.Start, so
        // `spawnedPid` is empty and the Track() above never ran. When readiness
        // matched a real window, attribute its pid only if the process started
        // after this launch began — same start-time identity check the reaper
        // applies — so a pre-existing same-name window is never claimed.
        if (!(spawnedPid is > 0) && ready.pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(ready.pid.Value);
                if (proc.StartTime.ToUniversalTime() >= launchStartUtc.AddSeconds(-2))
                    _processTracker?.Track(ready.pid.Value,
                        tool: PerfTrace.TryCurrent is { } tr2
                            ? $"{tr2.Kind}:{tr2.Id}"
                            : "computer_launch");
            }
            catch { /* bookkeeping must never break a launch */ }
        }

        // Provenance: stamp agent ownership on every pid THIS call
        // verifiably created — the pid the spawn returned, plus the
        // readiness pid when it started after this launch began (launcher
        // handoff / shell-alias / UWP frames). This is what makes a later
        // computer_close_window / close on the launched window closeable:
        // it does not depend on the MCP-side pre-launch-snapshot evidence
        // evaluator, which conservatively reports ownership:"user" whenever
        // creation evidence is even slightly inconclusive (e.g. a window
        // handle that predates the snapshot, or a start time that could
        // not be read). ExistingInstance / AlreadyRunning results never
        // reach this point — they return above before any spawn.
        if ((Provenance ?? AmbientProvenance) is { } provSink)
        {
            try
            {
                var launchArg = chosen.DisplayName;
                if (spawnedPid is > 0)
                    provSink.RegisterAgentLaunch(spawnedPid.Value,
                        ready.procName ?? chosen.DisplayName, ready.hwnd,
                        launchArg);
                if (ready.pid is > 0 && ready.pid != spawnedPid)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(ready.pid.Value);
                        if (proc.StartTime.ToUniversalTime() >=
                            launchStartUtc.AddSeconds(-2))
                            provSink.RegisterAgentLaunch(ready.pid.Value,
                                ready.procName ?? chosen.DisplayName,
                                ready.hwnd, launchArg);
                    }
                    catch { /* unreadable start time → not provably ours */ }
                }
            }
            catch { /* provenance must never break a launch */ }
        }
        return Done(chosen.Method, chosen.DisplayName, ready.procName,
            ready.pid, ready.hwnd, ready.title, ready.state!, total,
            readyMs: total.ElapsedMilliseconds - spawnMs,
            spawnedPid: spawnedPid, identifier: chosen.Identifier);
    }

    public IReadOnlyList<AppCandidate> ResolveCandidates(string app)
        => ResolveAll(app)
            .Select(c => new AppCandidate(c.DisplayName, c.Method,
                c.Identifier, c.Score)).ToList();

    /// <summary>settings.json "allowLaunch" per-target allowlist — matches
    /// on the full path or the bare file name, case-insensitive.</summary>
    private static bool LaunchAllowListed(string target)
    {
        try
        {
            var list = UserSettings.LoadCached().AllowLaunch;
            if (list == null) return false;
            var leaf = Path.GetFileName(target);
            foreach (var entry in list)
            {
                if (entry.Equals(target, StringComparison.OrdinalIgnoreCase) ||
                    entry.Equals(leaf, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* unreadable settings = not allow-listed */ }
        return false;
    }

    // ------------------------------------------------------------------
    //  resolution
    // ------------------------------------------------------------------

    private ResolvedApp? ResolveDirect(LaunchSpec spec, out string? err)
    {
        err = null;
        if (spec.Path is { } p)
        {
            // Script/UNC/shortcut gate BEFORE existence probing — a denied
            // shape must never even answer "does this file exist".
            var gate = LaunchPathGate.Evaluate(p);
            if (gate.Verdict == LaunchPathGate.Verdict.Denied)
            { err = $"PolicyDenied: {gate.Detail} (path '{p}')"; return null; }
            if (gate.Verdict == LaunchPathGate.Verdict.ScriptRequiresConsent)
            { err = $"ConfirmationRequired: {gate.Detail} (path '{p}') — " +
                    $"set {LaunchPathGate.AllowEnvVar}=1 or " +
                    "\"allowScriptLaunch\": true in settings.json to " +
                    "permit script launches"; return null; }
            if (!File.Exists(p))
            { err = $"TargetNotFound: no file at '{p}'"; return null; }
            var stem = Path.GetFileNameWithoutExtension(p);
            if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return new ResolvedApp(LaunchMethod.ExplicitPath, p,
                    stem, [stem], 100);
            // document/data file — spawned through shell-execute so the
            // registered handler opens it (.uproject → UnrealEditor).
            // The window to expect belongs to the HANDLER's process,
            // resolved via the file association — never matched by the
            // document's own name (a "Qwen - BRO3d" terminal is not Unreal).
            var assoc = AssocExeFor(p);
            return assoc != null
                ? new ResolvedApp(LaunchMethod.ExplicitPath, p,
                    Path.GetFileNameWithoutExtension(assoc),
                    [Path.GetFileNameWithoutExtension(assoc)], 100)
                : new ResolvedApp(LaunchMethod.ExplicitPath, p, stem, [stem], 100);
        }
        if (spec.Aumid is { } aumid)
        {
            if (!aumid.Contains('!'))
            { err = "Malformed: aumid must be PackageFamilyName!AppId"; return null; }
            return new ResolvedApp(LaunchMethod.Aumid, aumid,
                aumid.Split('!')[0].Split('_')[0], [], 100);
        }
        if (spec.Uri is { } uri)
        {
            if (!System.Uri.TryCreate(uri, UriKind.Absolute, out var u) ||
                string.IsNullOrEmpty(u.Scheme))
            { err = "Malformed: uri must include a scheme"; return null; }
            if (!SchemeRegistered(u.Scheme))
            { err = $"TargetNotFound: no handler registered for '{u.Scheme}:'"; return null; }
            return new ResolvedApp(LaunchMethod.Protocol, uri, u.Scheme, [], 100);
        }
        if (spec.Executable is { } exe)
        {
            // Same gate as path: — ResolveExecutable's File.Exists fast-path
            // ShellExecutes the file verbatim, so `executable:"foo.bat"`
            // would otherwise launder a script through the name lane. Bare
            // names (no extension) classify Allowed and resolve normally.
            var gate = LaunchPathGate.Evaluate(exe);
            if (gate.Verdict == LaunchPathGate.Verdict.Denied)
            { err = $"PolicyDenied: {gate.Detail} (executable '{exe}')"; return null; }
            if (gate.Verdict == LaunchPathGate.Verdict.ScriptRequiresConsent)
            { err = $"ConfirmationRequired: {gate.Detail} (executable '{exe}') — " +
                    $"set {LaunchPathGate.AllowEnvVar}=1 or " +
                    "\"allowScriptLaunch\": true in settings.json to " +
                    "permit script launches"; return null; }
            var hit = ResolveExecutable(exe);
            if (hit == null)
            { err = $"TargetNotFound: executable '{exe}' not found"; return null; }
            return hit;
        }
        return null;
    }

    /// <summary>The launchable-apps catalog — same sources ResolveAll
    /// consults, enumerated instead of queried. First registration wins
    /// (Start Menu names are the most user-facing), so packaged apps that
    /// also have a shortcut surface under the friendly name.</summary>
    public IReadOnlyList<AppInfo> ListApps()
    {
        var snapshot = _catalogService.GetSnapshot();
        return snapshot.Apps;
    }

    internal (IReadOnlyList<AppInfo> Apps, IReadOnlyList<AppCatalogEntryDto> Entries, string? Fingerprint) EnumerateAuthoritativeCatalog()
    {
        Interlocked.Increment(ref CatalogFullScanCount);
        var windir = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var byName = new Dictionary<string, AppInfo>(
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<AppCatalogEntryDto>();

        void Add(string? name, LaunchMethod m, string launch, string kind, string identifier, string[] hints, int score, string? targetStem)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            byName.TryAdd(Norm(name), new(name, m, launch, kind));
            entries.Add(new AppCatalogEntryDto
            {
                Name = name,
                Method = m,
                Launch = launch,
                Kind = kind,
                Identifier = identifier,
                ExeHints = hints,
                Score = score,
                TargetStem = targetStem
            });
        }

        foreach (var dir in StartMenuDirs())
        foreach (var lnk in EnumerateLinks(dir))
        {
            var (isValid, stem) = InspectLnk(lnk);
            if (!isValid) continue;
            var name = Path.GetFileNameWithoutExtension(lnk);
            var hints = stem != null ? new[] { name, stem } : new[] { name };
            Add(name, LaunchMethod.StartMenu, $"app:\"{name}\"", "installed", lnk, hints, 100, stem);
        }
        foreach (var (key, path) in AppPaths())
            if (File.Exists(path))
            {
                var name = Path.GetFileNameWithoutExtension(key);
                Add(name, LaunchMethod.AppPath,
                    $"executable:\"{key}\"",
                    path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)
                        ? "system" : "installed",
                    path, [name], 100, null);
            }
        foreach (var pkg in (PackageEnumerator ?? EnumeratePackages)())
            Add(pkg.DisplayName, LaunchMethod.Aumid,
                $"aumid:\"{pkg.Identifier}\"",
                pkg.Identifier.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                pkg.Identifier.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
                pkg.Identifier.Contains("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase)
                    ? "system" : "installed",
                pkg.Identifier, pkg.ExeHints, 100, null);

        // Steam games — the install-dir exe is a bootstrapper that dies
        // without Steam; the rungameid URI starts Steam AND the game, so it
        // is the correct catalog entry (signed-handler protocol launch).
        foreach (var (gname, appid) in SteamGames())
            Add(gname, LaunchMethod.Protocol,
                $"uri:\"steam://rungameid/{appid}\"", "installed",
                $"steam://rungameid/{appid}", [gname], 95, null);

        var list = byName.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return (list, entries, null);
    }

    /// <summary>Every resolution mechanism for a friendly name, scored.</summary>
    internal List<ResolvedApp> ResolveAll(string app)
    {
        var norm = Norm(app);
        if (_resolveCache.TryGetValue(norm, out var cached))
        {
            var ttl = cached.Apps.Count > 0 ? AppsCacheTtl : TimeSpan.FromSeconds(20);
            if ((DateTime.UtcNow - cached.Timestamp) < ttl)
            {
                if (cached.Apps.Count == 0)
                    Interlocked.Increment(ref NegativeCacheHitCount);
                else
                    Interlocked.Increment(ref ResolveCacheHitCount);
                return cached.Apps;
            }
        }

        var found = new List<ResolvedApp>();

        // Registered-app sources are served from the cached catalog
        // snapshot — background-built / warm-loaded from disk — instead of
        // rescanning Start Menu .lnk COM parses, the App Paths registry,
        // and every package manifest on each query. The PackageEnumerator
        // test seam bypasses the catalog so injected fakes always apply.
        if (PackageEnumerator == null &&
            TryCatalogEntries() is { } catEntries)
        {
            foreach (var e in catEntries) ScoreCatalogEntry(e, app, norm, found);
        }
        else
        {
            ResolveAllLiveSources(app, norm, found);
        }

        // executable on PATH / well-known dirs
        if (ResolveExecutable(app) is { } exeHit)
            found.Add(exeHit);

        // registered URI scheme as a last resort — never invented, only
        // used when the scheme actually exists in the registry
        if (SchemeRegistered(app))
            found.Add(new ResolvedApp(LaunchMethod.Protocol, app + ":", app,
                [app], 10));

        // filesystem last resort — engines/dev tools register nowhere;
        // a bounded exe-name scan finds UnrealEditor under Epic Games.
        // All hits score equal: UnrealPak.exe is NOT UnrealEditor.exe —
        // several distinct exes → AmbiguousApplication, never a blind pick
        if (found.Count == 0)
            foreach (var exe in ScanExeByName(norm))
                found.Add(new ResolvedApp(LaunchMethod.Executable, exe,
                    Path.GetFileNameWithoutExtension(exe),
                    [Path.GetFileNameWithoutExtension(exe)], 25));

        _resolveCache[norm] = (found, DateTime.UtcNow);
        return found;
    }

    /// <summary>The persisted catalog's scored entries — null when no
    /// snapshot is available (cold build failed/timed out), in which case
    /// resolution falls back to live source scans.</summary>
    private IReadOnlyList<AppCatalogEntryDto>? TryCatalogEntries()
    {
        try { return _catalogService.GetSnapshot().Entries; }
        catch { return null; }
    }

    /// <summary>Score one catalog entry exactly as the live-source loops
    /// did: Start Menu scores the display name (target stem joins ExeHints
    /// for reuse/window matching), App Paths the exe-name key, packages the
    /// display name + PFN + exe hints. Per-method score bonus preserved.</summary>
    private static void ScoreCatalogEntry(AppCatalogEntryDto e, string app,
        string norm, List<ResolvedApp> found)
    {
        switch (e.Method)
        {
            case LaunchMethod.StartMenu:
            {
                var s = Score(e.Name, app, Norm(e.Name), norm);
                if (s <= 0) break;
                var exes = new List<string> { e.Name };
                if (e.TargetStem is { } tgtStem)
                {
                    exes.Add(tgtStem);
                    if (tgtStem.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                    {
                        var baseStem = System.Text.RegularExpressions.Regex.Replace(tgtStem, @"[-_]?launcher", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!string.IsNullOrWhiteSpace(baseStem)) exes.Add(baseStem);
                    }
                }
                var parts = e.Name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1 && parts[0].Length >= 3)
                {
                    exes.Add(parts[0]);
                }
                found.Add(new ResolvedApp(LaunchMethod.StartMenu, e.Identifier,
                    e.Name,
                    exes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    s + 5));
                break;
            }
            case LaunchMethod.AppPath:
            {
                var s = Score(e.Name, app, Norm(e.Name), norm);
                if (s > 0)
                    found.Add(new ResolvedApp(LaunchMethod.AppPath, e.Identifier,
                        e.Name,
                        e.ExeHints.Length > 0 ? e.ExeHints : [e.Name], s + 4));
                break;
            }
            case LaunchMethod.Aumid:
            {
                var s = Math.Max(Score(e.Name, app, Norm(e.Name), norm),
                    Score(e.Identifier, app, Norm(e.Identifier), norm));
                foreach (var hint in e.ExeHints)
                    s = Math.Max(s, Score(hint, app, Norm(hint), norm));
                if (s > 0)
                    found.Add(new ResolvedApp(LaunchMethod.Aumid, e.Identifier,
                        e.Name, e.ExeHints, s + 3));
                break;
            }
            case LaunchMethod.Protocol:
            {
                // Registered-handler launches (steam://rungameid/…): score
                // the display name; below StartMenu/AppPath/Aumid priority.
                var s = Score(e.Name, app, Norm(e.Name), norm);
                foreach (var hint in e.ExeHints)
                    s = Math.Max(s, Score(hint, app, Norm(hint), norm));
                if (s > 0)
                    found.Add(new ResolvedApp(LaunchMethod.Protocol,
                        e.Identifier, e.Name, e.ExeHints, s + 2));
                break;
            }
        }
    }

    /// <summary>Live source scans — used when the catalog snapshot is
    /// unavailable, and always when the PackageEnumerator test seam is set
    /// so injected fake packages resolve deterministically.</summary>
    private void ResolveAllLiveSources(string app, string norm,
        List<ResolvedApp> found)
    {
        // Start Menu shortcuts — filename carries the display name.
        // The shortcut's TARGET exe joins ExeHints: "File Explorer.lnk"
        // launches explorer.exe, so running-instance reuse and post-spawn
        // window matching must know the process name behind the lnk —
        // without it every launch misses FindRunning and spawns a dupe.
        foreach (var dir in StartMenuDirs())
        foreach (var lnk in EnumerateLinks(dir))
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            var s = Score(name, app, Norm(name), norm);
            if (s <= 0) continue;
            var (isValid, tgtStem) = InspectLnk(lnk);
            if (!isValid) continue; // Skip stale/dead shortcuts to uninstalled apps
            var exes = new List<string> { name };
            if (tgtStem != null)
            {
                exes.Add(tgtStem);
                if (tgtStem.Contains("launcher", StringComparison.OrdinalIgnoreCase))
                {
                    var baseStem = System.Text.RegularExpressions.Regex.Replace(tgtStem, @"[-_]?launcher", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!string.IsNullOrWhiteSpace(baseStem)) exes.Add(baseStem);
                }
            }
            var parts = name.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[0].Length >= 3)
            {
                exes.Add(parts[0]);
            }
            found.Add(new ResolvedApp(LaunchMethod.StartMenu, lnk, name,
                exes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                s + 5)); // StartMenu beats equal-score PATH noise
        }

        // App Paths — key "<exe>" → (Default) path
        foreach (var (keyName, path) in AppPaths())
        {
            var name = Path.GetFileNameWithoutExtension(keyName);
            var s = Score(name, app, Norm(name), norm);
            if (s > 0 && File.Exists(path))
                found.Add(new ResolvedApp(LaunchMethod.AppPath, path, name,
                    [name], s + 4));
        }

        // packaged apps — display name, package family name, exe hint and
        // full AUMID all match: localized systems show "Hesap Makinesi"
        // while the PFN keeps the canonical "Microsoft.WindowsCalculator"
        foreach (var pkg in (PackageEnumerator ?? EnumeratePackages)())
        {
            var s = Math.Max(Score(pkg.DisplayName, app, Norm(pkg.DisplayName), norm),
                Score(pkg.Identifier, app, Norm(pkg.Identifier), norm));
            foreach (var hint in pkg.ExeHints)
                s = Math.Max(s, Score(hint, app, Norm(hint), norm));
            if (s > 0)
                found.Add(new ResolvedApp(LaunchMethod.Aumid, pkg.Identifier,
                    pkg.DisplayName, pkg.ExeHints, s + 3));
        }
    }

    /// <summary>Public filesystem fallback used by computer_apps when the
    /// registered-app catalog has no match for a query.</summary>
    public IReadOnlyList<string> FindExecutables(string query)
        => ScanExeByName(Norm(query));

    /// <summary>Bounded exe-name scan over well-known install roots —
    /// depth 6 reaches Epic Games\UE_x\Engine\Binaries\Win64; reparse
    /// points and inaccessible dirs are skipped. Last resort only.</summary>
    private static IReadOnlyList<string> ScanExeByName(string norm)
    {
        var found = new List<string>();
        if (norm.Length < 3) return found;
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32"),
        }.Distinct().ToList();
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.exe",
                    new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = true,
                        MaxRecursionDepth = 6,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                    }))
                {
                    if (Norm(Path.GetFileNameWithoutExtension(f)).Contains(norm))
                    {
                        found.Add(f);
                        if (found.Count >= 50) break;
                    }
                }
            }
            catch { /* mid-enumeration failures never abort the search */ }
        }
        // best first: prefix match, then shorter name — "unreal" ranks
        // UnrealEditor.exe above UnrealEngineLauncher/-Cmd variants
        return found
            .OrderByDescending(f => Score(Norm(
                Path.GetFileNameWithoutExtension(f)), norm))
            .ThenBy(f => Path.GetFileNameWithoutExtension(f).Length)
            .Take(5).ToList();
    }

    /// <summary>Inspects a .lnk via IShellLinkW — checks if target exists
    /// and resolves the target executable stem (File Explorer.lnk → "explorer").
    /// Stale/dead shortcuts to uninstalled apps return (false, null).</summary>
    private static (bool isValid, string? stem) InspectLnk(string lnk)
    {
        try
        {
            var path = LnkTargetPath(lnk);
            if (path == null) return (false, null);
            // shell:/folder targets have no exe path — they open in
            // Explorer (File Explorer.lnk is exactly this shape)
            if (path.Length == 0) return (true, "explorer");
            if (!File.Exists(path) && !Directory.Exists(path)) return (false, null);
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (true, Path.GetFileNameWithoutExtension(path));
            // .lnk to a document — the window belongs to the registered
            // handler, same resolution as a `path:` launch
            var assoc = AssocExeFor(path);
            return (true, assoc != null
                ? Path.GetFileNameWithoutExtension(assoc)
                : Path.GetFileNameWithoutExtension(path));
        }
        catch { return (false, null); }
    }

    private static unsafe string? LnkTargetPath(string lnk)
    {
        try
        {
            var link = (global::Windows.Win32.UI.Shell.IShellLinkW)
                new global::Windows.Win32.UI.Shell.ShellLink();
            ((global::Windows.Win32.System.Com.IPersistFile)link)
                .Load(lnk, global::Windows.Win32.System.Com.STGM.STGM_READ);
            var buf = new char[1024];
            var fd = new global::Windows.Win32.Storage.FileSystem
                .WIN32_FIND_DATAW();
            link.GetPath(buf, ref fd,
                (uint)global::Windows.Win32.UI.Shell.SLR_FLAGS.SLR_NO_UI);
            return new string(buf).TrimEnd('\0');
        }
        catch { return null; }
    }

    private static string? ResolveLaunchTarget(ResolvedApp app)
    {
        try
        {
            string? raw = app.Method switch
            {
                LaunchMethod.StartMenu => LnkTargetPath(app.Identifier),
                LaunchMethod.ExplicitPath or LaunchMethod.AppPath
                    or LaunchMethod.Executable =>
                    app.Identifier.EndsWith(".exe",
                        StringComparison.OrdinalIgnoreCase)
                        ? app.Identifier
                    : app.Identifier.EndsWith(".lnk",
                        StringComparison.OrdinalIgnoreCase)
                        ? LnkTargetPath(app.Identifier)
                        : LooksLikePe(app.Identifier)
                            ? app.Identifier : null,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(
                    raw.Trim().Trim('"')));
        }
        catch { return null; }
    }

    private static string? TargetIdentity(string path)
    {
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            return vi.OriginalFilename ?? vi.InternalName;
        }
        catch { return null; }
    }

    private static bool LooksLikePe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open,
                FileAccess.Read, FileShare.Read);
            Span<byte> magic = stackalloc byte[2];
            return fs.Read(magic) == 2 && magic[0] == 'M' && magic[1] == 'Z';
        }
        catch { return false; }
    }

    private static bool IsSignedBinary(string path)
    {
        // X509Certificate.CreateFromSignedFile only sees signatures
        // EMBEDDED in the PE — the overwhelming majority of Windows system
        // binaries (notepad.exe, cmd.exe, calc.exe, …) are CATALOG-signed:
        // their signature lives in a security catalog under CatRoot, not in
        // the file, so CreateFromSignedFile throws and they'd be refused as
        // "unsigned". WinVerifyTrust with the generic-verify action resolves
        // both embedded and catalog signatures.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                _ = System.Security.Cryptography.X509Certificates
                    .X509Certificate.CreateFromSignedFile(path);
                return true;
            }
            catch { return false; }
        }

        return VerifyFileTrust(path) || IsCatalogSigned(path);
    }

    /// <summary>WinVerifyTrust (GENERIC_VERIFY_V2) on a single file —
    /// covers embedded signatures AND .cat catalog files themselves.</summary>
    private static bool VerifyFileTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = Marshal.StringToHGlobalUni(path),
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WtdUiNone,
            fdwRevocationChecks = WtdRevokeNone,
            dwUnionChoice = WtdChoiceFile,
            dwProvFlags = WtdRevocationCheckNone,
            dwStateAction = WtdStateactionVerify,
        };

        IntPtr pFile = IntPtr.Zero;
        IntPtr pData = IntPtr.Zero;
        try
        {
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf(fileInfo));
            Marshal.StructureToPtr(fileInfo, pFile, false);
            data.pFile = pFile;
            pData = Marshal.AllocHGlobal(Marshal.SizeOf(data));
            Marshal.StructureToPtr(data, pData, false);
            var action = WintrustActionGenericVerifyV2;
            int status = WinVerifyTrust(InvalidHwnd, ref action, pData);
            // trust status is a literal — end the WVT state data either way
            data.dwStateAction = WtdStateactionClose;
            Marshal.StructureToPtr(data, pData, true);
            WinVerifyTrust(InvalidHwnd, ref action, pData);
            return status == 0;
        }
        catch { return false; }
        finally
        {
            if (fileInfo.pcwszFilePath != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfo.pcwszFilePath);
            if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
            if (pData != IntPtr.Zero) Marshal.FreeHGlobal(pData);
        }
    }

    /// <summary>
    /// Catalog fallback: WinVerifyTrust on a bare file does NOT resolve
    /// Windows catalog signatures (notepad.exe/calc.exe/cmd.exe report
    /// TRUST_E_NOSIGNATURE even though Get-AuthenticodeSignature validates
    /// them). Walk the catalog chain like Authenticode does: hash the file,
    /// enumerate catalogs containing that hash, and accept when any owning
    /// .cat file itself verifies.
    /// </summary>
    private static bool IsCatalogSigned(string path)
    {
        const uint GenericRead = 0x80000000;
        const uint OpenExisting = 3;
        const int MaxCatalogs = 16;
        IntPtr hFile = IntPtr.Zero;
        IntPtr hCatAdmin = IntPtr.Zero;
        IntPtr hash = IntPtr.Zero;
        try
        {
            hFile = CreateFileW(path, GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (hFile == InvalidHandle) return false;
            if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero,
                    IntPtr.Zero, IntPtr.Zero, 0))
                return false;
            var cbHash = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile,
                    ref cbHash, IntPtr.Zero, 0) || cbHash <= 0)
                return false;
            hash = Marshal.AllocHGlobal(cbHash);
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, hFile,
                    ref cbHash, hash, 0))
                return false;
            IntPtr prev = IntPtr.Zero;
            for (var i = 0; i < MaxCatalogs; i++)
            {
                var hCatInfo = CryptCATAdminEnumCatalogFromHash(
                    hCatAdmin, hash, cbHash, 0, ref prev);
                if (hCatInfo == IntPtr.Zero) break;
                var ci = new CATALOG_INFO
                { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0) &&
                    !string.IsNullOrEmpty(ci.wszCatalogFile) &&
                    VerifyFileTrust(ci.wszCatalogFile))
                {
                    CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0);
                    return true;
                }
                prev = hCatInfo;
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (hash != IntPtr.Zero) Marshal.FreeHGlobal(hash);
            if (hCatAdmin != IntPtr.Zero) CryptCATAdminReleaseContext(hCatAdmin, 0);
            if (hFile != IntPtr.Zero && hFile != InvalidHandle) CloseHandle(hFile);
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin,
        IntPtr pgSubsystem, IntPtr pwszHashAlgorithm, IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin,
        IntPtr hFile, ref int pcbHash, IntPtr pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin,
        IntPtr pbHash, int cbHash, uint dwFlags, ref IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin,
        IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo,
        ref CATALOG_INFO psCatInfo, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string wszCatalogFile;
    }

    private static readonly Guid WintrustActionGenericVerifyV2 =
        new("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");
    private static readonly IntPtr InvalidHwnd = new(-1);
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdRevocationCheckNone = 0x00000040;
    private const uint WtdStateactionVerify = 0;
    private const uint WtdStateactionClose = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd, ref Guid pgActionID, IntPtr pWVTData);

    /// <summary>The exe registered to open a document type —
    /// HKCR\.ext → ProgID → shell\open\command. Used so a `path:` launch
    /// of a .uproject/.sln expects the HANDLER's window, not a window
    /// that happens to contain the file's name in its title.</summary>
    private static string? AssocExeFor(string file)
    {
        try
        {
            var ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext)) return null;
            var progId = Registry.ClassesRoot.OpenSubKey(ext)
                ?.GetValue(null) as string;
            if (string.IsNullOrEmpty(progId)) return null;
            var cmd = Registry.ClassesRoot.OpenSubKey(
                progId + @"\shell\open\command")?.GetValue(null) as string;
            if (string.IsNullOrEmpty(cmd)) return null;
            // '"C:\...\app.exe" "%1"' or 'C:\...\app.exe %1' — take the exe
            var m = System.Text.RegularExpressions.Regex.Match(
                cmd, "\"([^\"]+\\.exe)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var exe = m.Success ? m.Groups[1].Value
                : cmd.Split(' ')[0].Trim('"');
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// <summary>Top-scoring distinct app wins; two different apps tied at the
    /// top are ambiguous (returned via <paramref name="ambiguous"/>).</summary>
    private static ResolvedApp? PickBest(List<ResolvedApp> candidates,
        out List<ResolvedApp>? ambiguous)
    {
        ambiguous = null;
        if (candidates.Count == 0) return null;
        var top = candidates.Max(c => c.Score);
        var winners = candidates.Where(c => c.Score == top)
            .GroupBy(c => Norm(c.DisplayName))
            .Select(g => g.OrderBy(c => MethodRank(c.Method)).First())
            .ToList();
        if (winners.Count > 1) { ambiguous = winners; return null; }
        return winners[0];
    }

    private static int MethodRank(LaunchMethod m) => m switch
    {
        LaunchMethod.AppPath => 0,      // canonical registered exe
        LaunchMethod.StartMenu => 1,    // user-facing shortcut
        LaunchMethod.Aumid => 2,
        LaunchMethod.Executable => 3,
        LaunchMethod.Protocol => 4,
        _ => 5,
    };

    private static readonly Dictionary<string, string[]> WellKnownLocalizedAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["settings"] = ["ayarlar", "settings", "immersivecontrolpanel"],
        ["ayarlar"] = ["settings", "ayarlar", "immersivecontrolpanel"],
        ["calculator"] = ["hesap makinesi", "calculator", "calc", "windowscalculator"],
        ["hesapmakinesi"] = ["calculator", "hesap makinesi", "calc", "windowscalculator"],
        ["photos"] = ["fotoğraflar", "photos", "windowsphotos"],
        ["fotograflar"] = ["photos", "fotoğraflar", "windowsphotos"],
        ["camera"] = ["kamera", "camera", "windowscamera"],
        ["kamera"] = ["camera", "kamera", "windowscamera"],
        ["store"] = ["microsoft store", "store", "mağaza", "windowsstore"],
        ["magaza"] = ["microsoft store", "store", "mağaza", "windowsstore"],
        ["alarms"] = ["saat", "alarms", "alarms & clock", "clock"],
        ["clock"] = ["saat", "alarms", "alarms & clock", "clock"],
        ["saat"] = ["clock", "alarms", "saat"],
        ["weather"] = ["hava durumu", "weather", "bingweather"],
        ["havadurumu"] = ["weather", "hava durumu", "bingweather"],
        ["paint"] = ["paint", "mspaint"],
        ["notepad"] = ["notepad", "not defteri"],
        ["notdefteri"] = ["notepad", "not defteri"],
        ["word"] = ["word", "microsoft word", "winword"],
        ["excel"] = ["excel", "microsoft excel"],
        ["powerpoint"] = ["powerpoint", "microsoft powerpoint"],
        ["edge"] = ["microsoft edge", "edge", "msedge"],
    };

    private static int Score(string rawCandidate, string rawQuery, string candidateNorm, string queryNorm)
    {
        if (candidateNorm == queryNorm) return 100;

        // Well-known localized aliases
        if (WellKnownLocalizedAliases.TryGetValue(queryNorm, out var aliases) &&
            aliases.Any(a => Norm(a) == candidateNorm))
            return 95;
        if (WellKnownLocalizedAliases.TryGetValue(candidateNorm, out var rAliases) &&
            rAliases.Any(a => Norm(a) == queryNorm))
            return 95;

        // Distinct word token matching in raw candidate (e.g. "Edge" inside "Microsoft Edge" gets 85 vs compound "GoAwayEdge" getting 30)
        var rawTokens = rawCandidate.Split(new[] { ' ', '-', '_', '.', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Any(t => string.Equals(Norm(t), queryNorm, StringComparison.OrdinalIgnoreCase)))
            return 85;

        if (candidateNorm.StartsWith(queryNorm, StringComparison.Ordinal)) return 60;
        if (candidateNorm.Contains(queryNorm, StringComparison.Ordinal)) return 30;
        return 0;
    }

    private static int Score(string candidate, string norm)
        => Score(candidate, norm, candidate, norm);

    private static string Norm(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>WindowInfo.ProcessName may arrive as "Notepad.exe" —
    /// normalize to the bare name before matching.</summary>
    private static string NormProc(string? processName)
        => Norm(Path.GetFileNameWithoutExtension(processName ?? ""));

    /// <summary>Processes whose top-level window titles embed documents,
    /// tabs, chats or session names from OTHER applications — IDEs,
    /// terminals, editors, browsers-as-editors and the agent host itself
    /// (Devin window titled "…Spotify/Chrome Açılmama Sorunu" is a chat
    /// about Spotify, not Spotify). A TITLE-ONLY match against these can
    /// never be evidence of the running app; process-name/exe-hint matches
    /// are unaffected (a real "notepad" still resolves by process name).</summary>
    private static readonly HashSet<string> DocumentTitleHosts = new(
        StringComparer.Ordinal)
    {
        "devin", "devinexe", "windsurf", "code", "codeinsiders",
        "devenv", "idea64", "rider64", "sublimetext", "notepadpp",
        "notepad", "wordpad", "winword", "excel", "powerpnt",
        "obsidian", "windowsterminal", "wt", "cmd", "powershell", "pwsh",
        "conhost", "wsl", "bash", "ubuntu", "explorer", "totalcmd",
        "七", // agent-host family listed in the triage report
    };

    /// <summary>Does <paramref name="title"/> contain the normalized app
    /// name as a whole word-run at the START or END (or equal it)?
    /// "Spotify" matches "Spotify", "Spotify Premium" and "Now playing —
    /// Spotify"; it never matches "Fixing Spotify/Chrome Açılmama Sorunu".
    /// Matching is on normalized tokens (letters/digits only), so
    /// "… / Spotify" and "… - Spotify" both count as boundary positions.</summary>
    internal static bool TitleBoundaryMatch(string? title, string normName)
    {
        if (string.IsNullOrEmpty(title) || normName.Length < 3) return false;
        var tokens = new List<string>();
        foreach (var raw in title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            foreach (var t in raw.Split(_titleSeparators,
                       StringSplitOptions.RemoveEmptyEntries))
            {
                var n = Norm(t);
                if (n.Length > 0) tokens.Add(n);
            }
        if (tokens.Count == 0) return false;
        // Leading run of tokens that concatenates to the name ("Spotify
        // Premium" → ["spotify","premium"], first token completes it).
        var acc = "";
        for (var i = 0; i < tokens.Count && acc.Length < normName.Length; i++)
        {
            acc += tokens[i];
            if (acc == normName) return true;
            if (acc.Length >= normName.Length) break;
        }
        // Trailing run ("Now playing - Spotify").
        acc = "";
        for (var i = tokens.Count - 1; i >= 0 && acc.Length < normName.Length; i--)
        {
            acc = tokens[i] + acc;
            if (acc == normName) return true;
            if (acc.Length >= normName.Length) break;
        }
        return false;
    }

    private static readonly char[] _titleSeparators =
    {
        '-', '–', '—', '_', '.', ',', ';', ':', '/', '\\', '|', '·', '•',
        '(', ')', '[', ']', '{', '}', '<', '>', '\'', '"', '‘', '’', '“',
        '”', '!', '?', '&', '+', '=', '#', '@', '~', '*',
    };

    // ------------------------------------------------------------------
    //  resolution sources
    // ------------------------------------------------------------------

    private static IEnumerable<string> StartMenuDirs()
    {
        foreach (var f in new[]
        {
            Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.StartMenu,
        })
        {
            var dir = Path.Combine(Environment.GetFolderPath(f), "Programs");
            if (Directory.Exists(dir)) yield return dir;
        }
    }

    private static IEnumerable<string> EnumerateLinks(string dir)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.lnk",
            SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var f in files)
        {
            Interlocked.Increment(ref FilesystemEntriesScanned);
            yield return f;
        }
    }

    internal static IEnumerable<(string Key, string Path)> AppPaths()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var root = hive.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (root == null) continue;
            foreach (var key in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(key);
                var path = sub?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(path))
                    yield return (key, path.Trim('"'));
            }
        }
    }

    /// <summary>Steam library roots: the Steam install dir plus every extra
    /// library listed in steamapps\libraryfolders.vdf. Empty when Steam is
    /// not installed — callers simply see no games.</summary>
    internal static IEnumerable<string> SteamLibraryRoots()
    {
        string? steamPath = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Valve\Steam");
            steamPath = key?.GetValue("SteamPath") as string;
        }
        catch { /* no Steam registry access → no games */ }
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath))
            yield break;
        yield return steamPath;
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (!t.StartsWith("\"path\"", StringComparison.Ordinal)) continue;
            var seg = t.Split('"');
            if (seg.Length > 3 && Directory.Exists(seg[3].Replace(@"\\", @"\")))
                yield return seg[3].Replace(@"\\", @"\");
        }
    }

    /// <summary>Installed Steam games as (display name, appid), read from
    /// appmanifest_*.acf across all libraries. VDF is parsed line-wise —
    /// only the flat "appid"/"name" pairs matter.</summary>
    internal static IEnumerable<(string Name, int AppId)> SteamGames()
    {
        var seen = new HashSet<int>();
        foreach (var root in SteamLibraryRoots())
        {
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateFiles(
                    Path.Combine(root, "steamapps"), "appmanifest_*.acf");
            }
            catch { continue; }
            foreach (var m in manifests)
            {
                string? name = null;
                var appid = 0;
                try
                {
                    foreach (var line in File.ReadLines(m))
                    {
                        var t = line.Trim();
                        var seg = t.Split('"');
                        if (seg.Length <= 3) continue;
                        if (seg[1].Equals("appid", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(seg[3], out appid);
                        else if (seg[1].Equals("name", StringComparison.OrdinalIgnoreCase))
                            name = seg[3];
                    }
                }
                catch { continue; }
                if (name is { Length: > 0 } && appid > 0 && seen.Add(appid))
                    yield return (name, appid);
            }
        }
    }

    /// <summary>Packaged apps — AUMID = PackageFamilyName!AppId, AppId read
    /// from the app manifest. Two sources: the per-user AppModel repository
    /// (installed packages) and the machine-wide AppxAllUserStore (inbox /
    /// staged system apps like Calculator that never appear per-user).</summary>
    internal IReadOnlyList<ResolvedApp> EnumeratePackages()
    {
        if (_cachedPackages != null && (DateTime.UtcNow - _packagesCacheTime) < PackagesCacheTtl)
            return _cachedPackages;

        lock (_packagesLock)
        {
            if (_cachedPackages != null && (DateTime.UtcNow - _packagesCacheTime) < PackagesCacheTtl)
                return _cachedPackages;

            var list = new List<ResolvedApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // WinRT PackageManager — the authoritative source: covers inbox and
            // staged apps (Calculator & co. never land in the per-user repo) and
            // returns LOCALIZED display names, so "Hesap Makinesi" matches too
            try
            {
                var pm = new global::Windows.Management.Deployment.PackageManager();
                foreach (var pkg in pm.FindPackagesForUser(""))
                {
                    try
                    {
                        var entries = pkg.GetAppListEntriesAsync()
                            .AsTask().GetAwaiter().GetResult();
                        var pkgName = pkg.DisplayName;
                        foreach (var e in entries)
                        {
                            if (string.IsNullOrEmpty(e.AppUserModelId) ||
                                !seen.Add(e.AppUserModelId)) continue;
                            list.Add(new ResolvedApp(LaunchMethod.Aumid,
                                e.AppUserModelId,
                                !string.IsNullOrEmpty(pkgName)
                                    ? pkgName : pkg.Id.Name,
                                [pkg.Id.Name], 0));
                        }
                    }
                    catch { /* one broken package never aborts enumeration */ }
                }
            }
            catch { /* WinRT unavailable → registry scan below still applies */ }

            foreach (var r in EnumeratePackagesRegistry())
                if (seen.Add(r.Identifier)) list.Add(r);

            _cachedPackages = list;
            _packagesCacheTime = DateTime.UtcNow;
            return list;
        }
    }

    /// <summary>Registry fallback for package enumeration — per-user AppModel
    /// repo plus machine-wide AppxAllUserStore (inbox/staged apps).</summary>
    private static IReadOnlyList<ResolvedApp> EnumeratePackagesRegistry()
    {
        var list = new List<ResolvedApp>();
        foreach (var (hive, subKey, pathIsManifest) in new (RegistryKey, string, bool)[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Classes\Local Settings\Software\" +
                @"Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages",
                false),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\" +
                @"Appx\AppxAllUserStore\Applications", true),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\" +
                @"Appx\AppxAllUserStore\InboxApplications", true),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(subKey);
                if (root == null) continue;
                foreach (var fullName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var pkg = root.OpenSubKey(fullName);
                        // HKCU repo stores the install dir; HKLM stores the
                        // manifest path directly (AppxMetadata\*.xml)
                        var manifest = pathIsManifest
                            ? pkg?.GetValue("Path") as string
                            : (pkg?.GetValue("PackageRootFolder")
                               ?? pkg?.GetValue("InstallLocation")) is string d
                                ? Path.Combine(d, "AppxManifest.xml") : null;
                        if (string.IsNullOrEmpty(manifest) ||
                            !File.Exists(manifest)) continue;
                        // full name: Name_Version_Arch_ResourceId_PublisherId
                        var parts = fullName.Split('_');
                        if (parts.Length < 2) continue;
                        var pfn = $"{parts[0]}_{parts[^1]}";
                        var appId = ReadAppId(manifest);
                        if (appId == null) continue;
                        var aumid = $"{pfn}!{appId}";
                        var exe = ReadManifestExe(manifest);
                        list.Add(new ResolvedApp(LaunchMethod.Aumid, aumid,
                            parts[0],
                            exe != null
                                ? [Path.GetFileNameWithoutExtension(exe)]
                                : [], 0));
                    }
                    catch { /* a broken package never aborts resolution */ }
                }
            }
            catch { }
        }
        return list;
    }


    private static string? ReadAppId(string manifestPath)
    {
        try
        {
            var doc = new System.Xml.Linq.XDocument();
            doc = System.Xml.Linq.XDocument.Load(manifestPath);
            var app = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application");
            return app?.Attribute("Id")?.Value;
        }
        catch { return null; }
    }

    private static string? ReadManifestExe(string manifestPath)
    {
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(manifestPath);
            var app = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Application");
            return app?.Attribute("Executable")?.Value;
        }
        catch { return null; }
    }

    /// <summary>exe name/path → App Paths, then PATH + per-user WindowsApps
    /// alias dir, then the well-known install roots.</summary>
    private ResolvedApp? ResolveExecutable(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        var file = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? exe : exe + ".exe";

        if (File.Exists(exe))
            return new ResolvedApp(
                Path.IsPathRooted(exe) ? LaunchMethod.ExplicitPath
                    : LaunchMethod.Executable,
                Path.GetFullPath(exe), name, [name], 100);

        foreach (var (key, path) in AppPaths())
            if (key.Equals(file, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path))
                return new ResolvedApp(LaunchMethod.AppPath, path, name,
                    [name], 95);

        // PATH dirs + per-user Store alias dir
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder
                    .LocalApplicationData),
                "Microsoft", "WindowsApps"));
        foreach (var dir in dirs)
        {
            try
            {
                var cand = Path.Combine(dir.Trim(), file);
                if (File.Exists(cand))
                    return new ResolvedApp(LaunchMethod.Executable, cand,
                        name, [name], 90);
            }
            catch { }
        }

        // per-user install roots — root\name\name.exe (Spotify, Discord…)
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Distinct())
        {
            if (string.IsNullOrEmpty(root)) continue;
            foreach (var cand in new[]
            {
                Path.Combine(root, name, file),
                Path.Combine(root, file),
            })
            {
                try
                {
                    if (File.Exists(cand))
                        return new ResolvedApp(LaunchMethod.Executable, cand,
                            name, [name], 80);
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>A URI scheme is usable only when the registry records a
    /// handler (HKCR\scheme\shell\open\command or URL Protocol marker).</summary>
    internal static bool SchemeRegistered(string scheme)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey(scheme);
            if (k == null) return false;
            if (k.GetValue("URL Protocol") != null) return true;
            using var cmd = k.OpenSubKey(@"shell\open\command");
            return cmd != null;
        }
        catch { return false; }
    }

    // ------------------------------------------------------------------
    //  running-instance detection
    // ------------------------------------------------------------------

    /// <summary>Shell-surface window classes — the desktop ("Program
    /// Manager"), wallpaper workers and trays are owned by explorer.exe but
    /// are never the app the user means. Excluding them keeps app:"Explorer"
    /// from binding the desktop itself.</summary>
    private static readonly HashSet<string> ShellSurfaceClasses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
    };

    private static bool IsShellSurface(long hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        return Native.NativeMethods.GetClassNameW(new IntPtr(hwnd), sb, sb.Capacity) > 0
            && ShellSurfaceClasses.Contains(sb.ToString());
    }

    /// <summary>Top-level user-facing window owned by a process matching the
    /// app. Helper processes never win — only windows with a real title and
    /// non-empty bounds count; shell surfaces (desktop/tray) never match.</summary>
    private WindowInfo? FindRunning(string displayName, string[]? exeHints,
        bool titleMatch = true)
    {
        var norm = Norm(displayName);
        var hints = (exeHints ?? []).Select(Norm).ToList();
        var wins = SnapshotWindows();
        return wins
            .Where(w =>
            {
                if (w.Bounds.IsEmpty || string.IsNullOrEmpty(w.Title) ||
                    IsShellSurface(w.Hwnd))
                    return false;
                var pn = NormProc(w.ProcessName);
                if (hints.Contains(pn)) return true;
                if (pn == norm) return true;
                // Title match only counts at the title BOUNDARY as a whole
                // word-run — a document title merely containing the app name
                // ("…Spotify/Chrome Hatası…" in an IDE window) is not the
                // running app. Windows owned by processes whose titles embed
                // documents/tabs/chats (IDEs, terminals, editors, the agent
                // host itself) are never attributed by title at all.
                return titleMatch && pn.Length > 0 && norm.Length >= 3 &&
                    !DocumentTitleHosts.Contains(pn) &&
                    TitleBoundaryMatch(w.Title, norm);
            })
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.Bounds.Width * w.Bounds.Height)
            .ThenBy(w => w.Hwnd)
            .FirstOrDefault();
    }

    // ------------------------------------------------------------------
    //  spawn + readiness
    // ------------------------------------------------------------------

    private int? SpawnReal(ResolvedApp app, IReadOnlyList<string>? args)
    {
        switch (app.Method)
        {
            case LaunchMethod.Aumid:
                return ActivateAumid(app.Identifier);
            case LaunchMethod.Protocol:
                DesktopBridge.RunOnDefaultDesktop(() =>
                {
                    Process.Start(new ProcessStartInfo(app.Identifier)
                        { UseShellExecute = true });
                });
                return null;
            default:
            {
                if (app.Identifier.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    return SpawnWindowsApp(app, args);
                }

                return DesktopBridge.CreateProcessOnDefaultDesktop(app.Identifier, args);
            }
        }
    }

    private int? SpawnWindowsApp(ResolvedApp app, IReadOnlyList<string>? args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var aliasDir = Path.Combine(localAppData, @"Microsoft\WindowsApps");
        var exeName = Path.GetFileName(app.Identifier);

        // 1. Try execution alias in %LOCALAPPDATA%\Microsoft\WindowsApps (e.g. notepad.exe, mspaint.exe)
        var candidates = new List<string> { exeName };
        if (exeName.Contains("paint", StringComparison.OrdinalIgnoreCase) ||
            app.DisplayName.Contains("paint", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("mspaint.exe");
            candidates.Add("pbrush.exe");
        }
        else if (exeName.Contains("notepad", StringComparison.OrdinalIgnoreCase) ||
                 app.DisplayName.Contains("notepad", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("notepad.exe");
        }
        foreach (var hint in app.ExeHints)
        {
            if (!string.IsNullOrEmpty(hint))
            {
                candidates.Add(hint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? hint : hint + ".exe");
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var aliasPath = Path.Combine(aliasDir, candidate);
            if (File.Exists(aliasPath))
            {
                return DesktopBridge.CreateProcessOnDefaultDesktop(aliasPath, args);
            }
        }

        // 2. Try AUMID resolution from package directory manifest
        var aumid = TryResolveAumidFromWindowsAppsPath(app.Identifier);
        if (aumid != null)
        {
            return ActivateAumid(aumid);
        }

        // 3. Fallback to package search
        var pkg = (PackageEnumerator ?? EnumeratePackages)()
            .FirstOrDefault(p => app.ExeHints.Any(h => p.ExeHints.Contains(h, StringComparer.OrdinalIgnoreCase)) ||
                                 string.Equals(p.DisplayName, app.DisplayName, StringComparison.OrdinalIgnoreCase));
        if (pkg != null)
        {
            return ActivateAumid(pkg.Identifier);
        }

        return DesktopBridge.CreateProcessOnDefaultDesktop(app.Identifier, args);
    }

    private static string? TryResolveAumidFromWindowsAppsPath(string path)
    {
        try
        {
            var marker = @"\Program Files\WindowsApps\";
            var idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            var sub = path.Substring(idx + marker.Length);
            var packageDirName = sub.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            var rootDir = path.Substring(0, idx + marker.Length);
            var manifestPath = Path.Combine(rootDir, packageDirName, "AppxManifest.xml");
            if (File.Exists(manifestPath))
            {
                var parts = packageDirName.Split('_');
                if (parts.Length >= 2)
                {
                    var pfn = $"{parts[0]}_{parts[^1]}";
                    var appId = ReadAppId(manifestPath);
                    if (!string.IsNullOrEmpty(appId))
                    {
                        return $"{pfn}!{appId}";
                    }
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>Packaged-app activation via IApplicationActivationManager;
    /// falls back to `explorer shell:AppsFolder\aumid` — a folder verb, not
    /// a command line.</summary>
    private static int? ActivateAumid(string aumid)
    {
        return DesktopBridge.RunOnDefaultDesktop(() =>
        {
            try
            {
                var mgr = (IApplicationActivationManager)
                    Activator.CreateInstance(
                        Type.GetTypeFromCLSID(new Guid(
                            "45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
                var hr = mgr.ActivateApplication(aumid, null, 0, out var pid);
                if (hr >= 0 && pid > 0) return (int?)pid;
            }
            catch { }
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
            return null;
        });
    }

    private static bool IsLauncherOrStub(ResolvedApp app, string? spawnedName)
    {
        if (app.Method == LaunchMethod.Aumid) return true;

        var id = app.Identifier ?? "";
        if (id.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
            id.Contains(@"\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase))
            return true;

        if (id.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("stub", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("bootstrapper", StringComparison.OrdinalIgnoreCase))
            return true;

        var disp = app.DisplayName ?? "";
        if (disp.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
            disp.Contains("stub", StringComparison.OrdinalIgnoreCase))
            return true;

        if (spawnedName != null && (spawnedName.Contains("launcher") || spawnedName.Contains("stub")))
            return true;

        if (app.ExeHints.Any(h => h.Contains("launcher", StringComparison.OrdinalIgnoreCase) ||
                                 h.Contains("stub", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

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

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager { }

    [System.Runtime.InteropServices.ComImport]
    [System.Runtime.InteropServices.InterfaceType(
        System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
    [System.Runtime.InteropServices.Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    private interface IApplicationActivationManager
    {
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateApplication(
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string appUserModelId,
            [System.Runtime.InteropServices.MarshalAs(
                System.Runtime.InteropServices.UnmanagedType.LPWStr)]
            string? arguments,
            int options, out int processId);

        // vtable placeholders — ActivateForFile / ActivateForProtocol
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateForFile(string aumid, System.IntPtr itemArray,
            string? verb, out int processId);
        [System.Runtime.InteropServices.PreserveSig]
        int ActivateForProtocol(string aumid, System.IntPtr itemArray,
            out int processId);
    }
}
