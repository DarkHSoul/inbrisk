using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Capture;

/// <summary>
/// M1 capture: GDI CopyFromScreen. Honest "basic capture" — WGC arrives in M2.
/// Physical px in, PNG + FrameTransform out.
/// </summary>
public sealed class GdiCapture : ICaptureService
{
    private readonly IWindowService _windows;

    public GdiCapture(IWindowService windows)
    {
        Dpi.EnsurePerMonitorV2();
        _windows = windows;
    }

    public Frame Capture(RectPx region, int maxImageWidth = 1600)
        => Capture(region, maxImageWidth, null);

    private Frame Capture(RectPx region, int maxImageWidth,
        long? sourceHwnd)
    {
        using var bmp = new Bitmap(region.Width, region.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(region.X, region.Y, 0, 0,
                new Size(region.Width, region.Height), CopyPixelOperation.SourceCopy);

        var outW = region.Width;
        var outH = region.Height;
        Bitmap? scaled = null;
        if (region.Width > maxImageWidth)
        {
            var scale = (double)maxImageWidth / region.Width;
            outW = maxImageWidth;
            outH = (int)Math.Round(region.Height * scale);
            scaled = new Bitmap(outW, outH);
            using var g2 = Graphics.FromImage(scaled);
            g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g2.DrawImage(bmp, 0, 0, outW, outH);
        }

        var source = scaled ?? bmp;
        using var ms = new MemoryStream();
        source.Save(ms, ImageFormat.Png);
        scaled?.Dispose();
        return new Frame(ms.ToArray(),
            new FrameTransform(region, outW, outH, sourceHwnd), DateTimeOffset.Now);
    }

    public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600)
    {
        var w = _windows.GetWindow(hwnd)
            ?? throw new InbriskException(ErrorCode.NotFound, $"window 0x{hwnd:X} not found");
        if (w.State == WindowState.Minimized)
            throw new InbriskException(ErrorCode.CaptureFailed, "window is minimized");
        var region = w.Bounds.Intersect(_windows.GetVirtualDesktopBounds());
        if (region.IsEmpty)
            throw new InbriskException(ErrorCode.CaptureFailed, "window has no visible area");
        return Capture(region, maxImageWidth, hwnd);
    }

    /// <summary>Raw BGRA capture of a desktop-space rect (no PNG encode).</summary>
    public RawFrame CaptureRaw(RectPx region)
    {
        var w = Math.Max(1, region.Width);
        var h = Math.Max(1, region.Height);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(region.X, region.Y, 0, 0,
                new Size(w, h), CopyPixelOperation.SourceCopy);
        }
        catch
        {
            // Desktop handle invalid (e.g. locked desktop / headless) — frame remains zeroed
        }
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return new RawFrame(bytes, w, h, Math.Abs(data.Stride),
                new FrameTransform(region, w, h), DateTimeOffset.Now);
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Pays the one-time GDI+ startup cost (gdiplus.dll init, JIT)
    /// without touching the screen — a tiny offscreen bitmap only.</summary>
    public void WarmUp()
    {
        using var bmp = new Bitmap(2, 2);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
    }

    /// <summary>Continuous session entry point for GDI-only paths — the router
    /// in CaptureService is the public factory.</summary>
    public ICaptureSession CreateSession(CaptureTarget target) =>
        CaptureService.CreateSession(target, this, _windows);

    /// <summary>
    /// Two small samples 250ms apart → fraction of differing bytes.
    /// Cheap local change signal; frame-diff heuristics get smarter in M2.
    /// </summary>
    public double DiffFraction(RectPx region, int sampleScale = 8)
    {
        var a = Sample(region, sampleScale);
        Thread.Sleep(250);
        var b = Sample(region, sampleScale);
        return Diff(a, b);
    }

    /// <summary>Small downsampled grayscale snapshot for diff loops.</summary>
    public byte[] Sample(RectPx region, int scale = 8)
    {
        var w = Math.Max(8, region.Width / scale);
        var h = Math.Max(8, region.Height / scale);
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = InterpolationMode.Bilinear;
            using var full = new Bitmap(region.Width, region.Height);
            using (var gf = Graphics.FromImage(full))
                gf.CopyFromScreen(region.X, region.Y, 0, 0, full.Size, CopyPixelOperation.SourceCopy);
            g.DrawImage(full, 0, 0, w, h);
        }
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }

    public static double Diff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 1.0;
        var diff = 0;
        for (var i = 0; i < a.Length; i += 3)
            if (Math.Abs(a[i] - b[i]) > 12) diff++;
        return (double)diff / (a.Length / 3);
    }
}
