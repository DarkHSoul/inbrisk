using System.Diagnostics;
using System.Text.Json;

namespace Inbrisk.Setup;

/// <summary>
/// Read-only view of the machine-wide emergency-control state for
/// `inbrisk status`/`doctor`: the latched stop marker plus the live panic
/// authority (the process that owns the emergency hotkey). The authority
/// record is written by whichever process holds the chord; validating its
/// pid + start time distinguishes "delegated to a live Inbrisk process"
/// from "chord held by a foreign process / stale record" — the state that
/// silently keeps every new MCP session EmergencyStopped.
/// </summary>
public static class EmergencyProbe
{
    public sealed record Snapshot(
        bool StopMarkerPresent,
        int? AuthorityPid,
        bool AuthorityAlive,
        int InbriskProcessCount,
        bool MutexAuthority)
    {
        /// <summary>Effective control state as a new session would see it.</summary>
        public string ControlState =>
            StopMarkerPresent ? "EmergencyStopped"
            : AuthorityAlive || MutexAuthority ? "Active"
            : "NoAuthority";

        public string Detail => this switch
        {
            { StopMarkerPresent: true } =>
                "STOPPED (panic marker present — Ctrl+Alt+Shift+Pause or tray to resume)",
            { AuthorityAlive: true, AuthorityPid: { } p } =>
                $"authority pid {p} holds the panic hotkey",
            { MutexAuthority: true } =>
                "panic hotkey owned by an Inbrisk process (mutex-held, no owner " +
                "record — an older binary; peers delegate via stop marker)",
            { AuthorityPid: { } p, AuthorityAlive: false } =>
                $"stale owner record (pid {p} gone) and panic hotkey unowned — " +
                "new sessions stay stopped; restart the inbrisk shell/tray",
            { InbriskProcessCount: > 0 } =>
                "no panic authority — the chord is held by a non-inbrisk process " +
                "or was never acquired; new sessions stay stopped until the " +
                "shell/tray claims it (restart inbrisk processes)",
            _ => "no panic authority yet — the shell/tray or an MCP session claims it on start",
        };
    }

    public static Snapshot Read()
    {
        var marker = File.Exists(InstallLayout.EmergencyMarkerPath);
        int? pid = null;
        var alive = false;
        try
        {
            var ownerPath = Path.Combine(
                Path.GetDirectoryName(InstallLayout.EmergencyMarkerPath)!,
                "emergency-owner.json");
            if (File.Exists(ownerPath))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(ownerPath));
                if (d.RootElement.TryGetProperty("pid", out var p) &&
                    p.ValueKind == JsonValueKind.Number)
                {
                    pid = p.GetInt32();
                    DateTime? start = d.RootElement.TryGetProperty("startTimeUtc", out var s) &&
                        DateTime.TryParse(s.GetString(), null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var st)
                        ? st : null;
                    try
                    {
                        using var proc = Process.GetProcessById(pid.Value);
                        alive = proc.ProcessName.Contains("inbrisk",
                            StringComparison.OrdinalIgnoreCase) &&
                            (start == null || Math.Abs(
                                (proc.StartTime.ToUniversalTime() - start.Value)
                                .TotalSeconds) < 2);
                    }
                    catch { }
                }
            }
        }
        catch { }
        // Fallback channel: an older binary holds the panic chord but never
        // wrote an owner record — detect it through the authority mutex so
        // "delegated to a live inbrisk process" isn't misreported as
        // NoAuthority. The mutex name is scoped by the configured chord +
        // marker path; replicate the EmergencyControl hash here (Setup has
        // no Core reference).
        var mutexHeld = false;
        try
        {
            var mutexName = @"Global\InbriskPanicHotkeyOwner-" +
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(
                        PanicChord().ToLowerInvariant() + "|" +
                        InstallLayout.EmergencyMarkerPath.ToLowerInvariant())))[..8];
            using var m = Mutex.OpenExisting(mutexName);
            try
            {
                var free = m.WaitOne(0);
                if (free) m.ReleaseMutex();
                mutexHeld = !free;
            }
            catch (AbandonedMutexException)
            { try { m.ReleaseMutex(); } catch { } }
        }
        catch { }
        var procs = 0;
        try { procs = Process.GetProcessesByName("inbrisk").Length; } catch { }
        return new Snapshot(marker, pid, alive, procs, mutexHeld);
    }

    /// <summary>The configured panic chord — same resolution the MCP host
    /// uses: env override, then settings.json, then the built-in default.</summary>
    private static string PanicChord()
    {
        var env = Environment.GetEnvironmentVariable("INBRISK_PANIC_HOTKEY");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var settingsPath = Path.Combine(InstallLayout.DataDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (d.RootElement.TryGetProperty("PanicHotkey", out var p) &&
                    p.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(p.GetString()))
                    return p.GetString()!;
                if (d.RootElement.TryGetProperty("panicHotkey", out var p2) &&
                    p2.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(p2.GetString()))
                    return p2.GetString()!;
            }
        }
        catch { }
        return "Ctrl+Alt+Pause";
    }
}
