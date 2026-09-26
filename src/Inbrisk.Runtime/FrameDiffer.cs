using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Cheap frame differ: compares two RawFrames on a coarse cell grid
/// (mean luma per cell), marks changed cells, clusters them into bounding
/// rects. Intentionally simple — the goal is a fast local change signal,
/// not perfect CV.
/// </summary>
public sealed class FrameDiffer
{
    private readonly int _cellPx;        // cell size in image pixels
    private readonly double _lumaThreshold; // mean |Δluma| per cell to count as changed
    private readonly double _minFraction;   // changedFraction below this → "no change"

    public FrameDiffer(int cellPx = 24, double lumaThreshold = 14.0, double minFraction = 0.001)
    {
        _cellPx = cellPx;
        _lumaThreshold = lumaThreshold;
        _minFraction = minFraction;
    }

    public FrameDiff Compare(RawFrame? prev, RawFrame next)
    {
        if (prev == null || prev.Width != next.Width || prev.Height != next.Height ||
            prev.Bgra.Length != next.Bgra.Length)
        {
            // first frame or resize → everything counts as changed
            var all = new RectPx(0, 0, next.Width, next.Height);
            return new FrameDiff(-1, next.Sequence, next.At, 1.0,
                [all], next.Transform);
        }

        var cols = Math.Max(1, next.Width / _cellPx);
        var rows = Math.Max(1, next.Height / _cellPx);
        var changed = new bool[cols * rows];
        var changedCount = 0;

        for (var cy = 0; cy < rows; cy++)
        {
            var y0 = cy * _cellPx;
            var y1 = Math.Min(y0 + _cellPx, next.Height);
            for (var cx = 0; cx < cols; cx++)
            {
                var x0 = cx * _cellPx;
                var x1 = Math.Min(x0 + _cellPx, next.Width);
                // sample ~every 4th pixel inside the cell
                var step = Math.Max(1, _cellPx / 4);
                long sum = 0; var n = 0;
                for (var y = y0; y < y1; y += step)
                {
                    var baseA = y * prev.Stride;
                    var baseB = y * next.Stride;
                    for (var x = x0; x < x1; x += step)
                    {
                        var i = x * 4;
                        var la = prev.Bgra[baseA + i] + prev.Bgra[baseA + i + 1] + prev.Bgra[baseA + i + 2];
                        var lb = next.Bgra[baseB + i] + next.Bgra[baseB + i + 1] + next.Bgra[baseB + i + 2];
                        sum += Math.Abs(la - lb);
                        n++;
                    }
                }
                var meanDelta = n > 0 ? (double)sum / n / 3.0 : 0;
                if (meanDelta > _lumaThreshold)
                {
                    changed[cy * cols + cx] = true;
                    changedCount++;
                }
            }
        }

        var fraction = (double)changedCount / (cols * rows);
        var regions = changedCount == 0
            ? (IReadOnlyList<RectPx>)Array.Empty<RectPx>()
            : Cluster(changed, cols, rows, _cellPx, next.Width, next.Height);
        if (fraction < _minFraction) regions = Array.Empty<RectPx>();
        return new FrameDiff(prev.Sequence, next.Sequence, next.At,
            fraction < _minFraction ? 0.0 : fraction, regions, next.Transform);
    }

    /// <summary>Connected components on the changed-cell grid → bounding rects.</summary>
    private static List<RectPx> Cluster(bool[] cells, int cols, int rows, int cellPx, int w, int h)
    {
        var seen = new bool[cells.Length];
        var rects = new List<RectPx>();
        var stack = new Stack<int>();
        for (var i = 0; i < cells.Length; i++)
        {
            if (!cells[i] || seen[i]) continue;
            var minCx = int.MaxValue; var maxCx = 0; var minCy = int.MaxValue; var maxCy = 0;
            stack.Push(i);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (seen[c] || !cells[c]) continue;
                seen[c] = true;
                var cy = c / cols; var cx = c % cols;
                minCx = Math.Min(minCx, cx); maxCx = Math.Max(maxCx, cx);
                minCy = Math.Min(minCy, cy); maxCy = Math.Max(maxCy, cy);
                if (cx > 0) stack.Push(c - 1);
                if (cx < cols - 1) stack.Push(c + 1);
                if (cy > 0) stack.Push(c - cols);
                if (cy < rows - 1) stack.Push(c + cols);
            }
            rects.Add(new RectPx(minCx * cellPx, minCy * cellPx,
                Math.Min((maxCx + 1) * cellPx, w) - minCx * cellPx,
                Math.Min((maxCy + 1) * cellPx, h) - minCy * cellPx));
        }
        return rects;
    }
}
