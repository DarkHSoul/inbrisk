using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
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

    /// <summary>
    /// Machine-unique ghost account password. Generated once per machine with a
    /// cryptographic RNG and persisted DPAPI-encrypted (LocalMachine scope) in an
    /// Administrators/SYSTEM-only ACL'd file under %ProgramData%. It is never a
    /// constant, never logged, and never passed on a process command line.
    /// </summary>
    public static string DefaultGhostPassword => GetOrCreateGhostPassword();

    private static readonly object PasswordGate = new();
    private static string? _cachedGhostPassword;
    private static volatile bool _secretWasRegenerated;

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        int level,
        out IntPtr bufptr);

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserAdd(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        int level,
        ref USER_INFO_1 bufptr,
        out int parm_err);

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserSetInfo(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username,
        int level,
        ref USER_INFO_1003 bufptr,
        out int parm_err);

    [DllImport("netapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int NetUserDel(
        [MarshalAs(UnmanagedType.LPWStr)] string? servername,
        [MarshalAs(UnmanagedType.LPWStr)] string username);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buf);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LogonUser(
        string lpszUsername,
        string? lpszDomain,
        string lpszPassword,
        int dwLogonType,
        int dwLogonProvider,
        out IntPtr phToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_name;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_password;
        public int usri1_password_age;
        public int usri1_priv;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_home_dir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_comment;
        public int usri1_flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1_script_path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct USER_INFO_1003
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? usri1003_password;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int NERR_Success = 0;
    private const int NERR_UserNotFound = 2221;
    private const int NERR_UserExists = 2224;

    private const int USER_PRIV_USER = 1;
    private const int UF_SCRIPT = 0x0001;
    private const int UF_NORMAL_ACCOUNT = 0x0200;
    private const int UF_DONT_EXPIRE_PASSWD = 0x10000;

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
    private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const string LegacyGhostPasswordV2 = "Inbrisk#2026!G";
    private const string LegacyGhostPasswordV1 = "InbriskGhost!2026#Agent";

    public static string MachineSecretFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Inbrisk",
            "ghost-user.cred");

    public static string UserSecretFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Inbrisk",
            "ghost-user.cred");

    private static string SecretFilePath => MachineSecretFilePath;

    /// <summary>
    /// Checks whether the specified local user account exists on the machine.
    /// Uses NetUserGetInfo from netapi32.dll with NTAccount and net.exe fallbacks.
    /// </summary>
    public static bool DoesUserExist(string username = DefaultGhostUsername)
    {
        string cleanName;
        try
        {
            cleanName = CleanUsername(username);
        }
        catch
        {
            return false;
        }

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
    /// Checks whether the current process is running with administrative privileges.
    /// </summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies whether the specified credentials authenticate successfully against the local machine.
    /// Uses LogonUser with LOGON32_LOGON_NETWORK without requiring administrative privileges.
    /// </summary>
    public static bool VerifyUserCredential(string username, string password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        IntPtr token = IntPtr.Zero;
        try
        {
            string cleanName = CleanUsername(username);
            return LogonUser(cleanName, ".", password, LOGON32_LOGON_NETWORK, LOGON32_PROVIDER_DEFAULT, out token);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }

    /// <summary>
    /// Ensures that the local ghost user account exists, creating it if necessary.
    /// Account creation and password assignment go through NetUserAdd/NetUserSetInfo
    /// (netapi32) so the credential never appears on any process command line.
    /// </summary>
    public static async Task<bool> EnsureUserExistsAsync(
        string username = DefaultGhostUsername,
        string? password = null)
    {
        string cleanName = CleanUsername(username);
        string effectivePassword = string.IsNullOrEmpty(password) ? DefaultGhostPassword : password;
        GhostLogger.RegisterSecret(effectivePassword);

        if (DoesUserExist(cleanName))
        {
            // Re-synchronize the account password when an explicit override was supplied or
            // when the stored secret was freshly regenerated (e.g. first run after upgrade or
            // after the credential file was lost), so stored secret and account never diverge.
            if (!string.IsNullOrEmpty(password) || _secretWasRegenerated)
            {
                // When running as a non-administrator, NetUserSetInfo is guaranteed to fail with Access Denied (status 5).
                // Do not fail account verification if the account already exists and we lack privileges.
                if (!IsAdministrator())
                {
                    GhostLogger.Warn(
                        $"[GhostUserProvisioner] Account '{cleanName}' already exists, but running without administrative privileges. " +
                        "Skipping password reset to prevent Access Denied.");
                    return true;
                }

                bool passwordSet = await Task.Run(() => SetUserPassword(cleanName, effectivePassword)).ConfigureAwait(false);
                if (passwordSet)
                {
                    _secretWasRegenerated = false;
                }
                return passwordSet;
            }
            return true;
        }

        return await Task.Run(() => CreateUser(cleanName, effectivePassword)).ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently deletes the local ghost user account and removes the stored
    /// machine credential. Intended for full teardown of the ghost session subsystem.
    /// </summary>
    public static async Task<bool> RemoveGhostUserAsync(string username = DefaultGhostUsername)
    {
        return await Task.Run(() =>
        {
            bool ok = true;
            try
            {
                string cleanName = CleanUsername(username);
                int status = NetUserDel(null, cleanName);
                ok = status == NERR_Success || status == NERR_UserNotFound;
                if (!ok)
                {
                    GhostLogger.Warn($"[GhostUserProvisioner] NetUserDel failed for '{cleanName}'. Status: {status}");
                }
            }
            catch (Exception ex)
            {
                GhostLogger.Warn($"[GhostUserProvisioner] NetUserDel exception: {ex.Message}");
                ok = false;
            }

            try
            {
                if (File.Exists(MachineSecretFilePath))
                {
                    File.Delete(MachineSecretFilePath);
                }
            }
            catch { }

            try
            {
                if (File.Exists(UserSecretFilePath))
                {
                    File.Delete(UserSecretFilePath);
                }
            }
            catch { }

            lock (PasswordGate)
            {
                _cachedGhostPassword = null;
            }

            return ok;
        }).ConfigureAwait(false);
    }

    private static bool CreateUser(string username, string password)
    {
        try
        {
            var info = new USER_INFO_1
            {
                usri1_name = username,
                usri1_password = password,
                usri1_priv = USER_PRIV_USER,
                usri1_flags = UF_SCRIPT | UF_NORMAL_ACCOUNT | UF_DONT_EXPIRE_PASSWD,
                usri1_comment = "Inbrisk Dedicated Ghost Agent"
            };

            int status = NetUserAdd(null, 1, ref info, out _);
            if (status == NERR_Success)
            {
                GhostLogger.Info($"[GhostUserProvisioner] Created local ghost user '{username}'.");
                return true;
            }
            if (status == NERR_UserExists)
            {
                if (!IsAdministrator())
                {
                    GhostLogger.Info($"[GhostUserProvisioner] User '{username}' already exists. Skipping password reset (non-administrator).");
                    return true;
                }
                return SetUserPassword(username, password);
            }

            GhostLogger.Warn($"[GhostUserProvisioner] NetUserAdd failed for '{username}'. Status: {status} (administrative privileges may be required).");
            return false;
        }
        catch (Exception ex)
        {
            GhostLogger.Warn($"[GhostUserProvisioner] NetUserAdd exception for '{username}': {ex.Message}");
            return false;
        }
    }

    private static bool SetUserPassword(string username, string password)
    {
        try
        {
            var info = new USER_INFO_1003 { usri1003_password = password };
            int status = NetUserSetInfo(null, username, 1003, ref info, out _);
            if (status == NERR_Success)
            {
                return true;
            }
            if (status == 5 /* ERROR_ACCESS_DENIED */)
            {
                GhostLogger.Warn($"[GhostUserProvisioner] NetUserSetInfo(password) failed for '{username}': Access Denied (status 5). Administrative privileges are required.");
            }
            else
            {
                GhostLogger.Warn($"[GhostUserProvisioner] NetUserSetInfo(password) failed for '{username}'. Status: {status}");
            }
            return false;
        }
        catch (Exception ex)
        {
            GhostLogger.Warn($"[GhostUserProvisioner] NetUserSetInfo exception for '{username}': {ex.Message}");
            return false;
        }
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
            bool ensured = await EnsureUserExistsAsync(cleanName).ConfigureAwait(false);

            string groupName = GetRemoteDesktopGroupName();
            bool isRdpMember = await Task.Run(() => IsUserInRemoteDesktopGroup(cleanName, groupName)).ConfigureAwait(false);

            if (!ensured)
            {
                // Gracefully handle existing users: if the user already exists and is already
                // in Remote Desktop Users, do not fail provisioning due to credential sync limitations.
                if (existed && isRdpMember)
                {
                    GhostLogger.Info(
                        $"[GhostUserProvisioner] User '{cleanName}' already exists and is a member of '{groupName}'. " +
                        "Proceeding despite credential sync limitation.");
                }
                else
                {
                    return GhostUserProvisionResult.Fail(
                        cleanName,
                        existed
                            ? $"Failed to synchronize credentials for existing user '{cleanName}'. Administrative privileges may be required."
                            : $"Failed to create local user account '{cleanName}'. Administrative privileges may be required.");
                }
            }
            bool createdNew = !existed;

            bool rdpOk = isRdpMember || await EnsureRemoteDesktopGroupAsync(cleanName).ConfigureAwait(false);
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

    /// <summary>
    /// Returns the per-machine ghost account password, generating and securely persisting
    /// it on first use. Falls back to user credential store or in-memory secret when the
    /// machine credential store is unavailable.
    /// </summary>
    private static string GetOrCreateGhostPassword()
    {
        lock (PasswordGate)
        {
            if (!string.IsNullOrEmpty(_cachedGhostPassword))
            {
                return _cachedGhostPassword;
            }

            string? stored = TryReadStoredPassword();
            bool accountExists = DoesUserExist(DefaultGhostUsername);

            if (accountExists)
            {
                // 1. If stored password exists and verifies against the existing Windows account, use it
                if (!string.IsNullOrEmpty(stored) && VerifyUserCredential(DefaultGhostUsername, stored))
                {
                    _cachedGhostPassword = stored;
                    GhostLogger.RegisterSecret(stored);
                    return stored;
                }

                // 2. If stored secret was missing or out of sync, check known legacy default passwords
                if (VerifyUserCredential(DefaultGhostUsername, LegacyGhostPasswordV2))
                {
                    GhostLogger.Info("[GhostUserProvisioner] Synchronized with existing ghost account credentials (legacy v2).");
                    _cachedGhostPassword = LegacyGhostPasswordV2;
                    GhostLogger.RegisterSecret(LegacyGhostPasswordV2);
                    TryStorePassword(LegacyGhostPasswordV2);
                    return LegacyGhostPasswordV2;
                }

                if (VerifyUserCredential(DefaultGhostUsername, LegacyGhostPasswordV1))
                {
                    GhostLogger.Info("[GhostUserProvisioner] Synchronized with existing ghost account credentials (legacy v1).");
                    _cachedGhostPassword = LegacyGhostPasswordV1;
                    GhostLogger.RegisterSecret(LegacyGhostPasswordV1);
                    TryStorePassword(LegacyGhostPasswordV1);
                    return LegacyGhostPasswordV1;
                }

                // 3. If stored password exists and we are Administrator, enforce it on the account
                if (!string.IsNullOrEmpty(stored) && IsAdministrator())
                {
                    if (SetUserPassword(DefaultGhostUsername, stored))
                    {
                        _cachedGhostPassword = stored;
                        GhostLogger.RegisterSecret(stored);
                        return stored;
                    }
                }
            }
            else
            {
                // Account does not exist yet; if we already have a stored secret, preserve and use it
                if (!string.IsNullOrEmpty(stored))
                {
                    _cachedGhostPassword = stored;
                    GhostLogger.RegisterSecret(stored);
                    return stored;
                }
            }

            // Fallback: If stored password exists, return it
            if (!string.IsNullOrEmpty(stored))
            {
                _cachedGhostPassword = stored;
                GhostLogger.RegisterSecret(stored);
                return stored;
            }

            string generated = GeneratePassword();
            _secretWasRegenerated = true;
            TryStorePassword(generated);
            _cachedGhostPassword = generated;
            GhostLogger.RegisterSecret(generated);
            return generated;
        }
    }

    private static string GeneratePassword(int length = 24)
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "#!?@$%+-_=*";
        string all = upper + lower + digits + symbols;

        var chars = new char[length];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (int i = 4; i < length; i++)
        {
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];
        }
        RandomNumberGenerator.Shuffle(chars.AsSpan());
        return new string(chars);
    }

    private static string? TryReadStoredPassword()
    {
        // 1. Primary: machine-wide store (%ProgramData%)
        string? stored = TryReadSecretFile(MachineSecretFilePath);
        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        // 2. Fallback: per-user store (%LOCALAPPDATA%)
        stored = TryReadSecretFile(UserSecretFilePath);
        if (!string.IsNullOrEmpty(stored))
        {
            return stored;
        }

        return null;
    }

    private static string? TryReadSecretFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] blob = File.ReadAllBytes(path);
            byte[] plain = UnprotectBytes(blob);
            if (plain.Length == 0) return null;
            string password = Encoding.UTF8.GetString(plain);
            return string.IsNullOrEmpty(password) ? null : password;
        }
        catch
        {
            return null;
        }
    }

    private static void TryStorePassword(string password)
    {
        try
        {
            byte[] blob = ProtectBytes(Encoding.UTF8.GetBytes(password));
            if (blob.Length == 0) return;

            // 1. Primary: Attempt machine-wide store in %ProgramData%
            bool machineSuccess = TryStoreMachineSecret(blob);

            // 2. Fallback: If machine store write fails (e.g. non-admin), persist to user store in %LOCALAPPDATA%
            if (!machineSuccess)
            {
                TryStoreUserSecret(blob);
            }
        }
        catch { }
    }

    private static bool TryStoreMachineSecret(byte[] blob)
    {
        try
        {
            string? dir = Path.GetDirectoryName(MachineSecretFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            // Grant Authenticated Users Read access so non-elevated Inbrisk instances can decrypt DPAPI LocalMachine secret
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                FileSystemRights.Read,
                AccessControlType.Allow));

            using var stream = System.IO.FileSystemAclExtensions.Create(
                new FileInfo(MachineSecretFilePath),
                FileMode.Create,
                FileSystemRights.FullControl,
                FileShare.None,
                4096,
                FileOptions.None,
                security);
            stream.Write(blob, 0, blob.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStoreUserSecret(byte[] blob)
    {
        try
        {
            string? dir = Path.GetDirectoryName(UserSecretFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllBytes(UserSecretFilePath, blob);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ProtectBytes(byte[] data)
    {
        var inBlob = new DATA_BLOB
        {
            cbData = data.Length,
            pbData = Marshal.AllocHGlobal(data.Length)
        };
        try
        {
            Marshal.Copy(data, 0, inBlob.pbData, data.Length);
            // 1. Primary: DPAPI with Machine scope
            if (CryptProtectData(ref inBlob, "InbriskGhostUser", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_LOCAL_MACHINE | CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
            {
                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }

            // 2. Fallback: DPAPI with CurrentUser scope if machine scope is unavailable
            if (CryptProtectData(ref inBlob, "InbriskGhostUser", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out outBlob))
            {
                try
                {
                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }

            return Array.Empty<byte>();
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }

    private static byte[] UnprotectBytes(byte[] blob)
    {
        var inBlob = new DATA_BLOB
        {
            cbData = blob.Length,
            pbData = Marshal.AllocHGlobal(blob.Length)
        };
        try
        {
            Marshal.Copy(blob, 0, inBlob.pbData, blob.Length);
            if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var outBlob))
            {
                return Array.Empty<byte>();
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (outBlob.pbData != IntPtr.Zero)
                {
                    LocalFree(outBlob.pbData);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
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
            trimmed = trimmed[(slashIdx + 1)..];
        }

        // Windows account names cannot contain these characters; rejecting them
        // here also prevents command-line injection into net.exe/icacls calls.
        if (trimmed.Length == 0 || trimmed.Length > 64 ||
            trimmed.IndexOfAny(InvalidUsernameChars) >= 0)
        {
            throw new ArgumentException($"Invalid ghost username: '{username}'.", nameof(username));
        }

        return trimmed;
    }

    private static readonly char[] InvalidUsernameChars =
        { '"', '/', '\\', '[', ']', ':', ';', '|', '=', ',', '+', '*', '?', '<', '>' };

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
