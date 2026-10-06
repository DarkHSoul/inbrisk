using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// High-speed Desktop Input Injector for Ghost Desktop sessions.
/// Bridges interactive input events from the host PiP window directly to the isolated ghost desktop.
/// When an IPC bus is connected to Session 2, input packets are serialized and forwarded
/// via the <c>forward_input</c> protocol to the Session 2 worker daemon.
/// In local standalone mode or when the IPC bus is disconnected, inputs are injected
/// directly via hardware-level Win32 SendInput or <see cref="IInputService"/>.
/// </summary>
public class GhostDesktopInput : IDisposable
{
    private readonly object _stateLock = new();
    private IGhostIpcBus? _ipcBus;
    private IInputService? _inputService;
    private object? _platformInjector;
    private MethodInfo? _platformForwardMethod;
    private bool _disposed;

    /// <summary>
    /// Gets or sets the IPC bus used to forward input to Session 2.
    /// </summary>
    public IGhostIpcBus? IpcBus
    {
        get => _ipcBus;
        set => _ipcBus = value;
    }

    /// <summary>
    /// Gets or sets the local input service used when IPC is disconnected.
    /// </summary>
    public IInputService InputService
    {
        get => _inputService ??= ResolveInputService();
        set => _inputService = value;
    }

    /// <summary>
    /// Gets whether input is currently being routed over the IPC bus to Session 2.
    /// </summary>
    public bool IsIpcActive => _ipcBus != null && _ipcBus.IsConnected;

    /// <summary>
    /// Raised whenever an input event is dispatched to the ghost desktop.
    /// Parameters: EventType, TargetX, TargetY, Button, WheelDelta.
    /// </summary>
    public event Action<PipInputEventType, int, int, MouseButton, int>? InputDispatched;

    /// <summary>
    /// Raised when dispatching an input event encounters an error.
    /// </summary>
    public event Action<Exception>? DispatchFailed;

    /// <summary>
    /// Initializes a new instance of <see cref="GhostDesktopInput"/>.
    /// </summary>
    /// <param name="ipcBus">Optional IPC channel connected to the ghost desktop daemon.</param>
    /// <param name="inputService">Optional direct input injection service.</param>
    public GhostDesktopInput(IGhostIpcBus? ipcBus = null, IInputService? inputService = null)
    {
        _ipcBus = ipcBus;
        _inputService = inputService;

        if (_inputService == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var pType = ResolvePlatformType("GhostDesktopInput");
                if (pType != null)
                {
                    var ctor = pType.GetConstructor(new[] { typeof(string), typeof(int), typeof(int), typeof(bool) });
                    if (ctor != null)
                    {
                        _platformInjector = ctor.Invoke(new object[] { "InbriskGhostDesktop", 1920, 1080, true });
                        _platformForwardMethod = pType.GetMethod("ForwardPipInput", new[] { typeof(PipInputEventArgs) });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostDesktopInput] Platform injector binding skipped: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Forwards a mapped interactive input event directly to the ghost desktop.
    /// Coordinates are expected to be in target ghost desktop space (e.g. 1920x1080).
    /// </summary>
    /// <param name="eventType">The type of input action.</param>
    /// <param name="targetX">X coordinate in target desktop space.</param>
    /// <param name="targetY">Y coordinate in target desktop space.</param>
    /// <param name="button">Mouse button for down/up/click events.</param>
    /// <param name="delta">Wheel scroll delta for mouse wheel events.</param>
    public void ForwardInput(
        PipInputEventType eventType,
        int targetX,
        int targetY,
        MouseButton button = MouseButton.Left,
        int delta = 0)
    {
        if (_disposed) return;

        try
        {
            if (IsIpcActive)
            {
                // Route via IPC packet to Session 2 worker daemon
                _ = DispatchIpcAsync(eventType, targetX, targetY, button, delta);
            }
            else if (_inputService != null)
            {
                // Route directly via supplied local input service
                DispatchLocal(eventType, targetX, targetY, button, delta);
            }
            else if (_platformInjector != null && _platformForwardMethod != null)
            {
                // Route directly to isolated desktop via Windows platform injector
                var args = new PipInputEventArgs(eventType, targetX, targetY, button, delta);
                _platformForwardMethod.Invoke(_platformInjector, new object[] { args });
            }
            else
            {
                // Route directly via default local input service
                DispatchLocal(eventType, targetX, targetY, button, delta);
            }

            InputDispatched?.Invoke(eventType, targetX, targetY, button, delta);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostDesktopInput] ForwardInput failed: {ex.Message}");
            DispatchFailed?.Invoke(ex);
        }
    }

    #region Individual Convenience Actions

    /// <summary>Moves the mouse pointer on the ghost desktop.</summary>
    public void SendMouseMove(int targetX, int targetY) =>
        ForwardInput(PipInputEventType.MouseMove, targetX, targetY);

    /// <summary>Presses and holds a mouse button on the ghost desktop.</summary>
    public void SendMouseDown(int targetX, int targetY, MouseButton button = MouseButton.Left) =>
        ForwardInput(PipInputEventType.MouseDown, targetX, targetY, button);

    /// <summary>Releases a held mouse button on the ghost desktop.</summary>
    public void SendMouseUp(int targetX, int targetY, MouseButton button = MouseButton.Left) =>
        ForwardInput(PipInputEventType.MouseUp, targetX, targetY, button);

    /// <summary>Clicks a mouse button at the designated coordinates on the ghost desktop.</summary>
    public void SendMouseClick(int targetX, int targetY, MouseButton button = MouseButton.Left, int count = 1)
    {
        if (count == 2)
        {
            ForwardInput(PipInputEventType.DoubleClick, targetX, targetY, button);
        }
        else
        {
            ForwardInput(PipInputEventType.MouseDown, targetX, targetY, button);
            ForwardInput(PipInputEventType.MouseUp, targetX, targetY, button);
        }
    }

    /// <summary>Performs a double-click on the ghost desktop.</summary>
    public void SendMouseDoubleClick(int targetX, int targetY, MouseButton button = MouseButton.Left) =>
        ForwardInput(PipInputEventType.DoubleClick, targetX, targetY, button);

    /// <summary>Scrolls the mouse wheel on the ghost desktop.</summary>
    public void SendMouseWheel(int targetX, int targetY, int delta) =>
        ForwardInput(PipInputEventType.MouseWheel, targetX, targetY, MouseButton.Left, delta);

    /// <summary>Presses a key down on the ghost desktop.</summary>
    public void SendKeyDown(KeyCode key)
    {
        if (IsIpcActive)
        {
            _ = SendKeyIpcAsync("keydown", (int)key);
        }
        else
        {
            InputService.KeyDown(key);
        }
    }

    /// <summary>Releases a key up on the ghost desktop.</summary>
    public void SendKeyUp(KeyCode key)
    {
        if (IsIpcActive)
        {
            _ = SendKeyIpcAsync("keyup", (int)key);
        }
        else
        {
            InputService.KeyUp(key);
        }
    }

    /// <summary>Types a Unicode character into the active application in the ghost desktop.</summary>
    public void SendChar(char c)
    {
        if (IsIpcActive)
        {
            _ = SendCharIpcAsync(c);
        }
        else
        {
            InputService.TypeText(c.ToString());
        }
    }

    /// <summary>Types text string into the active application in the ghost desktop.</summary>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (IsIpcActive)
        {
            _ = SendTextIpcAsync(text);
        }
        else
        {
            InputService.TypeText(text);
        }
    }

    /// <summary>Releases all held buttons and keys to prevent stuck inputs.</summary>
    public void ReleaseAll()
    {
        try
        {
            if (IsIpcActive)
            {
                var packet = new GhostInputPacket
                {
                    Type = "ReleaseAll",
                    Action = "release_all"
                };
                _ = _ipcBus?.SendRequestAsync("forward_input", packet.ToJson(), TimeSpan.FromSeconds(1));
            }

            _inputService?.ReleaseAll();
        }
        catch { }
    }

    #endregion

    #region Dispatch Handlers

    private void DispatchLocal(
        PipInputEventType eventType,
        int targetX,
        int targetY,
        MouseButton button,
        int delta)
    {
        var input = InputService;

        switch (eventType)
        {
            case PipInputEventType.MouseMove:
                input.MoveMouse(targetX, targetY);
                break;

            case PipInputEventType.MouseDown:
                input.MoveMouse(targetX, targetY);
                if (input is ILocalMouseControl localMouse)
                {
                    localMouse.MouseDown(button, targetX, targetY);
                }
                else
                {
                    // Fallback to Click if direct down/up not supported on service
                    input.Click(targetX, targetY, button, 1);
                }
                break;

            case PipInputEventType.MouseUp:
                input.MoveMouse(targetX, targetY);
                if (input is ILocalMouseControl localMouseUp)
                {
                    localMouseUp.MouseUp(button, targetX, targetY);
                }
                break;

            case PipInputEventType.DoubleClick:
                input.Click(targetX, targetY, button, 2);
                break;

            case PipInputEventType.MouseWheel:
                input.Scroll(targetX, targetY, delta);
                break;
        }
    }

    private async Task DispatchIpcAsync(
        PipInputEventType eventType,
        int targetX,
        int targetY,
        MouseButton button,
        int delta)
    {
        var bus = _ipcBus;
        if (bus == null || !bus.IsConnected) return;

        GhostInputPacket packet = eventType switch
        {
            PipInputEventType.MouseMove => GhostInputPacket.CreateMouseMove(targetX, targetY, 0, 0),
            PipInputEventType.MouseDown => GhostInputPacket.CreateMouseDown(targetX, targetY, 0, 0, button),
            PipInputEventType.MouseUp => GhostInputPacket.CreateMouseUp(targetX, targetY, 0, 0, button),
            PipInputEventType.DoubleClick => GhostInputPacket.CreateDoubleClick(targetX, targetY, 0, 0, button),
            PipInputEventType.MouseWheel => GhostInputPacket.CreateMouseWheel(targetX, targetY, 0, 0, delta),
            _ => GhostInputPacket.CreateMouseMove(targetX, targetY, 0, 0)
        };

        try
        {
            await bus.SendRequestAsync(
                action: PipInputTranslator.ForwardInputAction,
                payloadJson: packet.ToJson(),
                timeout: TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostDesktopInput] IPC dispatch failed: {ex.Message}");
        }
    }

    private async Task SendKeyIpcAsync(string eventKind, int virtualKey)
    {
        var bus = _ipcBus;
        if (bus == null || !bus.IsConnected) return;

        var payload = new
        {
            @event = eventKind,
            vk = virtualKey,
            timestamp = DateTime.UtcNow.Ticks
        };

        try
        {
            await bus.SendRequestAsync("forward_keyboard", JsonSerializer.Serialize(payload), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendCharIpcAsync(char c)
    {
        var bus = _ipcBus;
        if (bus == null || !bus.IsConnected) return;

        var payload = new
        {
            @event = "char",
            @char = c.ToString(),
            vk = (int)c,
            timestamp = DateTime.UtcNow.Ticks
        };

        try
        {
            await bus.SendRequestAsync("forward_keyboard", JsonSerializer.Serialize(payload), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendTextIpcAsync(string text)
    {
        var bus = _ipcBus;
        if (bus == null || !bus.IsConnected) return;

        var payload = new
        {
            @event = "type",
            @char = text,
            timestamp = DateTime.UtcNow.Ticks
        };

        try
        {
            await bus.SendRequestAsync("forward_keyboard", JsonSerializer.Serialize(payload), TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch { }
    }

    #endregion

    #region Input Service Resolution & Fallback

    private static IInputService ResolveInputService()
    {
        try
        {
            var type = ResolvePlatformType("Input.SendInputService");
            if (type != null && Activator.CreateInstance(type) is IInputService service)
            {
                return service;
            }
        }
        catch { }

        return new LocalWin32InputSimulator();
    }

    private static Type? ResolvePlatformType(string relativeName)
    {
        string fullName = $"Inbrisk.Platform.Windows.{relativeName}";
        var type = Type.GetType($"{fullName}, Inbrisk.Platform.Windows");
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "Inbrisk.Platform.Windows")
            {
                type = asm.GetType(fullName);
                if (type != null) return type;
            }
        }

        try
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "Inbrisk.Platform.Windows.dll");
            if (File.Exists(dllPath))
            {
                var asm = Assembly.LoadFrom(dllPath);
                return asm.GetType(fullName);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Interface for local direct down/up mouse control when available.
    /// </summary>
    public interface ILocalMouseControl
    {
        void MouseDown(MouseButton button, int x, int y);
        void MouseUp(MouseButton button, int x, int y);
    }

    /// <summary>
    /// Pure Win32 P/Invoke input simulator used as fallback when platform libraries are absent.
    /// </summary>
    private sealed class LocalWin32InputSimulator : IInputService, ILocalMouseControl
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        public void MoveMouse(int x, int y) => SetCursorPos(x, y);

        public void MouseDown(MouseButton button, int x, int y)
        {
            SetCursorPos(x, y);
            uint flag = button switch
            {
                MouseButton.Right => MOUSEEVENTF_RIGHTDOWN,
                MouseButton.Middle => MOUSEEVENTF_MIDDLEDOWN,
                _ => MOUSEEVENTF_LEFTDOWN
            };
            mouse_event(flag, x, y, 0, UIntPtr.Zero);
        }

        public void MouseUp(MouseButton button, int x, int y)
        {
            SetCursorPos(x, y);
            uint flag = button switch
            {
                MouseButton.Right => MOUSEEVENTF_RIGHTUP,
                MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                _ => MOUSEEVENTF_LEFTUP
            };
            mouse_event(flag, x, y, 0, UIntPtr.Zero);
        }

        public void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1)
        {
            SetCursorPos(x, y);
            for (int i = 0; i < count; i++)
            {
                MouseDown(button, x, y);
                Thread.Sleep(20);
                MouseUp(button, x, y);
                if (i + 1 < count) Thread.Sleep(50);
            }
        }

        public void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300, CancellationToken ct = default)
        {
            MouseDown(MouseButton.Left, fromX, fromY);
            Thread.Sleep(50);
            SetCursorPos(toX, toY);
            Thread.Sleep(50);
            MouseUp(MouseButton.Left, toX, toY);
        }

        public void Scroll(int x, int y, int wheelDelta)
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_WHEEL, x, y, (uint)wheelDelta, UIntPtr.Zero);
        }

        public void KeyPress(KeyCode key)
        {
            byte vk = (byte)MapKeyCodeToVk(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public void KeyDown(KeyCode key)
        {
            byte vk = (byte)MapKeyCodeToVk(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
        }

        public void KeyUp(KeyCode key)
        {
            byte vk = (byte)MapKeyCodeToVk(key);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        public void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key)
        {
            foreach (var mod in modifiers) KeyDown(mod);
            KeyPress(key);
            for (int i = modifiers.Count - 1; i >= 0; i--) KeyUp(modifiers[i]);
        }

        public void TypeText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            foreach (char c in text)
            {
                if (c == '\n') KeyPress(KeyCode.Enter);
                else if (c == '\t') KeyPress(KeyCode.Tab);
                else if (c == ' ') KeyPress(KeyCode.Space);
                else if (char.IsLetterOrDigit(c))
                {
                    bool isUpper = char.IsUpper(c);
                    if (isUpper) KeyDown(KeyCode.Shift);
                    if (Enum.TryParse<KeyCode>(c.ToString().ToUpperInvariant(), out var kc))
                    {
                        KeyPress(kc);
                    }
                    if (isUpper) KeyUp(KeyCode.Shift);
                }
            }
        }

        public (int X, int Y) CursorPosition()
        {
            if (GetCursorPos(out var pt)) return (pt.X, pt.Y);
            return (0, 0);
        }

        public void ReleaseAll()
        {
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, UIntPtr.Zero);
            keybd_event(0x10, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Shift
            keybd_event(0x11, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Ctrl
            keybd_event(0x12, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Alt
            keybd_event(0x5B, 0, KEYEVENTF_KEYUP, UIntPtr.Zero); // Win
        }

        private static int MapKeyCodeToVk(KeyCode key) => key switch
        {
            KeyCode.Enter => 0x0D,
            KeyCode.Escape => 0x1B,
            KeyCode.Tab => 0x09,
            KeyCode.Space => 0x20,
            KeyCode.Backspace => 0x08,
            KeyCode.Delete => 0x2E,
            KeyCode.Left => 0x25,
            KeyCode.Up => 0x26,
            KeyCode.Right => 0x27,
            KeyCode.Down => 0x28,
            KeyCode.Home => 0x24,
            KeyCode.End => 0x23,
            KeyCode.PageUp => 0x21,
            KeyCode.PageDown => 0x22,
            KeyCode.Ctrl => 0x11,
            KeyCode.Shift => 0x10,
            KeyCode.Alt => 0x12,
            KeyCode.Win => 0x5B,
            KeyCode.F1 => 0x70,
            KeyCode.F2 => 0x71,
            KeyCode.F3 => 0x72,
            KeyCode.F4 => 0x73,
            KeyCode.F5 => 0x74,
            KeyCode.F6 => 0x75,
            KeyCode.F7 => 0x76,
            KeyCode.F8 => 0x77,
            KeyCode.F9 => 0x78,
            KeyCode.F10 => 0x79,
            KeyCode.F11 => 0x7A,
            KeyCode.F12 => 0x7B,
            _ => (int)key >= (int)KeyCode.A && (int)key <= (int)KeyCode.Z ? (0x41 + (int)key - (int)KeyCode.A) :
                 (int)key >= (int)KeyCode.D0 && (int)key <= (int)KeyCode.D9 ? (0x30 + (int)key - (int)KeyCode.D0) : 0
        };
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseAll();
        if (_platformInjector is IDisposable d)
        {
            d.Dispose();
            _platformInjector = null;
        }
        GC.SuppressFinalize(this);
    }
}
