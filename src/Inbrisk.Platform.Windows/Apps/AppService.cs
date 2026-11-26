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
public sealed partial class AppService : IAppService, IDisposable
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
}
