using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Tests;

[Trait("Category", "Unit")]
public sealed class PipInputTranslatorTests
{
    private sealed class FakeIpcBus : IGhostIpcBus
    {
        public bool IsConnected { get; set; } = true;
        public Exception? ThrowOnSend { get; set; }
        public List<(string Action, string Payload)> Sent { get; } = new();

        public void RegisterHandler(string action, Func<string, Task<string>> handler) { }
        public Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default) => Task.FromResult(IsConnected);

        public Task<string> SendRequestAsync(string action, string payloadJson, TimeSpan timeout, CancellationToken ct = default)
        {
            if (ThrowOnSend != null) throw ThrowOnSend;
            Sent.Add((action, payloadJson));
            return Task.FromResult("{\"ok\":true}");
        }

        public Task<TResp?> SendRequestAsync<TReq, TResp>(string action, TReq payload, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult(default(TResp));

        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0, 0, 320, 180, 1920, 1080, 0, 0)]
    [InlineData(160, 90, 320, 180, 1920, 1080, 960, 540)]
    [InlineData(320, 180, 320, 180, 1920, 1080, 1919, 1079)] // clamped to W-1, H-1
    [InlineData(-50, -20, 320, 180, 1920, 1080, 0, 0)]       // negative clamped to 0
    [InlineData(999, 999, 320, 180, 1920, 1080, 1919, 1079)] // overflow clamped
    [InlineData(320, 180, 640, 360, 3840, 2160, 1920, 1080)] // 4K target
    [InlineData(100, 100, 0, 180, 1920, 1080, 0, 0)]         // invalid width -> (0,0)
    [InlineData(100, 100, 320, 180, 0, 1080, 0, 0)]          // invalid target -> (0,0)
    public void ScaleCoordinates_MapsAndClampsAccurately(
        int pipX, int pipY, int pipW, int pipH, int targetW, int targetH,
        int expectedX, int expectedY)
    {
        var (tx, ty) = PipInputTranslator.ScaleCoordinates(pipX, pipY, pipW, pipH, targetW, targetH);
        Assert.Equal(expectedX, tx);
        Assert.Equal(expectedY, ty);
    }

    [Fact]
    public void UpdateDimensions_UpdatesPositiveValuesAndIgnoresNonPositive()
    {
        var translator = new PipInputTranslator(pipWidth: 320, pipHeight: 180, targetWidth: 1920, targetHeight: 1080);

        translator.UpdateDimensions(640, 360, 2560, 1440);
        Assert.Equal((640, 360, 2560, 1440), (translator.PipWidth, translator.PipHeight, translator.TargetWidth, translator.TargetHeight));
        Assert.Equal((1280, 720), translator.ScaleCoordinates(320, 180));

        translator.UpdateDimensions(0, -10, -1, 0);
        Assert.Equal((640, 360, 2560, 1440), (translator.PipWidth, translator.PipHeight, translator.TargetWidth, translator.TargetHeight));
    }

    [Theory]
    [InlineData("left", MouseButton.Left)]
    [InlineData("Sol", MouseButton.Left)]
    [InlineData("right", MouseButton.Right)]
    [InlineData("Sağ", MouseButton.Right)]
    [InlineData("sag", MouseButton.Right)]
    [InlineData("middle", MouseButton.Middle)]
    [InlineData("Orta", MouseButton.Middle)]
    [InlineData(null, MouseButton.Left)]
    [InlineData("unknown", MouseButton.Left)]
    public void ParseMouseButton_SupportsEnglishAndTurkishAliases(string? input, MouseButton expected)
    {
        Assert.Equal(expected, PipInputTranslator.ParseMouseButton(input));
    }

    [Fact]
    public async Task TranslateMethods_ForwardOverIpcWhenInteractive_AndSuppressWhenDisabled()
    {
        var bus = new FakeIpcBus();
        var translator = new PipInputTranslator(bus, 320, 180, 1920, 1080);

        GhostInputPacket? forwarded = null;
        translator.InputForwarded += p => forwarded = p;

        var clickDown = await translator.TranslateMouseDownAsync(160, 90, "Sağ");
        Assert.Equal(960, clickDown.X);
        Assert.Equal(540, clickDown.Y);
        Assert.Equal("Right", clickDown.Button);
        Assert.Equal(clickDown, forwarded);
        Assert.Single(bus.Sent);
        Assert.Equal(PipInputTranslator.ForwardInputAction, bus.Sent[0].Action);

        var roundTripped = GhostInputPacket.FromJson(bus.Sent[0].Payload);
        Assert.NotNull(roundTripped);
        Assert.Equal(960, roundTripped!.X);
        Assert.Equal(540, roundTripped.Y);
        Assert.Equal("Right", roundTripped.Button);

        // Disable interactivity -> packet returned, nothing sent over IPC
        translator.IsInteractive = false;
        await translator.TranslateMouseWheelAsync(80, 45, -120);
        Assert.Single(bus.Sent);
    }

    [Fact]
    public async Task Translate_IpcFailure_RaisesInputForwardFailedWithoutThrowing()
    {
        var bus = new FakeIpcBus { ThrowOnSend = new GhostIpcException("pipe broken") };
        var translator = new PipInputTranslator(bus, 320, 180, 1920, 1080);

        Exception? captured = null;
        translator.InputForwardFailed += (_, ex) => captured = ex;

        var packet = await translator.TranslateDoubleClickAsync(100, 50);
        Assert.NotNull(packet);
        Assert.IsType<GhostIpcException>(captured);
        Assert.Contains("pipe broken", captured!.Message);
    }
}
