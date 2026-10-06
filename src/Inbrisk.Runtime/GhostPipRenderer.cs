using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Inbrisk.Runtime;

/// <summary>
/// High-performance Picture-in-Picture (PiP) desktop renderer.
/// Consumes frames from the cross-session shared memory buffer (GhostSharedFrameBuffer)
/// and paints them onto the native PiP window host (GhostPipWindowHost) at 30-60 FPS.
/// Features hardware-assisted GDI halftone downscaling, double-buffered tear-free rendering,
/// zero-overhead idle skipping via lastFrameIndex tracking, and a modern UI overlay with
/// a sleek semi-transparent border and a live status indicator dot.
/// </summary>
public sealed class GhostPipRenderer : IDisposable
{
    #region Win32 Constants & Structs

    private const string DefaultMapName = @"Global\InbriskGhostFrameBuffer";
    private const string FallbackMapName = @"Local\InbriskGhostFrameBuffer";
    private const string DefaultEventName = @"Global\InbriskGhostFrameEvent";
    private const string FallbackEventName = @"Local\InbriskGhostFrameEvent";

    private const uint SharedMemoryMagic = 0x49424642; // "IBFB"
    private const uint FILE_MAP_READ = 0x0004;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_TIMEOUT = 0x00000102;

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;
    private const int PS_SOLID = 0;
    private const int HOLLOW_BRUSH = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr ho);

        [DllImport("gdi32.dll")]
        public static extern int SetStretchBltMode(IntPtr hdc, int mode);

        [DllImport("gdi32.dll")]
        public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr lppt);

        [DllImport("gdi32.dll")]
        public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern int StretchDIBits(
            IntPtr hdc,
            int xDest,
            int yDest,
            int DestWidth,
            int DestHeight,
            int xSrc,
            int ySrc,
            int SrcWidth,
            int SrcHeight,
            IntPtr lpBits,
            ref BITMAPINFO lpbmi,
            uint iUsage,
            uint rop);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateSolidBrush(uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreatePen(int iStyle, int cWidth, uint color);

        [DllImport("gdi32.dll")]
        public static extern IntPtr GetStockObject(int fnObject);

        [DllImport("gdi32.dll")]
        public static extern bool Rectangle(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll")]
        public static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern bool TextOutW(IntPtr hdc, int x, int y, string lpString, int nCount);

        [DllImport("gdi32.dll")]
        public static extern uint SetTextColor(IntPtr hdc, uint crColor);

        [DllImport("gdi32.dll")]
        public static extern int SetBkMode(IntPtr hdc, int iBkMode);

        [DllImport("user32.dll")]
        public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFontW(
            int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight,
            uint fdwItalic, uint fdwUnderline, uint fdwStrikeOut, uint fdwCharSet,
            uint fdwOutputPrecision, uint fdwClipPrecision, uint fdwQuality,
            uint fdwPitchAndFamily, string lpszFace);
    }

    private static uint RGB(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    #endregion

    #region Fields & State

    private readonly object _syncRoot = new();
    private readonly string _preferredMapName;
    private int _targetFps;

    // Shared Memory Handles
    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _mappedView = IntPtr.Zero;
    private IntPtr _hFrameEvent = IntPtr.Zero;

    // Render loop threading
    private Thread? _renderThread;
    private volatile bool _isRunning;
    private IntPtr _targetHwnd = IntPtr.Zero;

    // Repaint suppression & FPS tracking
    private long _lastFrameIndex = -1;
    private long _totalRenderedFrames;
    private double _fps;
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private int _fpsCounter;

    // Cached GDI offscreen resources for double-buffering
    private IntPtr _cachedHdcMem = IntPtr.Zero;
    private IntPtr _cachedHbmpMem = IntPtr.Zero;
    private IntPtr _cachedOldBmp = IntPtr.Zero;
    private int _cachedWidth;
    private int _cachedHeight;

    private bool _isDisposed;

    #endregion

    #region Properties

    /// <summary>Target rendering framerate (default: 60 FPS, range: 1-120).</summary>
    public int TargetFps
    {
        get => _targetFps;
        set => _targetFps = Math.Clamp(value, 1, 120);
    }

    /// <summary>True if the background rendering thread is currently active.</summary>
    public bool IsRendering => _isRunning;

    /// <summary>Current target window handle receiving the rendered PiP output.</summary>
    public IntPtr TargetHwnd => _targetHwnd;

    /// <summary>Measured rendering framerate (FPS) computed over 1-second rolling windows.</summary>
    public double FPS => Volatile.Read(ref _fps);

    /// <summary>Integer rounded measured framerate.</summary>
    public int CurrentFps => (int)Math.Round(FPS);

    /// <summary>Sequence number of the most recently rendered desktop frame.</summary>
    public long LastRenderedFrameIndex => Volatile.Read(ref _lastFrameIndex);

    /// <summary>Height of the top drag handle bar in pixels when interactive.</summary>
    public const int HeaderHeight = 26;

    private volatile bool _isInteractive = false;

    /// <summary>Gets or sets whether interactive mode is active (draws drag header bar).</summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set
        {
            if (_isInteractive != value)
            {
                _isInteractive = value;
                _lastFrameIndex = -1;
                DrawStandbyFrame();
            }
        }
    }

    /// <summary>Total number of frames successfully presented to the PiP window.</summary>
    public long TotalRenderedFrames => Volatile.Read(ref _totalRenderedFrames);

    /// <summary>Whether to draw the small glowing green status dot in the corner (default: true).</summary>
    public bool ShowLiveIndicator { get; set; } = true;

    /// <summary>Whether to draw the thin sleek modern border around the PiP frame (default: true).</summary>
    public bool ShowBorder { get; set; } = true;

    #endregion

    #region Constructor & Initialization

    /// <summary>
    /// Initializes a new instance of GhostPipRenderer.
    /// </summary>
    /// <param name="mapName">Custom shared memory map name, or null for default Global\InbriskGhostFrameBuffer.</param>
    /// <param name="targetFps">Target framerate in FPS (default: 60).</param>
    public GhostPipRenderer(string? mapName = null, int targetFps = 60)
    {
        _preferredMapName = string.IsNullOrWhiteSpace(mapName) ? DefaultMapName : mapName;
        TargetFps = targetFps;
    }

    #endregion

    #region Public Control Methods

    /// <summary>
    /// Starts the background rendering thread targeting the specified native window handle.
    /// Connects to the shared frame buffer and continuously updates the window with smooth scaling.
    /// </summary>
    /// <param name="targetHwnd">HWND of the PiP window (e.g. GhostPipWindowHost.Hwnd).</param>
    public void StartRendering(IntPtr targetHwnd)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (targetHwnd == IntPtr.Zero || !Native.IsWindow(targetHwnd))
        {
            throw new ArgumentException("A valid native window handle (HWND) is required.", nameof(targetHwnd));
        }

        lock (_syncRoot)
        {
            if (_isRunning)
            {
                if (_targetHwnd == targetHwnd)
                {
                    return; // Already rendering to this target
                }
                StopRenderingInternal();
            }

            _targetHwnd = targetHwnd;
            _lastFrameIndex = -1;
            _fps = 0.0;
            _fpsCounter = 0;
            _fpsStopwatch.Restart();

            EnsureSharedMemoryConnected();

            _isRunning = true;
            _renderThread = new Thread(RenderLoop)
            {
                Name = "InbriskGhostPipRenderer",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _renderThread.Start();
        }
    }

    /// <summary>
    /// Gracefully stops the background rendering loop and releases target HWND binding.
    /// </summary>
    public void StopRendering()
    {
        lock (_syncRoot)
        {
            StopRenderingInternal();
        }
    }

    private void StopRenderingInternal()
    {
        _isRunning = false;

        if (_renderThread != null && _renderThread.IsAlive)
        {
            if (!_renderThread.Join(1500))
            {
                try { _renderThread.Interrupt(); } catch { }
            }
            _renderThread = null;
        }

        _targetHwnd = IntPtr.Zero;
        DestroyOffscreenBuffer();
    }

    /// <summary>
    /// Triggers an immediate one-shot frame check and render.
    /// Returns true if a new frame was rendered, false if no new frame was available.
    /// </summary>
    public bool RenderLatestFrameNow()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        lock (_syncRoot)
        {
            if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd))
            {
                return false;
            }

            EnsureSharedMemoryConnected();
            return TryRenderFrameFromSharedMemory();
        }
    }

    /// <summary>
    /// Directly presents custom pixel data to the target PiP window using the double-buffered halftone pipeline.
    /// Useful for testing, fallbacks, or external video streams.
    /// </summary>
    /// <param name="pPixels">Pointer to 32bpp BGRA/RGBA pixel data.</param>
    /// <param name="width">Source frame width.</param>
    /// <param name="height">Source frame height.</param>
    /// <param name="stride">Row pitch in bytes.</param>
    /// <param name="frameIndex">Optional sequence number for frame tracking.</param>
    public bool RenderCustomFrame(IntPtr pPixels, int width, int height, int stride, long frameIndex = 0)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (pPixels == IntPtr.Zero || width <= 0 || height <= 0 || stride <= 0)
        {
            return false;
        }

        lock (_syncRoot)
        {
            if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd))
            {
                return false;
            }

            return DrawFrameToWindow(pPixels, width, height, stride, frameIndex);
        }
    }

    /// <summary>
    /// Directly presents custom pixel data from a byte array to the target PiP window.
    /// </summary>
    public bool RenderCustomFrame(byte[] pixels, int width, int height, int stride, long frameIndex = 0)
    {
        if (pixels == null || pixels.Length == 0)
        {
            return false;
        }

        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return RenderCustomFrame(handle.AddrOfPinnedObject(), width, height, stride, frameIndex);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Directly presents custom pixel data from a byte span to the target PiP window.
    /// </summary>
    public bool RenderCustomFrame(ReadOnlySpan<byte> pixels, int width, int height, int stride, long frameIndex = 0)
    {
        if (pixels.IsEmpty)
        {
            return false;
        }

        return RenderCustomFrame(pixels.ToArray(), width, height, stride, frameIndex);
    }

    #endregion

    #region Render Loop Implementation

    private void RenderLoop()
    {
        var intervalWatch = Stopwatch.StartNew();

        while (_isRunning)
        {
            try
            {
                if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd))
                {
                    Thread.Sleep(100);
                    continue;
                }

                if (Native.IsIconic(_targetHwnd))
                {
                    // PiP window is minimized; sleep to conserve CPU
                    Thread.Sleep(50);
                    continue;
                }

                int frameBudgetMs = Math.Max(1, 1000 / _targetFps);

                // Wait on the frame event if available, otherwise sleep up to the frame interval
                if (_hFrameEvent != IntPtr.Zero)
                {
                    Native.WaitForSingleObject(_hFrameEvent, (uint)frameBudgetMs);
                }

                long elapsedMs = intervalWatch.ElapsedMilliseconds;
                if (elapsedMs < frameBudgetMs)
                {
                    int sleepMs = (int)(frameBudgetMs - elapsedMs);
                    if (sleepMs > 1)
                    {
                        Thread.Sleep(sleepMs);
                    }
                }
                intervalWatch.Restart();

                if (!_isRunning) break;

                EnsureSharedMemoryConnected();

                bool frameRendered = false;
                if (_mappedView != IntPtr.Zero)
                {
                    frameRendered = TryRenderFrameFromSharedMemory();
                }

                if (!frameRendered && _totalRenderedFrames == 0)
                {
                    DrawStandbyFrame();
                }
            }
            catch (ThreadInterruptedException)
            {
                break;
            }
            catch
            {
                // Silently back off on transient errors to keep renderer loop alive
                Thread.Sleep(30);
            }
        }
    }

    private bool TryRenderFrameFromSharedMemory()
    {
        if (_mappedView == IntPtr.Zero)
        {
            return false;
        }

        // Layout of BufferControlBlock:
        // offset 0: Magic (uint, 0x49424642)
        // offset 4: Version (int)
        // offset 8: SlotCount (int)
        // offset 12: ActiveSlotIndex (int)
        // offset 16: SlotCapacity (long)
        // offset 24: Slot0Offset (long)
        // offset 32: Slot1Offset (long)
        // offset 40: TotalFramesWritten (long)

        uint magic = (uint)Marshal.ReadInt32(_mappedView, 0);
        if (magic != SharedMemoryMagic)
        {
            return false;
        }

        int activeSlot = Marshal.ReadInt32(_mappedView, 12);
        if (activeSlot < 0 || activeSlot > 1)
        {
            return false; // No frame written yet
        }

        long slotOffset = activeSlot == 0
            ? Marshal.ReadInt64(_mappedView, 24)
            : Marshal.ReadInt64(_mappedView, 32);

        if (slotOffset <= 0)
        {
            return false;
        }

        IntPtr slotBase = IntPtr.Add(_mappedView, (int)slotOffset);

        // Layout of SlotControlHeader:
        // offset 0: Header.Magic (uint)
        // offset 4: Header.Width (int)
        // offset 8: Header.Height (int)
        // offset 12: Header.Stride (int)
        // offset 16: Header.FrameIndex (long)
        // offset 24: Header.TimestampTicks (long)
        // offset 32: WriteSequence (long)
        // offset 40: DataLength (long)
        // offset 64: Pixel data begins

        long seq1 = Marshal.ReadInt64(slotBase, 32);
        if ((seq1 & 1) != 0)
        {
            // Producer is actively writing to this slot; skip to prevent tearing
            return false;
        }

        uint headerMagic = (uint)Marshal.ReadInt32(slotBase, 0);
        if (headerMagic != SharedMemoryMagic)
        {
            return false;
        }

        long frameIndex = Marshal.ReadInt64(slotBase, 16);

        // Ekran değişmediğinde gereksiz repaint yapmayan lastFrameIndex kontrolü
        if (frameIndex == _lastFrameIndex)
        {
            return false;
        }

        int width = Marshal.ReadInt32(slotBase, 4);
        int height = Marshal.ReadInt32(slotBase, 8);
        int stride = Marshal.ReadInt32(slotBase, 12);
        long dataLength = Marshal.ReadInt64(slotBase, 40);

        if (width <= 0 || height <= 0 || stride <= 0 || dataLength <= 0)
        {
            return false;
        }

        IntPtr pPixels = IntPtr.Add(slotBase, 64);

        // Render to window
        bool rendered = DrawFrameToWindow(pPixels, width, height, stride, frameIndex);

        // Verify sequence consistency after painting
        Thread.MemoryBarrier();
        long seq2 = Marshal.ReadInt64(slotBase, 32);

        if (rendered && seq1 == seq2)
        {
            _lastFrameIndex = frameIndex;
            return true;
        }

        return false;
    }

    #endregion

    #region High-Performance GDI Drawing Pipeline

    private bool DrawFrameToWindow(IntPtr pPixels, int srcWidth, int srcHeight, int stride, long frameIndex)
    {
        if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd))
        {
            return false;
        }

        if (!Native.GetClientRect(_targetHwnd, out RECT rc))
        {
            return false;
        }

        int destWidth = rc.Right - rc.Left;
        int destHeight = rc.Bottom - rc.Top;

        if (destWidth <= 2 || destHeight <= 2)
        {
            return false;
        }

        IntPtr hdcWin = Native.GetDC(_targetHwnd);
        if (hdcWin == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            // Ensure double-buffer memory DC and bitmap match current client dimensions
            EnsureOffscreenBuffer(hdcWin, destWidth, destHeight);

            // Enable high-quality halftone bilinear downscaling
            Native.SetStretchBltMode(_cachedHdcMem, HALFTONE);
            Native.SetBrushOrgEx(_cachedHdcMem, 0, 0, IntPtr.Zero);

            // Configure DIB header (negative biHeight indicates top-down bitmap)
            BITMAPINFO bmi = default;
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = srcWidth;
            bmi.bmiHeader.biHeight = -srcHeight; // Top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB
            bmi.bmiHeader.biSizeImage = (uint)(stride * srcHeight);

            int headerOffset = IsInteractive ? HeaderHeight : 0;
            int renderHeight = destHeight - headerOffset;
            if (renderHeight <= 2) renderHeight = destHeight;

            // Fast hardware-assisted stretch directly from unmanaged shared memory
            Native.StretchDIBits(
                _cachedHdcMem,
                0, headerOffset, destWidth, renderHeight,
                0, 0, srcWidth, srcHeight,
                pPixels,
                ref bmi,
                DIB_RGB_COLORS,
                SRCCOPY);

            // Paint modern UI overlay (border & live dot, plus drag handle when interactive)
            DrawModernUiOverlay(_cachedHdcMem, destWidth, destHeight);

            // Composite final frame atomically to window DC with zero flicker
            Native.BitBlt(hdcWin, 0, 0, destWidth, destHeight, _cachedHdcMem, 0, 0, SRCCOPY);

            // Update performance statistics
            Interlocked.Increment(ref _totalRenderedFrames);
            _fpsCounter++;

            if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
            {
                double seconds = _fpsStopwatch.ElapsedMilliseconds / 1000.0;
                Volatile.Write(ref _fps, _fpsCounter / seconds);
                _fpsCounter = 0;
                _fpsStopwatch.Restart();
            }

            return true;
        }
        finally
        {
            Native.ReleaseDC(_targetHwnd, hdcWin);
        }
    }

    private void DrawModernUiOverlay(IntPtr hdc, int width, int height)
    {
        // 0. Drag Header Bar (Shown when interactive mode is active)
        if (IsInteractive)
        {
            // Dark Header Background (#181820)
            RECT rcHeader = new RECT { Left = 0, Top = 0, Right = width, Bottom = HeaderHeight };
            IntPtr hHeaderBrush = Native.CreateSolidBrush(RGB(24, 24, 32));
            Native.FillRect(hdc, ref rcHeader, hHeaderBrush);
            Native.DeleteObject(hHeaderBrush);

            // Cyan divider line at bottom of header (#06B6D4)
            IntPtr hDividerPen = Native.CreatePen(PS_SOLID, 1, RGB(6, 182, 212));
            IntPtr hOldPen = Native.SelectObject(hdc, hDividerPen);
            IntPtr hOldBrush = Native.SelectObject(hdc, Native.GetStockObject(HOLLOW_BRUSH));
            Native.Rectangle(hdc, 0, HeaderHeight - 1, width, HeaderHeight);
            Native.SelectObject(hdc, hOldBrush);
            Native.SelectObject(hdc, hOldPen);
            Native.DeleteObject(hDividerPen);

            // Drag handle grip and text
            IntPtr hFont = Native.CreateFontW(
                13, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            IntPtr hOldFont = Native.SelectObject(hdc, hFont);
            Native.SetBkMode(hdc, 1); // TRANSPARENT

            // Title and Grip Icon
            Native.SetTextColor(hdc, RGB(224, 231, 255)); // Soft White/Cyan
            string titleText = "\u283F Inbrisk Ghost  [Taşı / Drag]";
            Native.TextOutW(hdc, 8, 5, titleText, titleText.Length);

            // Interactive Badge
            Native.SetTextColor(hdc, RGB(34, 197, 94)); // Emerald Green
            string badgeText = "● CANLI / INTERACTIVE";
            int badgeX = Math.Max(width - 160, 170);
            Native.TextOutW(hdc, badgeX, 5, badgeText, badgeText.Length);

            Native.SelectObject(hdc, hOldFont);
            Native.DeleteObject(hFont);
        }

        // 1. Sleek subtle border around the outer perimeter
        if (ShowBorder)
        {
            IntPtr hBorderPen = Native.CreatePen(PS_SOLID, 1, RGB(55, 65, 81)); // Modern Slate Gray
            IntPtr hOldPen = Native.SelectObject(hdc, hBorderPen);
            IntPtr hOldBrush = Native.SelectObject(hdc, Native.GetStockObject(HOLLOW_BRUSH));

            Native.Rectangle(hdc, 0, 0, width, height);

            Native.SelectObject(hdc, hOldBrush);
            Native.SelectObject(hdc, hOldPen);
            Native.DeleteObject(hBorderPen);
        }

        // 2. Small glowing green live status indicator dot in top-left corner
        if (ShowLiveIndicator)
        {
            const int dotLeft = 8;
            const int dotTop = 8;
            const int haloSize = 10;
            const int innerSize = 6;

            // Subtle dark contrast halo
            IntPtr hHaloBrush = Native.CreateSolidBrush(RGB(15, 23, 42)); // Dark Slate
            IntPtr hHaloPen = Native.CreatePen(PS_SOLID, 1, RGB(15, 23, 42));
            IntPtr hOldBrush = Native.SelectObject(hdc, hHaloBrush);
            IntPtr hOldPen = Native.SelectObject(hdc, hHaloPen);

            Native.Ellipse(hdc, dotLeft, dotTop, dotLeft + haloSize, dotTop + haloSize);

            // Vibrant Emerald Green live dot
            IntPtr hDotBrush = Native.CreateSolidBrush(RGB(34, 197, 94)); // #22C55E Emerald
            IntPtr hDotPen = Native.CreatePen(PS_SOLID, 1, RGB(22, 163, 74));
            Native.SelectObject(hdc, hDotBrush);
            Native.SelectObject(hdc, hDotPen);

            int innerOffset = (haloSize - innerSize) / 2;
            Native.Ellipse(
                hdc,
                dotLeft + innerOffset,
                dotTop + innerOffset,
                dotLeft + innerOffset + innerSize,
                dotTop + innerOffset + innerSize);

            // Clean up GDI pens and brushes
            Native.SelectObject(hdc, hOldBrush);
            Native.SelectObject(hdc, hOldPen);
            Native.DeleteObject(hHaloBrush);
            Native.DeleteObject(hHaloPen);
            Native.DeleteObject(hDotBrush);
            Native.DeleteObject(hDotPen);
        }
    }

    private void EnsureOffscreenBuffer(IntPtr hdcRef, int width, int height)
    {
        if (_cachedHdcMem != IntPtr.Zero && _cachedWidth == width && _cachedHeight == height)
        {
            return;
        }

        DestroyOffscreenBuffer();

        _cachedHdcMem = Native.CreateCompatibleDC(hdcRef);
        _cachedHbmpMem = Native.CreateCompatibleBitmap(hdcRef, width, height);
        _cachedOldBmp = Native.SelectObject(_cachedHdcMem, _cachedHbmpMem);
        _cachedWidth = width;
        _cachedHeight = height;
    }

    private void DestroyOffscreenBuffer()
    {
        if (_cachedHdcMem != IntPtr.Zero)
        {
            if (_cachedOldBmp != IntPtr.Zero)
            {
                Native.SelectObject(_cachedHdcMem, _cachedOldBmp);
                _cachedOldBmp = IntPtr.Zero;
            }

            if (_cachedHbmpMem != IntPtr.Zero)
            {
                Native.DeleteObject(_cachedHbmpMem);
                _cachedHbmpMem = IntPtr.Zero;
            }

            Native.DeleteDC(_cachedHdcMem);
            _cachedHdcMem = IntPtr.Zero;
        }

        _cachedWidth = 0;
        _cachedHeight = 0;
    }

    #endregion

    #region Shared Memory Connection Management

    private void EnsureSharedMemoryConnected()
    {
        if (_mappedView != IntPtr.Zero)
        {
            return;
        }

        // Try primary mapping name
        _hMap = Native.OpenFileMappingW(FILE_MAP_READ, false, _preferredMapName);

        // Fallback to local namespace if global was denied or not found
        if (_hMap == IntPtr.Zero && _preferredMapName == DefaultMapName)
        {
            _hMap = Native.OpenFileMappingW(FILE_MAP_READ, false, FallbackMapName);
        }

        if (_hMap == IntPtr.Zero)
        {
            return;
        }

        _mappedView = Native.MapViewOfFile(_hMap, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        if (_mappedView == IntPtr.Zero)
        {
            Native.CloseHandle(_hMap);
            _hMap = IntPtr.Zero;
            return;
        }

        // Try to connect to sync event
        string eventName = _preferredMapName.Contains("Local") ? FallbackEventName : DefaultEventName;
        _hFrameEvent = Native.OpenEventW(SYNCHRONIZE, false, eventName);
        if (_hFrameEvent == IntPtr.Zero && eventName == DefaultEventName)
        {
            _hFrameEvent = Native.OpenEventW(SYNCHRONIZE, false, FallbackEventName);
        }
    }

    private void DrawStandbyFrame()
    {
        if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd)) return;

        if (!Native.GetClientRect(_targetHwnd, out var rc)) return;
        int destWidth = rc.Right - rc.Left;
        int destHeight = rc.Bottom - rc.Top;
        if (destWidth <= 2 || destHeight <= 2) return;

        IntPtr hdcWin = Native.GetDC(_targetHwnd);
        if (hdcWin == IntPtr.Zero) return;

        try
        {
            EnsureOffscreenBuffer(hdcWin, destWidth, destHeight);

            // Clean dark acrylic background (#13131A)
            RECT rcFull = new RECT { Left = 0, Top = 0, Right = destWidth, Bottom = destHeight };
            IntPtr hBgBrush = Native.CreateSolidBrush(RGB(19, 19, 26));
            Native.FillRect(_cachedHdcMem, ref rcFull, hBgBrush);
            Native.DeleteObject(hBgBrush);

            int headerOffset = IsInteractive ? HeaderHeight : 0;

            // Draw center graphic / standby text
            IntPtr hFontTitle = Native.CreateFontW(14, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            IntPtr hFontSub = Native.CreateFontW(11, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            IntPtr hOldFont = Native.SelectObject(_cachedHdcMem, hFontTitle);
            Native.SetBkMode(_cachedHdcMem, 1); // TRANSPARENT

            int centerY = headerOffset + (destHeight - headerOffset) / 2;

            // Title
            Native.SetTextColor(_cachedHdcMem, RGB(6, 182, 212)); // Cyan
            string title = "INBRISK GHOST OS";
            Native.TextOutW(_cachedHdcMem, Math.Max(12, (destWidth - title.Length * 8) / 2), Math.Max(headerOffset + 10, centerY - 24), title, title.Length);

            // Subtitle
            Native.SelectObject(_cachedHdcMem, hFontSub);
            Native.SetTextColor(_cachedHdcMem, RGB(148, 163, 184)); // Slate 400
            string sub = "Masaustu goruntusu baslatiliyor...";
            Native.TextOutW(_cachedHdcMem, Math.Max(10, (destWidth - sub.Length * 6) / 2), Math.Max(headerOffset + 32, centerY + 2), sub, sub.Length);

            Native.SelectObject(_cachedHdcMem, hOldFont);
            Native.DeleteObject(hFontTitle);
            Native.DeleteObject(hFontSub);

            // Modern UI Overlay (Header + Border)
            DrawModernUiOverlay(_cachedHdcMem, destWidth, destHeight);

            // Blit to window
            Native.BitBlt(hdcWin, 0, 0, destWidth, destHeight, _cachedHdcMem, 0, 0, SRCCOPY);
        }
        catch { }
        finally
        {
            Native.ReleaseDC(_targetHwnd, hdcWin);
        }
    }

    private void DisconnectSharedMemory()
    {
        if (_mappedView != IntPtr.Zero)
        {
            Native.UnmapViewOfFile(_mappedView);
            _mappedView = IntPtr.Zero;
        }

        if (_hMap != IntPtr.Zero)
        {
            Native.CloseHandle(_hMap);
            _hMap = IntPtr.Zero;
        }

        if (_hFrameEvent != IntPtr.Zero)
        {
            Native.CloseHandle(_hFrameEvent);
            _hFrameEvent = IntPtr.Zero;
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops rendering, releases all unmanaged Win32 and GDI resources, and detaches shared memory views.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        StopRendering();
        DisconnectSharedMemory();
        DestroyOffscreenBuffer();
        GC.SuppressFinalize(this);
    }

    #endregion
}
