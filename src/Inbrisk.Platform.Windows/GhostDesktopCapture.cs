using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Inbrisk.Platform.Windows;

/// <summary>
/// Captures the visual content of an isolated Win32 Desktop (e.g. <c>InbriskGhostDesktop</c>).
/// <para>
/// Operates on a dedicated background thread attached to the desktop via <see cref="SetThreadDesktop"/>.
/// Composites active application windows using GDI <c>PrintWindow</c> and <c>BitBlt</c> into a 32bpp BGRA
/// virtual surface. When no application windows are open, renders a sleek, modern Inbrisk Ghost OS Desktop
/// background with a dark gradient, cyber grid pattern, Inbrisk AI logo/watermark, and live system metrics clock.
/// </para>
/// <para>
/// Streams captured frames directly into <see cref="GhostSharedFrameBuffer"/> at 30-60 FPS for consumption
/// by <c>GhostPipRenderer</c>.
/// </para>
/// </summary>
public sealed class GhostDesktopCapture : IDisposable, IAsyncDisposable
{
    #region Win32 API Constants & Structs

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const uint DESKTOP_CREATEWINDOW = 0x0002;
    private const uint DESKTOP_CREATEMENU = 0x0004;
    private const uint DESKTOP_HOOKCONTROL = 0x0008;
    private const uint DESKTOP_JOURNALRECORD = 0x0010;
    private const uint DESKTOP_JOURNALPLAYBACK = 0x0020;
    private const uint DESKTOP_ENUMERATE = 0x0040;
    private const uint DESKTOP_WRITEOBJECTS = 0x0080;
    private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
    private const uint DESKTOP_ALL_ACCESS = 0x01FF;

    private const uint PW_DEFAULT = 0x00000000;
    private const uint PW_CLIENTONLY = 0x00000001;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    private const uint SRCCOPY = 0x00CC0020;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    private delegate bool EnumDesktopWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktop(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, uint dwFlags, uint dwDesiredAccess, IntPtr lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumDesktopWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

    #endregion

    #region Window Information Struct

    private sealed class WindowEntry
    {
        public IntPtr Hwnd;
        public RECT Rect;
        public string Title = string.Empty;
        public string ClassName = string.Empty;
    }

    #endregion

    #region Constants & Fields

    public const string DefaultDesktopName = "InbriskGhostDesktop";
    public const int DefaultWidth = 1920;
    public const int DefaultHeight = 1080;
    public const int DefaultFps = 30;

    private readonly object _stateLock = new();
    private readonly string _desktopName;
    private readonly string _sharedBufferMapName;
    private readonly int _width;
    private readonly int _height;
    private int _targetFps;

    private IntPtr _hDesktop;
    private bool _ownsDesktop;

    private Thread? _captureThread;
    private CancellationTokenSource? _cts;
    private volatile bool _isRunning;
    private volatile bool _isDisposed;
    private volatile bool _hasVisibleAppWindows;
    private string? _lastError;

    private long _totalFramesCaptured;
    private double _currentFps;
    private readonly Stopwatch _uptimeStopwatch = new();

    // Reusable GDI Resources for Zero-Allocation Loop
    private Bitmap? _compositeBitmap;
    private Graphics? _graphics;
    private GhostSharedFrameBuffer? _frameBuffer;
    private Image? _logoImage;

    // Metrics Tracking
    private double _currentCpuPct;
    private long _currentMemMb;
    private DateTime _lastCpuSampleTime = DateTime.UtcNow;
    private TimeSpan _lastCpuTotalProcessorTime;

    #endregion

    #region Public Properties

    /// <summary>Name of the targeted Win32 Desktop (e.g. "InbriskGhostDesktop").</summary>
    public string DesktopName => _desktopName;

    /// <summary>Width of the captured desktop surface in pixels.</summary>
    public int Width => _width;

    /// <summary>Height of the captured desktop surface in pixels.</summary>
    public int Height => _height;

    /// <summary>Target capture framerate (30-60 FPS).</summary>
    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Clamp(value, 1, 120);
    }

    /// <summary>Name of the shared memory frame buffer section.</summary>
    public string SharedBufferMapName => _sharedBufferMapName;

    /// <summary>Handle to the targeted Win32 Desktop.</summary>
    public IntPtr DesktopHandle => _hDesktop;

    /// <summary>True if the capture engine is actively running.</summary>
    public bool IsRunning => _isRunning;

    /// <summary>Total number of frames captured and committed to shared memory.</summary>
    public long TotalFramesCaptured => Volatile.Read(ref _totalFramesCaptured);

    /// <summary>Estimated instantaneous framerate presented to shared memory.</summary>
    public double CurrentFps => Volatile.Read(ref _currentFps);

    /// <summary>True if top-level application windows were detected on the desktop during the last frame.</summary>
    public bool HasVisibleAppWindows => _hasVisibleAppWindows;

    /// <summary>Last encountered operational error message, if any.</summary>
    public string? LastError => _lastError;

    /// <summary>Total active uptime of this capture session.</summary>
    public TimeSpan Uptime => _uptimeStopwatch.Elapsed;

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="GhostDesktopCapture"/> targeting the named desktop.
    /// </summary>
    /// <param name="desktopName">Name of the isolated Win32 Desktop (default "InbriskGhostDesktop").</param>
    /// <param name="width">Desktop surface width in pixels (default 1920).</param>
    /// <param name="height">Desktop surface height in pixels (default 1080).</param>
    /// <param name="targetFps">Target framerate between 30 and 60 FPS (default 30).</param>
    /// <param name="sharedBufferMapName">Shared memory map name (default <see cref="GhostSharedFrameBuffer.DefaultMapName"/>).</param>
    public GhostDesktopCapture(
        string desktopName = DefaultDesktopName,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int targetFps = DefaultFps,
        string sharedBufferMapName = GhostSharedFrameBuffer.DefaultMapName)
    {
        _desktopName = string.IsNullOrWhiteSpace(desktopName) ? DefaultDesktopName : desktopName;
        _width = width > 0 ? width : DefaultWidth;
        _height = height > 0 ? height : DefaultHeight;
        _targetFps = Math.Clamp(targetFps, 1, 120);
        _sharedBufferMapName = string.IsNullOrWhiteSpace(sharedBufferMapName)
            ? GhostSharedFrameBuffer.DefaultMapName
            : sharedBufferMapName;

        LoadLogoAsset();
    }

    /// <summary>
    /// Initializes a new instance of <see cref="GhostDesktopCapture"/> with an existing desktop handle.
    /// </summary>
    /// <param name="hDesktop">Explicit handle to the targeted Win32 Desktop.</param>
    /// <param name="width">Desktop surface width in pixels (default 1920).</param>
    /// <param name="height">Desktop surface height in pixels (default 1080).</param>
    /// <param name="targetFps">Target framerate between 30 and 60 FPS (default 30).</param>
    /// <param name="sharedBufferMapName">Shared memory map name.</param>
    public GhostDesktopCapture(
        IntPtr hDesktop,
        int width = DefaultWidth,
        int height = DefaultHeight,
        int targetFps = DefaultFps,
        string sharedBufferMapName = GhostSharedFrameBuffer.DefaultMapName)
        : this(DefaultDesktopName, width, height, targetFps, sharedBufferMapName)
    {
        _hDesktop = hDesktop;
        _ownsDesktop = false;
    }

    #endregion

    #region Start / Stop Lifecycle

    /// <summary>
    /// Starts the background desktop capture and frame streaming loop.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_stateLock)
        {
            if (_isRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _uptimeStopwatch.Restart();

            _captureThread = new Thread(CaptureLoop)
            {
                Name = "InbriskGhostDesktopCapture",
                IsBackground = true
            };
            _captureThread.SetApartmentState(ApartmentState.MTA);
            _captureThread.Start();
        }
    }

    /// <summary>
    /// Signals the background capture thread to stop and waits for it to exit gracefully.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_isRunning)
            {
                return;
            }

            _cts?.Cancel();
            _isRunning = false;
        }

        if (_captureThread != null && _captureThread.IsAlive)
        {
            if (!_captureThread.Join(3000))
            {
                try { _captureThread.Interrupt(); } catch { }
            }
        }

        _captureThread = null;
        _uptimeStopwatch.Stop();
    }

    /// <summary>
    /// Asynchronously starts the desktop screen capture loop.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Start();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Asynchronously stops the desktop screen capture loop.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(Stop, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures a one-off frame of the isolated desktop directly into PNG bytes.
    /// </summary>
    public static byte[] CaptureDesktopToPng(
        string desktopName = DefaultDesktopName,
        int width = DefaultWidth,
        int height = DefaultHeight)
    {
        using var capture = new GhostDesktopCapture(desktopName, width, height, targetFps: 1);
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hDesk = capture.EnsureDesktopAttached();
        capture._compositeBitmap = bmp;
        capture._graphics = g;
        capture.RenderDesktopFrame(hDesk);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    #endregion

    #region Capture Worker Loop

    private void CaptureLoop()
    {
        var token = _cts?.Token ?? CancellationToken.None;

        try
        {
            // 1. Attach MTA thread to target Win32 Desktop before creating any GDI objects
            IntPtr hDesk = EnsureDesktopAttached();

            // 2. Initialize Shared Memory Producer Buffer
            int maxSlotBytes = _width * _height * 4;
            try
            {
                _frameBuffer = GhostSharedFrameBuffer.CreateProducer(_sharedBufferMapName, maxSlotBytes);
            }
            catch (Exception ex)
            {
                _lastError = $"SharedFrameBuffer init failed: {ex.Message}";
                Debug.WriteLine($"[GhostDesktopCapture] Frame buffer init error: {ex}");
                // Fallback attempt to Local\ if Global\ failed
                try
                {
                    _frameBuffer = GhostSharedFrameBuffer.CreateProducer(GhostSharedFrameBuffer.FallbackMapName, maxSlotBytes);
                }
                catch { }
            }

            // 3. Initialize Reusable Composite Surface
            _compositeBitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            _graphics = Graphics.FromImage(_compositeBitmap);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
            _graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            _graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // 4. Capture & Presentation Loop
            var frameSw = Stopwatch.StartNew();
            long frameCount = 0;
            var fpsTimer = Stopwatch.StartNew();

            while (!token.IsCancellationRequested && _isRunning)
            {
                long startTicks = frameSw.ElapsedTicks;

                // Composite Desktop Surface
                RenderDesktopFrame(hDesk);

                // Publish Frame to Shared Memory
                PublishFrameToBuffer();

                frameCount++;
                if (fpsTimer.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = frameCount * 1000.0 / fpsTimer.ElapsedMilliseconds;
                    frameCount = 0;
                    fpsTimer.Restart();
                }

                // High-precision FPS throttle
                int targetFps = _targetFps;
                long targetTicks = Stopwatch.Frequency / (targetFps > 0 ? targetFps : 30);
                long elapsedTicks = frameSw.ElapsedTicks - startTicks;
                long remainingTicks = targetTicks - elapsedTicks;

                if (remainingTicks > 0)
                {
                    int sleepMs = (int)(remainingTicks * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 0)
                    {
                        Thread.Sleep(sleepMs);
                    }
                    else
                    {
                        Thread.SpinWait(50);
                    }
                }
            }
        }
        catch (ThreadInterruptedException)
        {
            // Expected on thread stop
        }
        catch (Exception ex)
        {
            _lastError = $"CaptureLoop faulted: {ex.Message}";
            Debug.WriteLine($"[GhostDesktopCapture] Fault: {ex}");
        }
        finally
        {
            CleanupLoopResources();
        }
    }

    internal IntPtr EnsureDesktopAttached()
    {
        IntPtr hDesk = _hDesktop;

        if (hDesk == IntPtr.Zero && !string.IsNullOrWhiteSpace(_desktopName))
        {
            hDesk = OpenDesktop(_desktopName, 0, false, DESKTOP_ALL_ACCESS);
            if (hDesk == IntPtr.Zero)
            {
                // Fallback: request standard rights
                hDesk = OpenDesktop(
                    _desktopName,
                    0,
                    false,
                    DESKTOP_READOBJECTS | DESKTOP_ENUMERATE | DESKTOP_WRITEOBJECTS | DESKTOP_CREATEWINDOW);
            }

            if (hDesk == IntPtr.Zero)
            {
                // Desktop doesn't exist yet -> Create it
                hDesk = CreateDesktop(
                    _desktopName,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0,
                    DESKTOP_ALL_ACCESS,
                    IntPtr.Zero);

                if (hDesk != IntPtr.Zero)
                {
                    _ownsDesktop = true;
                }
            }
            else
            {
                _ownsDesktop = true;
            }
        }

        if (hDesk == IntPtr.Zero)
        {
            // Fallback to current thread desktop
            hDesk = GetThreadDesktop(GetCurrentThreadId());
        }

        if (hDesk != IntPtr.Zero)
        {
            bool ok = SetThreadDesktop(hDesk);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                _lastError = $"SetThreadDesktop failed with Win32 error {err}";
            }
            _hDesktop = hDesk;
        }

        return hDesk;
    }

    #endregion

    #region Frame Rendering & Window Compositing

    private void RenderDesktopFrame(IntPtr hDesk)
    {
        if (_graphics == null || _compositeBitmap == null) return;

        UpdateSystemMetrics();

        // 1. Enumerate visible top-level windows on this isolated desktop
        List<WindowEntry> windows = EnumerateDesktopWindows(hDesk);
        _hasVisibleAppWindows = windows.Count > 0;

        // 2. Base Layer: Paint Dark Gradient + Subtle Grid Wallpaper
        PaintDesktopWallpaper(_graphics, _width, _height);

        // 3. Composite Windows or Render Hero Content
        if (_hasVisibleAppWindows)
        {
            // EnumDesktopWindows yields Z-order top-to-bottom.
            // Reverse list to composite from bottom-to-top so topmost windows appear on top.
            windows.Reverse();

            IntPtr hdcDest = _graphics.GetHdc();
            try
            {
                foreach (var win in windows)
                {
                    CompositeWindow(hdcDest, win);
                }
            }
            finally
            {
                _graphics.ReleaseHdc(hdcDest);
            }

            // Draw a subtle, sleek mini status indicator in the top-right corner
            DrawMiniStatusBar(_graphics, _width, _height);
        }
        else
        {
            // No app window is open yet: paint modern Inbrisk Ghost OS Desktop hero background
            PaintHeroDesktopContent(_graphics, _width, _height);
        }
    }

    private void CompositeWindow(IntPtr hdcDest, WindowEntry win)
    {
        IntPtr hWnd = win.Hwnd;
        RECT rect = win.Rect;
        int w = rect.Width;
        int h = rect.Height;

        if (w <= 0 || h <= 0) return;
        if (rect.Right <= 0 || rect.Bottom <= 0 || rect.Left >= _width || rect.Top >= _height) return;

        IntPtr hdcWinMem = CreateCompatibleDC(hdcDest);
        if (hdcWinMem == IntPtr.Zero) return;

        IntPtr hBitmap = CreateCompatibleBitmap(hdcDest, w, h);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(hdcWinMem);
            return;
        }

        IntPtr hOldBmp = SelectObject(hdcWinMem, hBitmap);

        try
        {
            bool printed = PrintWindow(hWnd, hdcWinMem, PW_RENDERFULLCONTENT);
            if (!printed)
            {
                printed = PrintWindow(hWnd, hdcWinMem, PW_DEFAULT);
            }

            if (printed)
            {
                BitBlt(hdcDest, rect.Left, rect.Top, w, h, hdcWinMem, 0, 0, SRCCOPY);
            }
            else
            {
                // Fallback to window DC
                IntPtr hdcSrc = GetWindowDC(hWnd);
                if (hdcSrc != IntPtr.Zero)
                {
                    try
                    {
                        BitBlt(hdcDest, rect.Left, rect.Top, w, h, hdcSrc, 0, 0, SRCCOPY);
                    }
                    finally
                    {
                        ReleaseDC(hWnd, hdcSrc);
                    }
                }
            }
        }
        catch { }
        finally
        {
            SelectObject(hdcWinMem, hOldBmp);
            DeleteObject(hBitmap);
            DeleteDC(hdcWinMem);
        }
    }

    private List<WindowEntry> EnumerateDesktopWindows(IntPtr hDesktop)
    {
        var list = new List<WindowEntry>();
        if (hDesktop == IntPtr.Zero) return list;

        EnumDesktopWindows(hDesktop, (hWnd, lParam) =>
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || !IsWindowVisible(hWnd) || IsIconic(hWnd))
            {
                return true;
            }

            if (!GetWindowRect(hWnd, out RECT rect))
            {
                return true;
            }

            int w = rect.Width;
            int h = rect.Height;
            if (w <= 16 || h <= 16)
            {
                return true;
            }

            var sbClass = new StringBuilder(256);
            GetClassNameW(hWnd, sbClass, 256);
            string cls = sbClass.ToString();

            // Filter out system background, message-only, or IME helper windows
            if (string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cls, "Default IME", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sbTitle = new StringBuilder(256);
            GetWindowTextW(hWnd, sbTitle, 256);
            string title = sbTitle.ToString();

            list.Add(new WindowEntry
            {
                Hwnd = hWnd,
                Rect = rect,
                ClassName = cls,
                Title = title
            });

            return true;
        }, IntPtr.Zero);

        return list;
    }

    #endregion

    #region Sleek Ghost OS Desktop Background & UI Painting

    /// <summary>
    /// Paints the dark gradient (#0F172A to #1E1B4B) with subtle grid lines as the base wallpaper.
    /// </summary>
    private static void PaintDesktopWallpaper(Graphics g, int width, int height)
    {
        // 1. Dark Gradient: Slate 900 (#0F172A) to Indigo 950 (#1E1B4B)
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, width, height),
            Color.FromArgb(15, 23, 42),
            Color.FromArgb(30, 27, 75),
            45f);
        g.FillRectangle(bgBrush, 0, 0, width, height);

        // 2. Subtle Cyber Grid Pattern (48px step)
        const int gridStep = 48;
        using var gridPen = new Pen(Color.FromArgb(16, 99, 102, 241), 1f);
        using var dotBrush = new SolidBrush(Color.FromArgb(35, 99, 102, 241));

        for (int x = 0; x < width; x += gridStep)
        {
            g.DrawLine(gridPen, x, 0, x, height);
        }

        for (int y = 0; y < height; y += gridStep)
        {
            g.DrawLine(gridPen, 0, y, width, y);
        }

        // Faint intersection points
        for (int x = 0; x < width; x += gridStep)
        {
            for (int y = 0; y < height; y += gridStep)
            {
                g.FillRectangle(dotBrush, x - 1, y - 1, 2, 2);
            }
        }

        // 3. Ambient Center Soft Glow
        int glowW = Math.Min(width, 1000);
        int glowH = Math.Min(height, 600);
        int glowX = (width - glowW) / 2;
        int glowY = (height - glowH) / 2;

        using var glowPath = new GraphicsPath();
        glowPath.AddEllipse(glowX, glowY, glowW, glowH);
        using var pgb = new PathGradientBrush(glowPath)
        {
            CenterColor = Color.FromArgb(24, 6, 182, 212),
            SurroundColors = new[] { Color.Transparent }
        };
        g.FillPath(pgb, glowPath);
    }

    /// <summary>
    /// Paints the sleek, modern Inbrisk Ghost OS Desktop hero background when no application window is open yet.
    /// Features watermark logo, brand title, live digital clock, and translucent system metrics HUD cards.
    /// </summary>
    private void PaintHeroDesktopContent(Graphics g, int width, int height)
    {
        int centerX = width / 2;
        int centerY = height / 2;

        // --- 1. Watermark & Inbrisk AI Logo ---
        int logoSize = 88;
        int logoTop = Math.Max(20, centerY - 250);

        if (_logoImage != null)
        {
            g.DrawImage(_logoImage, centerX - logoSize / 2, logoTop, logoSize, logoSize);
        }
        else
        {
            // Geometric Futuristic Inbrisk Vector Emblem
            DrawVectorLogo(g, centerX, logoTop + logoSize / 2, 38);
        }

        // --- 2. Title & Subtitle ---
        using var fontTitle = new Font("Segoe UI", 26, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontSub = new Font("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brushTitle = new SolidBrush(Color.FromArgb(248, 250, 252));
        using var brushCyan = new SolidBrush(Color.FromArgb(56, 189, 248));
        using var brushSlate = new SolidBrush(Color.FromArgb(148, 163, 184));

        string titleMain = "INBRISK GHOST OS";
        SizeF titleSize = g.MeasureString(titleMain, fontTitle);
        g.DrawString(titleMain, fontTitle, brushCyan, centerX - titleSize.Width / 2, logoTop + logoSize + 12);

        string sub = "Autonomous Isolated Win32 Execution Environment";
        SizeF subSize = g.MeasureString(sub, fontSub);
        g.DrawString(sub, fontSub, brushSlate, centerX - subSize.Width / 2, logoTop + logoSize + 48);

        // --- 3. Translucent Acrylic HUD Card (System Metrics & Clock) ---
        int cardW = Math.Min(660, width - 40);
        int cardH = 220;
        int cardX = centerX - cardW / 2;
        int cardY = logoTop + logoSize + 82;

        var cardRect = new Rectangle(cardX, cardY, cardW, cardH);
        using var cardPath = CreateRoundedRectanglePath(cardRect, 16);

        // Drop Shadow
        using var shadowPath = CreateRoundedRectanglePath(new Rectangle(cardX + 2, cardY + 4, cardW, cardH), 16);
        using var shadowBrush = new SolidBrush(Color.FromArgb(60, 0, 0, 0));
        g.FillPath(shadowBrush, shadowPath);

        // Glassmorphic Surface
        using var cardBg = new SolidBrush(Color.FromArgb(210, 15, 23, 42));
        g.FillPath(cardBg, cardPath);

        // Glowing Border
        using var borderPen = new Pen(Color.FromArgb(70, 56, 189, 248), 1.5f);
        g.DrawPath(borderPen, cardPath);

        // --- HUD Content: Header Pill ---
        int pad = 20;
        int innerTop = cardY + 16;

        // Pulsing Emerald Status Dot & Label
        using var brushGreen = new SolidBrush(Color.FromArgb(52, 211, 153));
        g.FillEllipse(brushGreen, cardX + pad, innerTop + 3, 9, 9);

        using var fontBadge = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        g.DrawString("DESKTOP READY • LISTENING FOR WORKLOADS", fontBadge, brushGreen, cardX + pad + 15, innerTop);

        // Right-aligned Desktop ID
        using var fontDeskId = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        string deskInfo = $"DESKTOP: {_desktopName}";
        SizeF deskInfoSize = g.MeasureString(deskInfo, fontDeskId);
        g.DrawString(deskInfo, fontDeskId, brushSlate, cardX + cardW - pad - deskInfoSize.Width, innerTop);

        // Divider
        using var divPen = new Pen(Color.FromArgb(30, 148, 163, 184), 1f);
        g.DrawLine(divPen, cardX + pad, innerTop + 24, cardX + cardW - pad, innerTop + 24);

        // --- Live Digital Clock ---
        int clockTop = innerTop + 32;
        string timeStr = DateTime.Now.ToString("HH:mm:ss");
        string dateStr = DateTime.Now.ToString("dddd, MMMM dd, yyyy");

        using var fontClock = new Font("Segoe UI", 34, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontDate = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);

        g.DrawString(timeStr, fontClock, brushTitle, cardX + pad, clockTop);
        g.DrawString(dateStr, fontDate, brushSlate, cardX + pad + 2, clockTop + 42);

        // --- System Metrics Row (Cards) ---
        int metricsY = clockTop + 72;
        int metricCardW = (cardW - (pad * 2) - 24) / 4;
        int metricCardH = 54;

        DrawMetricTile(g, cardX + pad + (metricCardW + 8) * 0, metricsY, metricCardW, metricCardH,
            "CPU USAGE", $"{_currentCpuPct:0.0}%", Color.FromArgb(56, 189, 248));

        DrawMetricTile(g, cardX + pad + (metricCardW + 8) * 1, metricsY, metricCardW, metricCardH,
            "MEMORY", $"{_currentMemMb:N0} MB", Color.FromArgb(167, 139, 250));

        DrawMetricTile(g, cardX + pad + (metricCardW + 8) * 2, metricsY, metricCardW, metricCardH,
            "SURFACE", $"{_width}x{_height}", Color.FromArgb(52, 211, 153));

        DrawMetricTile(g, cardX + pad + (metricCardW + 8) * 3, metricsY, metricCardW, metricCardH,
            "UPTIME", _uptimeStopwatch.Elapsed.ToString(@"hh\:mm\:ss"), Color.FromArgb(251, 191, 36));

        // --- Bottom Hint Text ---
        using var fontHint = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        string hint = "Applications spawned into InbriskGhostDesktop will automatically composite here in real-time.";
        SizeF hintSize = g.MeasureString(hint, fontHint);
        g.DrawString(hint, fontHint, brushSlate, centerX - hintSize.Width / 2, cardY + cardH + 20);
    }

    private static void DrawMetricTile(
        Graphics g,
        int x,
        int y,
        int w,
        int h,
        string label,
        string value,
        Color valueColor)
    {
        var rect = new Rectangle(x, y, w, h);
        using var path = CreateRoundedRectanglePath(rect, 8);
        using var tileBg = new SolidBrush(Color.FromArgb(150, 30, 41, 59));
        using var tileBorder = new Pen(Color.FromArgb(40, 148, 163, 184), 1f);
        using var brushLabel = new SolidBrush(Color.FromArgb(148, 163, 184));
        using var brushValue = new SolidBrush(valueColor);
        using var fontLabel = new Font("Segoe UI", 9, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fontValue = new Font("Segoe UI", 13, FontStyle.Bold, GraphicsUnit.Pixel);

        g.FillPath(tileBg, path);
        g.DrawPath(tileBorder, path);

        g.DrawString(label, fontLabel, brushLabel, x + 8, y + 6);
        g.DrawString(value, fontValue, brushValue, x + 8, y + 24);
    }

    private void DrawMiniStatusBar(Graphics g, int width, int height)
    {
        int pillW = 280;
        int pillH = 26;
        int pillX = width - pillW - 16;
        int pillY = 12;

        var rect = new Rectangle(pillX, pillY, pillW, pillH);
        using var path = CreateRoundedRectanglePath(rect, 13);
        using var bgBrush = new SolidBrush(Color.FromArgb(190, 15, 23, 42));
        using var borderPen = new Pen(Color.FromArgb(60, 56, 189, 248), 1f);
        using var greenBrush = new SolidBrush(Color.FromArgb(52, 211, 153));
        using var textBrush = new SolidBrush(Color.FromArgb(226, 232, 240));
        using var font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);

        g.FillPath(bgBrush, path);
        g.DrawPath(borderPen, path);

        // Dot
        g.FillEllipse(greenBrush, pillX + 10, pillY + 9, 8, 8);

        // Info Text
        string text = $"Ghost Desktop • {_currentFps:0} FPS • {DateTime.Now:HH:mm:ss}";
        g.DrawString(text, font, textBrush, pillX + 24, pillY + 6);
    }

    private static void DrawVectorLogo(Graphics g, int cx, int cy, int radius)
    {
        using var pen = new Pen(Color.FromArgb(56, 189, 248), 2.5f);
        using var brush = new SolidBrush(Color.FromArgb(30, 6, 182, 212));

        PointF[] pts = new PointF[6];
        for (int i = 0; i < 6; i++)
        {
            double angle = (i * 60 - 30) * Math.PI / 180.0;
            pts[i] = new PointF(
                (float)(cx + radius * Math.Cos(angle)),
                (float)(cy + radius * Math.Sin(angle)));
        }

        g.FillPolygon(brush, pts);
        g.DrawPolygon(pen, pts);

        // Inner glowing core
        using var coreBrush = new SolidBrush(Color.FromArgb(56, 189, 248));
        g.FillEllipse(coreBrush, cx - 6, cy - 6, 12, 12);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > rect.Width) d = rect.Width;
        if (d > rect.Height) d = rect.Height;

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void LoadLogoAsset()
    {
        try
        {
            using var stream = typeof(GhostDesktopCapture).Assembly.GetManifestResourceStream("Inbrisk.Platform.Windows.Assets.inbrisk_logo.png");
            if (stream != null)
            {
                _logoImage = Image.FromStream(stream);
            }
        }
        catch
        {
            _logoImage = null;
        }
    }

    private void UpdateSystemMetrics()
    {
        var now = DateTime.UtcNow;
        var elapsed = now - _lastCpuSampleTime;
        if (elapsed.TotalMilliseconds >= 1000)
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                var totalTime = proc.TotalProcessorTime;
                var cpuUsed = (totalTime - _lastCpuTotalProcessorTime).TotalMilliseconds;
                _lastCpuTotalProcessorTime = totalTime;
                _lastCpuSampleTime = now;
                _currentCpuPct = Math.Clamp(cpuUsed / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100.0, 0.0, 100.0);
                _currentMemMb = proc.WorkingSet64 / (1024 * 1024);
            }
            catch { }
        }
    }

    #endregion

    #region Shared Frame Buffer Publishing

    private unsafe void PublishFrameToBuffer()
    {
        if (_frameBuffer == null || _compositeBitmap == null) return;

        var rect = new Rectangle(0, 0, _width, _height);
        BitmapData? data = null;

        try
        {
            data = _compositeBitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = data.Stride;
            int totalBytes = Math.Abs(stride) * _height;

            var pixelSpan = new ReadOnlySpan<byte>((void*)data.Scan0, totalBytes);
            _frameBuffer.WriteFrame(_width, _height, stride, pixelSpan);

            Interlocked.Increment(ref _totalFramesCaptured);
        }
        catch (Exception ex)
        {
            _lastError = $"Frame publishing error: {ex.Message}";
        }
        finally
        {
            if (data != null)
            {
                _compositeBitmap.UnlockBits(data);
            }
        }
    }

    #endregion

    #region Cleanup & Disposal

    private void CleanupLoopResources()
    {
        try
        {
            _graphics?.Dispose();
            _graphics = null;

            _compositeBitmap?.Dispose();
            _compositeBitmap = null;

            _frameBuffer?.Dispose();
            _frameBuffer = null;

            if (_ownsDesktop && _hDesktop != IntPtr.Zero)
            {
                CloseDesktop(_hDesktop);
                _hDesktop = IntPtr.Zero;
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        Stop();

        _logoImage?.Dispose();
        _logoImage = null;

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        await StopAsync().ConfigureAwait(false);
        Dispose();
    }

    #endregion
}
