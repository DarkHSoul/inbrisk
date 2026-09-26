using Inbrisk.Core;
using Inbrisk.Platform.Windows.Coordinates;
using Xunit;

namespace Inbrisk.Tests;

/// <summary>Pure coordinate-math tests — no screen interaction needed.</summary>
public class MapperTests
{
    [Fact]
    public void ImageToDesktop_IdentityTransform()
    {
        var t = new FrameTransform(new RectPx(0, 0, 1920, 1080), 1920, 1080);
        Assert.Equal((960, 540), t.ImageToDesktop(960, 540));
    }

    [Fact]
    public void ImageToDesktop_Downscaled()
    {
        // 2560x1440 monitor, model image downscaled to 1280x720
        var t = new FrameTransform(new RectPx(0, 0, 2560, 1440), 1280, 720);
        Assert.Equal((1280, 720), t.ImageToDesktop(640, 360)); // center stays center
        Assert.Equal((2560, 1440), t.ImageToDesktop(1280, 720));
    }

    [Fact]
    public void ImageToDesktop_CroppedWindow()
    {
        // captured only a window region; model coords must land inside it
        var t = new FrameTransform(new RectPx(500, 300, 800, 600), 800, 600);
        Assert.Equal((500, 300), t.ImageToDesktop(0, 0));
        Assert.Equal((900, 600), t.ImageToDesktop(400, 300));
    }

    [Fact]
    public void NormalizedToDesktop_NegativeMonitorOrigin()
    {
        // monitor left of primary → negative X origin
        var t = new FrameTransform(new RectPx(-1920, 0, 1920, 1080), 1920, 1080);
        Assert.Equal((-1920, 0), t.NormalizedToDesktop(0, 0));
        Assert.Equal((-960, 540), t.NormalizedToDesktop(0.5, 0.5));
    }

    [Fact]
    public void DesktopToSendInputAbsolute_NormalizesOverVirtualDesktop()
    {
        var vd = new RectPx(-1920, 0, 3840, 1080); // two 1920 monitors
        Assert.Equal((0, 0), CoordinateMapper.DesktopToSendInputAbsolute(-1920, 0, vd));
        var (cx, cy) = CoordinateMapper.DesktopToSendInputAbsolute(0, 540, vd);
        Assert.InRange(cx, 32760, 32810);
        Assert.InRange(cy, 32760, 32810);
        Assert.Equal((65535, 65535), CoordinateMapper.DesktopToSendInputAbsolute(1919, 1079, vd));
    }

    [Fact]
    public void RoundTrip_DesktopToImageAndBack()
    {
        var t = new FrameTransform(new RectPx(200, 100, 1600, 900), 800, 450);
        var (ix, iy) = t.DesktopToImage(1000, 550);
        var (dx, dy) = t.ImageToDesktop(ix, iy);
        Assert.True(Math.Abs(dx - 1000) <= 1 && Math.Abs(dy - 550) <= 1);
    }
}
