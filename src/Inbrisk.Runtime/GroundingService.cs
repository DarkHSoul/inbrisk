using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// The ONLY image-space → desktop-space bridge for vision results.
/// Anything a vision/grounding backend returns is mapped through the frame's
/// FrameTransform here — never re-implemented at call sites.
/// </summary>
public static class GroundingService
{
    public static int _counter;

    /// <summary>Vision element (image space) → UiElement in desktop space.</summary>
    public static UiElement ToDesktop(VisionElement ve, FrameTransform t)
    {
        var (x, y) = t.ImageToDesktop(ve.ImageBounds.X, ve.ImageBounds.Y);
        var (r, b) = t.ImageToDesktop(ve.ImageBounds.Right, ve.ImageBounds.Bottom);
        var rect = new RectPx(x, y, Math.Max(1, r - x), Math.Max(1, b - y));
        var id = $"vis_{Interlocked.Increment(ref _counter)}";
        var role = ve.RoleHint != null && Enum.TryParse<Role>(ve.RoleHint, true, out var rr)
            ? rr : Role.Custom;
        return new UiElement(id, BackendId.Vision, role,
            ve.Label ?? ve.Description, rect, ["click"],
            new Dictionary<string, object?>
            {
                ["source"] = "vision",
                ["confidence"] = ve.Confidence,
                ["description"] = ve.Description,
            },
            new ElementHandle(BackendId.Vision, id,
                new ReResolveRecipe(null, t.SourceHwnd, null, role,
                    ve.Label, null, [], rect)),
            null, t.SourceHwnd, ve.Confidence);
    }

    /// <summary>Image-space point → canonical desktop point.</summary>
    public static (int X, int Y) ToDesktopPoint(double ix, double iy, FrameTransform t) =>
        t.ImageToDesktop(ix, iy);

    /// <summary>Desktop-space rect → element-crop of the source frame (new
    /// correct FrameTransform attached).</summary>
    public static RawFrame CropToElement(RawFrame frame, UiElement desktopElement) =>
        frame.CropDesktop(desktopElement.Bounds);
}
