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
