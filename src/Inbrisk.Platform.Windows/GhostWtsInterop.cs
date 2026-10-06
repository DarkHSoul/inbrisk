using System.Runtime.InteropServices;

namespace Inbrisk.Platform.Windows;

#region Enums & Structs

public enum WTS_CONNECTSTATE_CLASS
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

public enum WTS_INFO_CLASS
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
public struct WTS_SESSION_INFO
{
    public int SessionId;
    public IntPtr pWinStationName;
    public WTS_CONNECTSTATE_CLASS State;
}

public record GhostSessionInfo(
    int SessionId,
    string WinStationName,
    WTS_CONNECTSTATE_CLASS State,
    string? UserName);

#endregion

/// <summary>
/// Low-level P/Invoke interop for Windows Terminal Services (wtsapi32.dll) and session management.
/// Provides diagnostic, discovery, and lifecycle helpers for Windows sessions.
/// </summary>
public static class GhostWtsInterop
{
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    public const int WTS_CURRENT_SESSION = -1;

    #region Win32 P/Invoke

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr WTSOpenServer(string pServerName);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern void WTSCloseServer(IntPtr hServer);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr hServer, int SessionId, bool bWait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSDisconnectSession(IntPtr hServer, int SessionId, bool bWait);

    #endregion

    #region High-Level C# Helpers

    /// <summary>
    /// Enumerates all sessions on the local machine regardless of connection state.
    /// </summary>
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

    /// <summary>
    /// Gets active user sessions on the local machine.
    /// By default, filters for State == WTS_CONNECTSTATE_CLASS.WTSActive.
    /// Pass onlyActiveState = false to retrieve all sessions.
    /// </summary>
    public static List<GhostSessionInfo> GetActiveSessions(bool onlyActiveState = true)
    {
        var all = GetAllSessions();
        return onlyActiveState
            ? all.Where(s => s.State == WTS_CONNECTSTATE_CLASS.WTSActive).ToList()
            : all;
    }

    /// <summary>
    /// Finds a session matching the specified username (case-insensitive).
    /// If multiple sessions exist for the user, prioritizes active sessions over disconnected ones.
    /// </summary>
    public static GhostSessionInfo? FindSessionByUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        var all = GetAllSessions();
        return all
            .Where(s => string.Equals(s.UserName, username, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.State == WTS_CONNECTSTATE_CLASS.WTSActive ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// Queries the logged-on username for a given session ID.
    /// Returns null if no user is logged in or if query fails.
    /// </summary>
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

    /// <summary>
    /// Queries the connection state for a given session ID.
    /// Returns null if query fails.
    /// </summary>
    public static WTS_CONNECTSTATE_CLASS? GetSessionState(int sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (WTSQuerySessionInformation(
                WTS_CURRENT_SERVER_HANDLE,
                sessionId,
                WTS_INFO_CLASS.WTSConnectState,
                out buffer,
                out int bytesReturned))
            {
                if (buffer != IntPtr.Zero && bytesReturned >= sizeof(int))
                {
                    int stateVal = Marshal.ReadInt32(buffer);
                    return (WTS_CONNECTSTATE_CLASS)stateVal;
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

    /// <summary>
    /// Disconnects the specified session from the local server.
    /// </summary>
    public static bool DisconnectSession(int sessionId, bool wait = false) =>
        WTSDisconnectSession(WTS_CURRENT_SERVER_HANDLE, sessionId, wait);

    /// <summary>
    /// Logs off the specified session from the local server.
    /// </summary>
    public static bool LogoffSession(int sessionId, bool wait = false) =>
        WTSLogoffSession(WTS_CURRENT_SERVER_HANDLE, sessionId, wait);

    /// <summary>
    /// Retrieves the session ID of a process by its process ID.
    /// </summary>
    public static uint? GetProcessSessionId(uint processId) =>
        ProcessIdToSessionId(processId, out uint sid) ? sid : null;

    #endregion
}
