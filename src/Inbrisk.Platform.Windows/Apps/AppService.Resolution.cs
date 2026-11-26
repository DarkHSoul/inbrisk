using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Microsoft.Win32;
using Windows.Win32;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Apps;

public sealed partial class AppService : IAppService, IDisposable
{
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
}
