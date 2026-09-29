using System.Diagnostics;
using System.Runtime.InteropServices;
using Inbrisk.Sdk;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>
/// Shared desktop fixture: builds the path to TestApp, launches it, waits for
/// its window, and owns the single Inbrisk runtime for the whole collection.
/// All UI-touching tests run sequentially in the "desktop" collection.
/// </summary>
public sealed class DesktopFixture : IDisposable
{
    public Inbrisk.Sdk.InbriskRuntime Inbrisk { get; }
    public Process TestApp { get; }
    public long Hwnd { get; }
    public static readonly string StateFile =
        Path.Combine(Path.GetTempPath(), "inbrisk_testapp", "state.txt");

    /// <summary>Read the TestApp state file tolerantly — the app writes it
    /// from UI handlers while tests poll it, so a sharing violation is a
    /// normal transient, not a failure. Returns null while contested.</summary>
    public static string? ReadState()
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var fs = new FileStream(StateFile, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(fs);
                return r.ReadToEnd();
            }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
        return null;
    }

    /// <summary>Tolerant counterpart for test-side state resets.</summary>
    public static void WriteState(string text)
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                using var fs = new FileStream(StateFile, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                using var w = new StreamWriter(fs);
                w.Write(text);
                return;
            }
            catch (IOException) { Thread.Sleep(25); }
            catch (UnauthorizedAccessException) { Thread.Sleep(25); }
        }
        throw new IOException($"state file stayed locked: {StateFile}");
    }

    public DesktopFixture()
    {
        Environment.SetEnvironmentVariable("INBRISK_DESKTOP_BRIDGE", "off");
        var exe = FindTestAppExe();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        // ask the app to open on the secondary monitor directly — spawning
        // on the primary first would flash a window on the user's main
        // screen before MoveToSecondaryMonitor could reposition it
        if (SecondaryMonitorCenter(480, 640) is { } pos)
        {
            psi.Environment["INBRISK_TESTAPP_POS"] = $"{pos.X},{pos.Y}";
            psi.Arguments = $"{pos.X},{pos.Y}";
        }
        TestApp = Process.Start(psi)!;
        Inbrisk = new Inbrisk.Sdk.InbriskRuntime(new InbriskOptions(AutoConfirm: true,
            StartEvents: true,
            TelemetryPath: Path.Combine(Path.GetTempPath(), "inbrisk", "test-telemetry.jsonl")));

        // wait for the window to exist and be inspectable
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            var w = Inbrisk.Windows().FirstOrDefault(w => w.Title == "InbriskTestApp");
            if (w != null) { Hwnd = w.Hwnd; break; }
            if (TestApp.HasExited) break;
            Thread.Sleep(200);
        }

        if (Hwnd == 0)
        {
            var wins = Inbrisk.Windows();
            var desc = string.Join("; ", wins.Select(x => $"[pid={x.Pid}, title='{x.Title}', proc='{x.ProcessName}']"));
            throw new InvalidOperationException($"TestApp window never appeared. HasExited={TestApp.HasExited}, ExitCode={(TestApp.HasExited ? TestApp.ExitCode : -1)}. Available windows ({wins.Count}): {desc}");
        }
        // keep the test window off the primary monitor when a secondary
        // display exists — physical-input tests must not touch the user's
        // main screen
        MoveToSecondaryMonitor(Hwnd);
        Inbrisk.Focus(Hwnd);
        Thread.Sleep(300);
    }

    /// <summary>Physical-input tests need the window genuinely raised —
    /// Windows' foreground lock can silently refuse SetForegroundWindow from
    /// a background process, leaving the click to land on whatever is on
    /// top. Retry until the OS agrees, or fail loudly instead of clicking a
    /// wrong window.</summary>
    public void EnsureForeground()
    {
        for (var i = 0; i < 10; i++)
        {
            if (Inbrisk.ForegroundWindow()?.Hwnd == Hwnd) return;
            Inbrisk.Focus(Hwnd);
            Thread.Sleep(150);
        }
        Assert.True(Inbrisk.ForegroundWindow()?.Hwnd == Hwnd,
            "TestApp could not be brought to the foreground");
    }

    /// <summary>Which top-level window would actually receive a click at
    /// this desktop point — diagnostic for "click went to the wrong window".</summary>
    public string TopWindowAt(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return "<none>";
        var root = GetAncestor(h, 2); // GA_ROOT — the real top-level window
        GetWindowThreadProcessId(root, out var pid);
        var w = Inbrisk.Windows().FirstOrDefault(w => w.Hwnd == root.ToInt64());
        var pname = ProcessName(pid);
        return $"0x{root.ToInt64():X} {pname} \"{w?.Title}\" " +
            $"(testapp=0x{Hwnd:X} fg=0x{Inbrisk.ForegroundWindow()?.Hwnd ?? 0:X})";
    }

    private static string ProcessName(int? pid)
    {
        try { return pid is > 0 ? Process.GetProcessById(pid.Value).ProcessName : ""; }
        catch { return ""; }
    }

    /// <summary>Top-left point centering a W×H window on the first
    /// non-primary monitor, or null on a single-display machine.</summary>
    private static (int X, int Y)? SecondaryMonitorCenter(int w, int h)
    {
        RECT? secondary = null;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            delegate (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data)
            {
                var mi = new MONITORINFO
                    { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(hMon, ref mi) &&
                    (mi.dwFlags & MONITORINFOF_PRIMARY) == 0 &&
                    secondary == null)
                    secondary = mi.rcMonitor;
                return true;
            }, IntPtr.Zero);
        if (secondary is not { } r) return null;
        return (r.Left + Math.Max(0, (r.Right - r.Left - w) / 2),
                r.Top + Math.Max(0, (r.Bottom - r.Top - h) / 2));
    }

    /// <summary>Reposition the test window onto a non-primary monitor.
    /// No-op when the machine has a single display.</summary>
    private static void MoveToSecondaryMonitor(long hwnd)
    {
        GetWindowRect(new IntPtr(hwnd), out var w);
        var (ww, wh) = (w.Right - w.Left, w.Bottom - w.Top);
        if (SecondaryMonitorCenter(ww, wh) is { } pos)
            SetWindowPos(new IntPtr(hwnd), IntPtr.Zero, pos.X, pos.Y, 0, 0,
                0x0001 | 0x0004);
    }

    private const int MONITORINFOF_PRIMARY = 1;
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor,
        ref MONITORINFO lpmi);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    /// <summary>Close every leftover TestDialog so tests start clean.</summary>
    public void CloseDialogs()
    {
        for (var n = 0; n < 6; n++)
        {
            var closes = Inbrisk.Find(new Inbrisk.Core.FindSpec(
                WindowTitle: "TestDialog", AutomationId: "CloseDialog"));
            if (closes.Count == 0) return;
            foreach (var c in closes) Inbrisk.Invoke(c.Id);
            Thread.Sleep(250);
        }
    }

    /// <summary>Leave the pixel canvas in a known state — a leftover running
    /// animation produces permanent frame diffs that break change/stability
    /// and no-progress assertions in later tests.</summary>
    public void EnsureAnimationOff()
    {
        try
        {
            var anim = Inbrisk.Find(new Inbrisk.Core.FindSpec(
                Hwnd: Hwnd, AutomationId: "AnimButton"));
            if (anim.Count > 0 && anim[0].Name == "Stop animation")
                Inbrisk.Invoke(anim[0].Id);
            Thread.Sleep(300);
        }
        catch { }
    }

    private static string FindTestAppExe()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "inbrisk.sln")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("cannot locate repo root");
        var exe = Path.Combine(dir, "tests", "Inbrisk.TestApp", "bin", "Debug",
            "net8.0-windows", "Inbrisk.TestApp.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(
                $"TestApp not built — expected {exe}. Run `dotnet build` first.");
        return exe;
    }

    public void Dispose()
    {
        try { Inbrisk.Dispose(); } catch { }
        try { if (!TestApp.HasExited) TestApp.Kill(); } catch { }
        TestApp.Dispose();
        Environment.SetEnvironmentVariable("INBRISK_DESKTOP_BRIDGE", null);
    }
}

[CollectionDefinition("desktop")]
public sealed class DesktopCollection : ICollectionFixture<DesktopFixture> { }
