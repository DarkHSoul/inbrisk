using System;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Inbrisk.Platform.Windows;

/// <summary>
/// Direct GPU VRAM screen capture service leveraging Windows DXGI Desktop Duplication API
/// (IDXGIOutputDuplication) and Direct3D 11.
/// Provides zero-copy GPU captures, non-blocking / zero-CPU idle wait timeouts,
/// automatic session-loss recovery (re-init on lock/UAC/resolution switch),
/// and clean COM lifecycle management.
/// </summary>
public sealed class GhostDxgiCapture : IDisposable
{
    #region Win32 / DXGI Constants & COM GUIDs

    public const int S_OK = 0;
    public const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    public const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
    public const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A0033);
    public const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);
    public const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);

    private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    private static readonly Guid IID_IDXGIOutputDuplication = new("191cfac3-a341-470d-b26e-a864f428319c");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    #endregion

    #region DXGI Structs

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplFrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DxgiPoint PointerPosition;
        public int PointerPositionVisible;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    #endregion

    private readonly object _syncRoot = new();
    private readonly int _adapterIndex;
    private readonly int _outputIndex;

    private ID3D11Device? _d3dDevice;
    private ID3D11DeviceContext? _d3dContext;
    private IDXGIAdapter? _adapter;
    private IDXGIOutput? _output;
    private IntPtr _pOutputDuplication = IntPtr.Zero;

    private ID3D11Texture2D? _stagingTexture;
    private uint _stagingWidth;
    private uint _stagingHeight;
    private bool _isMapped;
    private IntPtr _currentDesktopResource = IntPtr.Zero;
    private bool _hasAcquiredFrame;

    private bool _isInitialized;
    private bool _disposed;
    private string? _lastError;

    public bool IsInitialized => _isInitialized && !_disposed;
    public int AdapterIndex => _adapterIndex;
    public int OutputIndex => _outputIndex;
    public string? LastError => _lastError;

    public RectPx DesktopBounds
    {
        get
        {
            lock (_syncRoot)
            {
                if (_output != null)
                {
                    try
                    {
                        var desc = _output.GetDesc();
                        return new RectPx(
                            desc.DesktopCoordinates.left,
                            desc.DesktopCoordinates.top,
                            desc.DesktopCoordinates.right - desc.DesktopCoordinates.left,
                            desc.DesktopCoordinates.bottom - desc.DesktopCoordinates.top);
                    }
                    catch { }
                }

                return new RectPx(0, 0, (int)_stagingWidth, (int)_stagingHeight);
            }
        }
    }

    public int Width => DesktopBounds.Width;
    public int Height => DesktopBounds.Height;

    public GhostDxgiCapture(int adapterIndex = 0, int outputIndex = 0, bool autoInit = true)
    {
        _adapterIndex = adapterIndex;
        _outputIndex = outputIndex;
        if (autoInit)
        {
            TryInitialize();
        }
    }

    ~GhostDxgiCapture()
    {
        DisposeInternal();
    }

    /// <summary>
    /// Checks whether DXGI desktop duplication can be initialized on the default display.
    /// </summary>
    public static bool IsSupported()
    {
        try
        {
            using var cap = new GhostDxgiCapture(0, 0, autoInit: true);
            return cap.IsInitialized;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Explicitly initializes the D3D11 device and DXGI Output Duplication session.
    /// Throws InvalidOperationException if initialization fails.
    /// </summary>
    public void Initialize()
    {
        if (!TryInitialize())
        {
            throw new InvalidOperationException($"Failed to initialize DXGI Desktop Duplication: {_lastError}");
        }
    }

    /// <summary>
    /// Attempts to initialize the D3D11 device, queries IDXGIOutput1, and calls DuplicateOutput.
    /// Safe to call multiple times or during re-initialization cycles.
    /// </summary>
    public bool TryInitialize()
    {
        lock (_syncRoot)
        {
            if (_disposed) return false;

            TeardownDuplication();

            try
            {
                // 1. Initialize Direct3D 11 Device & Context
                if (_d3dDevice == null || _d3dContext == null)
                {
                    var hr = PInvoke.D3D11CreateDevice(
                        null!,
                        D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
                        default,
                        D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                        ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
                        7, // D3D11_SDK_VERSION
                        out _d3dDevice,
                        out _,
                        out _d3dContext);

                    // Fallback to WARP if hardware adapter creation failed
                    if (hr < 0)
                    {
                        hr = PInvoke.D3D11CreateDevice(
                            null!,
                            D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
                            default,
                            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty,
                            7,
                            out _d3dDevice,
                            out _,
                            out _d3dContext);
                    }

                    if (hr < 0 || _d3dDevice == null || _d3dContext == null)
                    {
                        _lastError = $"D3D11CreateDevice failed with HR 0x{hr:X8}";
                        return false;
                    }
                }

                // 2. Query DXGI Device -> Adapter -> Output
                var dxgiDevice = (IDXGIDevice)_d3dDevice;
                dxgiDevice.GetAdapter(out _adapter);
                if (_adapter == null)
                {
                    _lastError = "IDXGIDevice.GetAdapter returned null";
                    return false;
                }

                var hrEnum = _adapter.EnumOutputs((uint)_outputIndex, out _output);
                if (hrEnum.Value < 0 || _output == null)
                {
                    _lastError = $"EnumOutputs({_outputIndex}) failed with HR 0x{hrEnum.Value:X8}";
                    return false;
                }

                // 3. Query IDXGIOutput1 and call DuplicateOutput
                unsafe
                {
                    var pOutput = Marshal.GetIUnknownForObject(_output);
                    try
                    {
                        var iidOutput1 = IID_IDXGIOutput1;
                        int hrQI = Marshal.QueryInterface(pOutput, ref iidOutput1, out IntPtr pOutput1);
                        if (hrQI < 0 || pOutput1 == IntPtr.Zero)
                        {
                            _lastError = $"QueryInterface IDXGIOutput1 failed with HR 0x{hrQI:X8}";
                            return false;
                        }

                        try
                        {
                            var pDeviceUnk = Marshal.GetIUnknownForObject(_d3dDevice);
                            try
                            {
                                // IDXGIOutput1::DuplicateOutput is slot 22 in IDXGIOutput1Vtbl
                                IntPtr* vtbl = *(IntPtr**)pOutput1;
                                var duplicateOutputFunc = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, out IntPtr, int>)vtbl[22];
                                int hrDup = duplicateOutputFunc(pOutput1, pDeviceUnk, out _pOutputDuplication);
                                if (hrDup < 0 || _pOutputDuplication == IntPtr.Zero)
                                {
                                    _lastError = $"DuplicateOutput failed with HR 0x{hrDup:X8}";
                                    return false;
                                }
                            }
                            finally
                            {
                                Marshal.Release(pDeviceUnk);
                            }
                        }
                        finally
                        {
                            Marshal.Release(pOutput1);
                        }
                    }
                    finally
                    {
                        Marshal.Release(pOutput);
                    }
                }

                _isInitialized = true;
                _lastError = null;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = $"Initialization exception: {ex.Message}";
                TeardownDuplication();
                return false;
            }
        }
    }

    /// <summary>
    /// Reinitializes the capture pipeline. Called automatically upon access loss (session lock, resolution change).
    /// </summary>
    public void Reinitialize()
    {
        lock (_syncRoot)
        {
            TryInitialize();
        }
    }

    /// <summary>
    /// Acquires the next desktop frame from the GPU duplication stream.
    /// If no new frame arrives within <paramref name="timeoutMs"/> (DXGI_ERROR_WAIT_TIMEOUT),
    /// blocks with zero CPU cost and returns false.
    /// If access is lost (DXGI_ERROR_ACCESS_LOST), automatically recovers by reinitializing.
    /// If successful, returns true and provides a CPU-mapped pointer <paramref name="pSurface"/>,
    /// image dimensions, and stride. Call <see cref="ReleaseFrame"/> when done processing the frame.
    /// </summary>
    public bool AcquireNextFrame(int timeoutMs, out IntPtr pSurface, out int width, out int height, out int stride)
    {
        pSurface = IntPtr.Zero;
        width = 0;
        height = 0;
        stride = 0;

        lock (_syncRoot)
        {
            if (_disposed) return false;

            if (!_isInitialized)
            {
                if (!TryInitialize())
                    return false;
            }

            // If a previous frame was not explicitly released, release it now
            if (_hasAcquiredFrame)
            {
                ReleaseFrame();
            }

            return TryAcquireInternal(timeoutMs, out pSurface, out width, out height, out stride, isRetry: false);
        }
    }

    /// <summary>
    /// Overload that copies pixels to a managed byte array and automatically releases the frame.
    /// </summary>
    public bool AcquireNextFrame(int timeoutMs, out byte[]? buffer, out int width, out int height, out int stride)
    {
        buffer = null;
        if (!AcquireNextFrame(timeoutMs, out IntPtr pSurface, out width, out height, out stride))
        {
            return false;
        }

        try
        {
            buffer = new byte[height * stride];
            Marshal.Copy(pSurface, buffer, 0, buffer.Length);
            return true;
        }
        finally
        {
            ReleaseFrame();
        }
    }

    /// <summary>
    /// Captures a one-shot <see cref="RawFrame"/> (BGRA32) suitable for Inbrisk pipelines.
    /// </summary>
    public RawFrame? CaptureRawFrame(int timeoutMs = 100)
    {
        if (!AcquireNextFrame(timeoutMs, out IntPtr pSurface, out int width, out int height, out int stride))
            return null;

        try
        {
            var bytes = new byte[width * 4 * height];
            unsafe
            {
                var src = (byte*)pSurface;
                for (int row = 0; row < height; row++)
                {
                    Marshal.Copy((IntPtr)(src + row * stride), bytes, row * width * 4, width * 4);
                }
            }

            var bounds = DesktopBounds;
            var transform = new FrameTransform(
                bounds.Width > 0 && bounds.Height > 0 ? bounds : new RectPx(0, 0, width, height),
                width, height, null);
            return new RawFrame(bytes, width, height, width * 4, transform, DateTimeOffset.Now);
        }
        finally
        {
            ReleaseFrame();
        }
    }

    private unsafe bool TryAcquireInternal(
        int timeoutMs,
        out IntPtr pSurface,
        out int width,
        out int height,
        out int stride,
        bool isRetry)
    {
        pSurface = IntPtr.Zero;
        width = 0;
        height = 0;
        stride = 0;

        if (_pOutputDuplication == IntPtr.Zero || _d3dDevice == null || _d3dContext == null)
            return false;

        IntPtr* vtbl = *(IntPtr**)_pOutputDuplication;
        // IDXGIOutputDuplication::AcquireNextFrame is slot 8
        var acquireFunc = (delegate* unmanaged[Stdcall]<IntPtr, uint, DxgiOutduplFrameInfo*, out IntPtr, int>)vtbl[8];

        DxgiOutduplFrameInfo frameInfo = default;
        IntPtr pDesktopResource = IntPtr.Zero;

        int hr = acquireFunc(_pOutputDuplication, (uint)Math.Max(0, timeoutMs), &frameInfo, out pDesktopResource);

        // 1. Wait timeout: screen hasn't changed, zero CPU spent
        if (hr == DXGI_ERROR_WAIT_TIMEOUT)
        {
            return false;
        }

        // 2. Access lost or session changed: lock screen, UAC elevation, resolution change
        if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_ACCESS_DENIED || hr == DXGI_ERROR_INVALID_CALL)
        {
            _lastError = $"DXGI frame error 0x{hr:X8}; triggering automatic re-initialization";
            TeardownDuplication();

            if (!isRetry && TryInitialize())
            {
                return TryAcquireInternal(timeoutMs, out pSurface, out width, out height, out stride, isRetry: true);
            }

            return false;
        }

        if (hr < 0 || pDesktopResource == IntPtr.Zero)
        {
            _lastError = $"AcquireNextFrame failed with HR 0x{hr:X8}";
            return false;
        }

        // 3. Frame acquired: Query ID3D11Texture2D
        try
        {
            var iidTex2D = IID_ID3D11Texture2D;
            int qihr = Marshal.QueryInterface(pDesktopResource, ref iidTex2D, out IntPtr pTex);
            if (qihr < 0 || pTex == IntPtr.Zero)
            {
                _lastError = $"QueryInterface ID3D11Texture2D failed with HR 0x{qihr:X8}";
                Marshal.Release(pDesktopResource);
                // Slot 14 is ReleaseFrame
                var releaseFrameFunc = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[14];
                releaseFrameFunc(_pOutputDuplication);
                return false;
            }

            ID3D11Texture2D acquiredTex;
            try
            {
                acquiredTex = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(pTex);
            }
            finally
            {
                Marshal.Release(pTex);
            }

            var desc = new D3D11_TEXTURE2D_DESC();
            acquiredTex.GetDesc(&desc);
            uint w = desc.Width;
            uint h = desc.Height;

            EnsureStagingTexture(w, h, desc.Format);

            // Copy from GPU duplicated texture to CPU staging texture
            _d3dContext.CopyResource((ID3D11Resource)(object)_stagingTexture!, (ID3D11Resource)(object)acquiredTex);

            // Map CPU staging texture
            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            _d3dContext.Map((ID3D11Resource)(object)_stagingTexture!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            _isMapped = true;
            _currentDesktopResource = pDesktopResource;
            _hasAcquiredFrame = true;

            pSurface = (IntPtr)mapped.pData;
            width = (int)w;
            height = (int)h;
            stride = (int)mapped.RowPitch;
            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Frame acquisition error: {ex.Message}";
            ReleaseFrame();
            return false;
        }
    }

    private unsafe void EnsureStagingTexture(uint w, uint h, DXGI_FORMAT format)
    {
        if (_stagingTexture != null && _stagingWidth == w && _stagingHeight == h)
            return;

        if (_stagingTexture != null)
        {
            try { Marshal.ReleaseComObject(_stagingTexture); } catch { }
            _stagingTexture = null;
        }

        var stagingDesc = new D3D11_TEXTURE2D_DESC
        {
            Width = w,
            Height = h,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        ID3D11Texture2D_unmanaged* stgUnmanaged;
        _d3dDevice!.CreateTexture2D(&stagingDesc, null, &stgUnmanaged);
        var stgPtr = (IntPtr)stgUnmanaged;
        _stagingTexture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(stgPtr);
        Marshal.Release(stgPtr);

        _stagingWidth = w;
        _stagingHeight = h;
    }

    /// <summary>
    /// Releases the currently mapped frame, unmaps the staging surface, and invokes
    /// IDXGIOutputDuplication::ReleaseFrame.
    /// </summary>
    public void ReleaseFrame()
    {
        lock (_syncRoot)
        {
            if (_isMapped)
            {
                if (_stagingTexture != null && _d3dContext != null)
                {
                    try
                    {
                        _d3dContext.Unmap((ID3D11Resource)(object)_stagingTexture, 0);
                    }
                    catch { }
                }
                _isMapped = false;
            }

            if (_currentDesktopResource != IntPtr.Zero)
            {
                try
                {
                    Marshal.Release(_currentDesktopResource);
                }
                catch { }
                _currentDesktopResource = IntPtr.Zero;
            }

            if (_hasAcquiredFrame && _pOutputDuplication != IntPtr.Zero)
            {
                try
                {
                    unsafe
                    {
                        IntPtr* vtbl = *(IntPtr**)_pOutputDuplication;
                        // Slot 14 is ReleaseFrame
                        var releaseFrameFunc = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[14];
                        releaseFrameFunc(_pOutputDuplication);
                    }
                }
                catch { }
                _hasAcquiredFrame = false;
            }
        }
    }

    private void TeardownDuplication()
    {
        ReleaseFrame();

        if (_stagingTexture != null)
        {
            try { Marshal.ReleaseComObject(_stagingTexture); } catch { }
            _stagingTexture = null;
            _stagingWidth = 0;
            _stagingHeight = 0;
        }

        if (_pOutputDuplication != IntPtr.Zero)
        {
            try { Marshal.Release(_pOutputDuplication); } catch { }
            _pOutputDuplication = IntPtr.Zero;
        }

        if (_output != null)
        {
            try { Marshal.ReleaseComObject(_output); } catch { }
            _output = null;
        }

        if (_adapter != null)
        {
            try { Marshal.ReleaseComObject(_adapter); } catch { }
            _adapter = null;
        }

        _isInitialized = false;
    }

    private void DisposeInternal()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            _disposed = true;

            TeardownDuplication();

            if (_d3dContext != null)
            {
                try { Marshal.ReleaseComObject(_d3dContext); } catch { }
                _d3dContext = null;
            }

            if (_d3dDevice != null)
            {
                try { Marshal.ReleaseComObject(_d3dDevice); } catch { }
                _d3dDevice = null;
            }
        }
    }

    public void Dispose()
    {
        DisposeInternal();
        GC.SuppressFinalize(this);
    }
}
