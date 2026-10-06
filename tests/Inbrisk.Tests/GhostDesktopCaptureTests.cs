using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Platform.Windows;
using Xunit;

namespace Inbrisk.Tests;

public class GhostDesktopCaptureTests
{
    [Fact]
    public void GhostDesktopCapture_Constructs_WithDefaultAndCustomParameters()
    {
        using var captureDefault = new GhostDesktopCapture();
        Assert.Equal(GhostDesktopCapture.DefaultDesktopName, captureDefault.DesktopName);
        Assert.Equal(1920, captureDefault.Width);
        Assert.Equal(1080, captureDefault.Height);
        Assert.Equal(30, captureDefault.TargetFps);
        Assert.False(captureDefault.IsRunning);
        Assert.Equal(0, captureDefault.TotalFramesCaptured);

        string customDesktop = $"InbriskGhostTest_{Guid.NewGuid():N}";
        string customMap = $@"Local\InbriskGhostBufferTest_{Guid.NewGuid():N}";
        using var captureCustom = new GhostDesktopCapture(
            desktopName: customDesktop,
            width: 1280,
            height: 720,
            targetFps: 60,
            sharedBufferMapName: customMap);

        Assert.Equal(customDesktop, captureCustom.DesktopName);
        Assert.Equal(1280, captureCustom.Width);
        Assert.Equal(720, captureCustom.Height);
        Assert.Equal(60, captureCustom.TargetFps);
        Assert.Equal(customMap, captureCustom.SharedBufferMapName);
        Assert.False(captureCustom.IsRunning);
    }

    [Fact]
    public async Task GhostDesktopCapture_Lifecycle_ProducesFramesToSharedBuffer()
    {
        string uniqueDesktop = $"InbriskGhostTestCapture_{Guid.NewGuid():N}";
        string uniqueMap = $@"Local\InbriskGhostFrameBuffer_{Guid.NewGuid():N}";

        using var capture = new GhostDesktopCapture(
            desktopName: uniqueDesktop,
            width: 1280,
            height: 720,
            targetFps: 60,
            sharedBufferMapName: uniqueMap);

        Assert.False(capture.IsRunning);

        // Start capture loop
        capture.Start();
        Assert.True(capture.IsRunning);

        // Wait up to 2 seconds for frames to be produced
        var sw = Stopwatch.StartNew();
        while (capture.TotalFramesCaptured < 2 && sw.ElapsedMilliseconds < 2500)
        {
            await Task.Delay(50);
        }

        Assert.True(capture.TotalFramesCaptured >= 1, $"Expected at least 1 frame captured, got {capture.TotalFramesCaptured}. Error: {capture.LastError}");
        Assert.False(capture.HasVisibleAppWindows); // In new test desktop, no app window open -> hero background rendered

        // Verify consumer can read from the shared frame buffer
        bool opened = GhostSharedFrameBuffer.TryOpenConsumer(out var consumer, uniqueMap);
        Assert.True(opened);
        Assert.NotNull(consumer);

        using (consumer)
        {
            byte[] pixelBuffer = new byte[1280 * 720 * 4];
            bool readOk = consumer.TryReadLatestFrame(pixelBuffer, out var header);
            Assert.True(readOk);
            Assert.True(header.IsValid);
            Assert.Equal(1280, header.Width);
            Assert.Equal(720, header.Height);
            Assert.Equal(1280 * 4, header.Stride);
            Assert.True(header.FrameIndex >= 1);
        }

        // Stop capture
        capture.Stop();
        Assert.False(capture.IsRunning);
    }

    [Fact]
    public async Task GhostDesktopCapture_StopAsync_And_DisposeAsync()
    {
        string uniqueDesktop = $"InbriskGhostTestCapture_{Guid.NewGuid():N}";
        string uniqueMap = $@"Local\InbriskGhostFrameBuffer_{Guid.NewGuid():N}";

        var capture = new GhostDesktopCapture(
            desktopName: uniqueDesktop,
            width: 800,
            height: 600,
            targetFps: 30,
            sharedBufferMapName: uniqueMap);

        await capture.StartAsync();
        Assert.True(capture.IsRunning);

        await Task.Delay(150);
        Assert.True(capture.TotalFramesCaptured >= 1);

        await capture.StopAsync();
        Assert.False(capture.IsRunning);

        await capture.DisposeAsync();
    }
}
