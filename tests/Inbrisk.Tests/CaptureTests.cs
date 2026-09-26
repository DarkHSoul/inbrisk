using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

[Collection("desktop")]
public class CaptureTests
{
    private readonly DesktopFixture _fx;
    public CaptureTests(DesktopFixture fx)
    {
        _fx = fx;
        _fx.Inbrisk.Focus(_fx.Hwnd);
        _fx.EnsureAnimationOff();
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(
        long hWnd, long insAfter, int x, int y, int cx, int cy, uint flags);

    private static string State() => DesktopFixture.ReadState() ?? "";

    [Fact]
    public void ContinuousCapture_WindowSession_FramesArrive()
    {
        using var s = _fx.Inbrisk.StartCapture(new CaptureTarget.Window(_fx.Hwnd));
        Assert.Equal(CaptureBackendKind.WindowsGraphicsCapture, s.Backend);
        Assert.True(SpinWait.SpinUntil(() => s.LatestFrame != null, 8000),
            "no frame arrived in 8s");
        var f = s.LatestFrame!;
        Assert.True(f.Width > 100 && f.Height > 100);
        // transform maps image center into the window bounds
        var (cx, cy) = f.Transform.ImageToDesktop(f.Width / 2.0, f.Height / 2.0);
        var bounds = _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds;
        Assert.True(bounds.Inflate(24).Contains(cx, cy));
        var m = s.Metrics;
        Assert.True(m.FramesReceived >= 1);
        Assert.True(m.FrameWidth == f.Width);
    }

    [Fact]
    public void Session_StartStop_Lifecycle()
    {
        var s = _fx.Inbrisk.StartCapture(new CaptureTarget.Window(_fx.Hwnd));
        Assert.True(s.IsRunning);
        SpinWait.SpinUntil(() => s.LatestFrame != null, 8000);
        s.Stop();
        Assert.False(s.IsRunning);
        s.Dispose();
        Assert.False(s.IsRunning);
    }

    [Fact]
    public void ChangeDetection_RegionsWhenAnimating()
    {
        DesktopFixture.WriteState("idle");
        using var mon = _fx.Inbrisk.Monitor(new CaptureTarget.Window(_fx.Hwnd), quietMs: 600);
        SpinWait.SpinUntil(() => mon.Session.LatestFrame != null, 8000);
        Thread.Sleep(500); // let it go quiet first

        var diffs = new List<FrameDiff>();
        mon.Diff += d => { lock (diffs) diffs.Add(d); };

        var anim = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "AnimButton"));
        _fx.Inbrisk.Invoke(anim[0].Id); // start animation
        SpinWait.SpinUntil(() => { lock (diffs) return diffs.Any(d => d.ChangedFraction > 0.001); }, 8000);

        List<FrameDiff> snap;
        lock (diffs) snap = diffs.Where(d => d.ChangedFraction > 0.001).ToList();
        _fx.Inbrisk.Invoke(anim[0].Id); // stop

        Assert.NotEmpty(snap);
        Assert.True(snap.Any(d => d.ImageRegions.Count > 0), "no changed regions");
        // moving ball lane is a strip — some diff should be a bounded region, not full-frame
        var fw = mon.Session.LatestFrame?.Width ?? 0;
        var fh = mon.Session.LatestFrame?.Height ?? 0;
        Assert.True(fw > 0);
        Assert.True(snap.Any(d => d.ImageRegions.Any(r => r.Width < fw / 2 && r.Height < fh / 2)),
            "every diff was full-frame — clustering produced nothing");
    }

    [Fact]
    public void NoChange_LowFalsePositive()
    {
        using var mon = _fx.Inbrisk.Monitor(new CaptureTarget.Window(_fx.Hwnd), quietMs: 600);
        SpinWait.SpinUntil(() => mon.Session.LatestFrame != null, 8000);
        var diffs = new List<FrameDiff>();
        mon.Diff += d => { lock (diffs) diffs.Add(d); };
        Thread.Sleep(2500); // static window
        lock (diffs)
        {
            var noisy = diffs.Where(d => !d.IsFirst && d.ChangedFraction > 0.01).ToList();
            Assert.True(noisy.Count <= diffs.Count / 10 + 1,
                $"{noisy.Count}/{diffs.Count} frames falsely reported change");
        }
    }

    [Fact]
    public async Task WaitForChange_Then_Stable()
    {
        _fx.EnsureAnimationOff();
        DesktopFixture.WriteState("idle");
        using var mon = _fx.Inbrisk.Monitor(new CaptureTarget.Window(_fx.Hwnd), quietMs: 700);
        SpinWait.SpinUntil(() => mon.Session.LatestFrame != null, 8000);
        Thread.Sleep(900); // settle → tracker should reach Stable

        var anim = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "AnimButton"));
        var changeWait = Task.Run(() => _fx.Inbrisk.WaitForChange(mon, 8000));
        Thread.Sleep(300);
        _fx.Inbrisk.Invoke(anim[0].Id);
        Assert.True((await changeWait).Success);

        var stableWait = Task.Run(() => _fx.Inbrisk.WaitForStable(mon, 15000));
        Thread.Sleep(500);
        _fx.Inbrisk.Invoke(anim[0].Id); // stop animation
        Assert.True((await stableWait).Success);
    }

    [Fact]
    public void Crop_FrameTransform_RoundTrip()
    {
        var frame = _fx.Inbrisk.CaptureRaw(new CaptureTarget.Window(_fx.Hwnd));
        var win = _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds;
        var inner = new RectPx(win.X + 60, win.Y + 60, 120, 80);
        var crop = frame.CropDesktop(inner);
        Assert.Equal(120, crop.Width);
        Assert.Equal(80, crop.Height);
        // crop center maps back to inner's desktop center
        var (cx, cy) = crop.Transform.ImageToDesktop(60, 40);
        Assert.True(Math.Abs(cx - (inner.X + 60)) <= 2 && Math.Abs(cy - (inner.Y + 40)) <= 2);
        // and the crop's PNG encodes
        Assert.True(crop.ToPng().Length > 100);
    }

    [Fact]
    public void Ocr_FindsRenderedText()
    {
        Assert.True(_fx.Inbrisk.OcrAvailable, "no OCR language pack available");
        var frame = _fx.Inbrisk.CaptureRaw(new CaptureTarget.Window(_fx.Hwnd));
        var spans = _fx.Inbrisk.Ocr(frame);
        var texts = spans.Select(s => s.Text).ToList();
        Assert.Contains(spans, s => s.Text.Contains("PIXEL", StringComparison.OrdinalIgnoreCase)
            || s.Text.Contains("TARGET", StringComparison.OrdinalIgnoreCase));
        var pixelSpan = spans.First(s => s.Text.Contains("PIXEL") || s.Text.Contains("TARGET"));
        var win = _fx.Inbrisk.Window(_fx.Hwnd)!.Bounds;
        Assert.True(win.Inflate(40).Contains(pixelSpan.Bounds.X, pixelSpan.Bounds.Y),
            $"OCR bounds {pixelSpan.Bounds} outside window {win}");
        Assert.All(spans, s => Assert.Equal(BackendId.Ocr, s.Source));
    }

    [Fact]
    public void SceneMerge_UiaPreferred_OcrDedup()
    {
        var scene = _fx.Inbrisk.ObserveScene(_fx.Hwnd, includeOcr: true);
        // UIA Save button present once
        var saveBtn = scene.Where(e => e.Name == "Save" && e.Role == Role.Button).ToList();
        Assert.Single(saveBtn);
        Assert.Equal(BackendId.Uia, saveBtn[0].Source);
        // OCR-visible pixel text merged in
        Assert.Contains(scene, e => e.Source == BackendId.Ocr &&
            (e.Name?.Contains("PIXEL", StringComparison.OrdinalIgnoreCase) == true ||
             e.Name?.Contains("TARGET", StringComparison.OrdinalIgnoreCase) == true));
        // no OCR duplicate of a UIA-exposed string
        var dupOcr = scene.Where(e => e.Source == BackendId.Ocr &&
            scene.Any(u => u.Source == BackendId.Uia && u.Role == Role.Button &&
                u.Name == e.Name)).ToList();
        Assert.Empty(dupOcr);
    }

    [Fact]
    public void PixelOnly_VisionFindsMarker_AndClickHits()
    {
        DesktopFixture.WriteState("idle");
        _fx.EnsureForeground();
        var vision = new PatternVisionBackend();
        _fx.Inbrisk.SetVisionBackend(vision);
        var frame = _fx.Inbrisk.CaptureRaw(new CaptureTarget.Window(_fx.Hwnd));
        var result = vision.Locate(frame, "magenta target");
        Assert.NotEmpty(result.Elements);
        var el = result.Elements.OrderByDescending(e => e.ImageBounds.Width * e.ImageBounds.Height).First();

        // image-space → desktop via transform, then SendInput click → app state
        var r = _fx.Inbrisk.ClickVision(el, frame.Transform);
        Assert.True(r.Success, r.ErrorMessage);
        Assert.Equal("SendInput.click", r.Method);
        SpinWait.SpinUntil(() => State().StartsWith("canvas:hit"), 4000);
        var st = State();
        var p = GroundingService.ToDesktopPoint(
            el.ImageBounds.X + el.ImageBounds.Width / 2,
            el.ImageBounds.Y + el.ImageBounds.Height / 2, frame.Transform);
        Assert.True(st.StartsWith("canvas:hit"),
            $"state={st} topAtClick={_fx.TopWindowAt(p.X, p.Y)}");
    }

    [Fact]
    public void EventCoalescing_ReducesStorm()
    {
        var fake = new FakeSource();
        var coal = new CoalescingEventSource(fake, windowMs: 100);
        var received = new List<ObservedEvent>();
        coal.Event += received.Add;
        coal.Start();

        // storm: 200 structure-changes on same hwnd + a few immediate kinds
        for (var n = 0; n < 200; n++)
            fake.Emit(new ObservedEvent(EventKind.StructureChanged,
                DateTimeOffset.Now, Hwnd: 0x1234));
        for (var n = 0; n < 3; n++)
            fake.Emit(new ObservedEvent(EventKind.ForegroundChanged,
                DateTimeOffset.Now, Hwnd: 0x1234 + n));
        Thread.Sleep(400);

        Assert.Equal(203, coal.RawCount);
        // all 3 foreground events arrive immediately; structure storm collapses to ~1
        Assert.True(coal.EmittedCount <= 10,
            $"coalescing didn't reduce: emitted={coal.EmittedCount}");
        Assert.True(coal.ReductionRatio > 0.9);
        Assert.Equal(3, received.Count(e => e.Kind == EventKind.ForegroundChanged));
        Assert.Equal(1, received.Count(e => e.Kind == EventKind.StructureChanged));
        coal.Dispose();
    }

    [Fact]
    public void Capture_WindowMove_TransformTracks()
    {
        using var s = _fx.Inbrisk.StartCapture(new CaptureTarget.Window(_fx.Hwnd));
        SpinWait.SpinUntil(() => s.LatestFrame != null, 8000);
        var before = s.LatestFrame!.Transform.SourceRect;

        // animation keeps frames flowing so a post-move frame arrives promptly
        var anim = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "AnimButton"));
        _fx.Inbrisk.Invoke(anim[0].Id);
        Thread.Sleep(400);

        var target = before with { X = before.X + 40, Y = before.Y + 30 };
        SetWindowPos(_fx.Hwnd, 0, target.X, target.Y, 0, 0, 0x0001 /*SWP_NOSIZE*/ | 0x0040);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var f = s.LatestFrame;
            return f != null && Math.Abs(f.Transform.SourceRect.X - target.X) <= 16
                && Math.Abs(f.Transform.SourceRect.Y - target.Y) <= 16;
        }, 6000), "transform didn't track window move");
        _fx.Inbrisk.Invoke(anim[0].Id); // stop animation
        Thread.Sleep(300);
    }

    [Fact]
    public void Capture_SessionEnds_WhenWindowCloses()
    {
        _fx.CloseDialogs();
        var dlg = _fx.Inbrisk.Find(new FindSpec(Hwnd: _fx.Hwnd, AutomationId: "DialogButton"));
        _fx.Inbrisk.Invoke(dlg[0].Id);
        var appeared = SpinWait.SpinUntil(() =>
            _fx.Inbrisk.Windows().Any(w => w.Title == "TestDialog"), 8000);
        Assert.True(appeared, "TestDialog never appeared");
        var dlgWin = _fx.Inbrisk.Windows().First(w => w.Title == "TestDialog");

        var s = _fx.Inbrisk.StartCapture(new CaptureTarget.Window(dlgWin.Hwnd));
        SpinWait.SpinUntil(() => s.LatestFrame != null, 8000);

        var close = _fx.Inbrisk.Find(new FindSpec(WindowTitle: "TestDialog", AutomationId: "CloseDialog"));
        _fx.Inbrisk.Invoke(close[0].Id);

        Assert.True(SpinWait.SpinUntil(() => !s.IsRunning, 8000),
            "session still running after target window closed");
        s.Dispose();
    }

    /// <summary>Test-local event source for the coalescer unit test.</summary>
    private sealed class FakeSource : IEventSource
    {
        public event Action<ObservedEvent>? Event;
        public void Emit(ObservedEvent e) => Event?.Invoke(e);
        public void Start() { }
        public void Dispose() { }
    }
}
