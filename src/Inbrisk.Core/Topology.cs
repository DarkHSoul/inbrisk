namespace Inbrisk.Core;

public enum WindowState { Normal, Minimized, Maximized }

public enum IntegrityRelation
{
    /// <summary>Target runs at/below our integrity level — input will land.</summary>
    Reachable,
    /// <summary>Target is elevated above us — SendInput will be silently dropped.</summary>
    TargetElevated,
    /// <summary>Could not determine.</summary>
    Unknown,
}

public sealed record WindowInfo(
    long Hwnd,
    int Pid,
    string Title,
    string? ProcessName,
    RectPx Bounds,
    WindowState State,
    bool IsForeground,
    bool IsElevated,
    bool OnCurrentVirtualDesktop,
    int MonitorIndex)
{
    public override string ToString() =>
        $"{Title} [{ProcessName} pid={Pid} hwnd=0x{Hwnd:X}] {Bounds} {State}" +
        (IsElevated ? " ELEVATED" : "") +
        (OnCurrentVirtualDesktop ? "" : " (other-vdesktop)");
}

public sealed record MonitorInfo(
    int Index,
    RectPx Bounds,
    int DpiX,
    int DpiY,
    bool IsPrimary,
    string DeviceName)
{
    public double ScaleX => DpiX / 96.0;
    public double ScaleY => DpiY / 96.0;
}
