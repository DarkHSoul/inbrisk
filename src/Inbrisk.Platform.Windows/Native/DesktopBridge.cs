using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Inbrisk.Platform.Windows.Native;

/// <summary>
/// Ensures worker threads and UIA dispatchers bind to the user's interactive
/// "Default" desktop even when Inbrisk is launched from an isolated desktop sandbox
/// (such as agent environments running on exebox-* desktops on WinSta0).
/// </summary>
public static class DesktopBridge
{
    private const uint DesktopAllAccess = 0x01FF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    /// <summary>
    /// Launches an executable process with STARTUPINFO.lpDesktop explicitly set to @"WinSta0\Default"
    /// so the window appears on the user's interactive desktop rather than a background sandbox desktop.
    /// </summary>
    public static int? CreateProcessOnDefaultDesktop(string applicationPath, IReadOnlyList<string>? args = null, string? workingDirectory = null)
    {
        try
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf<STARTUPINFO>();
            si.lpDesktop = @"WinSta0\Default";

            var cmdBuilder = new StringBuilder();
            var trimmedPath = applicationPath.Trim();
            if (trimmedPath.StartsWith("\"") && trimmedPath.EndsWith("\""))
                cmdBuilder.Append(trimmedPath);
            else
                cmdBuilder.Append('"').Append(trimmedPath).Append('"');

            if (args != null)
            {
                foreach (var arg in args)
                {
                    if (arg == null) continue;
                    cmdBuilder.Append(' ');
                    if (arg.Length == 0)
                    {
                        cmdBuilder.Append("\"\"");
                    }
                    else if (!arg.Contains(' ') && !arg.Contains('\t') && !arg.Contains('"'))
                    {
                        cmdBuilder.Append(arg);
                    }
                    else
                    {
                        cmdBuilder.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                    }
                }
            }

            var appName = File.Exists(trimmedPath) ? trimmedPath : null;
            bool ok = CreateProcessW(
                appName,
                cmdBuilder,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                ref si,
                out var pi);

            if (!ok && appName != null)
            {
                // Retry with null appName and command line token if absolute path failed
                ok = CreateProcessW(
                    null,
                    cmdBuilder,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    0,
                    IntPtr.Zero,
                    string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                    ref si,
                    out pi);
            }

            if (ok)
            {
                if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
                if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }

            // Fallback: start via Process.Start wrapped in RunOnDefaultDesktop
            return RunOnDefaultDesktop(() =>
            {
                var psi = new ProcessStartInfo(trimmedPath)
                {
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory ?? ""
                };
                if (args != null)
                {
                    foreach (var a in args) psi.ArgumentList.Add(a);
                }
                var p = Process.Start(psi);
                return p?.Id;
            });
        }
        catch
        {
            return null;
        }
    }

    public static string GetCurrentDesktopName()
    {
        try
        {
            var hCur = GetThreadDesktop(GetCurrentThreadId());
            if (hCur == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            return GetUserObjectInformation(hCur, 2, sb, 256, out _) ? sb.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    public static bool IsOnDefaultDesktop()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("INBRISK_DESKTOP_BRIDGE"), "off", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = GetCurrentDesktopName();
        return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TrySwitchCurrentThread()
    {
        if (IsOnDefaultDesktop()) return true;
        try
        {
            var hDef = OpenDesktop("Default", 0, false, DesktopAllAccess);
            if (hDef == IntPtr.Zero) return false;
            var ok = SetThreadDesktop(hDef);
            // Note: do not close hDef if SetThreadDesktop succeeded; thread now uses it
            if (!ok) CloseDesktop(hDef);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes the specified function on the Default desktop. If the calling thread
    /// is already on Default, runs inline. If the calling thread cannot switch (has windows/hooks),
    /// executes on a fresh MTA worker thread where SetThreadDesktop is invoked before any window calls.
    /// </summary>
    public static T RunOnDefaultDesktop<T>(Func<T> action)
    {
        if (IsOnDefaultDesktop() || TrySwitchCurrentThread())
        {
            return action();
        }

        // Spawn clean worker thread to cross desktop boundary
        T result = default!;
        Exception? error = null;

        var t = new Thread(() =>
        {
            try
            {
                var hDef = OpenDesktop("Default", 0, false, DesktopAllAccess);
                if (hDef != IntPtr.Zero)
                {
                    SetThreadDesktop(hDef);
                }
                result = action();
                if (hDef != IntPtr.Zero) CloseDesktop(hDef);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "inbrisk-desktop-bridge"
        };

        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();

        if (error != null) throw new InvalidOperationException("Failed on Default desktop", error);
        return result;
    }

    public static void RunOnDefaultDesktop(Action action)
    {
        RunOnDefaultDesktop<bool>(() =>
        {
            action();
            return true;
        });
    }
}

