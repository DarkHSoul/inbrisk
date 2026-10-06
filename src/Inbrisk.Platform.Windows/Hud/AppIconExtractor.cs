using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Hud;

/// <summary>
/// Extracts and caches application icons from target windows or process names.
/// Falls back to a clean Inbrisk activity icon when no application icon can be found.
/// </summary>
public static class AppIconExtractor
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int IconSize = 24;

    private const int GclpHiconSm = -34;
    private const int GclpHicon = -14;
    private const uint WmGetIcon = 0x007F;
    private const IntPtr IconSmall2 = 2;
    private const IntPtr IconSmall = 0;
    private const IntPtr IconBig = 1;

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Gets a 24x24 Bitmap icon for the given application or window.
    /// </summary>
    public static Bitmap GetIcon(string? appName, long? hwndHint = null)
    {
        var key = (appName ?? "") + "|" + (hwndHint?.ToString("X") ?? "");
        if (string.IsNullOrWhiteSpace(key) || key == "|") return GetFallbackIcon();

        if (Cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? bmp = null;
        if (hwndHint is { } h && h != 0)
        {
            bmp = ExtractFromHwnd(new IntPtr(h));
        }

        if (bmp == null && !string.IsNullOrWhiteSpace(appName))
        {
            bmp = ExtractFromAppName(appName);
        }

        bmp ??= GetFallbackIcon();
        Cache[key] = bmp;
        return bmp;
    }

    private static Bitmap? ExtractFromHwnd(IntPtr hwnd)
    {
        try
        {
            // 1. WM_GETICON
            var hIcon = SendMessage(hwnd, WmGetIcon, IconSmall2, IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = SendMessage(hwnd, WmGetIcon, IconSmall, IntPtr.Zero);
            if (hIcon == IntPtr.Zero) hIcon = SendMessage(hwnd, WmGetIcon, IconBig, IntPtr.Zero);

            // 2. Class Long
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GclpHiconSm);
            if (hIcon == IntPtr.Zero) hIcon = GetClassLongPtr(hwnd, GclpHicon);

            if (hIcon != IntPtr.Zero)
            {
                using var icon = Icon.FromHandle(hIcon);
                return ResizeToIcon(icon.ToBitmap());
            }

            // 3. Process executable path
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0)
            {
                using var proc = Process.GetProcessById((int)pid);
                var path = proc.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    using var assoc = Icon.ExtractAssociatedIcon(path);
                    if (assoc != null) return ResizeToIcon(assoc.ToBitmap());
                }
            }
        }
        catch { }
        return null;
    }

    private static Bitmap? ExtractFromAppName(string appName)
    {
        try
        {
            var clean = appName.Trim();
            if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                clean = clean[..^4];

            var procs = Process.GetProcessesByName(clean);
            foreach (var p in procs)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    p.Dispose();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        using var assoc = Icon.ExtractAssociatedIcon(path);
                        if (assoc != null) return ResizeToIcon(assoc.ToBitmap());
                    }
                }
                catch { p.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    private static Bitmap ResizeToIcon(Bitmap src)
    {
        var dst = new Bitmap(IconSize, IconSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(dst);
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, IconSize, IconSize);
        return dst;
    }

    /// <summary>
    /// Loads the official Inbrisk logo (luminous green smoke orb).
    /// </summary>
    public static Bitmap GetInbriskLogo(int size = 24)
    {
        var key = $"__inbrisk_logo_{size}__";
        if (Cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? raw = null;
        try
        {
            var asm = typeof(AppIconExtractor).Assembly;
            using var stream = asm.GetManifestResourceStream("Inbrisk.Platform.Windows.Assets.inbrisk_logo.png");
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                using var temp = Image.FromStream(ms);
                raw = new Bitmap(temp);
            }
        }
        catch { }

        if (raw == null)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "inbrisk_logo.png");
            if (File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);
                    ms.Position = 0;
                    using var temp = Image.FromStream(ms);
                    raw = new Bitmap(temp);
                }
                catch { }
            }
        }

        if (raw != null)
        {
            var resized = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(raw, 0, 0, size, size);
            }
            raw.Dispose();
            Cache[key] = resized;
            return resized;
        }

        return GetFallbackProceduralIcon(size);
    }

    /// <summary>
    /// Gets the Inbrisk activity icon.
    /// </summary>
    public static Bitmap GetFallbackIcon() => GetInbriskLogo(IconSize);

    private static Bitmap GetFallbackProceduralIcon(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // Outer flame teardrop path
            using var path = new GraphicsPath();
            var cx = size / 2.0f;
            var cy = size / 2.0f;
            var r = (size - 4) / 2.0f;

            // Flame silhouette
            path.AddBezier(cx, 2.0f, cx + r, cy - 2.0f, cx + r, size - 2.0f, cx, size - 2.0f);
            path.AddBezier(cx, size - 2.0f, cx - r, size - 2.0f, cx - r, cy - 2.0f, cx, 2.0f);
            path.CloseFigure();

            using var brush = new LinearGradientBrush(
                new PointF(cx, 2.0f),
                new PointF(cx, size - 2.0f),
                Color.FromArgb(245, 0xFF, 0x6B, 0x4A),  // Top bright ember
                Color.FromArgb(250, 0xE5, 0x4B, 0x35)); // Bottom deep ember
            g.FillPath(brush, path);

            // Inner flame highlight
            using var innerPath = new GraphicsPath();
            var ir = r * 0.45f;
            var icy = cy + (r * 0.2f);
            innerPath.AddBezier(cx, cy - 2.0f, cx + ir, icy, cx + ir, size - 4.0f, cx, size - 4.0f);
            innerPath.AddBezier(cx, size - 4.0f, cx - ir, size - 4.0f, cx - ir, icy, cx, cy - 2.0f);
            innerPath.CloseFigure();

            using var innerBrush = new SolidBrush(Color.FromArgb(220, 0xFF, 0xE0, 0x82)); // Warm gold inner glow
            g.FillPath(innerBrush, innerPath);
        }
        return bmp;
    }
}
