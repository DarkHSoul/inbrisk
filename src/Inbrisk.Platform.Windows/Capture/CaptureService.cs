using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Capture;

/// <summary>
/// One-shot + continuous capture front door. Backend selection:
///   window/monitor/region → WGC when supported (falls back to GDI),
///   multi-monitor full desktop → GDI (WGC is per-item).
/// </summary>
public sealed class CaptureService : ICaptureService
{
    /// <summary>Why the last session creation fell back from WGC (null = WGC used or never tried).</summary>
    public static string? LastWgcFallbackReason => WgcSession.LastInitError;
    /// <summary>Last frame-readback failure inside a WGC session.</summary>
    public static string? LastWgcFrameError => WgcSession.LastFrameError;

    private readonly GdiCapture _gdi;
    private readonly IWindowService _windows;

    public CaptureService(IWindowService windows)
    {
        _windows = windows;
        _gdi = new GdiCapture(windows);
    }

    public Frame Capture(RectPx region, int maxImageWidth = 1600) =>
        _gdi.Capture(region, maxImageWidth);

    public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) =>
        _gdi.CaptureWindow(hwnd, maxImageWidth);

    public RawFrame CaptureRaw(RectPx region) => _gdi.CaptureRaw(region);

    /// <summary>Background first-touch of capture plumbing: GDI+ init plus the
    /// WGC capability probe (warms the WinRT/D3D activation path without
    /// opening a session or reading pixels).</summary>
    public void WarmUp()
    {
        try { _gdi.WarmUp(); } catch { }
        try { _ = global::Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported(); }
        catch { }
    }

    public double DiffFraction(RectPx region, int sampleScale = 8) =>
        _gdi.DiffFraction(region, sampleScale);

    public byte[] Sample(RectPx region, int scale = 8) => _gdi.Sample(region, scale);

    public ICaptureSession CreateSession(CaptureTarget target) =>
        CreateSession(target, _gdi, _windows);

    internal static ICaptureSession CreateSession(CaptureTarget target,
        GdiCapture gdi, IWindowService windows)
    {
        var wgc = target is CaptureTarget.FullDesktop ? null : WgcSession.TryCreate(target, windows);
        if (wgc != null) return wgc;

        return new GdiSession(target, () => ResolveRect(target, windows), gdi, windows);
    }

    public static RectPx ResolveRect(CaptureTarget target, IWindowService windows) => target switch
    {
        CaptureTarget.Window w => windows.GetWindow(w.Hwnd)?.Bounds
            ?? throw new InbriskException(ErrorCode.NotFound, $"window 0x{w.Hwnd:X} gone"),
        CaptureTarget.Monitor m => windows.GetMonitors() is { } mns
            ? (m.Index < mns.Count ? mns[m.Index] : mns[0]).Bounds
            : windows.GetVirtualDesktopBounds(),
        CaptureTarget.Region r => r.Rect,
        CaptureTarget.FullDesktop => windows.GetVirtualDesktopBounds(),
        _ => windows.GetVirtualDesktopBounds(),
    };
}
