using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows;

/// <summary>
/// Handles detection, provisioning, security groups, and ACL configuration
/// for the dedicated local Windows Ghost Agent user account.
/// </summary>
public static class GhostUserProvisioner
{
    public const string DefaultGhostUsername = "InbriskAgent";
    public const string DefaultGhostPassword = "Inbrisk#2026!G";

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        int level,
        out IntPtr bufptr);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    private const int NERR_Success = 0;
    private const int NERR_UserNotFound = 2221;

    /// <summary>
    /// Checks whether the specified local user account exists on the machine.
    /// Uses NetUserGetInfo from netapi32.dll with NTAccount and net.exe fallbacks.
    /// </summary>
    public static bool DoesUserExist(string username = DefaultGhostUsername)
    {
        string cleanName = CleanUsername(username);

        // 1. Primary: NetUserGetInfo (netapi32.dll)
        IntPtr bufPtr = IntPtr.Zero;
        try
        {
            int status = NetUserGetInfo(null, cleanName, 0, out bufPtr);
            if (status == NERR_Success)
            {
                return true;
            }
            if (status == NERR_UserNotFound)
            {
                return false;
            }
        }
        catch
        {
            // Fall back if netapi32 invocation encounters issues
        }
        finally
        {
            if (bufPtr != IntPtr.Zero)
            {
                NetApiBufferFree(bufPtr);
            }
        }

        // 2. Secondary: NTAccount translation
        try
        {
            var ntAccount = new NTAccount(cleanName);
            var sid = ntAccount.Translate(typeof(SecurityIdentifier));
            if (sid != null)
            {
                return true;
            }
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
        catch
        {
            // Fall back to CLI
        }

        // 3. Fallback: net.exe user query
        try
        {
            var (exitCode, stdout, _) = RunProcess("net.exe", $"user \"{cleanName}\"");
            if (exitCode == 0)
            {
                return true;
            }
            if (stdout.Contains("The user name could not be found", StringComparison.OrdinalIgnoreCase) ||
                stdout.Contains("kullanıcı adı bulunamadı", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        return false;
    }

    /// <summary>
    /// Ensures that the local ghost user account exists, creating it if necessary.
    /// </summary>
    public static async Task<bool> EnsureUserExistsAsync(
        string username = DefaultGhostUsername,
        string? password = null)
    {
        string cleanName = CleanUsername(username);

        if (DoesUserExist(cleanName))
        {
            return true;
        }

        string effectivePassword = string.IsNullOrEmpty(password) ? DefaultGhostPassword : password;

        // 1. Try 'cmd.exe /c echo Y | net.exe user <name> <password> /add ...'
        // Using cmd pipe ensures that even if Windows prompts for password length compatibility, Y is supplied automatically.
        string cmdArgs = $"/c \"echo Y | net.exe user \"{cleanName}\" \"{effectivePassword}\" /add /comment:\"Inbrisk Dedicated Ghost Agent\" /passwordchg:no /expires:never\"";
        var (exitCode, _, _) = await Task.Run(() => RunProcess("cmd.exe", cmdArgs)).ConfigureAwait(false);

        if (exitCode == 0 || DoesUserExist(cleanName))
        {
            return true;
        }

        // 2. Fallback: PowerShell New-LocalUser via -EncodedCommand (avoids quoting & $ variable issues)
        try
        {
            string escapedPwd = effectivePassword.Replace("'", "''");
            string psScript = $"$p = ConvertTo-SecureString '{escapedPwd}' -AsPlainText -Force; " +
                              $"New-LocalUser -Name '{cleanName}' -Password $p -Description 'Inbrisk Dedicated Ghost Agent' -PasswordNeverExpires";
            string base64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));
            string psArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {base64}";
            var (psExit, _, _) = await Task.Run(() => RunProcess("powershell.exe", psArgs)).ConfigureAwait(false);
            if (psExit == 0 || DoesUserExist(cleanName))
            {
                return true;
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        return DoesUserExist(cleanName);
    }

    /// <summary>
    /// Ensures that the specified user is a member of the "Remote Desktop Users" local group.
    /// Resolves the localized group name via well-known SID S-1-5-32-555.
    /// </summary>
    public static async Task<bool> EnsureRemoteDesktopGroupAsync(string username = DefaultGhostUsername)
    {
        string cleanName = CleanUsername(username);
        string groupName = GetRemoteDesktopGroupName();

        // Check if already a member to avoid unnecessary modifications
        if (await Task.Run(() => IsUserInRemoteDesktopGroup(cleanName, groupName)).ConfigureAwait(false))
        {
            return true;
        }

        // 1. Try 'net.exe localgroup "<groupName>" "<cleanName>" /add'
        var (exitCode, stdout, stderr) = await Task.Run(() =>
            RunProcess("net.exe", $"localgroup \"{groupName}\" \"{cleanName}\" /add")).ConfigureAwait(false);

        if (exitCode == 0 || IsAlreadyMember(stdout, stderr))
        {
            return true;
        }

        // 2. Fallback: PowerShell Add-LocalGroupMember via Well-Known SID with -EncodedCommand
        try
        {
            string psScript = $"$group = Get-LocalGroup -SID 'S-1-5-32-555'; Add-LocalGroupMember -Group $group -Member '{cleanName}'";
            string base64 = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(psScript));
            string psArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {base64}";
            var (psExit, psOut, psErr) = await Task.Run(() => RunProcess("powershell.exe", psArgs)).ConfigureAwait(false);
            if (psExit == 0 || IsAlreadyMember(psOut, psErr))
            {
                return true;
            }
        }
        catch
        {
            // Ignore fallback errors
        }

        return await Task.Run(() => IsUserInRemoteDesktopGroup(cleanName, groupName)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures full control directory access permissions for the ghost user on the target directory path.
    /// </summary>
    public static async Task<bool> EnsureDirectoryAccessAsync(
        string username = DefaultGhostUsername,
        string path = @"%LOCALAPPDATA%\Inbrisk")
    {
        return await Task.Run(() =>
        {
            try
            {
                string cleanName = CleanUsername(username);
                string resolvedPath = Environment.ExpandEnvironmentVariables(path);

                if (!Directory.Exists(resolvedPath))
                {
                    Directory.CreateDirectory(resolvedPath);
                }

                // 1. Try .NET FileSystemAccessRule
                try
                {
                    var directoryInfo = new DirectoryInfo(resolvedPath);
                    var directorySecurity = directoryInfo.GetAccessControl();
                    var account = new NTAccount(cleanName);
                    var rule = new FileSystemAccessRule(
                        account,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow);

                    directorySecurity.AddAccessRule(rule);
                    directoryInfo.SetAccessControl(directorySecurity);
                    return true;
                }
                catch
                {
                    // Fall back to icacls CLI
                }

                // 2. Fallback: icacls.exe
                var (exitCode, _, _) = RunProcess("icacls.exe", $"\"{resolvedPath}\" /grant \"{cleanName}\":(OI)(CI)F /t /c /q");
                return exitCode == 0;
            }
            catch
            {
                return false;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Orchestrates full ghost user provisioning: verification/creation, RDP group membership, and directory access.
    /// </summary>
    public static async Task<GhostUserProvisionResult> EnsureGhostUserReadyAsync(string username = DefaultGhostUsername)
    {
        string cleanName = CleanUsername(username);

        try
        {
            bool existed = DoesUserExist(cleanName);
            bool createdNew = false;

            if (!existed)
            {
                bool created = await EnsureUserExistsAsync(cleanName).ConfigureAwait(false);
                if (!created)
                {
                    return GhostUserProvisionResult.Fail(
                        cleanName,
                        $"Failed to create local user account '{cleanName}'. Administrative privileges may be required.");
                }
                createdNew = true;
            }

            bool rdpOk = await EnsureRemoteDesktopGroupAsync(cleanName).ConfigureAwait(false);
            if (!rdpOk)
            {
                return GhostUserProvisionResult.Fail(
                    cleanName,
                    $"Failed to add user '{cleanName}' to Remote Desktop Users group. Administrative privileges may be required.");
            }

            bool dirOk = await EnsureDirectoryAccessAsync(cleanName).ConfigureAwait(false);
            if (!dirOk)
            {
                return GhostUserProvisionResult.Fail(
                    cleanName,
                    $"Failed to configure directory access permissions for '{cleanName}'.");
            }

            GhostTelemetry.RecordUserProvisioned();
            return GhostUserProvisionResult.Ok(cleanName, createdNew);
        }
        catch (Exception ex)
        {
            return GhostUserProvisionResult.Fail(cleanName, $"Ghost user provisioning exception: {ex.Message}");
        }
    }

    private static string CleanUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return DefaultGhostUsername;
        }

        string trimmed = username.Trim();
        int slashIdx = trimmed.LastIndexOf('\\');
        if (slashIdx >= 0 && slashIdx < trimmed.Length - 1)
        {
            return trimmed[(slashIdx + 1)..];
        }

        return trimmed;
    }

    private static string GetRemoteDesktopGroupName()
    {
        try
        {
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinRemoteDesktopUsersSid, null);
            var ntAccount = sid.Translate(typeof(NTAccount)).Value;
            int slashIdx = ntAccount.LastIndexOf('\\');
            return slashIdx >= 0 ? ntAccount[(slashIdx + 1)..] : ntAccount;
        }
        catch
        {
            return "Remote Desktop Users";
        }
    }

    private static bool IsUserInRemoteDesktopGroup(string username, string groupName)
    {
        try
        {
            var (exitCode, stdout, _) = RunProcess("net.exe", $"localgroup \"{groupName}\"");
            if (exitCode == 0)
            {
                var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string clean = CleanUsername(username);
                return lines.Any(l =>
                {
                    string line = l.Trim();
                    return line.Equals(clean, StringComparison.OrdinalIgnoreCase) ||
                           line.EndsWith("\\" + clean, StringComparison.OrdinalIgnoreCase);
                });
            }
        }
        catch
        {
            // Ignore query errors
        }

        return false;
    }

    private static bool IsAlreadyMember(string stdout, string stderr)
    {
        string combined = (stdout + " " + stderr).ToLowerInvariant();
        return combined.Contains("already a member") ||
               combined.Contains("zaten") ||
               combined.Contains("1378");
    }

    private static (int ExitCode, string Output, string Error) RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (process.WaitForExit(10000))
            {
                string stdout = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                return (process.ExitCode, stdout, stderr);
            }
            else
            {
                try { process.Kill(); } catch { }
                return (-1, string.Empty, "Process timed out after 10000ms.");
            }
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
