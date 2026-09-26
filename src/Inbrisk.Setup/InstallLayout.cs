using System.Reflection;
using System.Security.Cryptography;

namespace Inbrisk.Setup;

/// <summary>
/// Install paths and build provenance. Per-user install is the default —
/// no admin, no UAC, matches VS Code/Cursor conventions. Machine-wide
/// install (Program Files) is opt-in and requires elevation.
/// </summary>
public static class InstallLayout
{
    public const string ProductName = "Inbrisk";
    public const string ExeName = "inbrisk.exe";

    /// <summary>Default per-user install root: %LOCALAPPDATA%\Programs\Inbrisk.</summary>
    public static string UserInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ProductName);

    public static string MachineInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ProductName);

    /// <summary>Per-user runtime state: telemetry, backups, update staging.
    /// INBRISK_DATA_DIR overrides it (tests, portable mode).</summary>
    public static string DataDir =>
        Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "inbrisk");

    public static string BackupDir => Path.Combine(DataDir, "host-backups");
    public static string UpdateStagingDir => Path.Combine(DataDir, "update-staging");
    public static string PendingUpdateMarker => Path.Combine(DataDir, "pending-update.json");

    public static string EmergencyMarkerPath =>
        Environment.GetEnvironmentVariable("INBRISK_EMERGENCY_STATE") ??
        Path.Combine(DataDir, "emergency-stop.flag");

    /// <summary>Where this running binary lives.</summary>
    public static string ProcessExePath =>
        Environment.ProcessPath ??
        Path.Combine(AppContext.BaseDirectory, ExeName);

    public static string ProcessDir =>
        Path.GetDirectoryName(ProcessExePath) ?? AppContext.BaseDirectory;

    /// <summary>True when running from a known install root (not a build tree).</summary>
    public static bool IsInstalled =>
        PathsEqual(ProcessDir, UserInstallDir) ||
        PathsEqual(ProcessDir, MachineInstallDir);

    /// <summary>The exe path MCP host configs should point at.</summary>
    public static string CanonicalExePath =>
        Path.Combine(IsInstalled ? ProcessDir : UserInstallDir, ExeName);

    public static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static Assembly SelfAssembly => typeof(InstallLayout).Assembly;

    public static string Version =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0]
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "0.0.0";

    private static string? Meta(string key) =>
        Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

    public static string? BuildCommit =>
        string.IsNullOrWhiteSpace(Meta("BuildCommit")) ? null : Meta("BuildCommit");

    public static string? BuildTimestamp => Meta("BuildTimestamp");

    /// <summary>SHA-256 of the running executable — provenance anchor for
    /// doctor/status so a dev build can never masquerade as a release.</summary>
    public static string SelfSha256()
    {
        try
        {
            using var s = File.OpenRead(ProcessExePath);
            return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
        }
        catch { return "unavailable"; }
    }
}
