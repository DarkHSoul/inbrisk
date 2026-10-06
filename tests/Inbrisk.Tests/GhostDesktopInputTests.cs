using System;
using System.IO;
using System.Threading;
using Inbrisk.Core;
using Inbrisk.Platform.Windows;
using Xunit;

namespace Inbrisk.Tests;

public class GhostDesktopInputTests
{
    [Fact]
    public void GhostDesktopInput_InitialState_And_Properties()
    {
        using var injector = new GhostDesktopInput(
            desktopName: "InbriskGhostTest_Props",
            desktopWidth: 1920,
            desktopHeight: 1080,
            autoStart: false);

        Assert.Equal("InbriskGhostTest_Props", injector.DesktopName);
        Assert.Equal(1920, injector.DesktopWidth);
        Assert.Equal(1080, injector.DesktopHeight);
        Assert.Equal(320, injector.PipWidth);
        Assert.Equal(180, injector.PipHeight);
        Assert.Equal(26, injector.PipHeaderHeight);
        Assert.True(injector.IsPipInteractive);
        Assert.Null(injector.TargetHwnd);
        Assert.Equal(IntPtr.Zero, injector.LastTargetHwnd);
        Assert.Equal((0, 0), injector.LastCursorPosition);
    }

    [Fact]
    public void MapPipToDesktopCoordinates_Standard16x9_ScalesCorrectly()
    {
        // 320x180 overlay -> 1920x1080 desktop
        var (x0, y0) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 0, pipY: 0,
            pipWidth: 320, pipHeight: 180,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: 0);

        Assert.Equal(0, x0);
        Assert.Equal(0, y0);

        // Center point: (160, 90) -> (960, 540)
        var (midX, midY) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 160, pipY: 90,
            pipWidth: 320, pipHeight: 180,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: 0);

        Assert.Equal(960, midX);
        Assert.Equal(540, midY);

        // Max boundary: (320, 180) -> (1919, 1079) (clamped to max valid screen index)
        var (maxX, maxY) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 320, pipY: 180,
            pipWidth: 320, pipHeight: 180,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: 0);

        Assert.Equal(1919, maxX);
        Assert.Equal(1079, maxY);
    }

    [Fact]
    public void MapPipToDesktopCoordinates_WithHeaderOffset_OffsetsCorrectly()
    {
        // 320x(180+26)=320x206 window, where top 26px is header bar
        int headerHeight = 26;
        int totalHeight = 180 + headerHeight;

        // Click right at the content start: Y = 26 -> desktop Y = 0
        var (xContentTop, yContentTop) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 160, pipY: 26,
            pipWidth: 320, pipHeight: totalHeight,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: headerHeight);

        Assert.Equal(960, xContentTop);
        Assert.Equal(0, yContentTop);

        // Click halfway through content: Y = 26 + 90 = 116 -> desktop Y = 540
        var (xMid, yMid) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 160, pipY: 116,
            pipWidth: 320, pipHeight: totalHeight,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: headerHeight);

        Assert.Equal(960, xMid);
        Assert.Equal(540, yMid);

        // Click inside header bar (e.g. Y = 10) -> clamped to content top Y = 0
        var (xInHeader, yInHeader) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 80, pipY: 10,
            pipWidth: 320, pipHeight: totalHeight,
            desktopWidth: 1920, desktopHeight: 1080,
            pipHeaderHeight: headerHeight);

        Assert.Equal(480, xInHeader);
        Assert.Equal(0, yInHeader);
    }

    [Fact]
    public void MapPipToDesktopCoordinates_ClampsNegativeAndOutOfBounds()
    {
        var (negX, negY) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: -50, pipY: -20,
            pipWidth: 320, pipHeight: 180,
            desktopWidth: 1920, desktopHeight: 1080);

        Assert.Equal(0, negX);
        Assert.Equal(0, negY);

        var (overX, overY) = GhostDesktopInput.MapPipToDesktopCoordinates(
            pipX: 9999, pipY: 9999,
            pipWidth: 320, pipHeight: 180,
            desktopWidth: 1920, desktopHeight: 1080);

        Assert.Equal(1919, overX);
        Assert.Equal(1079, overY);
    }

    [Fact]
    public void KeyCode_ToVirtualKey_MapsCorrectly()
    {
        Assert.Equal(VirtualKey.Enter, GhostDesktopInput.ToVirtualKey(KeyCode.Enter));
        Assert.Equal(VirtualKey.Escape, GhostDesktopInput.ToVirtualKey(KeyCode.Escape));
        Assert.Equal(VirtualKey.Tab, GhostDesktopInput.ToVirtualKey(KeyCode.Tab));
        Assert.Equal(VirtualKey.Space, GhostDesktopInput.ToVirtualKey(KeyCode.Space));
        Assert.Equal(VirtualKey.Backspace, GhostDesktopInput.ToVirtualKey(KeyCode.Backspace));
        Assert.Equal(VirtualKey.Delete, GhostDesktopInput.ToVirtualKey(KeyCode.Delete));
        Assert.Equal(VirtualKey.Left, GhostDesktopInput.ToVirtualKey(KeyCode.Left));
        Assert.Equal(VirtualKey.Right, GhostDesktopInput.ToVirtualKey(KeyCode.Right));
        Assert.Equal(VirtualKey.Up, GhostDesktopInput.ToVirtualKey(KeyCode.Up));
        Assert.Equal(VirtualKey.Down, GhostDesktopInput.ToVirtualKey(KeyCode.Down));
        Assert.Equal(VirtualKey.Home, GhostDesktopInput.ToVirtualKey(KeyCode.Home));
        Assert.Equal(VirtualKey.End, GhostDesktopInput.ToVirtualKey(KeyCode.End));
        Assert.Equal(VirtualKey.PageUp, GhostDesktopInput.ToVirtualKey(KeyCode.PageUp));
        Assert.Equal(VirtualKey.PageDown, GhostDesktopInput.ToVirtualKey(KeyCode.PageDown));
        Assert.Equal(VirtualKey.Control, GhostDesktopInput.ToVirtualKey(KeyCode.Ctrl));
        Assert.Equal(VirtualKey.Shift, GhostDesktopInput.ToVirtualKey(KeyCode.Shift));
        Assert.Equal(VirtualKey.Alt, GhostDesktopInput.ToVirtualKey(KeyCode.Alt));
        Assert.Equal(VirtualKey.LeftWindows, GhostDesktopInput.ToVirtualKey(KeyCode.Win));
        Assert.Equal(VirtualKey.F1, GhostDesktopInput.ToVirtualKey(KeyCode.F1));
        Assert.Equal(VirtualKey.F12, GhostDesktopInput.ToVirtualKey(KeyCode.F12));
        Assert.Equal(VirtualKey.A, GhostDesktopInput.ToVirtualKey(KeyCode.A));
        Assert.Equal(VirtualKey.Z, GhostDesktopInput.ToVirtualKey(KeyCode.Z));
        Assert.Equal(VirtualKey.D0, GhostDesktopInput.ToVirtualKey(KeyCode.D0));
        Assert.Equal(VirtualKey.D9, GhostDesktopInput.ToVirtualKey(KeyCode.D9));
    }

    [Fact]
    public void GhostDesktopInput_MouseAndKeyboard_ExecutesWithoutExceptions()
    {
        string testDesktop = $"InbriskTestDesk_{Guid.NewGuid():N}";
        using var injector = new GhostDesktopInput(testDesktop, autoStart: true);

        // Mouse moves and clicks
        injector.SendMouseMove(500, 400);
        Assert.Equal((500, 400), injector.LastCursorPosition);

        injector.SendMouseDown(500, 400, MouseButton.Left);
        injector.SendMouseUp(500, 400, MouseButton.Left);

        injector.SendMouseClick(600, 450, MouseButton.Left, doubleClick: false);
        injector.SendMouseClick(600, 450, MouseButton.Right, doubleClick: false);
        injector.SendMouseClick(600, 450, MouseButton.Middle, doubleClick: false);

        injector.SendMouseClick(700, 500, MouseButton.Left, doubleClick: true);

        injector.SendMouseScroll(700, 500, delta: 120);
        injector.SendMouseScroll(700, 500, delta: -120);

        injector.SendMouseDrag(100, 100, 200, 200, MouseButton.Left, steps: 3, stepDelayMs: 1);

        // Keyboard actions
        injector.SendKeyPress(VirtualKey.Tab);
        injector.SendKeyPress(KeyCode.Space);
        injector.SendKeyPress((ushort)VirtualKey.Enter);
        injector.SendKeyPress((int)VirtualKey.Escape);

        injector.SendKeyDown(VirtualKey.Shift);
        injector.SendKeyUp(VirtualKey.Shift);

        injector.SendText("Hello Ghost Desktop!\n\tTest");

        // Release all
        injector.ReleaseAll();
    }

    [Fact]
    public void GhostDesktopInput_PipForwarding_ExecutesCorrectly()
    {
        string testDesktop = $"InbriskTestDesk_{Guid.NewGuid():N}";
        using var injector = new GhostDesktopInput(testDesktop, autoStart: true);

        // Test PiP event forwarding
        injector.SendPipMouseMove(160, 90);
        injector.SendPipMouseDown(160, 90, MouseButton.Left);
        injector.SendPipMouseUp(160, 90, MouseButton.Left);
        injector.SendPipMouseClick(160, 90, MouseButton.Left, doubleClick: false);
        injector.SendPipMouseScroll(160, 90, 120);

        // Test ForwardPipInput event args
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.MouseMove, 80, 45));
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.MouseDown, 80, 45, MouseButton.Right));
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.MouseUp, 80, 45, MouseButton.Right));
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.DoubleClick, 80, 45, MouseButton.Left));
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.MouseWheel, 80, 45, MouseButton.Left, delta: -120));

        // When IsPipInteractive is false, events are discarded without error
        injector.IsPipInteractive = false;
        injector.SendPipMouseMove(10, 10);
        injector.SendPipMouseClick(10, 10);
        injector.ForwardPipInput(new PipInputEventArgs(PipInputEventType.MouseMove, 10, 10));
    }

    [Fact]
    public void GhostDesktopInput_AttachToPipHost_WiresEvents()
    {
        using var pipHost = new GhostPipWindowHost(
            x: 50, y: 50, width: 320, height: 180,
            clickThrough: false, autoStart: false);

        using var injector = new GhostDesktopInput(autoStart: false);

        injector.AttachToPipHost(pipHost);
        injector.DetachFromPipHost(pipHost);
    }

    [Fact]
    public void GhostDesktopInput_Disposal_IsIdempotentAndSafe()
    {
        var injector = new GhostDesktopInput("InbriskTestDesk_Dispose", autoStart: true);
        injector.Dispose();
        injector.Dispose(); // second call must not throw

        Assert.Throws<ObjectDisposedException>(() => injector.SendMouseMove(100, 100));
    }
}
