using System.IO.Compression;

namespace Inbrisk.Core;

/// <summary>
/// A captured frame in raw BGRA pixels. Continuous capture pipelines operate on
/// RawFrame (no PNG encode per frame); PNG is derived on demand via ToPng().
/// </summary>
public sealed record RawFrame(
    byte[] Bgra,
    int Width,
    int Height,
    int Stride,
    FrameTransform Transform,
    DateTimeOffset At,
    long Sequence = 0)
{
    /// <summary>Crop to an image-space rect. Result carries a corrected transform.</summary>
    public RawFrame Crop(RectPx imageRect)
    {
        var w = Math.Clamp(imageRect.Width, 0, Width - Math.Max(0, imageRect.X));
        var h = Math.Clamp(imageRect.Height, 0, Height - Math.Max(0, imageRect.Y));
        var x = Math.Clamp(imageRect.X, 0, Width);
        var y = Math.Clamp(imageRect.Y, 0, Height);
        if (w <= 0 || h <= 0) throw new InbriskException(ErrorCode.CaptureFailed, "crop rect empty");
        var outBytes = new byte[w * 4 * h];
        for (var row = 0; row < h; row++)
            Array.Copy(Bgra, (y + row) * Stride + x * 4, outBytes, row * w * 4, w * 4);
        var (dx, dy) = Transform.ImageToDesktop(x, y);
        var (dr, db) = Transform.ImageToDesktop(x + w, y + h);
        return new RawFrame(outBytes, w, h, w * 4,
            new FrameTransform(new RectPx(dx, dy, Math.Max(1, dr - dx),
                Math.Max(1, db - dy)), w, h, Transform.SourceHwnd),
            At, Sequence);
    }

    /// <summary>Crop to a desktop-space rect (clamped to frame coverage).</summary>
    public RawFrame CropDesktop(RectPx desktopRect)
    {
        var (ix, iy) = Transform.DesktopToImage(desktopRect.X, desktopRect.Y);
        var (ir, ib) = Transform.DesktopToImage(desktopRect.Right, desktopRect.Bottom);
        return Crop(new RectPx((int)ix, (int)iy, (int)(ir - ix), (int)(ib - iy)));
    }

    public byte[] ToPng() => Png.EncodeBgra(Bgra, Width, Height, Stride);

    public Frame ToFrame() => new(ToPng(), Transform, At);

    /// <summary>Bilinear downscale so the image width never exceeds
    /// <paramref name="maxWidth"/>. The transform keeps pointing at the same
    /// desktop rect — image-space coordinates map back through the updated
    /// FrameTransform, so clicks stay correct.</summary>
    public RawFrame ScaledToMaxWidth(int maxWidth)
    {
        if (maxWidth <= 0 || Width <= maxWidth) return this;
        var w = maxWidth;
        var scale = (double)Width / w;
        var h = Math.Max(1, (int)Math.Round(Height / scale));
        // per-column source indices + weights are shared across rows
        var x0 = new int[w]; var x1 = new int[w];
        var wx0 = new double[w]; var wx1 = new double[w];
        for (var x = 0; x < w; x++)
        {
            var sx = (x + 0.5) * scale - 0.5;
            var xi = (int)Math.Floor(sx);
            wx1[x] = Math.Clamp(sx - xi, 0, 1);
            wx0[x] = 1 - wx1[x];
            x0[x] = Math.Clamp(xi, 0, Width - 1);
            x1[x] = Math.Clamp(xi + 1, 0, Width - 1);
        }
        var dst = new byte[w * 4 * h];
        for (var y = 0; y < h; y++)
        {
            var sy = (y + 0.5) * scale - 0.5;
            var yi = (int)Math.Floor(sy);
            var fy = Math.Clamp(sy - yi, 0, 1);
            var fy0 = 1 - fy;
            var ra = Math.Clamp(yi, 0, Height - 1) * Stride;
            var rb = Math.Clamp(yi + 1, 0, Height - 1) * Stride;
            var drow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var a0 = ra + x0[x] * 4; var a1 = ra + x1[x] * 4;
                var b0 = rb + x0[x] * 4; var b1 = rb + x1[x] * 4;
                var d = drow + x * 4;
                var wa = wx0[x] * fy0; var wb = wx1[x] * fy0;
                var wc = wx0[x] * fy;  var wd = wx1[x] * fy;
                for (var c = 0; c < 3; c++)
                    dst[d + c] = (byte)Math.Clamp(Math.Round(
                        Bgra[a0 + c] * wa + Bgra[a1 + c] * wb +
                        Bgra[b0 + c] * wc + Bgra[b1 + c] * wd), 0, 255);
                dst[d + 3] = 0xFF;
            }
        }
        return new RawFrame(dst, w, h, w * 4,
            new FrameTransform(Transform.SourceRect, w, h, Transform.SourceHwnd),
            At, Sequence);
    }

    /// <summary>Coarse bounding box (image space) of the blocks whose pixels
    /// differ from a previous frame of identical geometry. Null = pixel-
    /// identical. A geometry mismatch reports the whole image — callers must
    /// not assume pixels outside the returned box are equal.</summary>
    public RectPx? ChangedBounds(RawFrame previous, int blockSize = 24)
    {
        if (previous.Width != Width || previous.Height != Height
            || previous.Stride != Stride)
            return new RectPx(0, 0, Width, Height);
        if (Bgra.AsSpan().SequenceEqual(previous.Bgra.AsSpan()))
            return null;
        var minX = Width; var minY = Height; var maxX = -1; var maxY = -1;
        for (var by = 0; by < Height; by += blockSize)
        {
            var bh = Math.Min(blockSize, Height - by);
            for (var bx = 0; bx < Width; bx += blockSize)
            {
                var bw = Math.Min(blockSize, Width - bx);
                if (BlockDiffers(previous, bx, by, bw, bh))
                {
                    if (bx < minX) minX = bx;
                    if (by < minY) minY = by;
                    if (bx + bw > maxX) maxX = bx + bw;
                    if (by + bh > maxY) maxY = by + bh;
                }
            }
        }
        return maxX < 0 ? null : new RectPx(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Sampled block comparison — every other pixel, per-channel
    /// tolerance 12 (matches the capture-side diff threshold). A block counts
    /// as changed when at least ~1% of its samples differ.</summary>
    private bool BlockDiffers(RawFrame prev, int bx, int by, int bw, int bh)
    {
        var diff = 0; var samples = 0;
        for (var y = by; y < by + bh; y += 2)
        {
            var row = y * Stride;
            var prow = y * prev.Stride;
            for (var x = bx; x < bx + bw; x += 2)
            {
                samples++;
                var i = row + x * 4; var j = prow + x * 4;
                if (Math.Abs(Bgra[i] - prev.Bgra[j]) > 12 ||
                    Math.Abs(Bgra[i + 1] - prev.Bgra[j + 1]) > 12 ||
                    Math.Abs(Bgra[i + 2] - prev.Bgra[j + 2]) > 12)
                    diff++;
            }
        }
        return diff > 0 && diff * 100 >= samples;
    }

    /// <summary>Burns numbered tags into the pixel buffer at the centers of
    /// the given desktop-space bounds (set-of-mark overlay). Image-space
    /// mapping goes through the frame transform, so tags land correctly on
    /// downscaled or cropped frames. Indices render modulo 100 — callers
    /// should cap mark counts accordingly.</summary>
    public void DrawMarks(IReadOnlyList<(int Index, RectPx DesktopBounds)> marks)
    {
        foreach (var (index, bounds) in marks)
        {
            if (index <= 0) continue;
            var (cx, cy) = bounds.Center;
            var (ix, iy) = Transform.DesktopToImage(cx, cy);
            DrawTag((int)Math.Round(ix), (int)Math.Round(iy), index % 100);
        }
    }

    private void DrawTag(int cx, int cy, int n)
    {
        var digits = n >= 10 ? 2 : 1;
        var glyphW = digits * 8 - 2;            // 6px digits + 2px gap
        var tw = glyphW + 8;                     // pad + 1px outline each side
        const int th = 16;
        var x0 = Math.Clamp(cx - tw / 2, 0, Math.Max(0, Width - tw));
        var y0 = Math.Clamp(cy - th / 2, 0, Math.Max(0, Height - th));
        FillRect(x0, y0, tw, th, 0x10, 0x10, 0x10);
        FillRect(x0 + 1, y0 + 1, tw - 2, th - 2, 0xE8, 0x60, 0x10);
        var dx = x0 + (tw - glyphW) / 2;
        if (digits == 2) { DrawDigit(dx, y0 + 3, n / 10); dx += 8; }
        DrawDigit(dx, y0 + 3, n % 10);
    }

    private void DrawDigit(int x, int y, int d)
    {
        var glyph = DigitGlyphs[d];
        for (var r = 0; r < 5; r++)
        for (var c = 0; c < 3; c++)
            if (((glyph >> (14 - (r * 3 + c))) & 1) != 0)
                FillRect(x + c * 2, y + r * 2, 2, 2, 0xFF, 0xFF, 0xFF);
    }

    private void FillRect(int x, int y, int w, int h, byte r, byte g, byte b)
    {
        var x1 = Math.Min(x + w, Width);
        var y1 = Math.Min(y + h, Height);
        for (var yy = Math.Max(0, y); yy < y1; yy++)
        {
            var row = yy * Stride;
            for (var xx = Math.Max(0, x); xx < x1; xx++)
            {
                var i = row + xx * 4;
                Bgra[i] = b; Bgra[i + 1] = g; Bgra[i + 2] = r; Bgra[i + 3] = 0xFF;
            }
        }
    }

    /// <summary>3×5 digit glyphs, row-major, bit 14 = top-left pixel.</summary>
    private static readonly ushort[] DigitGlyphs =
    [
        0b111_101_101_101_111, // 0
        0b010_110_010_010_111, // 1
        0b111_001_111_100_111, // 2
        0b111_001_111_001_111, // 3
        0b101_101_111_001_001, // 4
        0b111_100_111_001_111, // 5
        0b111_100_111_101_111, // 6
        0b111_001_001_001_001, // 7
        0b111_101_111_101_111, // 8
        0b111_101_111_001_111, // 9
    ];
}

/// <summary>Minimal PNG encoder: BGRA → PNG8 RGB. Keeps Core dependency-free.</summary>
public static class Png
{
    public static byte[] EncodeBgra(byte[] bgra, int width, int height, int stride)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit truecolor RGB
        WriteChunk(ms, "IHDR", ihdr);

        var rawLen = (width * 3 + 1) * height;
        var raw = new byte[rawLen];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (width * 3 + 1);
            raw[dst] = 0; // filter: none
            var src = y * stride;
            for (var x = 0; x < width; x++)
            {
                var s = src + x * 4;
                var d = dst + 1 + x * 3;
                raw[d] = bgra[s + 2]; raw[d + 1] = bgra[s + 1]; raw[d + 2] = bgra[s];
            }
        }
        using var zlib = new MemoryStream();
        using (var z = new ZLibStream(zlib, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        WriteChunk(ms, "IDAT", zlib.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, data.Length); s.Write(len);
        var name = System.Text.Encoding.ASCII.GetBytes(type); s.Write(name);
        s.Write(data);
        var crcBuf = new byte[name.Length + data.Length];
        Array.Copy(name, crcBuf, name.Length);
        Array.Copy(data, 0, crcBuf, name.Length, data.Length);
        var crc = new byte[4]; WriteBE(crc, 0, (int)Crc32(crcBuf)); s.Write(crc);
    }

    private static void WriteBE(byte[] b, int o, int v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}

public enum CaptureBackendKind { WindowsGraphicsCapture, Gdi }

/// <summary>What a capture session targets. Region/FullDesktop resolve to the
/// covering monitor under WGC (then crop), GDI captures the rect directly.</summary>
public abstract record CaptureTarget
{
    public sealed record Window(long Hwnd) : CaptureTarget;
    public sealed record Monitor(int Index) : CaptureTarget;
    public sealed record Region(RectPx Rect) : CaptureTarget;
    public sealed record FullDesktop : CaptureTarget;
}

public sealed record CaptureMetrics(
    CaptureBackendKind Backend,
    double CaptureFps,
    double SampledFps,
    long FramesReceived,
    long FramesDropped,
    int FrameWidth,
    int FrameHeight,
    DateTimeOffset StartedAt);

/// <summary>A live capture stream. Frames arrive as RawFrame with a stamped
/// FrameTransform; consumers attach FrameReceived or poll LatestFrame.</summary>
public interface ICaptureSession : IDisposable
{
    CaptureTarget Target { get; }
    CaptureBackendKind Backend { get; }
    bool IsRunning { get; }
    RawFrame? LatestFrame { get; }
    FrameTransform? Transform => LatestFrame?.Transform;
    event Action<RawFrame>? FrameReceived;
    void Start();
    void Stop();
    CaptureMetrics Metrics { get; }
}

/// <summary>Backends that can ramp their sampling rate up/down.</summary>
public interface IAdaptiveSource { void SetBusy(bool busy); }
