using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Inbrisk.Setup;

/// <summary>
/// Self-install / uninstall / update mechanics. The distributable
/// InbriskSetup.exe IS the inbrisk binary — running it under that name (or
/// `inbrisk install`) copies the payload to the install dir and registers a
/// per-user "Apps & Features" uninstall entry. No external installer
/// toolchain, no admin for the default per-user install.
/// </summary>
public static class SelfInstaller
{
    public sealed record InstallResult(bool Ok, string InstallDir, string Message,
        bool Elevated = false);

    private const string UninstallKeyPerUser =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Inbrisk";
    private const string UninstallKeyMachine =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inbrisk";

    public static bool RunningAsSetupExe =>
        Path.GetFileName(InstallLayout.ProcessExePath)
            .StartsWith("inbrisksetup", StringComparison.OrdinalIgnoreCase);

    /// <summary>Copy payload files + register uninstall entry.</summary>
    public static InstallResult Install(bool machine = false)
    {
        var targetDir = machine ? InstallLayout.MachineInstallDir : InstallLayout.UserInstallDir;
        if (machine && !IsElevated())
        {
            // hand off to an elevated copy of ourselves — the user sees a
            // normal UAC prompt; we never silently elevate. runas requires
            // ShellExecute, so no redirected output: verify by the dir.
            try
            {
                using var p = Process.Start(new ProcessStartInfo(
                    InstallLayout.ProcessExePath, "install --machine --elevated-child")
                { Verb = "runas", UseShellExecute = true });
                p!.WaitForExit(120000);
                var done = File.Exists(Path.Combine(targetDir, InstallLayout.ExeName));
                return new InstallResult(done, targetDir,
                    done ? "installed (elevated)" : "elevated install did not complete",
                    Elevated: true);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new InstallResult(false, targetDir,
                    "UAC consent was declined — machine-wide install needs admin",
                    Elevated: true);
            }
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            var srcDir = InstallLayout.ProcessDir;
            // Payload = the exe + files a framework-dependent build needs.
            // Never copy arbitrary neighbors (zips, manifests, user files
            // sitting next to a downloaded InbriskSetup.exe).
            var payload = Directory.GetFiles(srcDir).Where(f =>
            {
                var n = Path.GetFileName(f);
                if (n.StartsWith("inbrisksetup", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("inbrisk.exe", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("inbrisk-mcp.exe", StringComparison.OrdinalIgnoreCase))
                    return true;
                return n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
            });
            try
            {
                foreach (var f in payload)
                {
                    var name = Path.GetFileName(f);
                    if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                    var dest = Path.Combine(targetDir,
                        name.StartsWith("inbrisksetup", StringComparison.OrdinalIgnoreCase)
                            ? InstallLayout.ExeName : name);
                    File.Copy(f, dest, overwrite: true);
                }
            }
            catch (IOException) when (File.Exists(Path.Combine(targetDir, InstallLayout.ExeName)))
            {
                // target locked — a live MCP session is running from the
                // install dir. If the installed binary is already identical,
                // this is a no-op reinstall; otherwise defer honestly rather
                // than swapping binaries under an active session.
                // Compare the managed payload (inbrisk.dll), not the apphost
                // exe — the exe sha stays identical across source changes.
                var localDll = Path.Combine(InstallLayout.ProcessDir, "inbrisk.dll");
                var targetDll = Path.Combine(targetDir, "inbrisk.dll");
                var same = File.Exists(localDll) && File.Exists(targetDll)
                    ? Sha256Of(localDll) == Sha256Of(targetDll)
                    : InstallLayout.SelfSha256() == Sha256Of(
                        Path.Combine(targetDir, InstallLayout.ExeName));
                if (same)
                {
                    WriteUninstallEntry(Path.Combine(targetDir, InstallLayout.ExeName),
                        targetDir, machine);
                    return new InstallResult(true, targetDir,
                        $"already installed at {targetDir} (running session kept)");
                }
                // different build, locked dir → stage a pending update instead
                // of failing: onboarding continues, swap happens at next idle
                var staged = StagePayload(payload, targetDir);
                return new InstallResult(true, targetDir,
                    $"new version staged — applies when the active session ends " +
                    $"(staged: {staged})", Elevated: machine);
            }

            var exe = Path.Combine(targetDir, InstallLayout.ExeName);
            WriteUninstallEntry(exe, targetDir, machine);
            File.WriteAllText(Path.Combine(targetDir, "install-metadata.json"),
                JsonSerializer.Serialize(new
                {
                    version = InstallLayout.Version,
                    buildCommit = InstallLayout.BuildCommit,
                    buildTimestamp = InstallLayout.BuildTimestamp,
                    sha256 = InstallLayout.SelfSha256(),
                    installedAt = DateTimeOffset.UtcNow,
                    scope = machine ? "machine" : "user",
                }, new JsonSerializerOptions { WriteIndented = true }));
            return new InstallResult(true, targetDir, $"installed to {targetDir}");
        }
        catch (Exception e)
        {
            return new InstallResult(false, targetDir, $"install failed: {e.Message}");
        }
    }

    /// <summary>Copy payload to a staging dir and mark it pending — the swap
    /// happens via ApplyPendingUpdate once no inbrisk process is running.</summary>
    private static string StagePayload(IEnumerable<string> payload, string targetDir)
    {
        var staging = Path.Combine(InstallLayout.UpdateStagingDir,
            "install-" + (InstallLayout.Version ?? "next"));
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        foreach (var f in payload)
            File.Copy(f, Path.Combine(staging, Path.GetFileName(f)
                .StartsWith("inbrisksetup", StringComparison.OrdinalIgnoreCase)
                    ? InstallLayout.ExeName : Path.GetFileName(f)), overwrite: true);
        File.WriteAllText(Path.Combine(staging, "install-metadata.json"),
            JsonSerializer.Serialize(new
            {
                version = InstallLayout.Version,
                buildCommit = InstallLayout.BuildCommit,
                buildTimestamp = InstallLayout.BuildTimestamp,
                sha256 = InstallLayout.SelfSha256(),
                installedAt = DateTimeOffset.UtcNow,
                staged = true,
            }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(InstallLayout.PendingUpdateMarker,
            JsonSerializer.Serialize(new
            {
                version = InstallLayout.Version, staging, targetDir,
            }));
        return staging;
    }

    private static string? ReadInstalledVersion(string installDir)
    {
        try
        {
            var meta = Path.Combine(installDir, "install-metadata.json");
            if (!File.Exists(meta)) return null;
            return JsonDocument.Parse(File.ReadAllText(meta))
                .RootElement.GetProperty("version").GetString();
        }
        catch { return null; }
    }

    private static string Sha256Of(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static void WriteUninstallEntry(string exe, string dir, bool machine)
    {
        using var key = (machine ? Registry.LocalMachine : Registry.CurrentUser)
            .CreateSubKey(machine ? UninstallKeyMachine : UninstallKeyPerUser);
        key.SetValue("DisplayName", "Inbrisk");
        key.SetValue("DisplayVersion", InstallLayout.Version);
        key.SetValue("Publisher", "Inbrisk");
        key.SetValue("InstallLocation", dir);
        key.SetValue("UninstallString", $"\"{exe}\" uninstall");
        key.SetValue("QuietUninstallString", $"\"{exe}\" uninstall --yes");
        key.SetValue("DisplayIcon", exe);
        key.SetValue("NoModify", 1);
        key.SetValue("NoRepair", 0);
    }

    public sealed record UninstallResult(bool Ok, List<string> Actions, List<string> Errors);

    /// <summary>
    /// 1) remove Inbrisk entry from every configured host (other servers
    ///    untouched) 2) stop running inbrisk processes 3) drop the uninstall
    ///    entry 4) delete the install dir (self-delete via cmd when we are
    ///    the installed exe) 5) clear emergency marker + optionally data dir.
    /// </summary>
    public static UninstallResult Uninstall(bool keepData, bool autoYes,
        Func<string, bool>? confirm = null)
    {
        var actions = new List<string>();
        var errors = new List<string>();
        confirm ??= _ => autoYes;

        foreach (var host in HostRegistry.All())
        {
            try
            {
                if (host.GetInbriskEntry() == null) continue;
                if (!confirm($"Remove Inbrisk registration from {host.DisplayName}?"))
                { actions.Add($"{host.DisplayName}: registration kept (user choice)"); continue; }
                var r = host.Disconnect();
                (r.Ok ? actions : errors).Add($"{host.DisplayName}: {r.Message}");
            }
            catch (Exception e) { errors.Add($"{host.DisplayName}: {e.Message}"); }
        }

        // stop inbrisk processes other than ourselves
        foreach (var p in Process.GetProcessesByName("inbrisk")
            .Concat(Process.GetProcessesByName("inbrisk-mcp")))
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                p.Kill();
                actions.Add($"stopped running process pid={p.Id}");
            }
            catch (Exception e) { errors.Add($"pid {p.Id}: {e.Message}"); }
            finally { p.Dispose(); }
        }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPerUser, false);
            actions.Add("removed uninstall registry entry (HKCU)");
        }
        catch (Exception e) { errors.Add($"registry: {e.Message}"); }

        try { File.Delete(InstallLayout.EmergencyMarkerPath); }
        catch { }
        actions.Add("cleared emergency-stop marker");

        var installDir = InstallLayout.ProcessDir;
        var runningInstalled = InstallLayout.IsInstalled;
        if (runningInstalled)
        {
            // self-delete: cmd waits for our pid to exit, then removes the dir
            var data = keepData ? "" : $" & rmdir /s /q \"{InstallLayout.DataDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping -n 2 127.0.0.1 >nul & rmdir /s /q \"{installDir}\"{data}")
            { CreateNoWindow = true, UseShellExecute = false });
            actions.Add($"scheduled removal of {installDir}" +
                (keepData ? "" : $" and {InstallLayout.DataDir}"));
        }
        else
        {
            errors.Add("not running from an install dir — nothing to delete");
        }
        return new UninstallResult(errors.Count == 0, actions, errors);
    }

    // ---------------------------------------------------------------- update

    public sealed record UpdateManifest(string? Version, string? Url,
        string? Sha256, string? InstallerSha256, string? MinWindowsBuild,
        // release-manifest fields: payloadUrl/payloadSha256 (zip), so one
        // manifest serves both humans and the updater
        string? PayloadUrl, string? PayloadSha256);

    public sealed record UpdateResult(bool Ok, string Message,
        bool Deferred = false);

    /// <summary>
    /// Update from a manifest (JSON: {version, url, sha256}). The feed URL is
    /// explicit (--manifest or INBRISK_UPDATE_MANIFEST) — Inbrisk ships no
    /// default endpoint yet. SHA-256 verification is mandatory; an unsigned
    /// or unverifiable payload is refused.
    /// </summary>
    public static async Task<UpdateResult> UpdateAsync(string? manifestUri)
    {
        manifestUri ??= Environment.GetEnvironmentVariable("INBRISK_UPDATE_MANIFEST");
        if (string.IsNullOrEmpty(manifestUri))
            return new(false,
                "no update feed configured — pass --manifest <url|file> " +
                "or set INBRISK_UPDATE_MANIFEST");

        try
        {
            string manifestJson;
            string? manifestDir = null;
            if (Uri.TryCreate(manifestUri, UriKind.Absolute, out var u) &&
                (u.Scheme == "https" || u.Scheme == "http"))
            {
                if (u.Scheme != "https")
                    return new(false, "refusing insecure http update feed");
                using var http = new HttpClient();
                manifestJson = await http.GetStringAsync(u);
                manifestDir = u.GetLeftPart(UriPartial.Path)
                    .TrimEnd('/') + "/";
            }
            else
            {
                manifestJson = File.ReadAllText(manifestUri);
                manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestUri));
            }

            var m = JsonSerializer.Deserialize<UpdateManifest>(manifestJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var payloadUrl = m?.Url ?? m?.PayloadUrl;
            var payloadSha = m?.Sha256 ?? m?.PayloadSha256;
            if (m == null || string.IsNullOrEmpty(payloadUrl) ||
                string.IsNullOrEmpty(payloadSha))
                return new(false, "manifest missing url/sha256 — refusing update");
            // relative payload path resolves against the manifest location
            if (manifestDir != null && !Uri.TryCreate(payloadUrl, UriKind.Absolute, out _)
                && !Path.IsPathRooted(payloadUrl))
                payloadUrl = Path.Combine(manifestDir, payloadUrl);
            // compare against the INSTALLED version, not whatever binary is
            // running this command (an unpacked/newer exe may drive the update)
            var installDir = InstallLayout.IsInstalled
                ? InstallLayout.ProcessDir : InstallLayout.UserInstallDir;
            var installedVersion = ReadInstalledVersion(installDir);
            if (m.Version != null && string.Equals(m.Version, installedVersion,
                    StringComparison.OrdinalIgnoreCase))
                return new(true, $"already at {m.Version}");

            // download payload to temp, verify hash BEFORE touching install dir
            var tmp = Path.Combine(Path.GetTempPath(), $"inbrisk-update-{Guid.NewGuid():N}.zip");
            if (Uri.TryCreate(payloadUrl, UriKind.Absolute, out var pu) &&
                (pu.Scheme == "https" || pu.Scheme == "http"))
            {
                if (pu.Scheme != "https")
                    return new(false, "refusing insecure http payload URL");
                using var http = new HttpClient();
                await File.WriteAllBytesAsync(tmp, await http.GetByteArrayAsync(pu));
            }
            else File.Copy(payloadUrl, tmp, overwrite: true);

            var hash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(tmp))).ToLowerInvariant();
            if (!hash.Equals(payloadSha, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tmp);
                return new(false, $"SHA-256 mismatch: expected {payloadSha}, got {hash} — aborted");
            }

            var staging = Path.Combine(InstallLayout.UpdateStagingDir,
                m.Version ?? "next");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(tmp, staging);
            File.Delete(tmp);

            // never swap binaries under a live MCP session — defer instead
            var busy = Process.GetProcessesByName("inbrisk")
                .Any(p => p.Id != Environment.ProcessId);
            if (busy)
            {
                File.WriteAllText(InstallLayout.PendingUpdateMarker,
                    JsonSerializer.Serialize(new { version = m.Version, staging }));
                return new(true,
                    $"update {m.Version} staged — will apply when no MCP session is active " +
                    "(restart the MCP host to finish)", Deferred: true);
            }

            var backupDir = installDir + ".old";
            if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
            Directory.Move(installDir, backupDir);
            try
            {
                Directory.Move(staging, installDir);
                WriteUninstallEntry(
                    Path.Combine(installDir, InstallLayout.ExeName), installDir,
                    machine: InstallLayout.PathsEqual(installDir, InstallLayout.MachineInstallDir));
            }
            catch
            {
                // rollback: restore previous directory
                if (!Directory.Exists(installDir)) Directory.Move(backupDir, installDir);
                throw;
            }
            return new(true, $"updated to {m.Version} (previous at {backupDir})");
        }
        catch (Exception e)
        {
            return new(false, $"update failed: {e.Message}");
        }
    }

    /// <summary>Apply a deferred update staged earlier, if nothing is running.</summary>
    public static UpdateResult ApplyPendingUpdate()
    {
        if (!File.Exists(InstallLayout.PendingUpdateMarker))
            return new(true, "no pending update");
        var doc = JsonDocument.Parse(File.ReadAllText(InstallLayout.PendingUpdateMarker));
        var staging = doc.RootElement.GetProperty("staging").GetString()!;
        var version = doc.RootElement.GetProperty("version").GetString();
        var installDir = doc.RootElement.TryGetProperty("targetDir", out var td)
            ? td.GetString()!
            : (InstallLayout.PathsEqual(InstallLayout.ProcessDir,
                InstallLayout.MachineInstallDir)
                ? InstallLayout.MachineInstallDir : InstallLayout.UserInstallDir);
        var busy = Process.GetProcessesByName("inbrisk")
            .Any(p => p.Id != Environment.ProcessId);
        if (busy) return new(true, "still deferred — inbrisk session active", Deferred: true);
        var backupDir = installDir + ".old";
        if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true);
        Directory.Move(installDir, backupDir);
        Directory.Move(staging, installDir);
        File.Delete(InstallLayout.PendingUpdateMarker);
        return new(true, $"applied staged update {version}");
    }

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
