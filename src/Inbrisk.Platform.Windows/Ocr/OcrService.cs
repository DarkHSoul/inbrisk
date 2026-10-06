using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Inbrisk.Core;
using OcrResult = Inbrisk.Core.OcrResult; // disambiguate vs Windows.Media.Ocr.OcrResult

namespace Inbrisk.Platform.Windows.Ocr;

/// <summary>
/// Windows.Media.Ocr backend. Input: RawFrame (BGRA, image space).
/// Output: TextSpans mapped to desktop space via the frame's transform.
/// OcrEngine exposes no confidence score — Confidence is null (honest).
/// Window/region capture paths need ICaptureService + IWindowService wired
/// in; without them RecognizeWindow/RecognizeRegion throw Unsupported.
/// </summary>
public sealed class OcrService : IOcrService
{
    private readonly OcrEngine? _engine;
    private readonly ICaptureService? _capture;
    private readonly IWindowService? _windows;
    /// <summary>Per-language recognizers, process-wide and created lazily —
    /// TryCreateFromLanguage pays a model-load cost and returns null for
    /// uninstalled packs; Language() throws on a malformed tag, so failures
    /// cache as null rather than faulting the call. OcrEngine is documented
    /// thread-safe for concurrent RecognizeAsync use.</summary>
    private static readonly ConcurrentDictionary<string, OcrEngine?> s_engines =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>User-profile-language engine — the default recognizer.
    /// Lazy so a no-pack machine caches the null instead of re-probing.</summary>
    private static readonly Lazy<OcrEngine?> s_profile = new(() =>
    {
        try { return OcrEngine.TryCreateFromUserProfileLanguages(); }
        catch { return null; }
    });

    public OcrService(ICaptureService? capture = null, IWindowService? windows = null)
    {
        // default = user profile languages; en-US is the last-resort fallback
        _engine = s_profile.Value ?? EngineFor("en-US", out _);
        _capture = capture;
        _windows = windows;
    }

    public bool Available => _engine != null;
    public string Language => _engine?.RecognizerLanguage.LanguageTag ?? "none";

    public OcrResult Recognize(RawFrame frame, string? language = null, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        OcrEngine? engine;
        string? warning = null;
        if (language == null) engine = _engine;
        else engine = EngineFor(language, out warning);
        var (bmp, effW, effH) = ToSoftwareBitmap(frame);
        IReadOnlyList<TextSpan> spans = Array.Empty<TextSpan>();
        if (engine == null)
            warning ??= "no OCR recognizer available";
        else if (bmp == null)
            warning ??= "empty frame";
        else
        {
            // map via the effective image size (post-downscale) covering the same rect
            var t = new FrameTransform(frame.Transform.SourceRect, effW, effH,
                frame.Transform.SourceHwnd);
            var wait = timeout ?? TimeSpan.FromSeconds(10);
            try
            {
                var task = engine.RecognizeAsync(bmp).AsTask();
                if (!task.Wait(wait))
                    warning ??= $"recognition timed out after {wait.TotalSeconds:0.#}s";
                else
                    spans = MapWords(task.Result, t);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                warning ??= $"recognition failed: {e.Message}";
            }
        }
        var result = new OcrResult(spans, sw.Elapsed.TotalMilliseconds,
            "windows.media.ocr", engine?.RecognizerLanguage.LanguageTag ?? "none",
            effW, effH, language, warning);
        PerfLog.Write(new
        {
            kind = "ocr.recognize",
            ms = Math.Round(result.ElapsedMs, 2),
            words = spans.Count,
            engine = result.Engine + ":" + result.Language,
            imageWxH = $"{effW}x{effH}",
            warn = warning,
        });
        return result;
    }

    public OcrResult RecognizeWindow(long hwnd, string? language = null, TimeSpan? timeout = null)
    {
        if (_capture == null || _windows == null)
            throw new InbriskException(ErrorCode.Unsupported,
                "OcrService was built without capture/window services");
        var w = _windows.GetWindow(hwnd)
            ?? throw new InbriskException(ErrorCode.NotFound,
                $"window 0x{hwnd:X} not found");
        if (w.State == WindowState.Minimized)
            throw new InbriskException(ErrorCode.CaptureFailed,
                "window is minimized");
        var region = w.Bounds.Intersect(_windows.GetVirtualDesktopBounds());
        if (region.IsEmpty)
            throw new InbriskException(ErrorCode.CaptureFailed,
                "window has no visible area");
        var raw = _capture.CaptureRaw(region);
        raw = raw with { Transform = raw.Transform with { SourceHwnd = hwnd } };
        return Recognize(raw, language, timeout);
    }

    public OcrResult RecognizeRegion(RectPx region, string? language = null, TimeSpan? timeout = null)
    {
        if (_capture == null)
            throw new InbriskException(ErrorCode.Unsupported,
                "OcrService was built without a capture service");
        return Recognize(_capture.CaptureRaw(region), language, timeout);
    }

    /// <summary>Engine for a BCP-47 tag. Unsupported/malformed tags fall back
    /// to the default (profile) engine and report through the out param —
    /// callers get a degraded result, not an empty one.</summary>
    private OcrEngine? EngineFor(string tag, out string? warning)
    {
        var e = s_engines.GetOrAdd(tag, t =>
        {
            try { return OcrEngine.TryCreateFromLanguage(new Language(t)); }
            catch { return null; } // malformed BCP-47 tag — treated as unavailable
        });
        if (e != null) { warning = null; return e; }
        warning = _engine != null
            ? $"OCR language '{tag}' unavailable; using '{Language}'"
            : $"OCR language '{tag}' unavailable";
        return _engine;
    }

    private static IReadOnlyList<TextSpan> MapWords(
        global::Windows.Media.Ocr.OcrResult result, FrameTransform t)
    {
        var spans = new List<TextSpan>();
        foreach (var line in result.Lines)
        foreach (var word in line.Words)
        {
            var r = word.BoundingRect;
            var (x, y) = t.ImageToDesktop(r.X, r.Y);
            var (rr, bb) = t.ImageToDesktop(r.X + r.Width, r.Y + r.Height);
            if (string.IsNullOrWhiteSpace(word.Text)) continue;
            spans.Add(new TextSpan(word.Text,
                new RectPx(x, y, Math.Max(1, rr - x), Math.Max(1, bb - y)),
                null, BackendId.Ocr));
        }
        return spans;
    }

    /// <summary>BGRA bytes → SoftwareBitmap. Fast path: tightly-packed frames
    /// copy straight through with CopyFromBuffer — no PNG encode/decode, no
    /// per-pixel work. Frames with row padding (Stride > w*4) or slack at the
    /// tail are compacted first — CopyFromBuffer would otherwise skew the
    /// image or throw. AlphaMode.Ignore: GDI captures can carry alpha=0 and
    /// Premultiplied would blank the image for the engine.</summary>
    private static (SoftwareBitmap? bmp, int w, int h) ToSoftwareBitmap(RawFrame frame)
    {
        var w = frame.Width; var h = frame.Height;
        if (w <= 0 || h <= 0 || frame.Bgra.Length < h * frame.Stride)
            return (null, w, h);
        byte[] data = frame.Bgra;
        var stride = frame.Stride;
        // engine caps input size — downscale nearest-neighbor if needed
        var maxDim = (int)OcrEngine.MaxImageDimension;
        if (w > maxDim || h > maxDim)
        {
            var scale = Math.Max((double)w / maxDim, (double)h / maxDim);
            var nw = Math.Max(1, (int)(w / scale)); var nh = Math.Max(1, (int)(h / scale));
            var shrunk = new byte[nw * 4 * nh];
            for (var y = 0; y < nh; y++)
            {
                var sy = Math.Min((int)(y * scale), h - 1);
                for (var x = 0; x < nw; x++)
                {
                    var sx = Math.Min((int)(x * scale), w - 1);
                    Array.Copy(data, sy * stride + sx * 4, shrunk,
                        (y * nw + x) * 4, 4);
                }
            }
            data = shrunk; w = nw; h = nh;
            stride = w * 4;
        }
        else if (stride != w * 4 || data.Length != w * 4 * h)
        {
            var packed = new byte[w * 4 * h];
            for (var y = 0; y < h; y++)
                Array.Copy(data, y * stride, packed, y * w * 4, w * 4);
            data = packed;
        }
        var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h,
            BitmapAlphaMode.Ignore);
        bmp.CopyFromBuffer(data.AsBuffer());
        return (bmp, w, h);
    }
}
