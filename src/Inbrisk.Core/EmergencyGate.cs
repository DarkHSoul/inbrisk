using System.Diagnostics;
using System.Text.Json;

namespace Inbrisk.Core;

/// <summary>
/// Cross-process emergency-stop state shared by every Inbrisk binary —
/// MCP servers, the desktop shell, and one-shot CLI invocations. The stop
/// marker is written only by the local emergency authority; automation
/// paths read it and must never clear it. The owner record identifies the
/// process currently holding the panic hotkey so peers and diagnostics can
/// tell "delegated to a live Inbrisk process" apart from "chord held by a
/// foreign process or stale state".
/// </summary>
public static class EmergencyGate
{
    private static string DataDir =>
        Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk");

    /// <summary>Latched panic marker — presence means computer control is
    /// stopped machine-wide until the local user resumes.</summary>
    public static string MarkerPath =>
        Environment.GetEnvironmentVariable("INBRISK_EMERGENCY_STATE") ??
        Path.Combine(DataDir, "emergency-stop.flag");

    /// <summary>Record of the process currently owning the panic hotkey.</summary>
    public static string OwnerPath => Path.Combine(
        Path.GetDirectoryName(MarkerPath)!, "emergency-owner.json");

    /// <summary>Named mutex the authority holds while it owns the panic
    /// hotkey — the fallback authority signal for processes running older
    /// binaries that never wrote an owner record. Scoped by chord + marker
    /// path: differently-configured processes must not share an authority
    /// channel.</summary>
    public static string OwnerMutexName(string panicHotkey) =>
        @"Global\InbriskPanicHotkeyOwner-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(
                panicHotkey.ToLowerInvariant() + "|" +
                MarkerPath.ToLowerInvariant())))[..8];

    /// <summary>Is the authority mutex currently held by a live process?
    /// Tri-state: true = held (an authority exists even without an owner
    /// record — e.g. an older binary), false = mutex exists but unowned,
    /// null = mutex was never created or could not be opened.</summary>
    public static bool? MutexAuthorityHeld(string panicHotkey)
    {
        try
        {
            using var m = Mutex.OpenExisting(OwnerMutexName(panicHotkey));
            try
            {
                var free = m.WaitOne(0);
                if (free) m.ReleaseMutex();
                return !free;
            }
            catch (AbandonedMutexException)
            { try { m.ReleaseMutex(); } catch { } return false; }
        }
        catch (WaitHandleCannotBeOpenedException) { return null; }
        catch { return null; }
    }

    /// <summary>A local panic stop is latched. Mutating computer actions
    /// must refuse while this is true — in EVERY process, not only the one
    /// holding the panic hotkey (CLI one-shots included).</summary>
    public static bool IsStopped => File.Exists(MarkerPath);

    /// <summary>Who currently owns the panic hotkey (the authority).</summary>
    public sealed record OwnerInfo(int Pid, DateTime StartTimeUtc,
        string? Exe, string PanicHotkey, DateTime WrittenAtUtc);

    /// <summary>Called by the process that successfully registers the panic
    /// hotkey — publishes itself as the authority for peers + diagnostics.</summary>
    public static void WriteOwner(string panicHotkey)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnerPath)!);
            using var p = Process.GetCurrentProcess();
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["pid"] = p.Id,
                ["startTimeUtc"] = p.StartTime.ToUniversalTime().ToString("O"),
                ["exe"] = p.MainModule?.FileName,
                ["panicHotkey"] = panicHotkey,
                ["writtenAtUtc"] = DateTime.UtcNow.ToString("O"),
            });
            var tmp = OwnerPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, OwnerPath, overwrite: true);
        }
        catch { /* advisory channel — never break control startup */ }
    }

    /// <summary>Remove the owner record only if it still names this process.</summary>
    public static void ClearOwner(int pid)
    {
        try { if (ReadOwner()?.Pid == pid) File.Delete(OwnerPath); }
        catch { }
    }

    public static OwnerInfo? ReadOwner()
    {
        try
        {
            if (!File.Exists(OwnerPath)) return null;
            using var d = JsonDocument.Parse(File.ReadAllText(OwnerPath));
            var r = d.RootElement;
            if (!r.TryGetProperty("pid", out var p) || p.ValueKind != JsonValueKind.Number)
                return null;
            var start = r.TryGetProperty("startTimeUtc", out var s) &&
                DateTime.TryParse(s.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var st)
                ? st : DateTime.MinValue;
            var written = r.TryGetProperty("writtenAtUtc", out var w) &&
                DateTime.TryParse(w.GetString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var wt)
                ? wt : DateTime.MinValue;
            return new OwnerInfo(p.GetInt32(), start,
                r.TryGetProperty("exe", out var e) ? e.GetString() : null,
                r.TryGetProperty("panicHotkey", out var h) ? h.GetString() ?? "" : "",
                written);
        }
        catch { return null; }
    }

    /// <summary>Is the recorded authority process still alive? Validates pid
    /// AND start time so a recycled pid cannot impersonate a dead authority.</summary>
    public static bool OwnerAlive(OwnerInfo o)
    {
        try
        {
            using var p = Process.GetProcessById(o.Pid);
            if (!p.ProcessName.Contains("inbrisk", StringComparison.OrdinalIgnoreCase))
                return false;
            if (o.StartTimeUtc != DateTime.MinValue &&
                Math.Abs((p.StartTime.ToUniversalTime() - o.StartTimeUtc).TotalSeconds) > 2)
                return false;
            return true;
        }
        catch { return false; }
    }
}
