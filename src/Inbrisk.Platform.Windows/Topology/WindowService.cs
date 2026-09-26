using System.Runtime.InteropServices;
using System.Text;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>Win32 truth about windows, monitors, virtual desktops, foreground.</summary>
public sealed class WindowService : IWindowService
{
    private static readonly int SelfPid = Environment.ProcessId;

    public WindowService() => Dpi.EnsurePerMonitorV2();

    public IReadOnlyList<WindowInfo> ListWindows() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var monitors = GetMonitors().ToList();
        var fg = NativeMethods.GetForegroundWindow();
        var vdm = TryCreateVdm();
        var list = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (string.IsNullOrEmpty(title)) return true; // skip untitled tool windows

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == SelfPid) return true;

            NativeMethods.GetWindowRect(hwnd, out var r);
            var bounds = new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

            var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monIdx = monitors.FindIndex(m => m.Bounds == RectOf(mon));

            var onVd = true;
            if (vdm != null)
            {
                try { onVd = vdm.IsWindowOnCurrentVirtualDesktop(hwnd) != 0; }
                catch { /* treat as on current */ }
            }

            list.Add(new WindowInfo(
                Hwnd: hwnd.ToInt64(),
                Pid: pid,
                Title: title,
                ProcessName: GetProcessName(pid),
                Bounds: bounds,
                State: NativeMethods.IsIconic(hwnd) ? WindowState.Minimized
                     : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized
                     : WindowState.Normal,
                IsForeground: hwnd == fg,
                IsElevated: IntegrityService.IsProcessElevated(pid),
                OnCurrentVirtualDesktop: onVd,
                MonitorIndex: monIdx));
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public WindowInfo? GetWindow(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return null;
        var fg = NativeMethods.GetForegroundWindow();
        var mon = NativeMethods.MonitorFromWindow(h, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitors = GetMonitors().ToList();
        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        NativeMethods.GetWindowRect(h, out var r);
        var onVd = true;
        var vdm = TryCreateVdm();
        if (vdm != null) try { onVd = vdm.IsWindowOnCurrentVirtualDesktop(h) != 0; } catch { }

        return new WindowInfo(
            hwnd, pid, GetTitle(h), GetProcessName(pid),
            new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            NativeMethods.IsIconic(h) ? WindowState.Minimized
                : NativeMethods.IsZoomed(h) ? WindowState.Maximized
                : WindowState.Normal,
            h == fg, IntegrityService.IsProcessElevated(pid), onVd,
            monitors.FindIndex(m => m.Bounds == RectOf(mon)));
    });

    /// <summary>Visible window bounds (DWM extended frame bounds) — excludes the
    /// invisible resize/shadow margins GetWindowRect includes. Matches what WGC
    /// captures for a window item.</summary>
    public RectPx? VisibleBounds(long hwnd) => DesktopBridge.RunOnDefaultDesktop<RectPx?>(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return null;
        var hr = NativeMethods.DwmGetWindowAttributeRect(h,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>());
        if (hr != 0) NativeMethods.GetWindowRect(h, out r);
        return new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    });

    public WindowInfo? GetForegroundWindow() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg == IntPtr.Zero ? null : GetWindow(fg.ToInt64());
    });

    public IReadOnlyList<MonitorInfo> GetMonitors() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdcMon, ref RECT r, IntPtr dwData) =>
        {
            var info = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            NativeMethods.GetMonitorInfoW(hMon, ref info);
            NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out var dy);
            list.Add(new MonitorInfo(
                Index: list.Count,
                Bounds: new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                DpiX: (int)(dx == 0 ? 96 : dx),
                DpiY: (int)(dy == 0 ? 96 : dy),
                IsPrimary: (info.DwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                DeviceName: info.SzDevice));
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    });

    public RectPx GetVirtualDesktopBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Foreground-lock aware activation: AttachThreadInput lets us borrow the
    /// foreground thread's right to call SetForegroundWindow.
    /// </summary>
    public bool FocusWindow(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return false;
        if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == h) return true;

        var cur = NativeMethods.GetCurrentThreadId();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(h, out _);

        var attachedFg = fgThread != 0 && fgThread != cur &&
            NativeMethods.AttachThreadInput(cur, fgThread, true);
        var attachedTarget = targetThread != 0 && targetThread != cur && targetThread != fgThread &&
            NativeMethods.AttachThreadInput(cur, targetThread, true);

        NativeMethods.BringWindowToTop(h);
        var ok = NativeMethods.SetForegroundWindow(h);

        if (attachedTarget) NativeMethods.AttachThreadInput(cur, targetThread, false);
        if (attachedFg) NativeMethods.AttachThreadInput(cur, fgThread, false);

        return ok || NativeMethods.GetForegroundWindow() == h;
    });

    internal static string GetTitle(IntPtr hwnd)
    {
        var len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string? GetProcessName(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(512);
            var size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size)
                ? Path.GetFileName(sb.ToString()) : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static RectPx RectOf(IntPtr monitor)
    {
        var info = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        return new RectPx(info.RcMonitor.Left, info.RcMonitor.Top,
            info.RcMonitor.Right - info.RcMonitor.Left,
            info.RcMonitor.Bottom - info.RcMonitor.Top);
    }

    private static IVirtualDesktopManager? TryCreateVdm()
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerCom(); }
        catch { return null; }
    }
}
