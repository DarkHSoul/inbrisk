using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Direct3D11;
using Windows.Win32.System.WinRT.Graphics.Capture;
using WinRT;
using WinRTInspectable = Windows.Win32.System.WinRT.IInspectable;

namespace Inbrisk.Platform.Windows.Capture;

/// <summary>
/// WinRT/COM glue for Windows Graphics Capture: D3D11 device → IDirect3DDevice,
/// GraphicsCaptureItem via IGraphicsCaptureItemInterop, frame surface → BGRA
/// bytes through a staging texture. All pointers stay inside this file.
/// </summary>
internal static class WgcInterop
{
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>D3D11 device + WinRT IDirect3DDevice + immediate context.</summary>
    public static IDirect3DDevice CreateDevice(
        out ID3D11Device d3dDevice, out ID3D11DeviceContext d3dContext)
    {
        var hr = PInvoke.D3D11CreateDevice(null!, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            default, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, 7,
            out d3dDevice, out _, out d3dContext);
        Marshal.ThrowExceptionForHR(hr);

        var dxgi = (IDXGIDevice)d3dDevice;
        hr = PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgi, out WinRTInspectable insp);
        Marshal.ThrowExceptionForHR(hr);
        var unk = Marshal.GetIUnknownForObject(insp);
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(unk); }
        finally { Marshal.Release(unk); }
    }

    private static unsafe IGraphicsCaptureItemInterop CaptureFactory()
    {
        const string clsName = "Windows.Graphics.Capture.GraphicsCaptureItem";
        PInvoke.WindowsCreateString(clsName, (uint)clsName.Length, out var cls).ThrowOnFailure();
        using (cls)
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            var hr = PInvoke.RoGetActivationFactory(new HSTRING(cls.DangerousGetHandle()),
                &iid, out object factory);
            Marshal.ThrowExceptionForHR(hr);
            return (IGraphicsCaptureItemInterop)factory;
        }
    }

    public static unsafe GraphicsCaptureItem ItemForWindow(long hwnd)
    {
        var iid = IID_IGraphicsCaptureItem;
        CaptureFactory().CreateForWindow((HWND)hwnd, &iid, out object itemObj);
        var unk = Marshal.GetIUnknownForObject(itemObj);
        try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(unk); }
        finally { Marshal.Release(unk); }
    }

    public static unsafe GraphicsCaptureItem ItemForMonitor(long hmon)
    {
        var iid = IID_IGraphicsCaptureItem;
        CaptureFactory().CreateForMonitor((HMONITOR)hmon, &iid, out object itemObj);
        var unk = Marshal.GetIUnknownForObject(itemObj);
        try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(unk); }
        finally { Marshal.Release(unk); }
    }

    /// <summary>IDirect3DSurface → ID3D11Texture2D RCW.
    /// CsWinRT objects aren't classic RCWs — Marshal.GetIUnknownForObject would
    /// mint a CCW; FromManaged gives the real ABI pointer.</summary>
    public static unsafe ID3D11Texture2D SurfaceAsTexture(IDirect3DSurface surface)
    {
        var unk = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iid = IID_IDirect3DDxgiInterfaceAccess;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, ref iid, out var accessPtr));
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                var texIid = IID_ID3D11Texture2D;
                access.GetInterface(&texIid, out object texObj);
                return (ID3D11Texture2D)texObj;
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(unk); }
    }
}
