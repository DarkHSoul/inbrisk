using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Ocr;

/// <summary>
/// Windows.Media.Ocr backend. Input: RawFrame (BGRA, image space).
/// Output: TextSpans mapped to desktop space via the frame's transform.
/// OcrEngine exposes no confidence score — Confidence is null (honest).
/// </summary>
public sealed class OcrService
{
    private readonly OcrEngine? _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool Available => _engine != null;
    public string Language => _engine?.RecognizerLanguage.LanguageTag ?? "none";

    public IReadOnlyList<TextSpan> Recognize(RawFrame frame, TimeSpan? timeout = null)
    {
        if (_engine == null) return Array.Empty<TextSpan>();
        var (bmp, effW, effH) = ToSoftwareBitmap(frame);
        if (bmp == null) return Array.Empty<TextSpan>();
        // map via the effective image size (post-downscale) covering the same rect
        var t = new FrameTransform(frame.Transform.SourceRect, effW, effH,
            frame.Transform.SourceHwnd);

        var task = _engine.RecognizeAsync(bmp).AsTask();
        if (!task.Wait(timeout ?? TimeSpan.FromSeconds(10))) return Array.Empty<TextSpan>();
        var result = task.Result;

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

    private static (SoftwareBitmap? bmp, int w, int h) ToSoftwareBitmap(RawFrame frame)
    {
        var w = frame.Width; var h = frame.Height;
        byte[] data = frame.Bgra;
        // engine caps input size — downscale nearest-neighbor if needed
        var maxDim = (int)OcrEngine.MaxImageDimension;
        if (w > maxDim || h > maxDim)
        {
            var scale = Math.Max((double)w / maxDim, (double)h / maxDim);
            var nw = (int)(w / scale); var nh = (int)(h / scale);
            var shrunk = new byte[nw * 4 * nh];
            for (var y = 0; y < nh; y++)
            {
                var sy = Math.Min((int)(y * scale), h - 1);
                for (var x = 0; x < nw; x++)
                {
                    var sx = Math.Min((int)(x * scale), w - 1);
                    Array.Copy(data, sy * frame.Stride + sx * 4, shrunk,
                        (y * nw + x) * 4, 4);
                }
            }
            data = shrunk; w = nw; h = nh;
        }
        var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h,
            BitmapAlphaMode.Premultiplied);
        bmp.CopyFromBuffer(data.AsBuffer());
        return (bmp, w, h);
    }
}
