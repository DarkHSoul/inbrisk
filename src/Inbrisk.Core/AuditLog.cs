using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inbrisk.Core;

/// <summary>
/// Audit-log location and integrity chain (F32).
///
/// The audit trail (one JSONL record per computer-control action, plus
/// emergency-control transitions) must live somewhere the agent cannot
/// quietly rewrite: %ProgramData%\Inbrisk\audit, created with an explicit
/// ACL — SYSTEM and Administrators full control, the owning user Modify —
/// instead of the world-writable profile directory. Every record carries a
/// chained hash ("prev" + "h"), so deleting or rewriting history breaks the
/// chain and is detectable via <see cref="VerifyChain"/>.
///
/// A byte-identical mirror stays at %LOCALAPPDATA%\inbrisk\*.jsonl so the
/// settings UI and diagnostic tools keep working; the mirror is
/// agent-readable AND agent-writable — it is a convenience copy, never the
/// source of truth. Divergence between mirror and primary is itself a
/// tamper signal.
///
/// INBRISK_AUDIT_DIR overrides the primary directory (tests, portable mode).
/// If %ProgramData% is not writable (exotic sandbox) we fall back to the
/// data dir and keep the hash chain — detection still works, only the
/// location hardening is lost.
/// </summary>
public static class AuditLog
{
    public const string DirEnvVar = "INBRISK_AUDIT_DIR";

    private static string? _dir;
    private static readonly object _gate = new();

    /// <summary>Primary audit directory (best available, see class doc).</summary>
    public static string Dir
    {
        get
        {
            lock (_gate)
                return _dir ??= Resolve();
        }
    }

    /// <summary>Primary audit file path for a given log name.</summary>
    public static string Path(string fileName) =>
        System.IO.Path.Combine(Dir, fileName);

    /// <summary>
    /// Agent-readable mirror path for the same log, or null when the primary
    /// already lives in the data dir (nothing to mirror).
    /// </summary>
    public static string? MirrorPath(string fileName)
    {
        var dataDir = UserSettings.DataDir;
        return Dir.TrimEnd('\\', '/')
            .Equals(dataDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            ? null
            : System.IO.Path.Combine(dataDir, fileName);
    }

    private static string Resolve()
    {
        var overrideDir = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            Directory.CreateDirectory(overrideDir);
            return overrideDir;
        }

        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Inbrisk", "audit");
            Directory.CreateDirectory(dir);
            TryHardenAcl(dir);
            // ProgramData accepts folder creation from standard users, but
            // exotic ACLs may still block writes — probe before committing.
            var probe = System.IO.Path.Combine(dir, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return dir;
        }
        catch
        {
            return UserSettings.DataDir;
        }
    }

    /// <summary>
    /// Replace inherited ACLs: SYSTEM + Administrators full control, the
    /// creating user Modify (append + rotation need write; other standard
    /// users get nothing). Best-effort — failure never blocks logging.
    /// </summary>
    private static void TryHardenAcl(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sec = new System.Security.AccessControl.DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const System.Security.AccessControl.InheritanceFlags inherit =
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                System.Security.AccessControl.FileSystemRights.FullControl,
                inherit, System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user != null)
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.Modify |
                    System.Security.AccessControl.FileSystemRights.Synchronize,
                    inherit, System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);
        }
        catch { /* ACL hardening is best-effort; the hash chain still detects tampering */ }
    }

    // ------------------------------------------------------------ hash chain

    /// <summary>Genesis link value for the first record in a file.</summary>
    public const string GenesisHash =
        "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// The "h" of the last chained line in a file, the SHA-256 of the whole
    /// file when it predates chaining, or <see cref="GenesisHash"/>.
    /// </summary>
    public static string LastChainHash(string path)
    {
        try
        {
            if (!File.Exists(path)) return GenesisHash;
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return GenesisHash;
            string? lastLine = null;
            foreach (var line in Encoding.UTF8.GetString(bytes)
                         .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length > 0) lastLine = t;
            }
            if (lastLine != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(lastLine);
                    if (doc.RootElement.TryGetProperty("h", out var h) &&
                        h.ValueKind == JsonValueKind.String)
                        return h.GetString()!;
                }
                catch { /* legacy/unparseable tail — fall through */ }
            }
            // Pre-chain log: anchor on the whole file so history stays bound.
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }
        catch { return GenesisHash; }
    }

    /// <summary>
    /// Stamp "prev" + "h" onto a record object and return the finished line.
    /// h = SHA-256 over the record JSON (including prev, excluding h).
    /// </summary>
    public static string ChainLine(JsonObject record, string prevHash)
    {
        record["prev"] = prevHash;
        var h = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(record.ToJsonString())))
            .ToLowerInvariant();
        record["h"] = h;
        return record.ToJsonString();
    }

    /// <summary>
    /// Verify the hash chain of an audit file. Returns true when every line's
    /// "prev" links to the previous "h" and each "h" recomputes.
    /// </summary>
    public static bool VerifyChain(string path, out string? error)
    {
        error = null;
        try
        {
            var prev = GenesisHash;
            var n = 0;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                n++;
                JsonObject obj;
                try { obj = JsonNode.Parse(line)!.AsObject(); }
                catch { error = $"line {n}: unparseable"; return false; }
                var p = obj["prev"]?.GetValue<string>();
                var h = obj["h"]?.GetValue<string>();
                if (p == null || h == null)
                {
                    error = $"line {n}: unchained record";
                    return false;
                }
                if (p != prev)
                {
                    error = $"line {n}: chain break (prev mismatch)";
                    return false;
                }
                obj.Remove("h");
                var expect = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(obj.ToJsonString())))
                    .ToLowerInvariant();
                if (expect != h)
                {
                    error = $"line {n}: record altered (h mismatch)";
                    return false;
                }
                prev = h;
            }
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }
}
