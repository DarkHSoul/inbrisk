using System.Diagnostics;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;

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
    private static readonly HashSet<string> ShellHosts = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cmd.exe", "powershell", "powershell.exe", "pwsh", "pwsh.exe",
        "wscript", "wscript.exe", "cscript", "cscript.exe", "mshta",
        "mshta.exe", "rundll32", "rundll32.exe", "regsvr32", "regsvr32.exe",
        "wmic", "wmic.exe", "msiexec", "msiexec.exe",
        "conhost", "conhost.exe", "bash", "bash.exe",
        "sh", "sh.exe", "wt", "wt.exe", "curl", "curl.exe",
    };

    private readonly IWindowService _windows;
    private readonly Func<long, bool>? _uiaProbe;
    private readonly Func<string, bool> _isDeniedProcess;

    // test seams — production uses the real spawn/registry path
    internal Func<ResolvedApp, IReadOnlyList<string>?, int?>? Spawner;
    internal Func<IReadOnlyList<ResolvedApp>>? PackageEnumerator;
    internal Func<int, bool>? ProcessAliveChecker;

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
        Func<DateTimeOffset>? clock = null)
    {
        _windows = windows;
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

    }

    internal void InvalidateCache()
    {
        lock (_appsLock) _cachedApps = null;
        lock (_packagesLock) _cachedPackages = null;
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
            if (ShellHosts.Contains(Norm(spec.App)) ||
                ShellHosts.Contains(Norm(spec.App) + ".exe") ||
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
            if (hint != null && (ShellHosts.Contains(hint) ||
                                 ShellHosts.Contains(hint + ".exe") ||
                                 _isDeniedProcess(hint)))
                return Fail("PolicyDenied",
                    $"refusing to launch '{hint}' — computer_launch is not a " +
                    "shell; use executable UI applications", total);

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
            spawnedPid = (Spawner ?? SpawnReal)(chosen, effectiveArgs.Count > 0 ? effectiveArgs : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return Fail("Failed",
                $"launch failed via {chosen.Method}: {e.Message}", total,
                resolvedName: chosen.DisplayName, identifier: chosen.Identifier);
        }

        // ---- readiness ----
        var ready = WaitReady(chosen, spawnedPid, readiness.Value,
            spec.TimeoutMs, ct, total, spec.App);
        if (ready.error != null)
            return Fail(ready.error, ready.detail, total,
                resolvedName: chosen.DisplayName,
                identifier: chosen.Identifier, pid: ready.pid,
                state: ready.state ?? "Started");
        return Done(chosen.Method, chosen.DisplayName, ready.procName,
            ready.pid, ready.hwnd, ready.title, ready.state!, total,
            readyMs: total.ElapsedMilliseconds - spawnMs,
            spawnedPid: spawnedPid, identifier: chosen.Identifier);
    }

    public IReadOnlyList<AppCandidate> ResolveCandidates(string app)
        => ResolveAll(app)
            .Select(c => new AppCandidate(c.DisplayName, c.Method,
                c.Identifier, c.Score)).ToList();

    // ------------------------------------------------------------------
    //  resolution
    // ------------------------------------------------------------------

    private ResolvedApp? ResolveDirect(LaunchSpec spec, out string? err)
    {
        err = null;
        if (spec.Path is { } p)
        {
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
    private static unsafe (bool isValid, string? stem) InspectLnk(string lnk)
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
            var path = new string(buf).TrimEnd('\0');
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
        var wins = _windows.ListWindows();
        return wins
            .Where(w =>
            {
                if (w.Bounds.IsEmpty || string.IsNullOrEmpty(w.Title) ||
                    IsShellSurface(w.Hwnd))
                    return false;
                var pn = NormProc(w.ProcessName);
                if (hints.Contains(pn)) return true;
                if (pn == norm) return true;
                return titleMatch && pn.Length > 0 &&
                    Norm(w.Title).Contains(norm);
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
                Process.Start(new ProcessStartInfo(app.Identifier)
                    { UseShellExecute = true });
                return null;
            default:
            {
                var psi = new ProcessStartInfo(app.Identifier)
                { UseShellExecute = true };
                if (args != null)
                    foreach (var a in args) psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                try { return p?.Id; } catch { return null; }
            }
        }
    }

    /// <summary>Packaged-app activation via IApplicationActivationManager;
    /// falls back to `explorer shell:AppsFolder\aumid` — a folder verb, not
    /// a command line.</summary>
    private static int? ActivateAumid(string aumid)
    {
        try
        {
            var mgr = (IApplicationActivationManager)
                Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid(
                        "45BA127D-10A8-46EA-8AB7-56EA9078943C"))!)!;
            var hr = mgr.ActivateApplication(aumid, null, 0, out var pid);
            if (hr >= 0 && pid > 0) return pid;
        }
        catch { }
        Process.Start(new ProcessStartInfo("explorer.exe",
            $"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
        return null;
    }

    private (string? error, string? detail, string? state, int? pid,
        long? hwnd, string? title, string? procName)
        WaitReady(ResolvedApp app, int? spawnedPid, LaunchReadiness readiness,
            int timeoutMs, CancellationToken ct, Stopwatch total,
            string? requestedName = null)
    {
        if (readiness == LaunchReadiness.None)
            return (null, null, "Started", spawnedPid, null, null, null);

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
            if (parts.Length > 1 && parts[0].Length >= 3)
            {
                rawHints.Add(parts[0]);
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

        while (total.ElapsedMilliseconds < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (readiness == LaunchReadiness.Process)
            {
                if (ProcessAlive(spawnedPid) ||
                    ProcessByName(hints, spawnedName))
                    return (null, null, "ProcessStarted", spawnedPid,
                        null, null, null);
                ct.WaitHandle.WaitOne(Math.Min(150, (int)Math.Max(1, deadline - total.ElapsedMilliseconds)));
                continue;
            }

            // Early detection: process exited before becoming ready
            if (spawnedPid.HasValue && !ProcessAlive(spawnedPid) && !ProcessByName(hints, spawnedName))
            {
                return ("Failed",
                    $"process {spawnedPid.Value} exited before becoming ready",
                    "Failed", spawnedPid, null, null, null);
            }

            bool eventWoke = false;
            if (_eventBuffer != null)
            {
                var remaining = (int)Math.Max(1, Math.Min(150, deadline - total.ElapsedMilliseconds));
                if (_eventBuffer.WaitForNextEvent(baselineGen, remaining, ct))
                {
                    baselineGen = _eventBuffer.CurrentGeneration;
                    var recents = _eventBuffer.Snapshot(5);
                    if (recents.Any(e => e.Kind is EventKind.WindowOpened or EventKind.WindowShown or EventKind.ForegroundChanged))
                    {
                        eventWoke = true;
                        Telemetry.IncLaunchEventWake();
                    }
                }
            }
            else
            {
                ct.WaitHandle.WaitOne(Math.Min(150, (int)Math.Max(1, deadline - total.ElapsedMilliseconds)));
            }

            if (!eventWoke)
            {
                Telemetry.IncLaunchFallbackPoll();
            }

            var win = MatchWindow(spawnedPid, hints, spawnedName,
                app.DisplayName, requestedName);
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
        var finalWin = MatchWindow(spawnedPid, hints, spawnedName, app.DisplayName, requestedName);
        if (finalWin != null)
        {
            return (null, null, "Ready", finalWin.Pid, finalWin.Hwnd,
                finalWin.Title, finalWin.ProcessName);
        }

        // debugging telemetry: what windows were visible at the deadline
        var seen = _windows.ListWindows()
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
        string? spawnedName, string displayName, string? requestedName = null)
    {
        var wins = _windows.ListWindows()
            .Where(w => !w.Bounds.IsEmpty && !string.IsNullOrEmpty(w.Title) &&
                        !IsShellSurface(w.Hwnd))
            .ToList();
        if (pid is { } p)
        {
            var byPid = wins.Where(w => w.Pid == p).ToList();
            if (byPid.Count > 0) return BestWindow(byPid);
            // spawned pid may be a launcher that exited or handed off —
            // fall through to name/title matching
        }
        var norm = Norm(displayName);
        var reqNorm = requestedName != null ? Norm(requestedName) : null;
        var named = wins.Where(w =>
        {
            var pn = NormProc(w.ProcessName);
            // 1. Direct process match against spawned name, requested name, or hints
            if (spawnedName != null && pn == spawnedName) return true;
            if (reqNorm != null && (pn == reqNorm || pn.StartsWith(reqNorm) || reqNorm.StartsWith(pn))) return true;
            if (hints.Count > 0 && (hints.Contains(pn) || hints.Any(h => h.Length >= 4 && (h.StartsWith(pn) || pn.StartsWith(h))))) return true;
            if (pn == norm || (norm.Length >= 4 && (norm.StartsWith(pn) || pn.StartsWith(norm)))) return true;

            // 2. Title matching fallback
            var winTitle = Norm(w.Title);
            if (winTitle.Contains(norm) || (reqNorm != null && winTitle.Contains(reqNorm))) return true;
            if (hints.Any(h => h.Length >= 4 && (winTitle.Contains(h) || pn.Contains(h)))) return true;

            // 3. UWP / ApplicationFrameHost special handling
            if (pn == "applicationframehost")
            {
                if (winTitle.Contains(norm) || (reqNorm != null && winTitle.Contains(reqNorm))) return true;
                if (hints.Any(h => h.Length >= 4 && winTitle.Contains(h))) return true;
                if (reqNorm != null && WellKnownLocalizedAliases.TryGetValue(reqNorm, out var reqAliases) &&
                    reqAliases.Any(a => winTitle.Contains(Norm(a)))) return true;
                if (WellKnownLocalizedAliases.TryGetValue(norm, out var normAliases) &&
                    normAliases.Any(a => winTitle.Contains(Norm(a)))) return true;
            }

            // 4. Localized aliases fallback for regular windows
            if (reqNorm != null && WellKnownLocalizedAliases.TryGetValue(reqNorm, out var rAliases) &&
                rAliases.Any(a => winTitle.Contains(Norm(a)) || pn.Contains(Norm(a)))) return true;
            if (WellKnownLocalizedAliases.TryGetValue(norm, out var nAliases) &&
                nAliases.Any(a => winTitle.Contains(Norm(a)) || pn.Contains(Norm(a)))) return true;

            return false;
        }).ToList();
        return BestWindow(named);
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
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                var n = NormProc(p.ProcessName);
                if (n == spawnedName || hints.Contains(n)) return true;
            }
        }
        catch { }
        return false;
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
