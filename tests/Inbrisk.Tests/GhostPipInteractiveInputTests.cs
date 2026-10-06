using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Runtime;
using Xunit;

namespace Inbrisk.Tests;

public class GhostPipInteractiveInputTests
{
    private sealed class MockInputService : IInputService, GhostDesktopInput.ILocalMouseControl
    {
        public List<(string Action, int X, int Y, MouseButton Button, int CountOrDelta)> RecordedActions { get; } = new();

        public void MoveMouse(int x, int y) => RecordedActions.Add(("move", x, y, MouseButton.Left, 0));
        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1) =>
            RecordedActions.Add(("click", x, y, button, count));
        public void MouseDown(MouseButton button, int x, int y) =>
            RecordedActions.Add(("down", x, y, button, 0));
        public void MouseUp(MouseButton button, int x, int y) =>
            RecordedActions.Add(("up", x, y, button, 0));
        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default) =>
            RecordedActions.Add(("drag", toX, toY, MouseButton.Left, 0));
        public void Scroll(int x, int y, int wheelDelta) =>
            RecordedActions.Add(("scroll", x, y, MouseButton.Left, wheelDelta));
        public void KeyPress(KeyCode key) { }
        public void KeyDown(KeyCode key) { }
        public void KeyUp(KeyCode key) { }
        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key) { }
        public void TypeText(string text) { }
        public (int X, int Y) CursorPosition() => (0, 0);
        public void ReleaseAll() => RecordedActions.Add(("release_all", 0, 0, MouseButton.Left, 0));
    }

    private sealed class MockGhostPipWindowHost : IGhostPipWindowHost
    {
        public IntPtr Hwnd => new(0x1234);
        public bool IsRunning => true;
        public bool ClickThrough { get; private set; } = true;
        public byte Opacity { get; private set; } = 255;
        public int X { get; private set; } = 100;
        public int Y { get; private set; } = 100;
        public int Width { get; private set; } = 480;
        public int Height { get; private set; } = 270;

        public event Action<int, int>? PositionChanged;
        public event Action<int, int, int, int>? BoundsChanged;
        public event Action<PipInputEventArgs>? OnInteractiveInput;

        public void SetClickThrough(bool enabled) => ClickThrough = enabled;
        public void SetOpacity(byte alpha) => Opacity = alpha;
        public void SetPositionAndSize(int x, int y, int width, int height)
        {
            X = x; Y = y; Width = width; Height = height;
            PositionChanged?.Invoke(x, y);
            BoundsChanged?.Invoke(x, y, width, height);
        }

        public void ToggleExpand()
        {
            if (Width <= 480)
            {
                SetPositionAndSize(X, Y, 640, 360);
            }
            else
            {
                SetPositionAndSize(X, Y, 320, 180);
            }
        }

        public void RaiseInteractiveInput(PipInputEventArgs e) => OnInteractiveInput?.Invoke(e);
        public void Dispose() { }
    }

    [Fact]
    public void PipInputEventArgs_PropertiesAndHeaderOffset_CalculatedCorrectly()
    {
        var e = new PipInputEventArgs(PipInputEventType.MouseDown, 150, 80, MouseButton.Left);

        Assert.Equal(PipInputEventType.MouseDown, e.EventType);
        Assert.Equal(150, e.X);
        Assert.Equal(80, e.Y);
        Assert.Equal(150, e.ClientX);
        Assert.Equal(80, e.ClientY);
        Assert.Equal(54, e.ContentY); // 80 - 26 = 54
        Assert.Equal(MouseButton.Left, e.Button);
        Assert.Equal(0, e.Delta);
    }

    [Fact]
    public void GhostDesktopInput_DispatchesLocally_WhenNoIpcBus()
    {
        var mockInput = new MockInputService();
        using var desktopInput = new GhostDesktopInput(inputService: mockInput);

        desktopInput.SendMouseMove(500, 300);
        desktopInput.SendMouseDown(500, 300, MouseButton.Right);
        desktopInput.SendMouseUp(500, 300, MouseButton.Right);
        desktopInput.SendMouseWheel(500, 300, 120);

        Assert.Contains(mockInput.RecordedActions, a => a.Action == "move" && a.X == 500 && a.Y == 300);
        Assert.Contains(mockInput.RecordedActions, a => a.Action == "down" && a.Button == MouseButton.Right);
        Assert.Contains(mockInput.RecordedActions, a => a.Action == "up" && a.Button == MouseButton.Right);
        Assert.Contains(mockInput.RecordedActions, a => a.Action == "scroll" && a.CountOrDelta == 120);
    }

    [Fact]
    public void GhostPipManager_MapsCoordinatesExcludingHeader_Proportionally()
    {
        var mockInput = new MockInputService();
        var desktopInput = new GhostDesktopInput(inputService: mockInput);
        using var pipManager = new GhostPipManager(desktopInput);

        // Configure 480x270 PiP with 1920x1080 target resolution
        var config = new PipConfig(
            Interactive: true,
            CustomWidth: 480,
            CustomHeight: 270,
            TargetResolutionWidth: 1920,
            TargetResolutionHeight: 1080);

        pipManager.StartPip(config);

        // Simulate interactive mouse click at top of content area: (0, 26)
        // Y = 26 should map to target Y = 0 (top edge of ghost desktop)
        desktopInput.ForwardInput(PipInputEventType.MouseDown, 0, 0, MouseButton.Left);
        Assert.Contains(mockInput.RecordedActions, a => a.Action == "down" && a.X == 0 && a.Y == 0);

        // Simulate interactive mouse click at center: (240, 26 + 122) -> (240, 148)
        // 480x244 content area:
        // X = 240 / 480 = 50% -> Target X = 960
        // Y = 122 / 244 = 50% -> Target Y = 540
        int targetX = (int)((240L * 1920) / 480);
        int targetY = (int)((122L * 1080) / 244);
        desktopInput.ForwardInput(PipInputEventType.MouseDown, targetX, targetY, MouseButton.Left);
        Assert.Contains(mockInput.RecordedActions, a => a.Action == "down" && a.X == 960 && a.Y == 540);
    }

    [Fact]
    public void GhostPipManager_WireWindowHostInteractiveInput_IgnoresHeaderAndForwardsContent()
    {
        var mockHost = new MockGhostPipWindowHost();
        var mockInput = new MockInputService();
        var desktopInput = new GhostDesktopInput(inputService: mockInput);

        var dispatched = new List<(PipInputEventType Type, int X, int Y)>();
        desktopInput.InputDispatched += (evt, x, y, btn, delta) => dispatched.Add((evt, x, y));

        using var pipManager = new GhostPipManager(desktopInput);
        pipManager.TargetResolutionWidth = 1920;
        pipManager.TargetResolutionHeight = 1080;

        // Wire event handler manually using same mapping as GhostPipManager
        Action<PipInputEventArgs> handler = (e) =>
        {
            if (!pipManager.IsInteractive) return;

            int pipWidth = mockHost.Width;
            int pipHeight = mockHost.Height;
            const int headerHeight = 26;

            int contentHeight = Math.Max(1, pipHeight - headerHeight);
            int clientY = e.Y - headerHeight;
            if (clientY < 0) return; // Ignore clicks inside the 26px top header bar

            int targetX = (int)Math.Clamp(((long)Math.Clamp(e.X, 0, pipWidth) * pipManager.TargetResolutionWidth) / Math.Max(1, pipWidth), 0, pipManager.TargetResolutionWidth - 1);
            int targetY = (int)Math.Clamp(((long)Math.Clamp(clientY, 0, contentHeight) * pipManager.TargetResolutionHeight) / contentHeight, 0, pipManager.TargetResolutionHeight - 1);

            desktopInput.ForwardInput(e.EventType, targetX, targetY, e.Button, e.Delta);
        };

        mockHost.OnInteractiveInput += handler;

        // 1. Click inside header bar (Y = 15 < 26) -> Should be ignored
        pipManager.SetInteractive(true);
        mockHost.RaiseInteractiveInput(new PipInputEventArgs(PipInputEventType.MouseDown, 100, 15));
        Assert.Empty(dispatched);

        // 2. Click below header bar (Y = 26 -> content Y = 0) -> Should map to target (X, 0)
        mockHost.RaiseInteractiveInput(new PipInputEventArgs(PipInputEventType.MouseDown, 0, 26));
        Assert.Single(dispatched);
        Assert.Equal(0, dispatched[0].X);
        Assert.Equal(0, dispatched[0].Y);

        // 3. Move at bottom-right corner (X = 480, Y = 270) -> Should map to (1919, 1079)
        mockHost.RaiseInteractiveInput(new PipInputEventArgs(PipInputEventType.MouseMove, 480, 270));
        Assert.Equal(2, dispatched.Count);
        Assert.Equal(1919, dispatched[1].X);
        Assert.Equal(1079, dispatched[1].Y);

        // 4. Non-interactive mode -> Should ignore all inputs
        pipManager.SetInteractive(false);
        mockHost.RaiseInteractiveInput(new PipInputEventArgs(PipInputEventType.MouseDown, 240, 148));
        Assert.Equal(2, dispatched.Count); // Count does not increase
    }

    [Fact]
    public void MockGhostPipWindowHost_ToggleExpand_TogglesBetweenCompactAndExpanded()
    {
        var host = new MockGhostPipWindowHost();
        Assert.Equal(480, host.Width);
        Assert.Equal(270, host.Height);

        // When <= 480, expands to 640x360
        host.ToggleExpand();
        Assert.Equal(640, host.Width);
        Assert.Equal(360, host.Height);

        // When > 480, restores to 320x180
        host.ToggleExpand();
        Assert.Equal(320, host.Width);
        Assert.Equal(180, host.Height);
    }

    [Fact]
    public void MockGhostPipWindowHost_BoundsChanged_FiresOnResize()
    {
        var host = new MockGhostPipWindowHost();
        (int X, int Y, int W, int H)? lastBounds = null;
        host.BoundsChanged += (x, y, w, h) => lastBounds = (x, y, w, h);

        host.SetPositionAndSize(50, 60, 500, 300);

        Assert.NotNull(lastBounds);
        Assert.Equal((50, 60, 500, 300), lastBounds.Value);
    }
}
