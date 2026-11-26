using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// High-speed shared memory writer writing desktop frames directly into the cross-session
/// Inbrisk double-buffered shared memory layout with a restricted DACL.
/// Binary-compatible with <c>GhostSharedFrameBuffer</c> and <c>GhostPipRenderer</c>.
/// </summary>
internal sealed class NativeGhostFrameBufferWriter : IDisposable
{
    private const uint DefaultMagic = 0x49424642; // "IBFB"
    private const int PageAlignment = 4096;
    private const uint PAGE_READWRITE = 0x04;
    private const uint FILE_MAP_ALL_ACCESS = 0x000F001F;
    private const uint SECURITY_DESCRIPTOR_REVISION = 1;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeSecurityDescriptor(IntPtr pSecurityDescriptor, uint dwRevision);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSecurityDescriptorDacl(IntPtr pSecurityDescriptor, [MarshalAs(UnmanagedType.Bool)] bool bDaclPresent, IntPtr pDacl, [MarshalAs(UnmanagedType.Bool)] bool bDaclDefaulted);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor, uint stringSdRevision, out IntPtr securityDescriptor, out int securityDescriptorSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileMappingW(IntPtr hFile, ref SECURITY_ATTRIBUTES lpFileMappingAttributes, uint flProtect, uint dwMaximumSizeHigh, uint dwMaximumSizeLow, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES lpEventAttributes, [MarshalAs(UnmanagedType.Bool)] bool bManualReset, [MarshalAs(UnmanagedType.Bool)] bool bInitialState, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr hEvent);

    private readonly string _mapName;
    private readonly string _eventName;
    private readonly long _slotCapacity;
    private readonly long _slot0Offset;
    private readonly long _slot1Offset;

    private IntPtr _hMap = IntPtr.Zero;
    private IntPtr _mappedView = IntPtr.Zero;
    private IntPtr _hEvent = IntPtr.Zero;
    private long _frameIndex;
    private bool _disposed;

    public bool IsInitialized => _mappedView != IntPtr.Zero && !_disposed;

    public NativeGhostFrameBufferWriter(string mapName, string eventName, int maxSlotSize = 3840 * 2160 * 4)
    {
        _mapName = mapName;
        _eventName = eventName;
        _slotCapacity = maxSlotSize;

        // SlotControlHeader size is 64 bytes (Magic, Width, Height, Stride, FrameIndex, TimestampTicks, WriteSequence, DataLength, Reserved1, Reserved2)
        const long slotHeaderSize = 64;
        long alignedSlotSpan = AlignUp(slotHeaderSize + maxSlotSize, PageAlignment);
        _slot0Offset = PageAlignment;
        _slot1Offset = _slot0Offset + alignedSlotSpan;
        long totalMappingSize = _slot1Offset + alignedSlotSpan;

        InitSharedMemory(totalMappingSize);
    }

    /// <summary>Restricted cross-session DACL matching
    /// <c>GhostSharedFrameBuffer</c>: full control SYSTEM/Admins, read-write
    /// for the creating user, read-only for interactive users (other-session
    /// consumers). A NULL DACL let any local process read/corrupt frames.</summary>
    private static IntPtr BuildRestrictedSd()
    {
        var sddl = new System.Text.StringBuilder("D:(A;;GA;;;SY)(A;;GA;;;BA)");
        try
        {
            var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrEmpty(userSid))
            {
                sddl.Append("(A;;GRGW;;;").Append(userSid).Append(')');
            }
        }
        catch { }
        sddl.Append("(A;;GR;;;IU)");

        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                sddl.ToString(), SECURITY_DESCRIPTOR_REVISION, out IntPtr pSd, out _))
        {
            return IntPtr.Zero;
        }
        return pSd;
    }

    private void InitSharedMemory(long totalSize)
    {
        IntPtr pSd = BuildRestrictedSd();
        try
        {
            // Fail closed: without a DACL we would create a world-accessible
            // mapping — rather run with default object security than none.
            var sa = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = pSd,
                bInheritHandle = false
            };

            uint sizeHigh = (uint)(totalSize >> 32);
            uint sizeLow = (uint)(totalSize & 0xFFFFFFFF);

            _hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, ref sa, PAGE_READWRITE, sizeHigh, sizeLow, _mapName);
            if (_hMap == IntPtr.Zero)
            {
                // Fallback to local namespace if Global requires elevated admin
                string localName = _mapName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
                    ? @"Local\" + _mapName.Substring(7)
                    : _mapName;
                _hMap = CreateFileMappingW(INVALID_HANDLE_VALUE, ref sa, PAGE_READWRITE, sizeHigh, sizeLow, localName);
            }

            if (_hMap != IntPtr.Zero)
            {
                _mappedView = MapViewOfFile(_hMap, FILE_MAP_ALL_ACCESS, 0, 0, UIntPtr.Zero);
                if (_mappedView != IntPtr.Zero)
                {
                    Marshal.WriteInt32(_mappedView, 0, unchecked((int)DefaultMagic));
                    Marshal.WriteInt32(_mappedView, 4, 1); // Version
                    Marshal.WriteInt32(_mappedView, 8, 2); // SlotCount
                    Marshal.WriteInt32(_mappedView, 12, -1); // ActiveSlotIndex
                    Marshal.WriteInt64(_mappedView, 16, _slotCapacity);
                    Marshal.WriteInt64(_mappedView, 24, _slot0Offset);
                    Marshal.WriteInt64(_mappedView, 32, _slot1Offset);
                    Marshal.WriteInt64(_mappedView, 40, 0); // TotalFramesWritten
                }
            }

            // Frame ready event
            _hEvent = CreateEventW(ref sa, false, false, _eventName);
            if (_hEvent == IntPtr.Zero && _eventName.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase))
            {
                string localEvt = @"Local\" + _eventName.Substring(7);
                _hEvent = CreateEventW(ref sa, false, false, localEvt);
            }
        }
        finally
        {
            if (pSd != IntPtr.Zero)
            {
                LocalFree(pSd);
            }
        }
    }

    public void WriteFrame(int width, int height, int stride, byte[] pixelData)
    {
        if (_disposed || _mappedView == IntPtr.Zero || pixelData == null || pixelData.Length == 0) return;
        if (pixelData.Length > _slotCapacity) return;

        // Double buffer slot selection: inactive slot
        int currentActive = Marshal.ReadInt32(_mappedView, 12);
        int targetSlot = (currentActive == 0) ? 1 : 0;

        long slotOffset = (targetSlot == 0) ? _slot0Offset : _slot1Offset;
        IntPtr slotBase = IntPtr.Add(_mappedView, (int)slotOffset);

        // Seqlock write start: sequence increment to odd
        long seq = Marshal.ReadInt64(slotBase, 32);
        Marshal.WriteInt64(slotBase, 32, seq + 1);
        Thread.MemoryBarrier();

        // Memory copy to slot pixels (offset 64 bytes after SlotControlHeader)
        IntPtr destPixels = IntPtr.Add(slotBase, 64);
        Marshal.Copy(pixelData, 0, destPixels, pixelData.Length);

        // Populate slot header
        Marshal.WriteInt32(slotBase, 0, unchecked((int)DefaultMagic));
        Marshal.WriteInt32(slotBase, 4, width);
        Marshal.WriteInt32(slotBase, 8, height);
        Marshal.WriteInt32(slotBase, 12, stride);
        Marshal.WriteInt64(slotBase, 16, Interlocked.Increment(ref _frameIndex));
        Marshal.WriteInt64(slotBase, 24, DateTime.UtcNow.Ticks);
        Marshal.WriteInt64(slotBase, 40, pixelData.Length);

        // Seqlock write complete: sequence increment to even
        Thread.MemoryBarrier();
        Marshal.WriteInt64(slotBase, 32, seq + 2);
        Thread.MemoryBarrier();

        // Commit active slot
        Marshal.WriteInt32(_mappedView, 12, targetSlot);
        long total = Marshal.ReadInt64(_mappedView, 40);
        Marshal.WriteInt64(_mappedView, 40, total + 1);

        // Signal consumer event
        if (_hEvent != IntPtr.Zero)
        {
            SetEvent(_hEvent);
        }
    }

    private static long AlignUp(long val, int align) => (val + (align - 1)) & ~(align - 1);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_mappedView != IntPtr.Zero)
        {
            UnmapViewOfFile(_mappedView);
            _mappedView = IntPtr.Zero;
        }

        if (_hMap != IntPtr.Zero)
        {
            CloseHandle(_hMap);
            _hMap = IntPtr.Zero;
        }

        if (_hEvent != IntPtr.Zero)
        {
            CloseHandle(_hEvent);
            _hEvent = IntPtr.Zero;
        }
    }
}



internal interface IGhostCaptureEngine : IDisposable
{
    bool IsInitialized { get; }
    int Width { get; }
    int Height { get; }
    bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride);
    void ReleaseFrame();
}

/// <summary>
/// Dynamically resolves and drives <c>GhostDxgiCapture</c> from Inbrisk.Platform.Windows.
/// </summary>
internal sealed class ReflectionDxgiCaptureEngine : IGhostCaptureEngine
{
    private readonly object _captureInstance;
    private readonly MethodInfo _acquireMethod;
    private readonly MethodInfo _releaseMethod;
    private readonly PropertyInfo _isInitProp;
    private readonly PropertyInfo _widthProp;
    private readonly PropertyInfo _heightProp;

    public bool IsInitialized => (bool)(_isInitProp.GetValue(_captureInstance) ?? false);
    public int Width => (int)(_widthProp.GetValue(_captureInstance) ?? 1920);
    public int Height => (int)(_heightProp.GetValue(_captureInstance) ?? 1080);

    private ReflectionDxgiCaptureEngine(object instance, Type type)
    {
        _captureInstance = instance;
        _acquireMethod = type.GetMethod("AcquireNextFrame", new[]
        {
            typeof(int),
            typeof(byte[]).MakeByRefType(),
            typeof(int).MakeByRefType(),
            typeof(int).MakeByRefType(),
            typeof(int).MakeByRefType()
        }) ?? throw new MissingMethodException(type.FullName, "AcquireNextFrame(int, out byte[], out int, out int, out int)");

        _releaseMethod = type.GetMethod("ReleaseFrame", Type.EmptyTypes) ??
                         throw new MissingMethodException(type.FullName, "ReleaseFrame()");

        _isInitProp = type.GetProperty("IsInitialized") ??
                      throw new MissingMemberException(type.FullName, "IsInitialized");
        _widthProp = type.GetProperty("Width") ??
                     throw new MissingMemberException(type.FullName, "Width");
        _heightProp = type.GetProperty("Height") ??
                      throw new MissingMemberException(type.FullName, "Height");
    }

    public static ReflectionDxgiCaptureEngine? TryCreate(int adapterIndex = 0, int outputIndex = 0)
    {
        try
        {
            var type = ResolvePlatformType("GhostDxgiCapture");
            if (type == null) return null;

            var ctor = type.GetConstructor(new[] { typeof(int), typeof(int), typeof(bool) });
            if (ctor == null) return null;

            var instance = ctor.Invoke(new object[] { adapterIndex, outputIndex, true });
            var engine = new ReflectionDxgiCaptureEngine(instance, type);
            if (engine.IsInitialized)
            {
                return engine;
            }
            engine.Dispose();
        }
        catch { }

        return null;
    }

    public bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride)
    {
        object?[] args = new object?[] { timeoutMs, null, 0, 0, 0 };
        bool ok = (bool)(_acquireMethod.Invoke(_captureInstance, args) ?? false);
        if (ok)
        {
            buffer = (byte[]?)args[1];
            width = (int)(args[2] ?? 0);
            height = (int)(args[3] ?? 0);
            stride = (int)(args[4] ?? 0);
            return true;
        }

        buffer = null;
        width = 0;
        height = 0;
        stride = 0;
        return false;
    }

    public void ReleaseFrame()
    {
        try
        {
            _releaseMethod.Invoke(_captureInstance, null);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_captureInstance is IDisposable d)
        {
            d.Dispose();
        }
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
}

/// <summary>
/// Native GDI desktop capture fallback when DXGI desktop duplication is unavailable
/// (e.g. headless loopback session initial bootstrap, locked desktop, or detached GPU driver).
/// </summary>
internal sealed class GdiDesktopCaptureEngine : IGhostCaptureEngine
{
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint uStartScan, uint cScanLines, [Out] byte[] lpvBits, ref BITMAPINFO lpbi, uint uUsage);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint DIB_RGB_COLORS = 0;

    private int _width;
    private int _height;
    private bool _disposed;

    public bool IsInitialized => !_disposed && _width > 0 && _height > 0;
    public int Width => _width;
    public int Height => _height;

    public GdiDesktopCaptureEngine(int defaultWidth = 1920, int defaultHeight = 1080)
    {
        try
        {
            _width = GetSystemMetrics(SM_CXSCREEN);
            _height = GetSystemMetrics(SM_CYSCREEN);
        }
        catch { }

        if (_width <= 0) _width = defaultWidth;
        if (_height <= 0) _height = defaultHeight;
    }

    public bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride)
    {
        width = _width;
        height = _height;
        stride = width * 4;
        buffer = null;

        if (_disposed) return false;

        IntPtr hDesktop = GetDesktopWindow();
        IntPtr hdcScreen = GetDC(hDesktop);
        if (hdcScreen == IntPtr.Zero) return false;

        IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
        IntPtr hBitmap = CreateCompatibleBitmap(hdcScreen, width, height);
        IntPtr hOld = SelectObject(hdcMem, hBitmap);

        try
        {
            BitBlt(hdcMem, 0, 0, width, height, hdcScreen, 0, 0, SRCCOPY);

            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            buffer = new byte[height * stride];
            GetDIBits(hdcMem, hBitmap, 0, (uint)height, buffer, ref bmi, DIB_RGB_COLORS);
            return true;
        }
        finally
        {
            SelectObject(hdcMem, hOld);
            DeleteObject(hBitmap);
            DeleteDC(hdcMem);
            ReleaseDC(hDesktop, hdcScreen);
        }
    }

    public void ReleaseFrame() { }

    public void Dispose()
    {
        _disposed = true;
    }
}



/// <summary>
/// Session 2 Ghost Worker Service (running inside InbriskAgent desktop session).
