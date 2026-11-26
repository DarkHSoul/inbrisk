using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

public sealed partial class GhostInputExecutor : IDisposable
{


    /// <summary>
    /// Launches an application or process inside the current environment.
    /// Resolves common application names (Notepad, Chrome, Blender, etc.) automatically.
    /// </summary>
    public Process LaunchProcess(
        string fileName,
        string? arguments = null,
        string? workingDirectory = null,
        bool waitForInputIdle = true,
        int waitTimeoutMs = 5000)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        waitTimeoutMs = Math.Clamp(waitTimeoutMs, 0, 120_000);

        string resolvedExe = ResolveExecutablePath(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedExe,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = workingDirectory ?? string.Empty,
            UseShellExecute = true
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to launch process for \"{resolvedExe}\".");

        if (waitForInputIdle)
        {
            try
            {
                process.WaitForInputIdle(waitTimeoutMs);
            }
            catch
            {
                // Non-GUI or console apps might throw InvalidOperationException on WaitForInputIdle
            }
        }

        return process;
    }

    /// <summary>
    /// Attempts to bring the specified window to the foreground and activate it.
    /// Uses Win32 thread input attachment to bypass standard Windows focus-lock restrictions.
    /// </summary>
    public bool SetForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return false;

        if (NativeMethods.IsIconic(hWnd))
            NativeMethods.ShowWindow(hWnd, SW_RESTORE);
        else
            NativeMethods.ShowWindow(hWnd, SW_SHOW);

        uint currentThreadId = NativeMethods.GetCurrentThreadId();
        IntPtr foregroundHwnd = NativeMethods.GetForegroundWindow();
        uint foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);

        bool attached = false;
        if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
        {
            attached = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
        }

        try
        {
            NativeMethods.BringWindowToTop(hWnd);
            NativeMethods.SetForegroundWindow(hWnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }

        return NativeMethods.GetForegroundWindow() == hWnd;
    }

    /// <summary>
    /// Activates the window represented by the given HWND.
    /// </summary>
    public bool ActivateWindow(IntPtr hWnd) => SetForegroundWindow(hWnd);

    /// <summary>
    /// Finds and activates a window by title or process name within the given timeout.
    /// </summary>
    public async Task<bool> ActivateWindowAsync(
        string windowTitleOrProcess,
        int timeoutMs = 5000,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = FindTargetWindow(windowTitleOrProcess, windowTitleOrProcess);
            if (hwnd != IntPtr.Zero)
            {
                if (SetForegroundWindow(hwnd))
                    return true;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Closes a window gracefully by sending WM_CLOSE.
    /// </summary>
    public bool CloseWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !NativeMethods.IsWindow(hWnd))
            return false;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        return PostMessageW(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Closes a window found by title.
    /// </summary>
    public bool CloseWindow(string windowTitle)
    {
        var hwnd = FindTargetWindow(windowTitle: windowTitle);
        return hwnd != IntPtr.Zero && CloseWindow(hwnd);
    }

    /// <summary>
    /// Waits asynchronously until a matching window appears or timeout expires.
    /// </summary>
    public async Task<IntPtr> WaitForWindowAsync(
        string windowTitleOrProcess,
        int timeoutMs = 10000,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            var hwnd = FindTargetWindow(windowTitleOrProcess, windowTitleOrProcess);
            if (hwnd != IntPtr.Zero)
                return hwnd;

            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Searches top-level windows matching title, process name, or process ID.
    /// </summary>
    public IntPtr FindTargetWindow(string? windowTitle = null, string? processName = null, int? processId = null)
    {
        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
                return true;

            if (processId.HasValue)
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                if (pid == processId.Value)
                {
                    found = hwnd;
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(windowTitle))
            {
                var sb = new StringBuilder(512);
                NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
                string title = sb.ToString();

                if (title.Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                {
                    found = hwnd;
                    return false;
                }
            }

            if (!string.IsNullOrEmpty(processName))
            {
                NativeMethods.GetWindowThreadProcessId(hwnd, out int pid);
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hwnd;
                        return false;
                    }
                }
                catch { /* Access denied or process exited */ }
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string ResolveExecutablePath(string raw)
    {
        string trimmed = raw.Trim().Trim('"');

        if (File.Exists(trimmed))
            return trimmed;

        string lower = Path.GetFileNameWithoutExtension(trimmed).ToLowerInvariant();

        switch (lower)
        {
            case "notepad":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

            case "chrome":
                string chromePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(chromePath)) return chromePath;
                string chromeX86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe");
                if (File.Exists(chromeX86)) return chromeX86;
                return "chrome.exe";

            case "blender":
                string blenderDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Blender Foundation");
                if (Directory.Exists(blenderDir))
                {
                    var found = Directory.GetFiles(blenderDir, "blender.exe", SearchOption.AllDirectories).FirstOrDefault();
                    if (found != null) return found;
                }
                return "blender.exe";

            case "calc" or "calculator":
                return "calc.exe";

            case "explorer":
                return "explorer.exe";

            case "cmd":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

            case "powershell":
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");

            default:
                if (!Path.HasExtension(trimmed))
                    return trimmed + ".exe";
                return trimmed;
        }
    }
}
