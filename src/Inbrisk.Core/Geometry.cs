namespace Inbrisk.Core;

/// <summary>
/// Physical-pixel rectangle in virtual-desktop space. Origin may be negative
/// (multi-monitor). This is the single canonical coordinate space of the runtime.
/// </summary>
public readonly record struct RectPx(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public (int X, int Y) Center => (X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public bool Intersects(RectPx o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public RectPx Intersect(RectPx o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var r = Math.Min(Right, o.Right);
        var b = Math.Min(Bottom, o.Bottom);
        return r <= x || b <= y ? new RectPx(0, 0, 0, 0) : new RectPx(x, y, r - x, b - y);
    }

    public RectPx Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}

/// <summary>
/// Stamped on every captured frame: which desktop-space rect the image covers
/// and the actual pixel size of the image that was produced (post downscale).
/// The only bridge between a model's image-space coordinates and desktop space.
/// </summary>
public sealed record FrameTransform(RectPx SourceRect, int ImageWidth,
    int ImageHeight, long? SourceHwnd = null)
{
    /// <summary>Image pixel coordinate → desktop physical pixel.</summary>
    public (int X, int Y) ImageToDesktop(double ix, double iy) => (
        SourceRect.X + (int)Math.Round(ix / ImageWidth * SourceRect.Width),
        SourceRect.Y + (int)Math.Round(iy / ImageHeight * SourceRect.Height));

    /// <summary>Normalized [0..1] image coordinate → desktop physical pixel.</summary>
    public (int X, int Y) NormalizedToDesktop(double nx, double ny) =>
        ImageToDesktop(nx * ImageWidth, ny * ImageHeight);

    /// <summary>Desktop physical pixel → image pixel coordinate.</summary>
    public (double X, double Y) DesktopToImage(int dx, int dy) => (
        (dx - SourceRect.X) * (double)ImageWidth / SourceRect.Width,
        (dy - SourceRect.Y) * (double)ImageHeight / SourceRect.Height);
}
