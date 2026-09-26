using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using WinRT;

namespace Inbrisk.Platform.Windows.Capture;

/// <summary>
/// Continuous capture session backed by Windows Graphics Capture
/// (Direct3D11CaptureFramePool.CreateFreeThreaded — frames arrive on the pool's
/// own thread only when the target repaints, so idle costs ~nothing).
/// Window/monitor targets are native; region targets capture the covering
/// monitor and crop in desktop space.
/// </summary>
internal sealed class WgcSession : ICaptureSession
{
    private readonly IWindowService _windows;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly ID3D11Device _d3dDevice;
    private readonly ID3D11DeviceContext _d3dContext;
    private readonly RectPx? _regionCrop;   // desktop-space crop for Region targets
    private readonly Func<RectPx> _sourceRectProvider; // desktop rect the item covers
    private readonly object _d3dLock = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private ID3D11Texture2D? _staging;
    private uint _stagingW, _stagingH;
    private RawFrame? _latest;
    private long _seq, _arrived, _emitted;
    private double _fpsEma;
    private DateTimeOffset _lastEmit = DateTimeOffset.MinValue;
    private volatile bool _running;
    private Timer? _watchdog;
    private bool _disposed;

    public CaptureTarget Target { get; }
    public CaptureBackendKind Backend => CaptureBackendKind.WindowsGraphicsCapture;
    public bool IsRunning => _running;
    public RawFrame? LatestFrame => _latest;
    public event Action<RawFrame>? FrameReceived;

    private WgcSession(CaptureTarget target, GraphicsCaptureItem item,
        IDirect3DDevice device, ID3D11Device d3d, ID3D11DeviceContext ctx,
        IWindowService windows, RectPx? regionCrop, Func<RectPx> srcProvider)
    {
        Target = target;
        _item = item;
        _device = device;
        _d3dDevice = d3d;
        _d3dContext = ctx;
        _windows = windows;
        _regionCrop = regionCrop;
        _sourceRectProvider = srcProvider;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _item.Closed += (_, _) => Stop();
        try { _session.IsCursorCaptureEnabled = false; } catch { /* older OS */ }
        // belt & suspenders: item.Closed can be unreliable for HWND items —
        // poll the handle too so a destroyed window always ends the session
        if (target is CaptureTarget.Window wt)
            _watchdog = new Timer(_ =>
            {
                if (_running && !NativeMethods.IsWindow(new IntPtr(wt.Hwnd))) Stop();
            }, null, 1000, 1000);
        LastInitError = null;
    }

    /// <summary>Last WGC init failure reason — diagnostics for the fallback path.</summary>
    public static string? LastInitError { get; private set; }
    /// <summary>Last frame-pipeline failure — diagnostics.</summary>
    public static string? LastFrameError { get; private set; }

    public static WgcSession? TryCreate(CaptureTarget target, IWindowService windows)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            LastInitError = "GraphicsCaptureSession.IsSupported() = false";
            return null;
        }
        try
        {
            var device = WgcInterop.CreateDevice(out var d3d, out var ctx);
            switch (target)
            {
                case CaptureTarget.Window w:
                {
                    var info = windows.GetWindow(w.Hwnd)
                        ?? throw new InbriskException(ErrorCode.NotFound, $"window 0x{w.Hwnd:X}");
                    var item = WgcInterop.ItemForWindow(w.Hwnd);
                    // WGC frame size == visible window area (DWM extended bounds),
                    // not GetWindowRect — resolve per-frame so moves/resizes track.
                    return new WgcSession(target, item, device, d3d, ctx, windows, null,
                        () => VisibleBounds(windows, w.Hwnd) ?? info.Bounds);
                }
                case CaptureTarget.Monitor m:
                {
                    var mons = windows.GetMonitors();
                    var mon = m.Index < mons.Count ? mons[m.Index] : mons[0];
                    var item = WgcInterop.ItemForMonitor(MonitorHandle(mon));
                    return new WgcSession(target, item, device, d3d, ctx, windows, null,
                        () => mon.Bounds);
                }
                case CaptureTarget.Region r:
                {
                    // cover with the intersecting monitor, crop after readback
                    var mons = windows.GetMonitors();
                    var mon = mons.FirstOrDefault(x => x.Bounds.Intersects(r.Rect)) ?? mons[0];
                    var crop = r.Rect.Intersect(mon.Bounds);
                    if (crop.IsEmpty) throw new InbriskException(ErrorCode.NotFound, "region off-screen");
                    var item = WgcInterop.ItemForMonitor(MonitorHandle(mon));
                    return new WgcSession(target, item, device, d3d, ctx, windows, crop,
                        () => mon.Bounds);
                }
                default:
                    return null;
            }
        }
        catch (InbriskException) { throw; }
        catch (Exception e) { LastInitError = $"{e.GetType().Name}: {e.Message}"; return null; }
    }

    private static RectPx? VisibleBounds(IWindowService windows, long hwnd) =>
        windows is WindowService ws ? ws.VisibleBounds(hwnd) : windows.GetWindow(hwnd)?.Bounds;

    private static long MonitorHandle(MonitorInfo m) =>
        (long)NativeMethods.MonitorFromPoint(
            new POINT { X = m.Bounds.X + 1, Y = m.Bounds.Y + 1 }, 2 /*MONITOR_DEFAULTTONEAREST*/);

    public void Start()
    {
        if (_running) return;
        _running = true;
        _session.StartCapture();
    }

    public void Stop()
    {
        _running = false;
        try { _session.Dispose(); } catch { /* already stopped */ }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
    {
        if (!_running || _disposed) return;
        Interlocked.Increment(ref _arrived);
        try
        {
            using var frame = pool.TryGetNextFrame();
            if (frame == null) return;

            if (frame.ContentSize.Width != _item.Size.Width ||
                frame.ContentSize.Height != _item.Size.Height)
            {
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2, frame.ContentSize);
            }

            var raw = Readback(frame);
            if (raw == null) return;
            var cropped = _regionCrop is { } rc ? raw.CropDesktop(rc) : raw;
            _latest = cropped;
            Interlocked.Increment(ref _emitted);

            var now = DateTimeOffset.Now;
            var dt = (now - _lastEmit).TotalSeconds;
            if (_lastEmit != DateTimeOffset.MinValue && dt > 0.001)
                _fpsEma = _fpsEma <= 0 ? 1.0 / dt : _fpsEma * 0.8 + 0.2 / dt;
            _lastEmit = now;

            FrameReceived?.Invoke(cropped);
        }
        catch (ObjectDisposedException) { }
        catch (COMException) { /* target vanished mid-frame */ }
        catch (Exception e) { LastFrameError = $"{e.GetType().Name}: {e.Message} @ {e.StackTrace?.Split('\n').FirstOrDefault()}"; }
    }

    private unsafe RawFrame? Readback(Direct3D11CaptureFrame frame)
    {
        var tex = WgcInterop.SurfaceAsTexture(frame.Surface);
        var desc = new D3D11_TEXTURE2D_DESC();
        tex.GetDesc(&desc);
        uint w = desc.Width, h = desc.Height;
        if (w == 0 || h == 0) return null;

        lock (_d3dLock)
        {
            if (_staging == null || _stagingW != w || _stagingH != h)
            {
                _staging = null;
                var sd = new D3D11_TEXTURE2D_DESC
                {
                    Width = w, Height = h, MipLevels = 1, ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                    Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                    BindFlags = 0,
                    CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
                    MiscFlags = 0,
                };
                ID3D11Texture2D_unmanaged* stg;
                _d3dDevice.CreateTexture2D(&sd, null, &stg);
                var stgPtr = (IntPtr)stg;
                _staging = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(stgPtr);
                Marshal.Release(stgPtr);
                _stagingW = w; _stagingH = h;
            }

            _d3dContext.CopyResource((ID3D11Resource)(object)_staging!, (ID3D11Resource)(object)tex);

            var mapped = new D3D11_MAPPED_SUBRESOURCE();
            _d3dContext.Map((ID3D11Resource)(object)_staging!, 0,
                D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
            try
            {
                var bytes = new byte[w * 4 * h];
                var src = (byte*)mapped.pData;
                for (var row = 0; row < h; row++)
                    Marshal.Copy((IntPtr)(src + row * mapped.RowPitch), bytes,
                        row * (int)w * 4, (int)w * 4);

                var srcRect = _sourceRectProvider();
                var transform = new FrameTransform(
                    new RectPx(srcRect.X, srcRect.Y, (int)w, (int)h), (int)w, (int)h,
                    Target is CaptureTarget.Window wt ? wt.Hwnd : null);
                Interlocked.Increment(ref _seq);
                return new RawFrame(bytes, (int)w, (int)h, (int)w * 4,
                    transform, DateTimeOffset.Now, _seq - 1);
            }
            finally { _d3dContext.Unmap((ID3D11Resource)(object)_staging!, 0); }
        }
    }

    public CaptureMetrics Metrics => new(
        Backend, _fpsEma, _fpsEma, _arrived,
        Math.Max(0, _arrived - _emitted), _latest?.Width ?? 0, _latest?.Height ?? 0,
        _startedAt);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _watchdog?.Dispose();
        try { _pool.Dispose(); } catch { }
        try { (_item as IWinRTObject)?.NativeObject.Dispose(); } catch { }
        try { (_device as IWinRTObject)?.NativeObject.Dispose(); } catch { }
    }
}
