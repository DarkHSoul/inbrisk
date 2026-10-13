using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Inbrisk.Setup;
using Microsoft.Win32.SafeHandles;

namespace Inbrisk.Mcp.Daemon;

/// <summary>
/// F12: server-side verification of named-pipe clients. The pipe DACL
/// confines connections to the daemon's user (+ admins/SYSTEM) at default
/// integrity, but any process running as that user could still open a
/// session and drive the desktop. On each accepted connection the daemon
/// resolves the peer's process id via GetNamedPipeClientProcessId and
/// requires the peer image to be an Inbrisk host binary (name allowlist,
/// install-root path, or matching product name) — everything else is
/// rejected and logged. Verification fails closed: a peer whose identity
/// cannot be resolved gets no session.
/// </summary>
internal static class PipeClientAuthenticator
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Verifies the client on the other end of a just-accepted pipe
    /// connection. Returns true when the peer is allowed; on false,
    /// <paramref name="rejectReason"/> describes why (for logging) and
    /// <paramref name="peerDescription"/> identifies the peer as far as it
    /// could be resolved.
    /// </summary>
    public static bool TryVerifyClient(
        NamedPipeServerStream stream,
        out string peerDescription,
        out string? rejectReason,
        out int clientProcessId)
    {
        peerDescription = "unresolved peer";
        rejectReason = null;
        clientProcessId = 0;

        // Peer-process identity is a Windows facility — the DACL that
        // makes this a security boundary only exists there too. The
        // non-Windows fallback stream has no such boundary to extend.
        if (!OperatingSystem.IsWindows())
            return true;

        // Escape hatch for dev/test harnesses hosted by non-Inbrisk
        // binaries (e.g. a custom automation host connecting cross-process).
        if (Environment.GetEnvironmentVariable("INBRISK_IPC_AUTH") is
            "0" or "off" or "false" or "disabled")
            return true;

        uint pid;
        try
        {
            if (!GetNamedPipeClientProcessId(stream.SafePipeHandle, out pid) || pid == 0)
            {
                rejectReason = "GetNamedPipeClientProcessId failed";
                return false;
            }
        }
        catch (Exception ex)
        {
            rejectReason = $"GetNamedPipeClientProcessId threw: {ex.Message}";
            return false;
        }

        clientProcessId = (int)pid;
        peerDescription = $"pid={pid}";

        // In-process clients: the proxy can host the daemon inside its own
        // process (ConnectOrStartDaemonAsync) and tests attach both ends to
        // one host — a client whose pid is ours is definitionally trusted.
        if (pid == (uint)Environment.ProcessId)
        {
            peerDescription = $"pid={pid} (self)";
            return true;
        }

        var exePath = QueryProcessImagePath(pid);
        string? exeName = null;
        if (exePath != null)
        {
            exeName = Path.GetFileNameWithoutExtension(exePath);
        }
        else
        {
            // Fall back to Process for the name — covers paths the
            // limited-information query can't reach.
            try
            {
                using var p = Process.GetProcessById((int)pid);
                exeName = p.ProcessName;
                try { exePath = p.MainModule?.FileName; } catch { }
            }
            catch { /* peer may have exited between connect and query */ }
        }

        peerDescription = exePath != null
            ? $"pid={pid} exe={exePath}"
            : $"pid={pid} name={exeName ?? "?"}";

        if (string.IsNullOrEmpty(exeName))
        {
            rejectReason = "peer image could not be resolved";
            return false;
        }

        if (IsAllowedPeer(exeName, exePath))
            return true;

        rejectReason = "peer binary is not an allowlisted Inbrisk host";
        return false;
    }

    private static bool IsAllowedPeer(string exeName, string? exePath)
    {
        // The only production pipe clients: the CLI (inbrisk.exe) and the
        // thin MCP proxy (inbrisk-mcp.exe).
        if (exeName.Equals("inbrisk", StringComparison.OrdinalIgnoreCase) ||
            exeName.Equals("inbrisk-mcp", StringComparison.OrdinalIgnoreCase))
            return true;

        if (exePath != null)
        {
            // Any binary launched from an Inbrisk root — covers renamed
            // binaries, side-by-side tools shipped in the install dir, and
            // dotnet-hosted runs (under `dotnet inbrisk-mcp.dll` the peer
            // image is dotnet.exe, whose directory is then ProcessDir).
            foreach (var root in AllowedRoots())
            {
                if (IsUnderDirectory(exePath, root))
                    return true;
            }

            // Cheap second identity signal for renamed/sideloaded Inbrisk
            // binaries: the managed product name in the exe version block.
            try
            {
                var product = FileVersionInfo.GetVersionInfo(exePath).ProductName;
                if (product != null &&
                    product.Contains("inbrisk", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { }
        }

        // Operator extension: ';'-separated exe names or full paths.
        var extra = Environment.GetEnvironmentVariable("INBRISK_IPC_ALLOW_EXE");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var entry in extra.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (entry.Equals(exeName, StringComparison.OrdinalIgnoreCase) ||
                    entry.Equals(exeName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                    (exePath != null && InstallLayout.PathsEqual(entry, exePath)))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> AllowedRoots()
    {
        yield return InstallLayout.ProcessDir;
        yield return InstallLayout.UserInstallDir;
        yield return InstallLayout.MachineInstallDir;
    }

    private static bool IsUnderDirectory(string path, string dir)
    {
        string fullPath, fullDir;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd('\\');
            fullDir = Path.GetFullPath(dir).TrimEnd('\\');
        }
        catch { return false; }

        return fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryProcessImagePath(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageName(h, 0, sb, ref size))
                return null;
            var path = sb.ToString();
            // Normalize the \\?\ prefix the API returns for some paths.
            return path.StartsWith(@"\\?\", StringComparison.Ordinal)
                ? path[4..]
                : path;
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
