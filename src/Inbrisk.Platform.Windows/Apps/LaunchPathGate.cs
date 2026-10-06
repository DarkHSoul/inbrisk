using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;
using Windows.Win32;

namespace Inbrisk.Platform.Windows.Apps;

/// <summary>
/// Launch-target path policy for computer_launch's explicit file lanes
/// (<c>path:</c>, and the file fast-path of <c>executable:</c>).
///
/// Rationale: <see cref="AppService"/> already refuses shell hosts BY NAME
/// ("computer_launch is an app launcher, not a shell"), but a raw file path
/// used to bypass that — <c>Process.Start</c> with <c>UseShellExecute</c> on
/// a <c>.bat</c>/<c>.ps1</c>/<c>.lnk</c>→script executes arbitrary commands
/// with no consent and no audit trail.
///
/// Three verdicts:
///   <see cref="Verdict.Allowed"/> — <c>.exe</c>, and document/data files
///     that open through the registered handler (.uproject → UnrealEditor).
///   <see cref="Verdict.ScriptRequiresConsent"/> — extensions whose "open"
///     verb runs attacker-controlled code (or mutates system state), and
///     shortcuts whose target can't be verified. Blocked unless the user
///     opted in: env <see cref="AllowEnvVar"/> or
///     <c>"allowScriptLaunch": true</c> in settings.json.
///   <see cref="Verdict.Denied"/> — never launchable: empty, malformed
///     (illegal filename chars / control chars), UNC (remote payload
///     staging), or a .lnk pointing at a shell host. Consent never
///     overrides Denied.
///
/// Every non-allowed decision AND every consent-permitted script launch is
/// written to the hash-chained audit log (launch-gate.jsonl) — same ACL'd
/// ProgramData location + data-dir mirror as the emergency-control trail.
/// </summary>
internal static class LaunchPathGate
{
    public enum Verdict
    {
        Allowed,
        ScriptRequiresConsent,
        Denied,
    }

    /// <summary>One classified target. <paramref name="Target"/> is the
    /// caller-supplied path (or the resolved .lnk target for inner
    /// decisions); <paramref name="Detail"/> carries the human/agent-facing
    /// reason for non-allowed verdicts.</summary>
    public sealed record Decision(Verdict Verdict, string Target,
        string? Extension, string? Detail);

    /// <summary>Env escape hatch — per-process opt-in for scripted
    /// automation. "1"/"true"/"yes"/"on" enables. Default OFF.</summary>
    public const string AllowEnvVar = "INBRISK_ALLOW_SCRIPT_LAUNCH";

    /// <summary>Extensions whose "open" verb executes attacker-controlled
    /// code or mutates system state rather than rendering a document.</summary>
    private static readonly HashSet<string> ScriptExtensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd",          // cmd.exe batch
        ".ps1",                  // PowerShell
        ".vbs", ".vbe",          // WSH VBScript (vbe = encoded)
        ".js", ".jse",           // WSH JScript (jse = encoded)
        ".wsf", ".wsh",          // Windows Script Files / settings
        ".hta",                  // mshta — full-trust HTML application
        ".msi", ".msp", ".mst",  // msiexec packages / patches / transforms
        ".reg",                  // registry merge — silent state mutation
        ".scr",                  // screensaver — a PE run on "open"
        ".com", ".pif",          // legacy executable formats (non-.exe)
        ".msc",                  // MMC console — loads snap-ins in mmc.exe
        ".cpl",                  // control-panel applet — DLL run via rundll32
        ".url",                  // internet shortcut — wraps ANY scheme,
                                 // bypassing the uri: DeepLinkSecurity gate
    };

    /// <summary>Chain limit for .lnk → .lnk resolution — past this the
    /// target is treated as unverifiable.</summary>
    private const int MaxLnkDepth = 3;

    // ------------------------------------------------------------------
    //  classification
    // ------------------------------------------------------------------

    /// <summary>Classify a caller-supplied launch path — no consent check,
    /// no I/O beyond optional .lnk target resolution.</summary>
    public static Decision Classify(string? rawPath)
        => ClassifyCore(rawPath, 0);

    private static Decision ClassifyCore(string? rawPath, int lnkDepth)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return new(Verdict.Denied, rawPath ?? "", null, "empty path");

        var p = rawPath.Trim().Trim('"');

        // Control characters can hide a second command in logs/error echoes
        // and are never legal in a Windows path anyway.
        foreach (var c in p)
            if (char.IsControl(c))
                return new(Verdict.Denied, p, null,
                    "control character in path");

        // The leaf must be a plausible filename — <>"|?*: are illegal in
        // Windows filenames (and ':' also covers NTFS alternate-stream
        // launches), so their presence means the string was never a real
        // file name: treat as malformed/denied.
        var lastSep = p.LastIndexOfAny(Separators);
        var leaf = lastSep >= 0 ? p[(lastSep + 1)..] : p;
        if (leaf.Length == 0 ||
            leaf.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new(Verdict.Denied, p, null,
                $"illegal filename character in '{leaf}'");

        // UNC — remote payload staging (\\server\share, //server/share).
        // The extended-length local prefixes \\?\ and \\.\ stay local.
        var unc = p.StartsWith("//", StringComparison.Ordinal) ||
                  (p.StartsWith(@"\\", StringComparison.Ordinal) &&
                   !p.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                   !p.StartsWith(@"\\.\", StringComparison.Ordinal));
        if (unc)
            return new(Verdict.Denied, p, null,
                "UNC path — remote script/payload staging is not launchable");

        var ext = Path.GetExtension(p);
        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return ClassifyLnk(p, lnkDepth);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(Verdict.Allowed, p, ext, null);
        if (ScriptExtensions.Contains(ext))
            return new(Verdict.ScriptRequiresConsent, p, ext,
                $"extension '{ext}' executes arbitrary code on open");
        // document/data file — spawned through the registered handler
        // (.uproject → UnrealEditor); the handler exe is what runs.
        return new(Verdict.Allowed, p, ext, null);
    }

    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>A .lnk inherits the verdict of its TARGET — a shortcut to a
    /// real exe/document is benign; a shortcut to a script is the script;
    /// a shortcut to a shell host is a denial (same bar as the interpreter
    /// blocklist — `cmd /c` must not be laundered through a file).
    /// Unresolvable targets fail closed to consent.</summary>
    private static Decision ClassifyLnk(string lnk, int depth)
    {
        if (depth >= MaxLnkDepth)
            return new(Verdict.ScriptRequiresConsent, lnk, ".lnk",
                "shortcut chain too deep — target unverifiable");

        var target = LnkTargetPath(lnk);
        if (target == null)
            return new(Verdict.ScriptRequiresConsent, lnk, ".lnk",
                "shortcut target could not be resolved — refusing to " +
                "launch an unverifiable shortcut");

        var stem = Path.GetFileNameWithoutExtension(target);
        if (AppService.ShellHosts.Contains(stem) ||
            AppService.ShellHosts.Contains(stem + ".exe"))
            return new(Verdict.Denied, lnk, ".lnk",
                $"shortcut points at shell host '{stem}' — " +
                "computer_launch is not a shell");

        var inner = ClassifyCore(target, depth + 1);
        if (inner.Verdict == Verdict.Allowed)
            return new(Verdict.Allowed, lnk, ".lnk", null);
        return new(inner.Verdict, lnk, ".lnk",
            $"shortcut → {inner.Target}: {inner.Detail}");
    }

    /// <summary>IShellLinkW target path of a .lnk — same COM read as
    /// AppService.InspectLnk, kept local so the gate stays self-contained
    /// and read-only (SLR_NO_UI: never surface a repair dialog).</summary>
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
            var path = new string(buf).TrimEnd('\0');
            return path.Length > 0 ? path : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    //  consent
    // ------------------------------------------------------------------

    /// <summary>Script-launch consent — env flag (per-process escape hatch)
    /// or the persisted user setting in settings.json. Default OFF.
    /// <paramref name="source"/> reports which grant applied, for audit.</summary>
    public static bool ConsentGranted(out string? source)
    {
        source = null;
        var env = Environment.GetEnvironmentVariable(AllowEnvVar);
        if (env != null &&
            (env == "1" ||
             env.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             env.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
             env.Equals("on", StringComparison.OrdinalIgnoreCase)))
        {
            source = "env:" + AllowEnvVar;
            return true;
        }
        try
        {
            if (UserSettings.LoadCached().AllowScriptLaunch)
            {
                source = "settings.json:allowScriptLaunch";
                return true;
            }
        }
        catch { /* unreadable settings = no consent */ }
        return false;
    }

    // ------------------------------------------------------------------
    //  gate entry point
    // ------------------------------------------------------------------

    /// <summary>Full gate: classify, then consult consent only for
    /// script-class targets. Denied never consults consent. Every gated
    /// decision (denied, consent-blocked, consent-permitted) is audited —
    /// the audit is fire-and-forget and can never unblock a launch.</summary>
    public static Decision Evaluate(string? rawPath)
    {
        var d = Classify(rawPath);
        if (d.Verdict == Verdict.ScriptRequiresConsent &&
            ConsentGranted(out var src))
        {
            Audit(d, outcome: "AllowedScriptByConsent", via: src);
            return d with
            {
                Verdict = Verdict.Allowed,
                Detail = $"{d.Detail} — permitted by {src}",
            };
        }
        if (d.Verdict != Verdict.Allowed)
            Audit(d, outcome: d.Verdict.ToString());
        return d;
    }

    // ------------------------------------------------------------------
    //  audit
    // ------------------------------------------------------------------

    private static readonly object AuditGate = new();
    private static string? _auditPath;
    private static string? _mirrorPath;
    private static bool _pathsResolved;

    /// <summary>Hash-chained record per gated decision — launch-gate.jsonl
    /// in the ACL'd audit dir plus the data-dir mirror, matching the
    /// emergency-control trail. If the audit write fails we fall back to
    /// PerfLog; either way the verdict stands (log failure ≠ allow).</summary>
    private static void Audit(Decision d, string outcome, string? via = null)
    {
        try
        {
            if (!_pathsResolved)
            {
                _auditPath = AuditLog.Path("launch-gate.jsonl");
                _mirrorPath = AuditLog.MirrorPath("launch-gate.jsonl");
                _pathsResolved = true;
            }
            var record = JsonSerializer.SerializeToNode(new
            {
                at = DateTimeOffset.UtcNow,
                category = "launch_path_gate",
                verdict = outcome,
                via,
                target = d.Target,
                extension = d.Extension,
                detail = d.Detail,
            })!.AsObject();
            lock (AuditGate)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(_auditPath!)!);
                var line = AuditLog.ChainLine(record,
                    AuditLog.LastChainHash(_auditPath!));
                File.AppendAllText(_auditPath!, line + "\n");
                try
                {
                    if (_mirrorPath != null)
                        File.AppendAllText(_mirrorPath, line + "\n");
                }
                catch { /* best-effort mirror */ }
            }
        }
        catch (Exception e)
        {
            try
            {
                PerfLog.Write(new
                {
                    kind = "launch_path_gate",
                    at = DateTimeOffset.UtcNow,
                    verdict = outcome,
                    via,
                    target = d.Target,
                    extension = d.Extension,
                    detail = d.Detail,
                    auditError = e.Message,
                });
            }
            catch { /* telemetry must never break a launch */ }
        }
    }
}
