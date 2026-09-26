using Inbrisk.Core;
using Xunit;

namespace Inbrisk.Tests;

[Collection("desktop")]
public class TopologyTests
{
    private readonly DesktopFixture _fx;
    public TopologyTests(DesktopFixture fx) => _fx = fx;

    [Fact]
    public void Windows_ListContainsTestApp()
    {
        var w = _fx.Inbrisk.Windows().FirstOrDefault(w =>
            w.Title == "InbriskTestApp" && w.Pid == _fx.TestApp.Id);
        Assert.NotNull(w);
        Assert.Equal(_fx.TestApp.Id, w.Pid);
        Assert.True(w.Bounds.Width > 0);
        Assert.False(w.IsElevated);
        Assert.True(w.OnCurrentVirtualDesktop);
    }

    [Fact]
    public void Monitors_AtLeastOnePrimary_WithDpi()
    {
        var mons = _fx.Inbrisk.Monitors();
        Assert.NotEmpty(mons);
        Assert.Contains(mons, m => m.IsPrimary);
        Assert.All(mons, m => Assert.True(m.DpiX >= 96));
    }

    [Fact]
    public void VirtualDesktop_NonZeroBounds()
    {
        var vd = _fx.Inbrisk.VirtualDesktop();
        Assert.True(vd.Width > 0 && vd.Height > 0);
        // must cover the primary monitor
        var primary = _fx.Inbrisk.Monitors().First(m => m.IsPrimary);
        Assert.True(vd.Contains(primary.Bounds.X, primary.Bounds.Y));
    }

    [Fact]
    public void FocusWindow_BringsTestAppForeground()
    {
        var r = _fx.Inbrisk.Focus(_fx.Hwnd);
        Assert.True(r.Success, r.ErrorMessage);
        var fg = _fx.Inbrisk.ForegroundWindow();
        Assert.NotNull(fg);
        Assert.Equal(_fx.Hwnd, fg.Hwnd);
    }
}
