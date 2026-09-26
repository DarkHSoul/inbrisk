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
public sealed class AppService : IAppService
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

    internal sealed record ResolvedApp(LaunchMethod Method, string Identifier,
        string DisplayName, string[] ExeHints, int Score);

    public AppService(IWindowService windows, Func<long, bool>? uiaProbe = null,
        Func<string, bool>? isDeniedProcess = null)
    {
        _windows = windows;
        _uiaProbe = uiaProbe;
        _isDeniedProcess = isDeniedProcess ?? (_ => false);
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

        // ---- explicit identifiers resolve directly ----
        var resolved = ResolveDirect(spec, out var directErr);
        if (directErr != null)
        {
            var kind = directErr.Split(':', 2);
            return Fail(kind[0], kind.Length > 1 ? kind[1].Trim() : null,
                total);
        }
        ResolvedApp? chosen = null;
        List<ResolvedApp>? candidates = null;

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
            if (!spec.NewInstance &&
                FindRunning(spec.App, null) is { } existing)
                return Done(LaunchMethod.ExistingInstance, spec.App,
                    existing.ProcessName, existing.Pid, existing.Hwnd,
                    existing.Title, "AlreadyRunning", total, readyMs: 0);

            candidates = ResolveAll(spec.App);
            var best = PickBest(candidates, out var ambiguous);
            if (ambiguous != null)
                return new LaunchResult(false, null, spec.App, null, null,
                    null, null, "Failed", total.ElapsedMilliseconds, 0,
                    "AmbiguousApplication",
                    $"'{spec.App}' matches several applications — refine " +
                    "(use executable:/path:/aumid: or a more specific name)",
                    ambiguous.Select(c => new AppCandidate(c.DisplayName,
                        c.Method, c.Identifier, c.Score)).ToList());
            if (best == null)
                return Fail("TargetNotFound",
                    $"no application resolvable as '{spec.App}' " +
                    "(checked running windows, Start Menu, App Paths, " +
                    "packaged apps, PATH)", total);
            chosen = best;
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
        if (!spec.NewInstance && chosen.Method != LaunchMethod.Protocol &&
            FindRunning(chosen.DisplayName, chosen.ExeHints,
                titleMatch: spec.App != null) is { } running)
            return Done(LaunchMethod.ExistingInstance, chosen.DisplayName,
                running.ProcessName, running.Pid, running.Hwnd, running.Title,
                "AlreadyRunning", total, readyMs: 0);

        // ---- spawn ----
        var spawnMs = total.ElapsedMilliseconds;
        int? spawnedPid;
        try
        {
            spawnedPid = (Spawner ?? SpawnReal)(chosen, spec.Arguments);
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
            spec.TimeoutMs, ct, total);
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
        var windir = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var byName = new Dictionary<string, AppInfo>(
            StringComparer.OrdinalIgnoreCase);
        void Add(string? name, LaunchMethod m, string launch, string kind)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            byName.TryAdd(Norm(name), new(name, m, launch, kind));
        }

        foreach (var dir in StartMenuDirs())
        foreach (var lnk in EnumerateLinks(dir))
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            Add(name, LaunchMethod.StartMenu, $"app:\"{name}\"", "installed");
        }
        foreach (var (key, path) in AppPaths())
            if (File.Exists(path))
                Add(Path.GetFileNameWithoutExtension(key), LaunchMethod.AppPath,
                    $"executable:\"{key}\"",
                    path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)
                        ? "system" : "installed");
        foreach (var pkg in (PackageEnumerator ?? EnumeratePackages)())
            Add(pkg.DisplayName, LaunchMethod.Aumid,
                $"aumid:\"{pkg.Identifier}\"",
                pkg.Identifier.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                pkg.Identifier.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase) ||
                // _cw5n1h2txyewy is Microsoft's publisher id — covers
                // GUID-named inbox PFNs (FilePicker & friends)
                pkg.Identifier.Contains("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase)
                    ? "system" : "installed");

        return byName.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Every resolution mechanism for a friendly name, scored.</summary>
    internal List<ResolvedApp> ResolveAll(string app)
    {
        var found = new List<ResolvedApp>();
        var norm = Norm(app);

        // Start Menu shortcuts — filename carries the display name.
        // The shortcut's TARGET exe joins ExeHints: "File Explorer.lnk"
        // launches explorer.exe, so running-instance reuse and post-spawn
        // window matching must know the process name behind the lnk —
        // without it every launch misses FindRunning and spawns a dupe.
        foreach (var dir in StartMenuDirs())
        foreach (var lnk in EnumerateLinks(dir))
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            var s = Score(Norm(name), norm);
            if (s <= 0) continue;
            var tgtStem = LnkTargetStem(lnk);
            found.Add(new ResolvedApp(LaunchMethod.StartMenu, lnk, name,
                tgtStem != null ? [name, tgtStem] : [name],
                s + 5)); // StartMenu beats equal-score PATH noise
        }

        // App Paths — key "<exe>" → (Default) path
        foreach (var (keyName, path) in AppPaths())
        {
            var name = Path.GetFileNameWithoutExtension(keyName);
            var s = Score(Norm(name), norm);
            if (s > 0 && File.Exists(path))
                found.Add(new ResolvedApp(LaunchMethod.AppPath, path, name,
                    [name], s + 4));
        }

        // packaged apps — display name, package family name, exe hint and
        // full AUMID all match: localized systems show "Hesap Makinesi"
        // while the PFN keeps the canonical "Microsoft.WindowsCalculator"
        foreach (var pkg in (PackageEnumerator ?? EnumeratePackages)())
        {
            var s = Math.Max(Score(Norm(pkg.DisplayName), norm),
                Score(Norm(pkg.Identifier), norm));
            foreach (var hint in pkg.ExeHints)
                s = Math.Max(s, Score(Norm(hint), norm));
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

    /// <summary>Target executable stem of a .lnk via IShellLinkW — the
    /// process name the shortcut actually starts (File Explorer.lnk →
    /// "explorer"). Only resolved for scoring candidates, never for the
    /// whole Start Menu enumeration.</summary>
    private static unsafe string? LnkTargetStem(string lnk)
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
            if (path.Length == 0) return "explorer";
            if (!File.Exists(path)) return null;
            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return Path.GetFileNameWithoutExtension(path);
            // .lnk to a document — the window belongs to the registered
            // handler, same resolution as a `path:` launch
            var assoc = AssocExeFor(path);
            return assoc != null
                ? Path.GetFileNameWithoutExtension(assoc)
                : Path.GetFileNameWithoutExtension(path);
        }
        catch { return null; }
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

    private static int Score(string candidate, string norm)
        => candidate == norm ? 100
         : candidate.StartsWith(norm, StringComparison.Ordinal) ? 60
         : candidate.Contains(norm, StringComparison.Ordinal) ? 30
         : 0;

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
        foreach (var f in files) yield return f;
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
        return list;
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
            int timeoutMs, CancellationToken ct, Stopwatch total)
    {
        if (readiness == LaunchReadiness.None)
            return (null, null, "Started", spawnedPid, null, null, null);

        var deadline = total.ElapsedMilliseconds + timeoutMs;
        var hints = app.ExeHints.Append(app.DisplayName)
            .Select(Norm).Where(s => s.Length > 0).ToList();
        var spawnedName = app.Method is LaunchMethod.ExplicitPath
            or LaunchMethod.AppPath or LaunchMethod.Executable
            ? Norm(Path.GetFileNameWithoutExtension(app.Identifier))
            : null;

        while (total.ElapsedMilliseconds < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (readiness == LaunchReadiness.Process)
            {
                if (ProcessAlive(spawnedPid) ||
                    ProcessByName(hints, spawnedName))
                    return (null, null, "ProcessStarted", spawnedPid,
                        null, null, null);
            }
            else
            {
                var win = MatchWindow(spawnedPid, hints, spawnedName,
                    app.DisplayName);
                if (win != null && !IsHung(win.Hwnd) &&
                    (_uiaProbe?.Invoke(win.Hwnd) ?? true))
                    return (null, null, "Ready", win.Pid, win.Hwnd, win.Title,
                        win.ProcessName);
            }
            ct.WaitHandle.WaitOne(150);
        }
        // debugging telemetry: what windows were visible at the deadline
        var seen = _windows.ListWindows()
            .Where(w => !w.Bounds.IsEmpty)
            .Select(w => $"{w.ProcessName} \"{w.Title}\" pid={w.Pid}")
            .Take(12);
        return ("Timeout",
            $"launched '{app.DisplayName}' via {app.Method} but no " +
            $"{(readiness == LaunchReadiness.Window ? "usable window" : "process")} " +
            $"within {timeoutMs}ms — windows: {string.Join(" | ", seen)}",
            "TimedOut", spawnedPid, null, null, null);
    }

    private WindowInfo? MatchWindow(int? pid, List<string> hints,
        string? spawnedName, string displayName)
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
        var named = wins.Where(w =>
        {
            var pn = NormProc(w.ProcessName);
            return pn == spawnedName || hints.Contains(pn) ||
                   (pn.Length > 0 && Norm(w.Title).Contains(norm));
        }).ToList();
        return BestWindow(named);
    }

    private static WindowInfo? BestWindow(List<WindowInfo> wins)
        => wins
            .OrderByDescending(w => w.IsForeground)
            .ThenByDescending(w => w.Bounds.Width * w.Bounds.Height)
            .ThenBy(w => w.Hwnd)
            .FirstOrDefault();

    private static bool ProcessAlive(int? pid)
    {
        if (pid is not { } p) return false;
        try { return !Process.GetProcessById(p).HasExited; }
        catch { return false; }
    }

    private static bool ProcessByName(List<string> hints, string? spawnedName)
    {
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
