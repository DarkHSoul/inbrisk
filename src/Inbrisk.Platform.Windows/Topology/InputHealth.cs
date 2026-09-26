using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>A visible window that can silently swallow desktop input.</summary>
public sealed record InputBlocker(long Hwnd, int Pid, string? ProcessName,
    string Title, RectPx Bounds, int MonitorIndex, double Coverage, int ExStyle);

/// <summary>
/// Detects and remediates windows that swallow user input while appearing
/// invisible — the signature is a VISIBLE, LAYERED window that is NOT
/// click-through (missing WS_EX_TRANSPARENT) and covers most of a monitor.
/// GPU/HUD overlays (NVIDIA GeForce Overlay et al.) hit exactly this state
/// when they glitch: the user sees the desktop through the layer, but every
/// click lands on the overlay. Diagnosed live 2026-02: a stuck overlay
/// window (0x08080080, full-monitor) deadened every app on that monitor.
/// </summary>
public static class InputHealth
{
    /// <summary>Fraction of a monitor a window must cover to qualify.</summary>
    public const double CoverageThreshold = 0.8;

    /// <summary>Pure classifier — testable without real windows.</summary>
    public static bool IsBlockerSuspect(RectPx bounds, int exStyle,
        IReadOnlyList<RectPx> monitors, out int monitorIndex, out double coverage)
    {
        monitorIndex = -1;
        coverage = 0;
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0 ||
            (exStyle & NativeMethods.WS_EX_TRANSPARENT) != 0)
            return false;
        for (var i = 0; i < monitors.Count; i++)
        {
            var c = OverlapFraction(bounds, monitors[i]);
            if (c > coverage) { coverage = c; monitorIndex = i; }
        }
        return coverage >= CoverageThreshold;
    }

    /// <summary>All visible top-level windows matching the blocker signature.</summary>
    public static List<InputBlocker> FindBlockingWindows()
    {
        var monitors = new WindowService().GetMonitors()
            .Select(m => m.Bounds).ToList();
        var found = new List<InputBlocker>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd) ||
                IsCloaked(hwnd)) return true;
            NativeMethods.GetWindowRect(hwnd, out var r);
            var bounds = new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            var exStyle = (int)(long)NativeMethods.GetWindowLongPtr(hwnd,
                NativeMethods.GWL_EXSTYLE);
            if (!IsBlockerSuspect(bounds, exStyle, monitors, out var mon, out var cov))
                return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            found.Add(new InputBlocker(hwnd.ToInt64(), pid,
                WindowService.GetProcessName(pid), WindowService.GetTitle(hwnd),
                bounds, mon, cov, exStyle));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Give a window WS_EX_TRANSPARENT so clicks pass through it —
    /// restores input without killing the owning process. No-op if already
    /// transparent.</summary>
    public static bool MakeClickThrough(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return false;
        var ex = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE);
        if ((ex & NativeMethods.WS_EX_TRANSPARENT) != 0) return true;
        NativeMethods.SetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE,
            new IntPtr(ex | NativeMethods.WS_EX_TRANSPARENT));
        // verify the flag actually landed before trusting it
        var after = (long)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE);
        if ((after & NativeMethods.WS_EX_TRANSPARENT) == 0) return false;
        return NativeMethods.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_FRAMECHANGED);
    }

    public static int GetExStyle(long hwnd) =>
        (int)(long)NativeMethods.GetWindowLongPtr(new IntPtr(hwnd),
            NativeMethods.GWL_EXSTYLE);

    private static double OverlapFraction(RectPx a, RectPx b)
    {
        var w = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X));
        var h = Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        var inter = (double)w * h;
        var monArea = (double)b.Width * b.Height;
        return monArea <= 0 ? 0 : inter / monArea;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }
}
