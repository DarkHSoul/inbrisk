using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

#region Enums & Configuration Records

/// <summary>
/// Preset desktop anchor positions for the Picture-in-Picture (PiP) window.
/// </summary>
public enum PipPresetPosition
{
    /// <summary>Top-right corner of the primary display work area (Default).</summary>
    TopRight,

    /// <summary>Top-left corner of the primary display work area.</summary>
    TopLeft,

    /// <summary>Bottom-right corner of the primary display work area.</summary>
    BottomRight,

    /// <summary>Bottom-left corner of the primary display work area.</summary>
    BottomLeft,

    /// <summary>Arbitrary custom coordinates specified via CustomX/CustomY.</summary>
    Custom
}

/// <summary>
/// Configuration parameters for initializing and configuring the Ghost PiP display.
/// </summary>
public record PipConfig(
    PipPresetPosition Position = PipPresetPosition.TopRight,
    float Scale = 1.0f,
    bool Interactive = false,
    int Margin = 16,
    int? CustomX = null,
    int? CustomY = null,
    int? CustomWidth = null,
    int? CustomHeight = null,
    byte Opacity = 255,
    string Title = "Inbrisk Ghost PiP",
    int TargetFps = 60,
    string? SharedBufferMapName = null);

#endregion

#region Window Host Abstraction

/// <summary>
/// Common contract for PiP native window hosts across Platform.Windows and runtime fallbacks.
/// </summary>
public interface IGhostPipWindowHost : IDisposable
{
    /// <summary>Gets the native HWND handle of the PiP window.</summary>
    IntPtr Hwnd { get; }

    /// <summary>Gets whether the window host message loop is running.</summary>
    bool IsRunning { get; }

    /// <summary>Gets whether clicks pass through the window (WS_EX_TRANSPARENT).</summary>
    bool ClickThrough { get; }

    /// <summary>Gets current alpha opacity (0-255).</summary>
    byte Opacity { get; }

    /// <summary>Gets current desktop X coordinate.</summary>
    int X { get; }

    /// <summary>Gets current desktop Y coordinate.</summary>
    int Y { get; }

    /// <summary>Gets current window width in pixels.</summary>
    int Width { get; }

    /// <summary>Gets current window height in pixels.</summary>
    int Height { get; }

    /// <summary>Dynamically updates click-through behavior.</summary>
    void SetClickThrough(bool enabled);

    /// <summary>Dynamically updates alpha opacity.</summary>
    void SetOpacity(byte alpha);

    /// <summary>Dynamically repositions and resizes the window.</summary>
    void SetPositionAndSize(int x, int y, int width, int height);

    /// <summary>Raised when the window is dragged or moved.</summary>
    event Action<int, int>? PositionChanged;
}

#endregion

#region Telemetry Extension

/// <summary>
/// Telemetry extension for PiP lifecycle events.
/// Bridges directly into PerfTrace counters.
/// </summary>
public static class GhostTelemetry
{
    public const string MetricPipStart = "ghostPipStart";
    public const string MetricPipStop = "ghostPipStop";

    public static void RecordPipStart() => PerfTrace.Count(MetricPipStart);
    public static void RecordPipStop() => PerfTrace.Count(MetricPipStop);
    public static void RecordSessionStart() => Inbrisk.Core.GhostTelemetry.RecordSessionStart();
    public static void RecordSessionStop() => Inbrisk.Core.GhostTelemetry.RecordSessionStop();
    public static void RecordSessionFail() => Inbrisk.Core.GhostTelemetry.RecordSessionFail();
    public static void RecordUserProvisioned() => Inbrisk.Core.GhostTelemetry.RecordUserProvisioned();
    public static void RecordIpcSent() => Inbrisk.Core.GhostTelemetry.RecordIpcSent();
    public static void RecordIpcReceived() => Inbrisk.Core.GhostTelemetry.RecordIpcReceived();
}

#endregion

/// <summary>
/// Picture-in-Picture (PiP) Orchestrator and Lifecycle Manager.
/// Unifies desktop capture, cross-session shared memory buffering, native Win32 window hosting,
/// and high-performance frame rendering behind a single high-level API.
/// </summary>
public class GhostPipManager : IDisposable
{
    #region Win32 Coordinates Helpers

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class Win32
    {
        public const uint SPI_GETWORKAREA = 0x0030;
        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    /// <summary>
    /// Gets the primary display working area (excluding taskbar).
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) GetPrimaryWorkingArea()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                if (Win32.SystemParametersInfoW(Win32.SPI_GETWORKAREA, 0, out RECT rc, 0))
                {
                    if (rc.Right > rc.Left && rc.Bottom > rc.Top)
                    {
                        return (rc.Left, rc.Top, rc.Right, rc.Bottom);
                    }
                }
            }
            catch
            {
                // Fallback to GetSystemMetrics
            }

            try
            {
                int cx = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
                int cy = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
                if (cx > 0 && cy > 0)
                {
                    return (0, 0, cx, cy);
                }
            }
            catch
            {
                // Fallback below
            }
        }

        return (0, 0, 1920, 1080);
    }

    /// <summary>
    /// Calculates desktop coordinates for a preset anchor position.
    /// </summary>
    public static (int X, int Y) CalculatePresetCoordinates(
        PipPresetPosition position,
        int width,
        int height,
        int margin = 16,
        int? customX = null,
        int? customY = null)
    {
        var (wLeft, wTop, wRight, wBottom) = GetPrimaryWorkingArea();

        return position switch
        {
            PipPresetPosition.TopRight => (wRight - width - margin, wTop + margin),
            PipPresetPosition.TopLeft => (wLeft + margin, wTop + margin),
            PipPresetPosition.BottomRight => (wRight - width - margin, wBottom - height - margin),
            PipPresetPosition.BottomLeft => (wLeft + margin, wBottom - height - margin),
            PipPresetPosition.Custom => (customX ?? (wRight - width - margin), customY ?? (wTop + margin)),
            _ => (wRight - width - margin, wTop + margin)
        };
    }

    #endregion

    #region Base Dimension Constants

    public const int BaseWidth = 320;
    public const int BaseHeight = 180;

    #endregion

    #region State Fields

    private readonly object _gate = new();
    private PipConfig _currentConfig = new();
    private IGhostPipWindowHost? _windowHost;
    private GhostPipRenderer? _renderer;
    private bool _isRunning;
    private bool _isInteractive;
    private PipPresetPosition _currentPosition = PipPresetPosition.TopRight;
    private float _currentScale = 1.0f;
    private int _currentWidth = BaseWidth;
    private int _currentHeight = BaseHeight;
    private int _currentX;
    private int _currentY;
    private bool _disposed;

    #endregion

    #region Properties

    /// <summary>Gets whether the PiP window and renderer are currently active.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Gets whether mouse interaction (click-through disabled) is active.</summary>
    public bool IsInteractive => _isInteractive;

    /// <summary>Gets the current preset position.</summary>
    public PipPresetPosition CurrentPosition => _currentPosition;

    /// <summary>Gets the current scale multiplier.</summary>
    public float CurrentScale => _currentScale;

    /// <summary>Gets the current window width in pixels.</summary>
    public int Width => _currentWidth;

    /// <summary>Gets the current window height in pixels.</summary>
    public int Height => _currentHeight;

    /// <summary>Gets the current desktop X coordinate.</summary>
    public int X => _currentX;

    /// <summary>Gets the current desktop Y coordinate.</summary>
    public int Y => _currentY;

    /// <summary>Gets the native window handle (HWND) of the PiP display.</summary>
    public IntPtr WindowHandle => _windowHost?.Hwnd ?? IntPtr.Zero;

    /// <summary>Gets the active renderer instance, if running.</summary>
    public GhostPipRenderer? Renderer => _renderer;

    /// <summary>Gets the active window host instance, if running.</summary>
    public IGhostPipWindowHost? WindowHost => _windowHost;

    /// <summary>Gets the current PiP configuration.</summary>
    public PipConfig CurrentConfig => _currentConfig;

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Launches and begins the PiP overlay display.
    /// Calculates default top-right corner placement on the primary monitor,
    /// initializes the native Win32 window host, and starts the high-speed renderer.
    /// </summary>
    /// <param name="config">Optional configuration settings.</param>
    public void StartPip(PipConfig? config = null)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(GhostPipManager));
            }

            if (_isRunning)
            {
                StopPip();
            }

            _currentConfig = config ?? new PipConfig();
            _currentScale = Math.Clamp(_currentConfig.Scale, 0.25f, 5.0f);
            _currentPosition = _currentConfig.Position;
            _isInteractive = _currentConfig.Interactive;

            // Dimensions: 1.0 -> 320x180, 1.5 -> 480x270, 2.0 -> 640x360
            _currentWidth = _currentConfig.CustomWidth ?? (int)Math.Round(BaseWidth * _currentScale);
            _currentHeight = _currentConfig.CustomHeight ?? (int)Math.Round(BaseHeight * _currentScale);

            // Position: defaults to TopRight
            var (x, y) = CalculatePresetCoordinates(
                _currentPosition,
                _currentWidth,
                _currentHeight,
                _currentConfig.Margin,
                _currentConfig.CustomX,
                _currentConfig.CustomY);

            _currentX = x;
            _currentY = y;

            // 1. Create native PiP window host (Click-through = !Interactive)
            bool clickThrough = !_isInteractive;
            _windowHost = CreateWindowHost(
                _currentX,
                _currentY,
                _currentWidth,
                _currentHeight,
                _currentConfig.Opacity,
                clickThrough,
                _currentConfig.Title);

            // 2. Start high-performance renderer
            _renderer = new GhostPipRenderer(_currentConfig.SharedBufferMapName, _currentConfig.TargetFps);
            _renderer.IsInteractive = _isInteractive;
            if (_windowHost.Hwnd != IntPtr.Zero)
            {
                _renderer.StartRendering(_windowHost.Hwnd);
            }

            if (_windowHost != null)
            {
                _windowHost.PositionChanged += (newX, newY) =>
                {
                    lock (_gate)
                    {
                        _currentX = newX;
                        _currentY = newY;
                        _currentPosition = PipPresetPosition.Custom;
                    }
                };
            }

            _isRunning = true;

            // 3. Telemetry hook
            GhostTelemetry.RecordPipStart();
        }
    }

    /// <summary>
    /// Stops the PiP display and safely shuts down the renderer and window host.
    /// </summary>
    public void StopPip()
    {
        lock (_gate)
        {
            if (!_isRunning)
            {
                return;
            }

            try
            {
                if (_renderer != null)
                {
                    _renderer.StopRendering();
                    _renderer.Dispose();
                    _renderer = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostPipManager] Error stopping renderer: {ex.Message}");
            }

            try
            {
                if (_windowHost != null)
                {
                    _windowHost.Dispose();
                    _windowHost = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostPipManager] Error disposing window host: {ex.Message}");
            }

            _isRunning = false;

            // Telemetry hook
            GhostTelemetry.RecordPipStop();
        }
    }

    /// <summary>
    /// Toggles click-through behavior (WS_EX_TRANSPARENT).
    /// When interactive is true, the user can click directly on the PiP window.
    /// When interactive is false, clicks pass transparently through to background applications.
    /// </summary>
    /// <param name="interactive">True to enable user interaction, false for ghost click-through.</param>
    public void SetInteractive(bool interactive)
    {
        lock (_gate)
        {
            _isInteractive = interactive;
            _windowHost?.SetClickThrough(!interactive);
            if (_renderer != null)
            {
                _renderer.IsInteractive = interactive;
            }
        }
    }

    /// <summary>
    /// Repositions the PiP window to one of the predefined desktop anchor presets.
    /// </summary>
    /// <param name="position">Target preset position.</param>
    public void SetPosition(PipPresetPosition position)
    {
        lock (_gate)
        {
            _currentPosition = position;
            var (x, y) = CalculatePresetCoordinates(
                position,
                _currentWidth,
                _currentHeight,
                _currentConfig.Margin,
                _currentConfig.CustomX,
                _currentConfig.CustomY);

            _currentX = x;
            _currentY = y;

            _windowHost?.SetPositionAndSize(_currentX, _currentY, _currentWidth, _currentHeight);
        }
    }

    /// <summary>
    /// Repositions the PiP window to arbitrary desktop coordinates.
    /// </summary>
    /// <param name="x">Desktop X coordinate.</param>
    /// <param name="y">Desktop Y coordinate.</param>
    public void SetPosition(int x, int y)
    {
        lock (_gate)
        {
            _currentPosition = PipPresetPosition.Custom;
            _currentX = x;
            _currentY = y;
            _windowHost?.SetPositionAndSize(_currentX, _currentY, _currentWidth, _currentHeight);
        }
    }

    /// <summary>
    /// Sets the scaling factor and recalculates window dimensions:
    /// 1.0 -> 320x180, 1.5 -> 480x270, 2.0 -> 640x360.
    /// Preserves current anchor alignment.
    /// </summary>
    /// <param name="scale">Scale multiplier (0.25 to 5.0).</param>
    public void SetScale(float scale)
    {
        lock (_gate)
        {
            _currentScale = Math.Clamp(scale, 0.25f, 5.0f);
            _currentWidth = (int)Math.Round(BaseWidth * _currentScale);
            _currentHeight = (int)Math.Round(BaseHeight * _currentScale);

            var (x, y) = CalculatePresetCoordinates(
                _currentPosition,
                _currentWidth,
                _currentHeight,
                _currentConfig.Margin,
                _currentX,
                _currentY);

            _currentX = x;
            _currentY = y;

            _windowHost?.SetPositionAndSize(_currentX, _currentY, _currentWidth, _currentHeight);
        }
    }

    /// <summary>
    /// Updates alpha opacity of the PiP window (0 = fully transparent, 255 = fully opaque).
    /// </summary>
    /// <param name="alpha">Alpha value between 0 and 255.</param>
    public void SetOpacity(byte alpha)
    {
        lock (_gate)
        {
            _windowHost?.SetOpacity(alpha);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPip();
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Internal Factory & Dynamic Host Resolution

    private static Type? ResolvePlatformWindowHostType()
    {
        var type = Type.GetType("Inbrisk.Platform.Windows.GhostPipWindowHost, Inbrisk.Platform.Windows");
        if (type != null) return type;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.GetName().Name == "Inbrisk.Platform.Windows")
            {
                type = asm.GetType("Inbrisk.Platform.Windows.GhostPipWindowHost");
                if (type != null) return type;
            }
        }

        try
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, "Inbrisk.Platform.Windows.dll");
            if (File.Exists(dllPath))
            {
                var asm = Assembly.LoadFrom(dllPath);
                return asm.GetType("Inbrisk.Platform.Windows.GhostPipWindowHost");
            }
        }
        catch
        {
            // Fall through to native fallback
        }

        return null;
    }

    private static IGhostPipWindowHost CreateWindowHost(
        int x,
        int y,
        int width,
        int height,
        byte opacity,
        bool clickThrough,
        string title)
    {
        var hostType = ResolvePlatformWindowHostType();
        if (hostType != null)
        {
            try
            {
                // Constructor: GhostPipWindowHost(int x, int y, int width, int height, byte initialOpacity, bool clickThrough, bool autoStart, string title)
                var ctor = hostType.GetConstructor(new[]
                {
                    typeof(int), typeof(int), typeof(int), typeof(int),
                    typeof(byte), typeof(bool), typeof(bool), typeof(string)
                });

                if (ctor != null)
                {
                    var instance = ctor.Invoke(new object[] { x, y, width, height, opacity, clickThrough, true, title });
                    if (instance is IDisposable)
                    {
                        return new ReflectionPipWindowHost(instance);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostPipManager] Failed to instantiate reflection GhostPipWindowHost: {ex.Message}");
            }
        }

        // Native fallback implementation using User32 Win32 directly
        return new NativePipWindowHost(x, y, width, height, opacity, clickThrough, title);
    }

    #endregion

    #region Reflection Host Wrapper

    private sealed class ReflectionPipWindowHost : IGhostPipWindowHost
    {
        private readonly object _instance;
        private readonly PropertyInfo? _hwndProp;
        private readonly PropertyInfo? _isRunningProp;
        private readonly PropertyInfo? _clickThroughProp;
        private readonly PropertyInfo? _opacityProp;
        private readonly PropertyInfo? _xProp;
        private readonly PropertyInfo? _yProp;
        private readonly PropertyInfo? _widthProp;
        private readonly PropertyInfo? _heightProp;
        private readonly MethodInfo? _setClickThroughMethod;
        private readonly MethodInfo? _setOpacityMethod;
        private readonly MethodInfo? _setPosSizeMethod;

        public ReflectionPipWindowHost(object instance)
        {
            _instance = instance;
            var t = instance.GetType();
            _hwndProp = t.GetProperty("Hwnd");
            _isRunningProp = t.GetProperty("IsRunning");
            _clickThroughProp = t.GetProperty("ClickThrough");
            _opacityProp = t.GetProperty("Opacity");
            _xProp = t.GetProperty("X");
            _yProp = t.GetProperty("Y");
            _widthProp = t.GetProperty("Width");
            _heightProp = t.GetProperty("Height");
            _setClickThroughMethod = t.GetMethod("SetClickThrough", new[] { typeof(bool) });
            _setOpacityMethod = t.GetMethod("SetOpacity", new[] { typeof(byte) });
            _setPosSizeMethod = t.GetMethod("SetPositionAndSize", new[] { typeof(int), typeof(int), typeof(int), typeof(int) });

            try
            {
                var ev = t.GetEvent("PositionChanged");
                if (ev != null)
                {
                    Action<int, int> handler = (nx, ny) => PositionChanged?.Invoke(nx, ny);
                    ev.AddEventHandler(_instance, handler);
                }
            }
            catch { }
        }

        public event Action<int, int>? PositionChanged;

        public IntPtr Hwnd => (IntPtr)(_hwndProp?.GetValue(_instance) ?? IntPtr.Zero);
        public bool IsRunning => (bool)(_isRunningProp?.GetValue(_instance) ?? false);
        public bool ClickThrough => (bool)(_clickThroughProp?.GetValue(_instance) ?? false);
        public byte Opacity => (byte)(_opacityProp?.GetValue(_instance) ?? (byte)255);
        public int X => (int)(_xProp?.GetValue(_instance) ?? 0);
        public int Y => (int)(_yProp?.GetValue(_instance) ?? 0);
        public int Width => (int)(_widthProp?.GetValue(_instance) ?? 0);
        public int Height => (int)(_heightProp?.GetValue(_instance) ?? 0);

        public void SetClickThrough(bool enabled) => _setClickThroughMethod?.Invoke(_instance, new object[] { enabled });
        public void SetOpacity(byte alpha) => _setOpacityMethod?.Invoke(_instance, new object[] { alpha });
        public void SetPositionAndSize(int x, int y, int width, int height) =>
            _setPosSizeMethod?.Invoke(_instance, new object[] { x, y, width, height });

        public void Dispose()
        {
            if (_instance is IDisposable d)
            {
                d.Dispose();
            }
        }
    }

    #endregion

    #region Pure Win32 Native Fallback Window Host

    private sealed class NativePipWindowHost : IGhostPipWindowHost
    {
        private const string FallbackClassName = "InbriskRuntimeNativePipWindowHost";
        private const uint WS_POPUP = 0x80000000;
        private const uint WS_VISIBLE = 0x10000000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_LAYERED = 0x00080000;
        private const uint WS_EX_NOACTIVATE = 0x08000000;

        private const int GWL_EXSTYLE = -20;
        private const uint LWA_ALPHA = 0x00000002;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint WM_DESTROY = 0x0002;
        private const uint WM_QUIT = 0x0012;

        private static readonly IntPtr HwndTopmost = new(-1);
        private static bool _classRegistered;
        private static readonly object ClassLock = new();

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private static class Native
        {
            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

            [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateWindowExW(
                uint dwExStyle,
                string lpClassName,
                string lpWindowName,
                uint dwStyle,
                int x,
                int y,
                int nWidth,
                int nHeight,
                IntPtr hWndParent,
                IntPtr hMenu,
                IntPtr hInstance,
                IntPtr lpParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

            [DllImport("user32.dll")]
            public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

            [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
            public static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

            [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
            public static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

            [DllImport("user32.dll")]
            public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

            [DllImport("user32.dll")]
            public static extern bool TranslateMessage(ref MSG lpMsg);

            [DllImport("user32.dll")]
            public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

            [DllImport("user32.dll")]
            public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandleW(string? lpModuleName);
        }

        private static WndProc? _staticWndProc;
        private readonly ManualResetEventSlim _ready = new();
        private Thread? _thread;
        private IntPtr _hwnd = IntPtr.Zero;
        private volatile bool _clickThrough;
        private volatile byte _opacity;
        private volatile int _x;
        private volatile int _y;
        private volatile int _width;
        private volatile int _height;
        private readonly string _title;
        private volatile bool _disposed;

        public IntPtr Hwnd => _hwnd;
        public bool IsRunning => _thread != null && _thread.IsAlive && _hwnd != IntPtr.Zero;
        public bool ClickThrough => _clickThrough;
        public byte Opacity => _opacity;
        public int X => _x;
        public int Y => _y;
        public int Width => _width;
        public int Height => _height;

        public NativePipWindowHost(int x, int y, int width, int height, byte opacity, bool clickThrough, string title)
        {
            _x = x;
            _y = y;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _opacity = opacity;
            _clickThrough = clickThrough;
            _title = title;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _thread = new Thread(Pump)
                {
                    IsBackground = true,
                    Name = "InbriskRuntimeNativePipWindowHost"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                _ready.Wait(5000);
            }
        }

        private void Pump()
        {
            lock (ClassLock)
            {
                if (!_classRegistered)
                {
                    _staticWndProc = Native.DefWindowProcW;
                    var wc = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        style = 0,
                        lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticWndProc),
                        cbClsExtra = 0,
                        cbWndExtra = 0,
                        hInstance = Native.GetModuleHandleW(null),
                        hIcon = IntPtr.Zero,
                        hCursor = IntPtr.Zero,
                        hbrBackground = IntPtr.Zero,
                        lpszMenuName = null,
                        lpszClassName = FallbackClassName,
                        hIconSm = IntPtr.Zero
                    };
                    Native.RegisterClassExW(ref wc);
                    _classRegistered = true;
                }
            }

            uint exStyle = WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED;
            if (_clickThrough)
            {
                exStyle |= WS_EX_TRANSPARENT;
            }

            _hwnd = Native.CreateWindowExW(
                exStyle,
                FallbackClassName,
                _title,
                WS_POPUP | WS_VISIBLE,
                _x,
                _y,
                _width,
                _height,
                IntPtr.Zero,
                IntPtr.Zero,
                Native.GetModuleHandleW(null),
                IntPtr.Zero);

            if (_hwnd != IntPtr.Zero)
            {
                Native.SetLayeredWindowAttributes(_hwnd, 0, _opacity, LWA_ALPHA);
                Native.SetWindowPos(_hwnd, HwndTopmost, _x, _y, _width, _height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }

            _ready.Set();

            while (Native.GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }

            if (_hwnd != IntPtr.Zero)
            {
                Native.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        public void SetClickThrough(bool enabled)
        {
            _clickThrough = enabled;
            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    long style = Native.GetWindowLongPtr64(_hwnd, GWL_EXSTYLE).ToInt64();
                    if (enabled) style |= WS_EX_TRANSPARENT;
                    else style &= ~WS_EX_TRANSPARENT;
                    Native.SetWindowLongPtr64(_hwnd, GWL_EXSTYLE, new IntPtr(style));
                    Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
                catch { }
            }
        }

        public void SetOpacity(byte alpha)
        {
            _opacity = alpha;
            if (_hwnd != IntPtr.Zero)
            {
                try { Native.SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA); } catch { }
            }
        }

        public void SetPositionAndSize(int x, int y, int width, int height)
        {
            _x = x;
            _y = y;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    Native.SetWindowPos(_hwnd, HwndTopmost, x, y, _width, _height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
                catch { }
            }
        }

        public event Action<int, int>? PositionChanged;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_hwnd != IntPtr.Zero)
            {
                try { Native.PostMessageW(_hwnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            }

            _thread?.Join(2000);
            _ready.Dispose();
        }
    }

    #endregion
}
