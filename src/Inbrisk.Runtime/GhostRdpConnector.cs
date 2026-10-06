using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Represents an established headless RDP ghost session.
/// </summary>
public record GhostRdpSession(int SessionId, int ProcessId, string Username)
{
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Headless Loopback RDP Connector.
/// Manages silent loopback RDP connections on 127.0.0.1:3389 for dedicated ghost users (e.g. InbriskAgent).
/// Spawns minimized or background MSTSC/FreeRDP processes and monitors session lifecycle.
/// </summary>
public class GhostRdpConnector
{
    private static readonly ConcurrentDictionary<int, Process> TrackedProcesses = new();
    private static readonly ConcurrentDictionary<int, string> TrackedRdpFiles = new();

    /// <summary>
    /// Default loopback host and port.
    /// </summary>
    public const string DefaultLoopbackAddress = "127.0.0.1";
    public const int DefaultRdpPort = 3389;

    /// <summary>
    /// Generates an optimized .rdp configuration file for headless loopback RDP sessions.
    /// </summary>
    /// <param name="targetPath">File path where the .rdp configuration will be saved.</param>
    /// <param name="width">Screen width in pixels (default: 1920).</param>
    /// <param name="height">Screen height in pixels (default: 1080).</param>
    /// <returns>The target file path.</returns>
    public static string GenerateRdpConfig(string targetPath, int width = 1920, int height = 1080)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target path cannot be null or empty.", nameof(targetPath));
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();
        sb.AppendLine("screen mode id:i:1");
        sb.AppendLine("use multimon:i:0");
        sb.AppendLine($"desktopwidth:i:{width}");
        sb.AppendLine($"desktopheight:i:{height}");
        sb.AppendLine("session bpp:i:32");
        sb.AppendLine($"winposstr:s:0,1,0,0,{width},{height}");
        sb.AppendLine($"full address:s:{DefaultLoopbackAddress}:{DefaultRdpPort}");
        sb.AppendLine("compression:i:1");
        sb.AppendLine("keyboardhook:i:2");
        sb.AppendLine("audiomode:i:2");
        sb.AppendLine("redirectprinters:i:0");
        sb.AppendLine("redirectcomports:i:0");
        sb.AppendLine("redirectsmartcards:i:0");
        sb.AppendLine("redirectclipboard:i:0");
        sb.AppendLine("redirectposdevices:i:0");
        sb.AppendLine("redirectdirectx:i:0");
        sb.AppendLine("autoreconnection enabled:i:1");
        sb.AppendLine("authentication level:i:0");
        sb.AppendLine("prompt for credentials:i:0");
        sb.AppendLine("negotiate security layer:i:1");
        sb.AppendLine("remoteapplicationmode:i:0");
        sb.AppendLine("allow font smoothing:i:1");
        sb.AppendLine("alternate shell:s:");
        sb.AppendLine("shell working directory:s:");
        sb.AppendLine("gatewayhostname:s:");
        sb.AppendLine("gatewayusagemethod:i:0");
        sb.AppendLine("gatewaycredentialssource:i:4");
        sb.AppendLine("gatewayprofileusagemethod:i:0");
        sb.AppendLine("promptcredentialonce:i:0");
        sb.AppendLine("dvc_gfx:i:1");
        sb.AppendLine("smart sizing:i:0");

        File.WriteAllText(targetPath, sb.ToString(), Encoding.ASCII);
        return targetPath;
    }

    /// <summary>
    /// Configures Windows Credential Manager for the loopback target using cmdkey.
    /// This allows silent authentication without interactive credential prompts.
    /// </summary>
    /// <param name="username">Username for the ghost session (e.g. InbriskAgent).</param>
    /// <param name="password">Password for the user.</param>
    /// <returns>True if credentials were successfully registered.</returns>
    public static bool SetLoopbackCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        }

        bool ok1 = RunCmdKey($"/generic:TERMSRV/{DefaultLoopbackAddress} /user:\"{username}\" /pass:\"{password}\"");
        bool ok2 = RunCmdKey($"/generic:TERMSRV/localhost /user:\"{username}\" /pass:\"{password}\"");
        return ok1 || ok2;
    }

    /// <summary>
    /// Clears cached loopback credentials from Windows Credential Manager.
    /// </summary>
    public static bool ClearLoopbackCredentials()
    {
        bool ok1 = RunCmdKey($"/delete:TERMSRV/{DefaultLoopbackAddress}");
        bool ok2 = RunCmdKey("/delete:TERMSRV/localhost");
        return ok1 || ok2;
    }

    /// <summary>
    /// Starts a headless loopback RDP session for the specified ghost username.
    /// Spawns a background or minimized MSTSC or FreeRDP process and polls until
    /// the session enters WTSActive or WTSConnected state.
    /// </summary>
    /// <param name="username">Username of the ghost account (default: InbriskAgent).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The established GhostRdpSession.</returns>
    public static async Task<GhostRdpSession> StartHeadlessRdpSessionAsync(
        string username = "InbriskAgent",
        CancellationToken ct = default)
    {
        return await StartHeadlessRdpSessionAsync(username, password: null, width: 1920, height: 1080, ct: ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts a headless loopback RDP session with explicit parameters.
    /// </summary>
    public static async Task<GhostRdpSession> StartHeadlessRdpSessionAsync(
        string username,
        string? password,
        int width,
        int height,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username cannot be empty.", nameof(username));
        }

        // 1. If password provided, register loopback credentials
        if (!string.IsNullOrEmpty(password))
        {
            SetLoopbackCredentials(username, password);
        }

        // 2. Check if user already has an active session
        var existing = GhostWtsInterop.FindSessionByUsername(username);
        if (existing != null && (existing.State == WTS_CONNECTSTATE_CLASS.WTSActive || existing.State == WTS_CONNECTSTATE_CLASS.WTSConnected))
        {
            GhostTelemetry.RecordSessionStart();
            return new GhostRdpSession(existing.SessionId, ProcessId: 0, Username: username);
        }

        // 3. Verify Remote Desktop service is listening on 127.0.0.1:3389
        var (rdpReady, rdpStatus) = EnsureRdpServiceReady();
        if (!rdpReady)
        {
            GhostTelemetry.RecordSessionFail();
            throw new InvalidOperationException(rdpStatus);
        }

        // 4. Prepare temporary RDP config file
        string rdpDir = Path.Combine(Path.GetTempPath(), "Inbrisk_Ghost_RDP");
        Directory.CreateDirectory(rdpDir);
        string rdpPath = Path.Combine(rdpDir, $"ghost_{Guid.NewGuid():N}.rdp");
        GenerateRdpConfig(rdpPath, width, height);

        // 5. Launch client (FreeRDP if present, otherwise mstsc.exe)
        Process? process = null;
        string? freeRdpPath = FindFreeRdpExecutable();

        try
        {
            if (!string.IsNullOrEmpty(freeRdpPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = freeRdpPath,
                    Arguments = $"/v:{DefaultLoopbackAddress}:{DefaultRdpPort} /u:\"{username}\" " +
                                (string.IsNullOrEmpty(password) ? "" : $"/p:\"{password}\" ") +
                                $"/size:{width}x{height} /bpp:32 /cert:ignore +auto-reconnect /kbd:0x00000409",
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };
                process = Process.Start(psi);
            }
            else
            {
                string mstscPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "mstsc.exe");

                if (!File.Exists(mstscPath))
                {
                    mstscPath = "mstsc.exe";
                }

                var psi = new ProcessStartInfo
                {
                    FileName = mstscPath,
                    Arguments = $"\"{rdpPath}\"",
                    WindowStyle = ProcessWindowStyle.Minimized,
                    UseShellExecute = true
                };
                process = Process.Start(psi);
            }

            int pid = process?.Id ?? 0;

            // 5. Poll GhostWtsInterop until session becomes WTSActive or WTSConnected (max 15 seconds)
            var timeout = TimeSpan.FromSeconds(15);
            var sw = Stopwatch.StartNew();

            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                var session = GhostWtsInterop.FindSessionByUsername(username);
                if (session != null &&
                    (session.State == WTS_CONNECTSTATE_CLASS.WTSActive || session.State == WTS_CONNECTSTATE_CLASS.WTSConnected))
                {
                    GhostTelemetry.RecordSessionStart();
                    var rdpSession = new GhostRdpSession(
                        SessionId: session.SessionId,
                        ProcessId: pid,
                        Username: username);

                    if (process != null)
                    {
                        TrackedProcesses[session.SessionId] = process;
                    }
                    TrackedRdpFiles[session.SessionId] = rdpPath;

                    return rdpSession;
                }

                await Task.Delay(250, ct).ConfigureAwait(false);
            }

            // If timeout occurred
            GhostTelemetry.RecordSessionFail();
            throw new TimeoutException(
                $"Timed out waiting for loopback RDP session for user '{username}' to reach Active/Connected state.");
        }
        catch (Exception)
        {
            GhostTelemetry.RecordSessionFail();
            if (File.Exists(rdpPath))
            {
                try { File.Delete(rdpPath); } catch { }
            }
            if (process != null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            throw;
        }
    }

    /// <summary>
    /// Starts a headless loopback RDP session based on GhostSessionConfig.
    /// </summary>
    public static Task<GhostRdpSession> StartHeadlessRdpSessionAsync(
        GhostSessionConfig config,
        string? password = null,
        CancellationToken ct = default)
    {
        return StartHeadlessRdpSessionAsync(
            username: config.Username,
            password: password,
            width: config.Width,
            height: config.Height,
            ct: ct);
    }

    /// <summary>
    /// Stops the headless ghost session by logging off or disconnecting it.
    /// Also terminates the background MSTSC/FreeRDP client process and cleans up temp files.
    /// </summary>
    /// <param name="sessionId">The Terminal Services Session ID.</param>
    /// <returns>True if session was successfully logged off or disconnected.</returns>
    public static async Task<bool> StopHeadlessRdpSessionAsync(int sessionId)
    {
        bool success = false;
        try
        {
            // Attempt logoff first for complete cleanup
            success = GhostWtsInterop.LogoffSession(sessionId, wait: false);
            if (!success)
            {
                success = GhostWtsInterop.DisconnectSession(sessionId, wait: false);
            }

            if (success)
            {
                GhostTelemetry.RecordSessionStop();
            }

            // Cleanup tracked client process
            if (TrackedProcesses.TryRemove(sessionId, out var proc))
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        if (!proc.HasExited)
                        {
                            proc.Kill(entireProcessTree: true);
                        }
                    }
                    proc.Dispose();
                }
                catch { }
            }

            // Cleanup tracked temporary rdp file
            if (TrackedRdpFiles.TryRemove(sessionId, out var rdpPath))
            {
                try
                {
                    if (File.Exists(rdpPath))
                    {
                        File.Delete(rdpPath);
                    }
                }
                catch { }
            }
        }
        catch
        {
            success = false;
        }

        return success;
    }

    #region Helper Methods

    private static bool RunCmdKey(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmdkey.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindFreeRdpExecutable()
    {
        try
        {
            string[] probePaths =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Inbrisk", "bin", "wfreerdp.exe"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wfreerdp.exe"),
                @"C:\Program Files\FreeRDP\wfreerdp.exe",
                @"C:\Program Files (x86)\FreeRDP\wfreerdp.exe"
            };

            foreach (var path in probePaths)
            {
                if (File.Exists(path)) return path;
            }

            // Check PATH environment variable
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var full = Path.Combine(dir.Trim(), "wfreerdp.exe");
                    if (File.Exists(full)) return full;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Checks whether port 3389 is actively listening on loopback (127.0.0.1).
    /// </summary>
    public static bool IsRdpPortListening(int timeoutMs = 600)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var ar = client.BeginConnect(DefaultLoopbackAddress, DefaultRdpPort, null, null);
            bool success = ar.AsyncWaitHandle.WaitOne(timeoutMs);
            if (success && client.Connected)
            {
                client.EndConnect(ar);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies if Remote Desktop service is enabled and listening.
    /// If not listening, attempts to enable TermService and fDenyTSConnections if running as admin.
    /// </summary>
    public static (bool Ready, string StatusMessage) EnsureRdpServiceReady()
    {
        if (IsRdpPortListening())
        {
            return (true, "RDP service is listening on 127.0.0.1:3389.");
        }

        // Try to enable and start TermService if elevated on Windows
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\Terminal Server", writable: true);
                if (key != null)
                {
                    key.SetValue("fDenyTSConnections", 0, Microsoft.Win32.RegistryValueKind.DWord);
                }
            }
            catch { }

            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "net.exe",
                    Arguments = "start TermService",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                proc?.WaitForExit(5000);
            }
            catch { }
        }

        // Re-check after attempt
        if (IsRdpPortListening(1500))
        {
            return (true, "RDP service successfully started on 127.0.0.1:3389.");
        }

        return (false, "Remote Desktop is disabled in Windows Settings (fDenyTSConnections=1 or TermService stopped). Enable Remote Desktop in Windows Settings -> System -> Remote Desktop to allow background loopback sessions.");
    }

    #endregion
}

#region Terminal Services Interop Bridge (Runtime Scope)

internal enum WTS_CONNECTSTATE_CLASS
{
    WTSActive,
    WTSConnected,
    WTSConnectQuery,
    WTSShadow,
    WTSDisconnected,
    WTSIdle,
    WTSListen,
    WTSReset,
    WTSDown,
    WTSInit
}

internal enum WTS_INFO_CLASS
{
    WTSInitialProgram,
    WTSApplicationName,
    WTSWorkingDirectory,
    WTSOEMId,
    WTSSessionId,
    WTSUserName,
    WTSWinStationName,
    WTSDomainName,
    WTSConnectState,
    WTSClientBuildNumber,
    WTSClientName,
    WTSClientDirectory,
    WTSClientDisplay,
    WTSClientHardwareId,
    WTSClientAddress,
    WTSClientDisplay2,
    WTSClientProtocolType
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WTS_SESSION_INFO
{
    public int SessionId;
    public IntPtr pWinStationName;
    public WTS_CONNECTSTATE_CLASS State;
}

internal record GhostSessionInfo(
    int SessionId,
    string WinStationName,
    WTS_CONNECTSTATE_CLASS State,
    string? UserName);

/// <summary>
/// Native WTS interop bridge for Inbrisk.Runtime.
/// Calls wtsapi32.dll directly to ensure zero external assembly coupling.
/// </summary>
internal static class GhostWtsInterop
{
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int Reserved,
        int Version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        int SessionId,
        WTS_INFO_CLASS WTSInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSDisconnectSession(IntPtr hServer, int SessionId, bool bWait);

    public static List<GhostSessionInfo> GetAllSessions()
    {
        var list = new List<GhostSessionInfo>();
        IntPtr ppSessionInfo = IntPtr.Zero;
        int count = 0;

        if (!WTSEnumerateSessions(WTS_CURRENT_SERVER_HANDLE, 0, 1, out ppSessionInfo, out count))
        {
            return list;
        }

        try
        {
            int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            IntPtr current = ppSessionInfo;

            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                string winStationName = si.pWinStationName != IntPtr.Zero
                    ? (Marshal.PtrToStringUni(si.pWinStationName) ?? string.Empty)
                    : string.Empty;

                string? username = GetSessionUsername(si.SessionId);

                list.Add(new GhostSessionInfo(
                    SessionId: si.SessionId,
                    WinStationName: winStationName,
                    State: si.State,
                    UserName: username));

                current = IntPtr.Add(current, structSize);
            }
        }
        finally
        {
            if (ppSessionInfo != IntPtr.Zero)
            {
                WTSFreeMemory(ppSessionInfo);
            }
        }

        return list;
    }

    public static GhostSessionInfo? FindSessionByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        var all = GetAllSessions();
        return all
            .Where(s => string.Equals(s.UserName, username, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.State == WTS_CONNECTSTATE_CLASS.WTSActive ? 0 : 1)
            .FirstOrDefault();
    }

    public static string? GetSessionUsername(int sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformation(
                WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                WTS_INFO_CLASS.WTSUserName,
                out buffer,
                out int bytesReturned))
            {
                if (buffer != IntPtr.Zero && bytesReturned > 1)
                {
                    string? user = Marshal.PtrToStringUni(buffer);
                    return string.IsNullOrWhiteSpace(user) ? null : user.Trim();
                }
            }
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                WTSFreeMemory(buffer);
            }
        }
    }

    public static bool DisconnectSession(int sessionId, bool wait = false) =>
        WTSDisconnectSession(WTS_CURRENT_SERVER_HANDLE, sessionId, wait);

    public static bool LogoffSession(int sessionId, bool wait = false) =>
        WTSLogoffSession(WTS_CURRENT_SERVER_HANDLE, sessionId, wait);
}

#endregion
