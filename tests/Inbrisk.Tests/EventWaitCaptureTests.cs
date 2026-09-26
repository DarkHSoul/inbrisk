using Inbrisk.Core;
using Xunit;

namespace Inbrisk.Tests;

[Collection("desktop")]
public class EventWaitCaptureTests
{
    private readonly DesktopFixture _fx;
    public EventWaitCaptureTests(DesktopFixture fx) => _fx = fx;

    [Fact]
    public void Events_Captured_WhenDialogOpens()
    {
        _fx.CloseDialogs();
        var observed = new List<ObservedEvent>();
        void Handler(ObservedEvent e) { lock (observed) observed.Add(e); }
        _fx.Inbrisk.Event += Handler;
        try
        {
            var dlg = Assert.Single(_fx.Inbrisk.Find(
                new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DialogButton")));
            _fx.Inbrisk.Invoke(dlg.Id);

            var hit = SpinWait.SpinUntil(() =>
            {
                lock (observed)
                    return observed.Any(e => e.Kind is EventKind.WindowOpened
                        or EventKind.StructureChanged or EventKind.WindowShown);
            }, TimeSpan.FromSeconds(5));

            Assert.True(hit, "no semantic event observed when dialog opened");
            lock (observed)
                Assert.All(observed, e => Assert.True(e.At <= DateTimeOffset.Now));
        }
        finally
        {
            _fx.Inbrisk.Event -= Handler;
            _fx.CloseDialogs();
        }
    }

    [Fact]
    public void WaitForElement_ResolvesWhenAppears()
    {
        _fx.CloseDialogs();
        var dlg = Assert.Single(_fx.Inbrisk.Find(
            new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DialogButton")));
        _fx.Inbrisk.Invoke(dlg.Id);

        var r = _fx.Inbrisk.WaitForElement(
            new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog"), 8000);
        Assert.True(r.Success, r.Reason);

        var close = Assert.Single(_fx.Inbrisk.Find(
            new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog")));
        _fx.Inbrisk.Invoke(close.Id);
        _fx.CloseDialogs();
    }

    [Fact]
    public void WaitForStable_QuietScreen_Verifies()
    {
        var w = _fx.Inbrisk.Window(_fx.Hwnd)!;
        var r = _fx.Inbrisk.WaitForStable(w.Bounds, quietMs: 800, timeoutMs: 8000);
        Assert.True(r.Success, r.Reason);
    }

    [Fact]
    public void Capture_WindowShot_IsPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "inbrisk_test_shot.png");
        var bytes = _fx.Inbrisk.Shot(path, _fx.Hwnd);
        Assert.True(bytes.Length > 1000);
        Assert.Equal((byte)0x89, bytes[0]); // PNG magic
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Capture_DiffFraction_QuietIsNearZero()
    {
        var w = _fx.Inbrisk.Window(_fx.Hwnd)!;
        var d = _fx.Inbrisk.WaitForStable(w.Bounds, quietMs: 500, timeoutMs: 5000);
        Assert.True(d.Success);
    }

    [Fact]
    public void Guards_SecureDesktopNotActive_IntegrityReachable()
    {
        Assert.False(_fx.Inbrisk.SecureDesktopActive());
        Assert.Equal(IntegrityRelation.Reachable, _fx.Inbrisk.IntegrityOf(_fx.Hwnd));
    }

    [Fact]
    public void Clipboard_RoundTrip()
    {
        _fx.Inbrisk.ClipboardWrite("inbrisk-clip-42");
        Assert.Equal("inbrisk-clip-42", _fx.Inbrisk.ClipboardRead());
    }
}
