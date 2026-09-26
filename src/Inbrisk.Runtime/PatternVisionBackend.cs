using System.Diagnostics;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Deterministic vision backend for tests and pipeline verification: scans
/// BGRA pixels for a target color and reports contiguous blobs as elements in
/// image space. Real image analysis — no mocks — just not an ML model.
/// Default target: pure magenta (#FF00FF), the TestApp's marker color.
/// </summary>
public sealed class PatternVisionBackend : IVisionBackend, IGroundingBackend
{
    public string Name => "pattern-vision";

    private readonly byte _b, _g, _r;
    private readonly int _tolerance;
    private readonly int _minPixels;
    private readonly int _sampleStep;

    public PatternVisionBackend(byte r = 0xFF, byte g = 0x00, byte b = 0xFF,
        int tolerance = 24, int minPixels = 40, int sampleStep = 2)
    {
        _r = r; _g = g; _b = b;
        _tolerance = tolerance;
        _minPixels = minPixels;
        _sampleStep = sampleStep;
    }

    public VisionResult Analyze(RawFrame frame, VisionQuery query)
    {
        var sw = Stopwatch.StartNew();
        var blobs = FindBlobs(frame);
        var elements = blobs
            .Select((b, i) => new VisionElement(
                $"marker_{i}", $"color-blob #{i} ({b.Width}x{b.Height})", b, 1.0, "Custom"))
            .ToList();
        return new VisionResult(elements,
            elements.Count == 0 ? "no markers" : $"{elements.Count} marker(s)",
            sw.Elapsed, Name);
    }

    public VisionResult Locate(RawFrame frame, string targetDescription)
    {
        var sw = Stopwatch.StartNew();
        var blobs = FindBlobs(frame);
        var elements = blobs
            .Select((b, i) => new VisionElement(
                targetDescription, $"match for '{targetDescription}'", b, 1.0, "Custom"))
            .ToList();
        return new VisionResult(elements, null, sw.Elapsed, Name);
    }

    /// <summary>Bounding boxes of contiguous matching-pixel regions (grid flood).</summary>
    internal List<RectPx> FindBlobs(RawFrame f)
    {
        var cols = Math.Max(1, f.Width / _sampleStep);
        var rows = Math.Max(1, f.Height / _sampleStep);
        var hit = new bool[cols * rows];

        for (var gy = 0; gy < rows; gy++)
        {
            var y = Math.Min(gy * _sampleStep, f.Height - 1);
            var baseIdx = y * f.Stride;
            for (var gx = 0; gx < cols; gx++)
            {
                var x = Math.Min(gx * _sampleStep, f.Width - 1);
                var i = baseIdx + x * 4;
                if (Math.Abs(f.Bgra[i] - _b) <= _tolerance &&
                    Math.Abs(f.Bgra[i + 1] - _g) <= _tolerance &&
                    Math.Abs(f.Bgra[i + 2] - _r) <= _tolerance)
                    hit[gy * cols + gx] = true;
            }
        }

        var seen = new bool[hit.Length];
        var rects = new List<RectPx>();
        var stack = new Stack<int>();
        for (var c = 0; c < hit.Length; c++)
        {
            if (!hit[c] || seen[c]) continue;
            var minX = int.MaxValue; var maxX = 0; var minY = int.MaxValue; var maxY = 0;
            var count = 0;
            stack.Push(c);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (seen[cur] || !hit[cur]) continue;
                seen[cur] = true;
                count++;
                var cy = cur / cols; var cx = cur % cols;
                minX = Math.Min(minX, cx); maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy); maxY = Math.Max(maxY, cy);
                if (cx > 0) stack.Push(cur - 1);
                if (cx < cols - 1) stack.Push(cur + 1);
                if (cy > 0) stack.Push(cur - cols);
                if (cy < rows - 1) stack.Push(cur + cols);
            }
            if (count < _minPixels / (_sampleStep * _sampleStep)) continue;
            rects.Add(new RectPx(minX * _sampleStep, minY * _sampleStep,
                Math.Min((maxX + 1) * _sampleStep, f.Width) - minX * _sampleStep,
                Math.Min((maxY + 1) * _sampleStep, f.Height) - minY * _sampleStep));
        }
        return rects;
    }
}
