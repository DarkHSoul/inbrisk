//! Native Windows screen and window capture to PNG using GDI and GDI+.

use std::ptr::{null, null_mut};

use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS};
use windows::Win32::Graphics::Gdi::{
    BitBlt, CreateCompatibleBitmap, CreateCompatibleDC, CreateDCW, DeleteDC, DeleteObject, GetDC,
    ReleaseDC, SelectObject, CAPTUREBLT, HGDIOBJ, SRCCOPY,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetForegroundWindow, GetSystemMetrics, GetWindowRect, IsIconic, IsWindow, SM_CXSCREEN,
    SM_CYSCREEN, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
};

#[repr(C)]
struct GdiplusStartupInput {
    gdiplus_version: u32,
    debug_event_callback: usize,
    suppress_background_thread: i32,
    suppress_external_codecs: i32,
}

#[repr(C)]
struct GdiplusStartupOutput {
    notification_hook: usize,
    notification_unhook: usize,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct GUID {
    data1: u32,
    data2: u16,
    data3: u16,
    data4: [u8; 8],
}

// PNG Encoder CLSID: {557cf406-1a04-11d3-9a73-0000f81ef32e}
const CLSID_PNG: GUID = GUID {
    data1: 0x557cf406,
    data2: 0x1a04,
    data3: 0x11d3,
    data4: [0x9a, 0x73, 0x00, 0x00, 0xf8, 0x1e, 0xf3, 0x2e],
};

#[link(name = "gdiplus")]
extern "system" {
    fn GdiplusStartup(
        token: *mut usize,
        input: *const GdiplusStartupInput,
        output: *mut GdiplusStartupOutput,
    ) -> i32;
    fn GdiplusShutdown(token: usize);
    fn GdipCreateBitmapFromHBITMAP(
        hbm: isize,
        hpal: isize,
        bitmap: *mut usize,
    ) -> i32;
    fn GdipDisposeImage(image: usize) -> i32;
    fn GdipSaveImageToStream(
        image: usize,
        stream: *mut core::ffi::c_void,
        clsid_encoder: *const GUID,
        encoder_params: *const core::ffi::c_void,
    ) -> i32;
    fn GdipGetImageWidth(image: usize, width: *mut u32) -> i32;
    fn GdipGetImageHeight(image: usize, height: *mut u32) -> i32;
}

#[link(name = "ole32")]
extern "system" {
    fn CreateStreamOnHGlobal(
        hglobal: isize,
        delete_on_release: i32,
        stream: *mut *mut core::ffi::c_void,
    ) -> i32;
    fn GetHGlobalFromStream(
        stream: *mut core::ffi::c_void,
        hglobal: *mut isize,
    ) -> i32;
}

#[link(name = "kernel32")]
extern "system" {
    fn GlobalLock(hmem: isize) -> *mut u8;
    fn GlobalUnlock(hmem: isize) -> i32;
    fn GlobalSize(hmem: isize) -> usize;
}

#[derive(Debug, Clone)]
pub enum CaptureTarget {
    Desktop,
    Foreground,
    Window(isize),
    Region { x: i32, y: i32, w: i32, h: i32 },
}

#[derive(Debug, Clone)]
pub struct CaptureResult {
    pub png_bytes: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub x: i32,
    pub y: i32,
    pub hwnd: Option<isize>,
}

unsafe fn release_com(p: *mut core::ffi::c_void) {
    if !p.is_null() {
        let vtable = *(p as *mut *mut usize);
        let release_fn: unsafe extern "system" fn(*mut core::ffi::c_void) -> u32 =
            std::mem::transmute(*vtable.add(2));
        release_fn(p);
    }
}

unsafe fn ensure_attached_to_desktop() {
    use windows::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress, LoadLibraryA};
    use windows::core::s;
    let user32 = GetModuleHandleA(s!("user32.dll"))
        .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
    if user32.is_invalid() {
        return;
    }
    if let (Some(open_p), Some(set_p)) = (
        GetProcAddress(user32, s!("OpenInputDesktop")),
        GetProcAddress(user32, s!("SetThreadDesktop")),
    ) {
        type OpenDesktopFn = unsafe extern "system" fn(
            u32,
            windows::core::BOOL,
            u32,
        ) -> windows::Win32::Foundation::HANDLE;
        type SetDesktopFn =
            unsafe extern "system" fn(windows::Win32::Foundation::HANDLE) -> windows::core::BOOL;
        let open_f: OpenDesktopFn = std::mem::transmute(open_p);
        let set_f: SetDesktopFn = std::mem::transmute(set_p);

        let desk = open_f(0, windows::core::BOOL(0), 0x01FF);
        if !desk.is_invalid() && !desk.0.is_null() {
            let _ = set_f(desk);
        }
    }
}

/// Capture screen/window to PNG bytes.
pub fn capture_screenshot(
    target: CaptureTarget,
    _max_width: Option<u32>,
) -> Result<CaptureResult, String> {
    unsafe {
        ensure_attached_to_desktop();
    }
    let mut resolved_hwnd: Option<isize> = None;

    let (x, y, w, h) = match target {
        CaptureTarget::Desktop => {
            let mut vx = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
            let mut vy = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
            let mut vw = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) };
            let mut vh = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };
            if vw <= 0 || vh <= 0 {
                vx = 0;
                vy = 0;
                vw = unsafe { GetSystemMetrics(SM_CXSCREEN) };
                vh = unsafe { GetSystemMetrics(SM_CYSCREEN) };
            }
            (vx, vy, vw, vh)
        }
        CaptureTarget::Foreground => {
            let hwnd = unsafe { GetForegroundWindow() };
            if hwnd.0.is_null() {
                let vw = unsafe { GetSystemMetrics(SM_CXSCREEN) };
                let vh = unsafe { GetSystemMetrics(SM_CYSCREEN) };
                (0, 0, vw, vh)
            } else {
                resolved_hwnd = Some(hwnd.0 as isize);
                get_window_bounds(hwnd)?
            }
        }
        CaptureTarget::Window(h) => {
            let hwnd = HWND(h as *mut core::ffi::c_void);
            if !unsafe { IsWindow(Some(hwnd)) }.as_bool() {
                return Err(format!("window handle 0x{:X} does not exist", h));
            }
            if unsafe { IsIconic(hwnd) }.as_bool() {
                unsafe {
                    use windows::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_RESTORE};
                    let _ = ShowWindow(hwnd, SW_RESTORE);
                }
                std::thread::sleep(std::time::Duration::from_millis(50));
            }
            resolved_hwnd = Some(h);
            get_window_bounds(hwnd)?
        }
        CaptureTarget::Region { x, y, w, h } => {
            if w <= 0 || h <= 0 {
                return Err(format!("invalid region dimensions: {}x{}", w, h));
            }
            (x, y, w, h)
        }
    };

    if w <= 0 || h <= 0 {
        return Err(format!("capture area has zero size: {}x{}", w, h));
    }

    // Allocate memory bitmap and copy pixels via BitBlt
    let hbm = unsafe {

        let (hdc_screen, is_created) = {
            let dc = CreateDCW(
                windows::core::w!("DISPLAY"),
                windows::core::PCWSTR::null(),
                windows::core::PCWSTR::null(),
                None,
            );
            if !dc.is_invalid() {
                (dc, true)
            } else {
                (GetDC(None), false)
            }
        };
        if hdc_screen.is_invalid() {
            return Err("failed to acquire screen DC".into());
        }
        let hdc_mem = CreateCompatibleDC(Some(hdc_screen));
        if hdc_mem.is_invalid() {
            if is_created {
                let _ = DeleteDC(hdc_screen);
            } else {
                let _ = ReleaseDC(None, hdc_screen);
            }
            return Err("failed to create memory DC".into());
        }
        let hbm = CreateCompatibleBitmap(hdc_screen, w, h);
        if hbm.is_invalid() {
            let _ = DeleteDC(hdc_mem);
            if is_created {
                let _ = DeleteDC(hdc_screen);
            } else {
                let _ = ReleaseDC(None, hdc_screen);
            }
            return Err("failed to create compatible bitmap".into());
        }

        let old_bm = SelectObject(hdc_mem, HGDIOBJ(hbm.0));
        let mut blt_res = BitBlt(
            hdc_mem,
            0,
            0,
            w,
            h,
            Some(hdc_screen),
            x,
            y,
            SRCCOPY | CAPTUREBLT,
        );
        if blt_res.is_err() {
            // Fallback to plain SRCCOPY without CAPTUREBLT
            blt_res = BitBlt(hdc_mem, 0, 0, w, h, Some(hdc_screen), x, y, SRCCOPY);
        }

        let _ = SelectObject(hdc_mem, old_bm);
        let _ = DeleteDC(hdc_mem);
        if is_created {
            let _ = DeleteDC(hdc_screen);
        } else {
            let _ = ReleaseDC(None, hdc_screen);
        }

        if let Err(err) = blt_res {
            let _ = DeleteObject(HGDIOBJ(hbm.0));
            return Err(format!(
                "BitBlt failed to copy pixels from screen (rect: {}x{} at {},{}, error: {})",
                w, h, x, y, err
            ));
        }
        hbm
    };

    // Convert HBITMAP to PNG via GDI+
    let (png_bytes, final_w, final_h) = unsafe {
        let mut token: usize = 0;
        let input = GdiplusStartupInput {
            gdiplus_version: 1,
            debug_event_callback: 0,
            suppress_background_thread: 0,
            suppress_external_codecs: 0,
        };
        if GdiplusStartup(&mut token, &input, null_mut()) != 0 {
            let _ = DeleteObject(HGDIOBJ(hbm.0));
            return Err("GDI+ initialization failed".into());
        }

        let mut gp_image: usize = 0;
        if GdipCreateBitmapFromHBITMAP(hbm.0 as isize, 0, &mut gp_image) != 0 {
            let _ = DeleteObject(HGDIOBJ(hbm.0));
            GdiplusShutdown(token);
            return Err("GdipCreateBitmapFromHBITMAP failed".into());
        }
        let _ = DeleteObject(HGDIOBJ(hbm.0));

        let mut orig_w: u32 = 0;
        let mut orig_h: u32 = 0;
        GdipGetImageWidth(gp_image, &mut orig_w);
        GdipGetImageHeight(gp_image, &mut orig_h);

        let final_image = gp_image;
        let final_w = orig_w;
        let final_h = orig_h;

        let mut p_stream: *mut core::ffi::c_void = null_mut();
        if CreateStreamOnHGlobal(0, 1, &mut p_stream) != 0 {
            GdipDisposeImage(final_image);
            GdiplusShutdown(token);
            return Err("CreateStreamOnHGlobal failed".into());
        }

        if GdipSaveImageToStream(final_image, p_stream, &CLSID_PNG, null()) != 0 {
            release_com(p_stream);
            GdipDisposeImage(final_image);
            GdiplusShutdown(token);
            return Err("GdipSaveImageToStream failed to encode PNG".into());
        }
        GdipDisposeImage(final_image);

        let mut h_global: isize = 0;
        if GetHGlobalFromStream(p_stream, &mut h_global) != 0 || h_global == 0 {
            release_com(p_stream);
            GdiplusShutdown(token);
            return Err("GetHGlobalFromStream failed".into());
        }

        let p_bytes = GlobalLock(h_global);
        let size = GlobalSize(h_global);
        let mut bytes = Vec::with_capacity(size);
        if !p_bytes.is_null() && size > 0 {
            std::ptr::copy_nonoverlapping(p_bytes, bytes.as_mut_ptr(), size);
            bytes.set_len(size);
        }
        GlobalUnlock(h_global);
        release_com(p_stream);
        GdiplusShutdown(token);

        (bytes, final_w, final_h)
    };

    Ok(CaptureResult {
        png_bytes,
        width: final_w,
        height: final_h,
        x,
        y,
        hwnd: resolved_hwnd,
    })
}

fn get_window_bounds(hwnd: HWND) -> Result<(i32, i32, i32, i32), String> {
    let mut rect = RECT::default();
    let hr = unsafe {
        DwmGetWindowAttribute(
            hwnd,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            &mut rect as *mut _ as *mut _,
            std::mem::size_of::<RECT>() as u32,
        )
    };
    if hr.is_err() {
        if unsafe { GetWindowRect(hwnd, &mut rect) }.is_err() {
            return Err(format!("GetWindowRect failed for hwnd {:?}", hwnd));
        }
    }
    let w = rect.right - rect.left;
    let h = rect.bottom - rect.top;
    Ok((rect.left, rect.top, w, h))
}

/// Convert byte slice to base64 string without external dependencies.
pub fn to_base64(data: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::with_capacity((data.len() + 2) / 3 * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as usize;
        let b1 = if chunk.len() > 1 { chunk[1] as usize } else { 0 };
        let b2 = if chunk.len() > 2 { chunk[2] as usize } else { 0 };
        let n = (b0 << 16) | (b1 << 8) | b2;
        out.push(TABLE[(n >> 18) & 0x3F] as char);
        out.push(TABLE[(n >> 12) & 0x3F] as char);
        if chunk.len() > 1 {
            out.push(TABLE[(n >> 6) & 0x3F] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(TABLE[n & 0x3F] as char);
        } else {
            out.push('=');
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn base64_encoding_works() {
        assert_eq!(to_base64(b""), "");
        assert_eq!(to_base64(b"f"), "Zg==");
        assert_eq!(to_base64(b"fo"), "Zm8=");
        assert_eq!(to_base64(b"foo"), "Zm9v");
        assert_eq!(to_base64(b"foob"), "Zm9vYg==");
        assert_eq!(to_base64(b"fooba"), "Zm9vYmE=");
        assert_eq!(to_base64(b"foobar"), "Zm9vYmFy");
    }

    #[test]
    fn capture_desktop_smoke_test() {
        let res = capture_screenshot(CaptureTarget::Desktop, None);
        match res {
            Ok(capture) => {
                assert!(!capture.png_bytes.is_empty());
                assert_eq!(&capture.png_bytes[0..4], b"\x89PNG");
                assert!(capture.width > 0);
                assert!(capture.height > 0);
            }
            Err(e) => {
                panic!("Desktop capture failed: {}", e);
            }
        }
    }
}
