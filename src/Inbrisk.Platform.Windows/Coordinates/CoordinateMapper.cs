using Inbrisk.Core;

namespace Inbrisk.Platform.Windows.Coordinates;

/// <summary>
/// The single place allowed to convert between coordinate spaces:
///   image space (what a vision model sees)
///   ↔ desktop space (physical px, virtual screen, possibly negative origin)
///   ↔ SendInput normalized space (0..65535 over the virtual desktop).
/// </summary>
public static class CoordinateMapper
{
    /// <summary>Model/image coordinate → desktop physical px.</summary>
    public static (int X, int Y) ImageToDesktop(FrameTransform t, double ix, double iy)
        => t.ImageToDesktop(ix, iy);

    /// <summary>Normalized [0..1] image coordinate → desktop physical px.</summary>
    public static (int X, int Y) NormalizedToDesktop(FrameTransform t, double nx, double ny)
        => t.NormalizedToDesktop(nx, ny);

    /// <summary>Desktop px → normalized SendInput coordinate (0..65535) over the
    /// whole virtual desktop (requires MOUSEEVENTF_VIRTUALDESK).</summary>
    public static (int X, int Y) DesktopToSendInputAbsolute(int dx, int dy, RectPx virtualDesktop)
    {
        var x = (int)Math.Round((dx - virtualDesktop.X) * 65535.0 / (virtualDesktop.Width - 1));
        var y = (int)Math.Round((dy - virtualDesktop.Y) * 65535.0 / (virtualDesktop.Height - 1));
        return (Math.Clamp(x, 0, 65535), Math.Clamp(y, 0, 65535));
    }
}
