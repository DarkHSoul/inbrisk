using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public sealed class ExpectationWaitTests
{
    private sealed class DummyEventSource : IEventSource
    {
        public event Action<ObservedEvent>? Event;
        public void Start() { }
        public void Dispose() { }
        public void Fire(ObservedEvent e) => Event?.Invoke(e);
    }

    private sealed class DummyCaptureService : ICaptureService
    {
        public byte[] NextSample = [0, 0, 0];
        public Frame Capture(RectPx region, int maxImageWidth = 1600) => throw new NotImplementedException();
        public Frame CaptureWindow(long hwnd, int maxImageWidth = 1600) => throw new NotImplementedException();
        public RawFrame CaptureRaw(RectPx region) => throw new NotImplementedException();
        public double DiffFraction(RectPx region, int sampleScale = 8) => 0;
        public byte[] Sample(RectPx region, int scale = 8) => NextSample;
        public ICaptureSession CreateSession(CaptureTarget target) => throw new NotImplementedException();
    }

    private sealed class DummyWindowService : IWindowService
    {
        public WindowInfo? Foreground;
        public IReadOnlyList<WindowInfo> ListWindows() => Foreground != null ? [Foreground] : [];
        public WindowInfo? GetWindow(long hwnd) => Foreground?.Hwnd == hwnd ? Foreground : null;
        public WindowInfo? GetForegroundWindow() => Foreground;
        public IReadOnlyList<MonitorInfo> GetMonitors() => [];
        public RectPx GetVirtualDesktopBounds() => new(0, 0, 1920, 1080);
        public bool FocusWindow(long hwnd) => true;
        public bool CloseWindow(long hwnd) => true;
        public WindowInfo? GetModalPopup(long hwnd) => null;
        public bool IsWindowEnabled(long hwnd) => true;
        public IReadOnlyList<WindowInfo> FindSystemDialogs() => [];
        public bool IsWindowProtected(long hwnd, out string? reason) { reason = null; return false; }
    }

    private static UiElement MakeEl(string id, string name, Role role, string? value = null, string? state = null)
    {
        var props = new Dictionary<string, object?>
        {
            ["enabled"] = true,
            ["value"] = value,
            ["state"] = state,
        };
        var bounds = new RectPx(10, 10, 100, 30);
        var handle = new ElementHandle(BackendId.Uia, id,
            new ReResolveRecipe(1, 100, "app", role, name, id, Array.Empty<AncestryStep>(), bounds));
        return new UiElement(id, BackendId.Uia, role, name, bounds,
            ["invoke"], props, handle, 1, 100);
    }

    [Fact]
    public void ForCondition_MatchesElementName_Immediately()
    {
        var elements = new List<UiElement>
        {
            MakeEl("btn1", "Kaydet", Role.Button),
        };

        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var wait = new WaitService(spec => elements.Where(e => e.Name == spec.Name).ToList(),
            capture, events, registry);

        var result = wait.ForCondition(new WaitCondition(Name: "Kaydet"), timeoutMs: 1000);

        Assert.True(result.Success);
        Assert.NotNull(result.MatchedElement);
        Assert.Equal("btn1", result.MatchedElement.Id);
    }

    [Fact]
    public void ForCondition_MatchesExpectedState_AndValue()
    {
        var elements = new List<UiElement>
        {
            MakeEl("chk1", "Bildirimler", Role.CheckBox, value: "On", state: "checked"),
        };

        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var wait = new WaitService(spec => elements, capture, events, registry);

        var result = wait.ForCondition(new WaitCondition(
            Name: "Bildirimler",
            ExpectedState: "checked",
            ExpectedValue: "On"), timeoutMs: 1000);

        Assert.True(result.Success);
        Assert.Equal("chk1", result.MatchedElement?.Id);
    }

    [Fact]
    public void ForCondition_Gone_SucceedsWhenElementDisappears()
    {
        var elements = new List<UiElement>(); // empty: element is already gone

        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var wait = new WaitService(spec => elements, capture, events, registry);

        var result = wait.ForCondition(new WaitCondition(
            Name: "Yükleniyor...",
            Gone: true), timeoutMs: 1000);

        Assert.True(result.Success);
        Assert.Equal("element gone", result.Reason);
    }

    [Fact]
    public void ForCondition_InterruptedByDialog_AbortsWaitImmediately()
    {
        var elements = new List<UiElement>(); // target element not found

        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);
        var winService = new DummyWindowService
        {
            Foreground = new WindowInfo(0x1234, 100, "Hata: Dosya Bulunamadı", "app",
                new RectPx(100, 100, 400, 200), WindowState.Normal, true, false, true, 0)
        };

        var wait = new WaitService(spec => elements, capture, events, registry, winService);

        var result = wait.ForCondition(new WaitCondition(
            Name: "Devam Et",
            Hwnd: 0x9999, // scoped to main window
            StopOnUnexpectedDialog: true), timeoutMs: 2000);

        Assert.False(result.Success);
        Assert.True(result.InterruptedByDialog);
        Assert.Equal(0x1234, result.DialogHwnd);
        Assert.Contains("Hata: Dosya Bulunamadı", result.DialogTitle);
        Assert.Contains("interrupted_by_dialog", result.Reason);
    }

    [Fact]
    public void ForStable_IgnoresSpecifiedRegions_WhenDiffInsideIgnoreRegion()
    {
        var events = new DummyEventSource();
        var capture = new DummyCaptureService();
        var registry = new ElementRegistry([]);

        // Window region: 0,0 80x80 (at scale 8 => 10x10 = 100 pixels * 3 = 300 bytes)
        var sample1 = new byte[300];
        var sample2 = new byte[300];

        // Introduce a difference at pixel 0 (desktop coords: 0,0)
        sample1[0] = 0;
        sample2[0] = 100;

        capture.NextSample = sample1;
        var wait = new WaitService(spec => [], capture, events, registry);

        // Ignore region covering pixel 0: (0, 0, 16, 16)
        var ignoreRegions = new List<RectPx> { new(0, 0, 16, 16) };

        // Should settle as stable because differences are within the ignored region
        var result = wait.ForStable(new RectPx(0, 0, 80, 80), quietMs: 200, timeoutMs: 1000, ignoreRegions: ignoreRegions);

        Assert.True(result.Success);
    }
}
