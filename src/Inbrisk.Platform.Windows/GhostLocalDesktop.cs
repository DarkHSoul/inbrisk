using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows;

#region Win32 Desktop Native Wrapper & Structs

/// <summary>
/// Low-level Win32 P/Invoke definitions and native interop wrapper for Windows Desktop objects
/// and isolated desktop process execution.
/// </summary>
public static class GhostDesktopNative
{
    #region Access Rights & Flags

    public const string DefaultGhostDesktopName = "InbriskGhostDesktop";

    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const uint DESKTOP_CREATEWINDOW = 0x0002;
    public const uint DESKTOP_CREATEMENU = 0x0004;
    public const uint DESKTOP_HOOKCONTROL = 0x0008;
    public const uint DESKTOP_JOURNALRECORD = 0x0010;
    public const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    public const uint DESKTOP_ENUMERATE = 0x0040;
    public const uint DESKTOP_WRITEOBJECTS = 0x0080;
    public const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    public const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;

    /// <summary>
    /// Standard full access rights for Windows desktop objects (0x000F01FF).
    /// </summary>
    public const uint DESKTOP_ALL_ACCESS = 0x000F01FF;

    public const uint STARTF_USESHOWWINDOW = 0x00000001;
    public const short SW_SHOWNORMAL = 1;
    public const short SW_HIDE = 0;

    public const uint CREATE_NEW_CONSOLE = 0x00000010;

    public const int ERROR_ALREADY_EXISTS = 183;

    #endregion

    #region Structs & Delegates

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
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
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool EnumDesktopWindowsProc(IntPtr hWnd, IntPtr lParam);

    #endregion

    #region Win32 P/Invoke Declarations

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(
        string lpszDesktop,
        IntPtr lpszDevice,
        IntPtr pDevmode,
        uint dwFlags,
        uint dwDesiredAccess,
        IntPtr lpsa);

    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenDesktopW(
        string lpszDesktop,
        uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit,
        uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SwitchDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumDesktopWindowsProc lpfn, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(
        [MarshalAs(UnmanagedType.LPWStr)] string? lpApplicationName,
        StringBuilder? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    #endregion

    #region High-Level Native Wrappers

    /// <summary>
    /// Creates or opens a desktop with the specified name and desired access rights (defaults to DESKTOP_ALL_ACCESS).
    /// </summary>
    public static IntPtr CreateDesktop(string desktopName = DefaultGhostDesktopName, uint desiredAccess = DESKTOP_ALL_ACCESS)
    {
        return CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, desiredAccess, IntPtr.Zero);
    }

    /// <summary>
    /// Opens an existing desktop with the specified name and access rights.
    /// </summary>
    public static IntPtr OpenDesktop(string desktopName = DefaultGhostDesktopName, uint desiredAccess = DESKTOP_ALL_ACCESS, bool inherit = false)
    {
        return OpenDesktopW(desktopName, 0, inherit, desiredAccess);
    }

    /// <summary>
    /// Enumerates all top-level window handles on the specified desktop.
    /// </summary>
    public static List<IntPtr> EnumerateWindows(IntPtr hDesktop)
    {
        var windows = new List<IntPtr>();
        if (hDesktop == IntPtr.Zero) return windows;

        EnumDesktopWindows(hDesktop, (hWnd, _) =>
        {
            if (hWnd != IntPtr.Zero)
            {
                windows.Add(hWnd);
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Starts a process attached to the target desktop via STARTUPINFO.lpDesktop.
    /// </summary>
    public static bool CreateProcessWithDesktop(
        string desktopName,
        string fileName,
        string? arguments,
        string? workingDir,
        out PROCESS_INFORMATION processInformation,
        uint dwCreationFlags = CREATE_NEW_CONSOLE)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string commandLine = BuildCommandLine(fileName, arguments);

        var si = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>(),
            lpDesktop = desktopName,
            dwFlags = (int)STARTF_USESHOWWINDOW,
            wShowWindow = SW_SHOWNORMAL
        };

        var sb = new StringBuilder(commandLine);

        return CreateProcessW(
            lpApplicationName: null,
            lpCommandLine: sb,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: dwCreationFlags,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: string.IsNullOrWhiteSpace(workingDir) ? null : workingDir,
            lpStartupInfo: ref si,
            lpProcessInformation: out processInformation);
    }

    /// <summary>
    /// Starts a process attached to the target desktop and returns a managed Process instance.
    /// </summary>
    public static Process CreateProcessWithDesktop(
        string desktopName,
        string fileName,
        string? arguments = null,
        string? workingDir = null,
        uint dwCreationFlags = CREATE_NEW_CONSOLE)
    {
        if (!CreateProcessWithDesktop(desktopName, fileName, arguments, workingDir, out var pi, dwCreationFlags))
        {
            int err = Marshal.GetLastWin32Error();
            GhostLogger.Error($"[GhostDesktopNative] CreateProcessWithDesktop failed for '{fileName}' on desktop '{desktopName}'. Win32 Error: {err}");
            throw new Win32Exception(err, $"Failed to create process '{fileName}' on desktop '{desktopName}' (Win32 Error: {err}).");
        }

        try
        {
            var proc = Process.GetProcessById(pi.dwProcessId);
            proc.EnableRaisingEvents = true;
            return proc;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
        }
    }

    /// <summary>
    /// Formulates a well-quoted command line for Win32 CreateProcessW.
    /// </summary>
    public static string BuildCommandLine(string fileName, string? arguments)
    {
        string trimmedFile = fileName.Trim();
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            string quotedFile = (trimmedFile.StartsWith("\"") && trimmedFile.EndsWith("\""))
                ? trimmedFile
                : $"\"{trimmedFile}\"";
            return $"{quotedFile} {arguments.Trim()}";
        }

        if (trimmedFile.StartsWith("\"") && trimmedFile.EndsWith("\""))
        {
            return trimmedFile;
        }

        // If it's an existing file or contains path separators, quote the entire path
        if (File.Exists(trimmedFile) || trimmedFile.Contains('\\') || trimmedFile.Contains('/'))
        {
            return $"\"{trimmedFile}\"";
        }

        int firstSpace = trimmedFile.IndexOf(' ');
        if (firstSpace > 0)
        {
            string exePart = trimmedFile[..firstSpace];
            string argPart = trimmedFile[(firstSpace + 1)..].Trim();
            string quotedExe = (exePart.StartsWith("\"") && exePart.EndsWith("\"")) ? exePart : $"\"{exePart}\"";
            return $"{quotedExe} {argPart}";
        }

        return $"\"{trimmedFile}\"";
    }

    /// <summary>
    /// Retrieves window title, class name, and visibility status for diagnostic inspection.
    /// </summary>
    public static (string Title, string ClassName, bool IsVisible) GetWindowDetails(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return (string.Empty, string.Empty, false);

        var titleSb = new StringBuilder(512);
        GetWindowTextW(hWnd, titleSb, titleSb.Capacity);

        var classSb = new StringBuilder(256);
        GetClassNameW(hWnd, classSb, classSb.Capacity);

        bool visible = IsWindowVisible(hWnd);
        return (titleSb.ToString(), classSb.ToString(), visible);
    }

    /// <summary>
    /// Inspects windows on the target desktop to determine if an active application or terminal already exists,
    /// and collects process IDs of any console windows.
    /// </summary>
    public static (bool HasActiveApp, List<int> ConsolePids) InspectActiveDesktopWindows(IntPtr hDesktop)
    {
        bool hasActiveApp = false;
        var consolePids = new List<int>();
        var windows = EnumerateWindows(hDesktop);

        foreach (var hWnd in windows)
        {
            if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) continue;

            var (_, className, _) = GetWindowDetails(hWnd);
            if (string.Equals(className, "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
            {
                hasActiveApp = true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid > 0 && !consolePids.Contains((int)pid))
                {
                    consolePids.Add((int)pid);
                }
            }
            else if (!string.IsNullOrEmpty(className) &&
                     !string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "tooltips_class32", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(className, "Default IME", StringComparison.OrdinalIgnoreCase))
            {
                hasActiveApp = true;
            }
        }

        return (hasActiveApp, consolePids);
    }

    #endregion
}

#endregion

#region High-Level GhostLocalDesktop Manager

/// <summary>
/// High-level lifecycle manager for an isolated Windows Desktop (Ghost Desktop).
/// Supports thread-safe desktop creation, process execution with isolated desktop binding,
/// window enumeration, worker thread desktop attachment, and clean resource disposal.
/// </summary>
public sealed class GhostLocalDesktop : IDisposable
{
    public const string DefaultDesktopName = GhostDesktopNative.DefaultGhostDesktopName;

    private readonly object _syncLock = new();
    private readonly string _desktopName;
    private IntPtr _desktopHandle;
    private readonly bool _wasCreatedNew;
    private readonly List<Process> _startedProcesses = new();
    private bool _isDisposed;

    /// <summary>
    /// Gets the name of the ghost desktop.
    /// </summary>
    public string DesktopName => _desktopName;

    /// <summary>
    /// Gets the native handle to the desktop object.
    /// </summary>
    public IntPtr DesktopHandle
    {
        get
        {
            lock (_syncLock)
            {
                return _desktopHandle;
            }
        }
    }

    /// <summary>
    /// Indicates whether the desktop handle is valid and active.
    /// </summary>
    public bool IsCreated
    {
        get
        {
            lock (_syncLock)
            {
                return _desktopHandle != IntPtr.Zero && !_isDisposed;
            }
        }
    }

    /// <summary>
    /// Indicates whether this instance newly created the desktop (true) or opened an existing one (false).
    /// </summary>
    public bool WasCreatedNew => _wasCreatedNew;

    /// <summary>
    /// Gets the number of currently active processes launched by this desktop manager.
    /// Exited processes are automatically purged upon query.
    /// </summary>
    public int ProcessCount
    {
        get
        {
            lock (_syncLock)
            {
                PurgeExitedProcessesLocked();
                return _startedProcesses.Count;
            }
        }
    }

    /// <summary>
    /// Creates or opens the isolated ghost desktop with DESKTOP_ALL_ACCESS rights.
    /// </summary>
    /// <param name="desktopName">Name of the desktop. Defaults to "InbriskGhostDesktop".</param>
    /// <param name="createIfNotExists">True to create if not already existing; false to only open existing.</param>
    public GhostLocalDesktop(string desktopName = DefaultDesktopName, bool createIfNotExists = true)
    {
        if (string.IsNullOrWhiteSpace(desktopName))
        {
            desktopName = DefaultDesktopName;
        }

        _desktopName = desktopName;

        // 1. Try to open existing desktop first
        _desktopHandle = GhostDesktopNative.OpenDesktop(_desktopName, GhostDesktopNative.DESKTOP_ALL_ACCESS, inherit: false);

        if (_desktopHandle != IntPtr.Zero)
        {
            _wasCreatedNew = false;
            GhostLogger.Info($"[GhostLocalDesktop] Opened existing desktop '{_desktopName}' with handle 0x{_desktopHandle:X}.");
            return;
        }

        if (!createIfNotExists)
        {
            int err = Marshal.GetLastWin32Error();
            GhostLogger.Error($"[GhostLocalDesktop] Desktop '{_desktopName}' does not exist and createIfNotExists is false. Win32 Error: {err}");
            throw new Win32Exception(err, $"Desktop '{_desktopName}' does not exist and creation was not requested.");
        }

        // 2. Create desktop with DESKTOP_ALL_ACCESS (0x000F01FF)
        _desktopHandle = GhostDesktopNative.CreateDesktop(_desktopName, GhostDesktopNative.DESKTOP_ALL_ACCESS);
        if (_desktopHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            GhostLogger.Error($"[GhostLocalDesktop] Failed to create desktop '{_desktopName}'. Win32 Error: {err}");
            throw new Win32Exception(err, $"Failed to create desktop '{_desktopName}' (Win32 Error: {err}).");
        }

        int lastError = Marshal.GetLastWin32Error();
        _wasCreatedNew = lastError != GhostDesktopNative.ERROR_ALREADY_EXISTS;
        GhostLogger.Info($"[GhostLocalDesktop] Created desktop '{_desktopName}' with handle 0x{_desktopHandle:X} (NewlyCreated={_wasCreatedNew}).");
    }

    /// <summary>
    /// Starts a process attached to this ghost desktop via STARTUPINFO.lpDesktop.
    /// </summary>
    /// <param name="fileName">The executable file or command name (e.g. cmd.exe, powershell.exe, chrome.exe).</param>
    /// <param name="arguments">Optional command-line arguments.</param>
    /// <param name="workingDir">Optional working directory.</param>
    /// <returns>The started <see cref="Process"/> instance.</returns>
    public Process StartProcess(
        string fileName,
        string? arguments = null,
        string? workingDir = null,
        uint dwCreationFlags = GhostDesktopNative.CREATE_NEW_CONSOLE)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!GhostDesktopNative.CreateProcessWithDesktop(_desktopName, fileName, arguments, workingDir, out var pi, dwCreationFlags))
        {
            int err = Marshal.GetLastWin32Error();
            GhostLogger.Error($"[GhostLocalDesktop] Failed to spawn process '{fileName}' on desktop '{_desktopName}'. Win32 Error: {err}");
            throw new Win32Exception(err, $"Failed to start process '{fileName}' on desktop '{_desktopName}' (Win32 Error: {err}).");
        }

        Process proc;
        try
        {
            proc = Process.GetProcessById(pi.dwProcessId);
            proc.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            GhostLogger.Warn($"[GhostLocalDesktop] Process {pi.dwProcessId} was created on desktop '{_desktopName}' but could not be inspected via Process.GetProcessById: {ex.Message}");
            throw;
        }
        finally
        {
            if (pi.hThread != IntPtr.Zero) GhostDesktopNative.CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) GhostDesktopNative.CloseHandle(pi.hProcess);
        }

        lock (_syncLock)
        {
            _startedProcesses.Add(proc);
            proc.Exited += (_, _) =>
            {
                lock (_syncLock)
                {
                    _startedProcesses.Remove(proc);
                }
            };
        }

        GhostLogger.Info($"[GhostLocalDesktop] Started process '{proc.ProcessName}' (PID: {proc.Id}) on desktop '{_desktopName}'. Tracked processes: {ProcessCount}");
        return proc;
    }

    /// <summary>
    /// Enumerates all top-level window handles currently residing on this ghost desktop.
    /// </summary>
    public IReadOnlyList<IntPtr> GetWindowHandles()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            var windows = GhostDesktopNative.EnumerateWindows(_desktopHandle);
            GhostLogger.Debug($"[GhostLocalDesktop] Enumerated {windows.Count} window handle(s) on desktop '{_desktopName}'.");
            return windows.AsReadOnly();
        }
    }

    /// <summary>
    /// Attaches the calling thread to this ghost desktop using Win32 SetThreadDesktop.
    /// Note: Will fail if the calling thread already owns windows or hooks on another desktop.
    /// For arbitrary threads, consider <see cref="RunOnDesktop{T}(Func{T})"/>.
    /// </summary>
    /// <returns>True if the thread was successfully assigned to this desktop; otherwise false.</returns>
    public bool AttachCurrentThread()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            bool ok = GhostDesktopNative.SetThreadDesktop(_desktopHandle);
            if (ok)
            {
                GhostLogger.Info($"[GhostLocalDesktop] Attached current thread {Environment.CurrentManagedThreadId} (Win32 Thread {GhostDesktopNative.GetCurrentThreadId()}) to desktop '{_desktopName}'.");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                GhostLogger.Warn($"[GhostLocalDesktop] SetThreadDesktop failed for thread {Environment.CurrentManagedThreadId}. Win32 Error: {err} (thread may already possess window handles or hooks).");
            }
            return ok;
        }
    }

    /// <summary>
    /// Executes a delegate on a clean MTA worker thread attached to this ghost desktop.
    /// Ideal for calling UI Automation or desktop-bound APIs without thread desktop conflict.
    /// </summary>
    public T RunOnDesktop<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ThrowIfDisposed();

        IntPtr handle;
        lock (_syncLock)
        {
            handle = _desktopHandle;
        }

        T result = default!;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                if (!GhostDesktopNative.SetThreadDesktop(handle))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"Failed to attach worker thread to desktop '{_desktopName}'. Win32 Error: {err}");
                }

                result = action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = $"ghost-worker-{_desktopName}"
        };

        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new InvalidOperationException($"Execution failed on ghost desktop '{_desktopName}'.", error);
        }

        return result;
    }

    /// <summary>
    /// Executes an action on a clean MTA worker thread attached to this ghost desktop.
    /// </summary>
    public void RunOnDesktop(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RunOnDesktop<object?>(() =>
        {
            action();
            return null;
        });
    }

    /// <summary>
    /// Makes this ghost desktop the active, visible interactive desktop on the display screen.
    /// </summary>
    public bool SwitchToDesktop()
    {
        ThrowIfDisposed();
        lock (_syncLock)
        {
            bool ok = GhostDesktopNative.SwitchDesktop(_desktopHandle);
            if (ok)
            {
                GhostLogger.Info($"[GhostLocalDesktop] Switched display focus to desktop '{_desktopName}'.");
            }
            else
            {
                int err = Marshal.GetLastWin32Error();
                GhostLogger.Warn($"[GhostLocalDesktop] SwitchDesktop failed for '{_desktopName}'. Win32 Error: {err}.");
            }
            return ok;
        }
    }

    /// <summary>
    /// Returns a snapshot of all active processes launched on this desktop.
    /// </summary>
    public IReadOnlyList<Process> GetTrackedProcesses()
    {
        lock (_syncLock)
        {
            PurgeExitedProcessesLocked();
            return _startedProcesses.ToArray();
        }
    }

    /// <summary>
    /// Terminates all tracked processes launched on this desktop.
    /// </summary>
    public void TerminateAllProcesses(int timeoutMs = 3000)
    {
        List<Process> procs;
        lock (_syncLock)
        {
            procs = new List<Process>(_startedProcesses);
        }

        foreach (var proc in procs)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(timeoutMs);
                }
            }
            catch (Exception ex)
            {
                GhostLogger.Warn($"[GhostLocalDesktop] Error terminating process {proc.Id}: {ex.Message}");
            }
        }

        lock (_syncLock)
        {
            PurgeExitedProcessesLocked();
        }
    }

    private void PurgeExitedProcessesLocked()
    {
        _startedProcesses.RemoveAll(p =>
        {
            try
            {
                return p.HasExited;
            }
            catch
            {
                return true;
            }
        });
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(GhostLocalDesktop), $"Desktop '{_desktopName}' has been disposed.");
        }
    }

    #region IDisposable

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (disposing)
            {
                try
                {
                    TerminateAllProcesses(1500);
                }
                catch (Exception ex)
                {
                    GhostLogger.Warn($"[GhostLocalDesktop] Exception during TerminateAllProcesses in Dispose: {ex.Message}");
                }

                foreach (var proc in _startedProcesses)
                {
                    try { proc.Dispose(); } catch { }
                }
                _startedProcesses.Clear();
            }

            if (_desktopHandle != IntPtr.Zero)
            {
                GhostLogger.Info($"[GhostLocalDesktop] Closing desktop handle 0x{_desktopHandle:X} for '{_desktopName}'.");
                bool closed = GhostDesktopNative.CloseDesktop(_desktopHandle);
                if (!closed)
                {
                    int err = Marshal.GetLastWin32Error();
                    GhostLogger.Warn($"[GhostLocalDesktop] CloseDesktop returned false for handle 0x{_desktopHandle:X}. Win32 Error: {err}.");
                }
                _desktopHandle = IntPtr.Zero;
            }
        }
    }

    ~GhostLocalDesktop()
    {
        Dispose(false);
    }

    #endregion
}

#endregion
