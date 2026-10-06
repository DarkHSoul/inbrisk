use std::sync::OnceLock;

use inbrisk_core::{ErrorCode, InbriskError, Rect, Result};
use parking_lot::Mutex;
use windows::Win32::Foundation::{HWND, LPARAM, MAX_PATH, RECT};
use windows::Win32::Graphics::Dwm::{
    DwmGetWindowAttribute, DWMWA_CLOAKED, DWMWA_EXTENDED_FRAME_BOUNDS,
};
use windows::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, MonitorFromWindow, HDC, HMONITOR, MONITORINFO,
    MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetAncestor, GetClassNameW, GetForegroundWindow, GetWindow, GetWindowLongPtrW,
    GetWindowRect, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindow, IsWindowVisible, IsZoomed, PostMessageW, SetCursorPos, SetForegroundWindow,
    ShowWindow, GA_ROOTOWNER, GWL_EXSTYLE, GWL_STYLE, GW_OWNER, SW_RESTORE, WM_CLOSE, WS_CHILD,
    WS_EX_TOOLWINDOW,
};

use crate::{from_hwnd, process, to_hwnd};
use inbrisk_core::Hwnd;

/// Cheap Win32 description of a window, before ownership is applied.
#[derive(Debug, Clone)]
pub struct RawWindow {
    pub hwnd: Hwnd,
    pub process_id: u32,
    pub process_name: String,
    pub title: String,
    pub class_name: String,
    pub bounds: Rect,
    pub visible: bool,
    pub minimized: bool,
    pub maximized: bool,
    pub foreground: bool,
    pub tool_window: bool,
}

#[derive(Debug, Clone)]
pub struct RawMonitor {
    pub id: u32,
    pub bounds: Rect,
    pub work_area: Rect,
    pub primary: bool,
    pub dpi: u32,
}

/// Cached "is this window still a thing" lookups used by the event monitor.
static TITLE_CACHE: OnceLock<Mutex<std::collections::HashMap<isize, (u64, String)>>> =
    OnceLock::new();

fn title_cache() -> &'static Mutex<std::collections::HashMap<isize, (u64, String)>> {
    TITLE_CACHE.get_or_init(|| Mutex::new(std::collections::HashMap::new()))
}

pub fn title_of(hwnd: Hwnd) -> String {
    let h = to_hwnd(hwnd);
    unsafe {
        let len = GetWindowTextLengthW(h);
        if len <= 0 {
            return String::new();
        }
        let mut buf = vec![0u16; (len + 1) as usize];
        let written = GetWindowTextW(h, &mut buf);
        if written <= 0 {
            return String::new();
        }
        String::from_utf16_lossy(&buf[..written as usize])
    }
}

pub fn class_of(hwnd: Hwnd) -> String {
    let h = to_hwnd(hwnd);
    let mut buf = [0u16; 256];
    unsafe {
        let written = GetClassNameW(h, &mut buf);
        if written <= 0 {
            return String::new();
        }
        String::from_utf16_lossy(&buf[..written as usize])
    }
}

pub fn process_id_of(hwnd: Hwnd) -> u32 {
    let mut pid = 0u32;
    unsafe {
        GetWindowThreadProcessId(to_hwnd(hwnd), Some(&mut pid));
    }
    pid
}

pub fn is_alive(hwnd: Hwnd) -> bool {
    hwnd.0 != 0 && unsafe { IsWindow(Some(to_hwnd(hwnd))).as_bool() }
}

pub fn foreground() -> Option<Hwnd> {
    let h = unsafe { GetForegroundWindow() };
    if h.is_invalid() {
        None
    } else {
        Some(from_hwnd(h))
    }
}

pub fn is_foreground(hwnd: Hwnd) -> bool {
    foreground() == Some(hwnd)
}

/// Window rectangle in physical pixels, preferring DWM's extended frame bounds
/// so that the shadow padding of `GetWindowRect` does not leak to agents.
pub fn window_rect(hwnd: Hwnd) -> Rect {
    let h = to_hwnd(hwnd);
    unsafe {
        let mut rect = RECT::default();
        let ok = DwmGetWindowAttribute(
            h,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            &mut rect as *mut RECT as *mut core::ffi::c_void,
            std::mem::size_of::<RECT>() as u32,
        );
        if ok.is_err() && GetWindowRect(h, &mut rect).is_err() {
            return Rect::default();
        }
        Rect::new(
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        )
    }
}

pub fn is_cloaked(hwnd: Hwnd) -> bool {
    let mut cloaked: u32 = 0;
    unsafe {
        DwmGetWindowAttribute(
            to_hwnd(hwnd),
            DWMWA_CLOAKED,
            &mut cloaked as *mut u32 as *mut core::ffi::c_void,
            std::mem::size_of::<u32>() as u32,
        )
        .is_ok()
            && cloaked != 0
    }
}

fn ex_style(hwnd: Hwnd) -> isize {
    unsafe { GetWindowLongPtrW(to_hwnd(hwnd), GWL_EXSTYLE) }
}

fn style(hwnd: Hwnd) -> isize {
    unsafe { GetWindowLongPtrW(to_hwnd(hwnd), GWL_STYLE) }
}

fn has_owner(hwnd: Hwnd) -> bool {
    unsafe {
        GetWindow(to_hwnd(hwnd), GW_OWNER)
            .map(|h| !h.is_invalid())
            .unwrap_or(false)
    }
}

/// Describe one window. Cheap: only user32 + DWM, never UIA.
pub fn describe(hwnd: Hwnd) -> Option<RawWindow> {
    if !is_alive(hwnd) {
        return None;
    }
    let title = title_of(hwnd);
    let class_name = class_of(hwnd);
    let process_id = process_id_of(hwnd);
    let process_name = process::process_name(process_id).unwrap_or_default();
    let fg = foreground() == Some(hwnd);
    Some(RawWindow {
        hwnd,
        process_id,
        process_name,
        title,
        class_name,
        bounds: window_rect(hwnd),
        visible: unsafe { IsWindowVisible(to_hwnd(hwnd)).as_bool() },
        minimized: unsafe { IsIconic(to_hwnd(hwnd)).as_bool() },
        maximized: unsafe { IsZoomed(to_hwnd(hwnd)).as_bool() },
        foreground: fg,
        tool_window: ex_style(hwnd) & WS_EX_TOOLWINDOW.0 as isize != 0,
    })
}

/// Enumeration filter: what counts as a real, agent-visible top-level window.
pub fn is_agent_visible(w: &RawWindow) -> bool {
    if !w.visible || w.title.trim().is_empty() {
        return false;
    }
    if w.tool_window && w.title.trim().is_empty() {
        return false;
    }
    if is_cloaked(w.hwnd) {
        // UWP apps keep cloaked ghost windows around.
        return false;
    }
    // Owned popups without a title are noise; owned dialogs with a title are
    // exactly what agents need (Save As, Confirm, ...).
    if has_owner(w.hwnd) && w.title.trim().is_empty() {
        return false;
    }
    true
}

unsafe extern "system" fn enum_proc(hwnd: HWND, lparam: LPARAM) -> windows::core::BOOL {
    let out = &mut *(lparam.0 as *mut Vec<Hwnd>);
    let h = from_hwnd(hwnd);
    if h.0 != 0 && unsafe { IsWindowVisible(hwnd).as_bool() } && style(h) & WS_CHILD.0 as isize == 0
    {
        out.push(h);
    }
    windows::core::BOOL(1)
}

/// Enumerate visible top-level windows (raw, unfiltered by ownership).
pub fn enumerate_top_level() -> Vec<RawWindow> {
    let mut handles: Vec<Hwnd> = Vec::with_capacity(128);
    unsafe {
        let _ = EnumWindows(
            Some(enum_proc),
            LPARAM(&mut handles as *mut Vec<Hwnd> as isize),
        );
    }
    if handles.is_empty() {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        unsafe {
            let user32 = GetModuleHandleA(s!("user32.dll"))
                .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
            if !user32.is_invalid() {
                if let (Some(open_desk), Some(enum_desk), Some(close_desk)) = (
                    GetProcAddress(user32, s!("OpenInputDesktop")),
                    GetProcAddress(user32, s!("EnumDesktopWindows")),
                    GetProcAddress(user32, s!("CloseDesktop")),
                ) {
                    type OpenDesktopFn =
                        unsafe extern "system" fn(
                            u32,
                            windows::core::BOOL,
                            u32,
                        )
                            -> windows::Win32::Foundation::HANDLE;
                    type EnumDesktopFn = unsafe extern "system" fn(
                        windows::Win32::Foundation::HANDLE,
                        Option<unsafe extern "system" fn(HWND, LPARAM) -> windows::core::BOOL>,
                        LPARAM,
                    )
                        -> windows::core::BOOL;
                    type CloseDesktopFn = unsafe extern "system" fn(
                        windows::Win32::Foundation::HANDLE,
                    )
                        -> windows::core::BOOL;
                    let open_f: OpenDesktopFn = std::mem::transmute(open_desk);
                    let enum_f: EnumDesktopFn = std::mem::transmute(enum_desk);
                    let close_f: CloseDesktopFn = std::mem::transmute(close_desk);
                    let desk = open_f(0, windows::core::BOOL(0), 0x01FF);
                    if !desk.is_invalid() {
                        let _ = enum_f(
                            desk,
                            Some(enum_proc),
                            LPARAM(&mut handles as *mut Vec<Hwnd> as isize),
                        );
                        let _ = close_f(desk);
                    }
                }
            }
        }
    }
    let mut out: Vec<RawWindow> = handles.into_iter().filter_map(describe).collect();
    out.retain(is_agent_visible);
    out.sort_by_key(|w| (w.process_name.clone(), w.hwnd.0));
    out
}

/// Find windows belonging to a process (used right after `launch`).
pub fn windows_of_process(pid: u32) -> Vec<Hwnd> {
    let mut handles: Vec<Hwnd> = Vec::new();
    unsafe {
        let _ = EnumWindows(
            Some(enum_proc),
            LPARAM(&mut handles as *mut Vec<Hwnd> as isize),
        );
    }
    handles
        .into_iter()
        .filter(|h| process_id_of(*h) == pid)
        .collect()
}

/// Root owner window (for UWP `ApplicationFrameHost` unwrapping).
pub fn root_owner(hwnd: Hwnd) -> Hwnd {
    let h = unsafe { GetAncestor(to_hwnd(hwnd), GA_ROOTOWNER) };
    if h.is_invalid() {
        hwnd
    } else {
        from_hwnd(h)
    }
}

/// Bring a window to the foreground, working around Windows' foreground lock.
pub fn focus_window(hwnd: Hwnd) -> Result<()> {
    if !is_alive(hwnd) {
        return Err(InbriskError::not_found(format!("window {hwnd} is gone")));
    }
    let h = to_hwnd(hwnd);
    unsafe {
        // Break Windows foreground lock by pulsing Alt (VK_MENU) first
        use windows::Win32::UI::Input::KeyboardAndMouse::{keybd_event, KEYEVENTF_KEYUP, VK_MENU};
        keybd_event(VK_MENU.0 as u8, 0, Default::default(), 0);
        keybd_event(VK_MENU.0 as u8, 0, KEYEVENTF_KEYUP, 0);

        if IsIconic(h).as_bool() {
            use windows::Win32::UI::WindowsAndMessaging::{ShowWindowAsync, WM_SYSCOMMAND};
            let _ = ShowWindow(h, SW_RESTORE);
            let _ = ShowWindowAsync(h, SW_RESTORE);
            let _ = PostMessageW(
                Some(h),
                WM_SYSCOMMAND,
                windows::Win32::Foundation::WPARAM(0xF120),
                windows::Win32::Foundation::LPARAM(0),
            );
            for _ in 0..10 {
                if !IsIconic(h).as_bool() {
                    break;
                }
                std::thread::sleep(std::time::Duration::from_millis(20));
            }
        }

        if SetForegroundWindow(h).as_bool() {
            for _ in 0..10 {
                if is_foreground(hwnd) {
                    return Ok(());
                }
                std::thread::sleep(std::time::Duration::from_millis(20));
            }
        }

        // Foreground lock: attach to the current foreground thread's input queue,
        // set focus, then detach.
        use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
        use windows::Win32::UI::Input::KeyboardAndMouse::SetFocus;
        use windows::Win32::UI::WindowsAndMessaging::{
            BringWindowToTop, GetWindowThreadProcessId as GwTid,
        };

        let target_thread = GwTid(h, None);
        let fg = GetForegroundWindow();
        let fg_thread = if fg.is_invalid() { 0 } else { GwTid(fg, None) };
        let me = GetCurrentThreadId();
        let attached_fg =
            fg_thread != 0 && fg_thread != me && AttachThreadInput(me, fg_thread, true).as_bool();
        let attached_target = target_thread != 0
            && target_thread != me
            && AttachThreadInput(me, target_thread, true).as_bool();
        let _ = BringWindowToTop(h);
        let ok = SetForegroundWindow(h).as_bool();
        if !ok {
            let _ = SetFocus(Some(h));
        }
        if attached_target {
            let _ = AttachThreadInput(me, target_thread, false);
        }
        if attached_fg {
            let _ = AttachThreadInput(me, fg_thread, false);
        }
        if !is_foreground(hwnd) {
            use windows::core::s;
            use windows::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};
            if let Ok(user32) = GetModuleHandleA(s!("user32.dll")) {
                if let Some(proc) = GetProcAddress(user32, s!("SwitchToThisWindow")) {
                    type SwitchToFn = unsafe extern "system" fn(HWND, windows::core::BOOL);
                    let switch_fn: SwitchToFn = std::mem::transmute(proc);
                    switch_fn(h, windows::core::BOOL(1));
                }
            }
        }
        for _ in 0..15 {
            if is_foreground(hwnd) {
                return Ok(());
            }
            std::thread::sleep(std::time::Duration::from_millis(20));
        }
        if is_foreground(hwnd) {
            Ok(())
        } else {
            // Final fallback: try setting foreground from an input-desktop thread
            let h_val = hwnd.0;
            let success = std::thread::spawn(move || {
                use windows::core::s;
                use windows::Win32::System::LibraryLoader::{
                    GetModuleHandleA, GetProcAddress, LoadLibraryA,
                };
                let user32 = GetModuleHandleA(s!("user32.dll"))
                    .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
                if user32.is_invalid() {
                    return false;
                }
                if let (Some(open_desk), Some(set_desk), Some(close_desk), Some(set_fg)) = (
                    GetProcAddress(user32, s!("OpenInputDesktop")),
                    GetProcAddress(user32, s!("SetThreadDesktop")),
                    GetProcAddress(user32, s!("CloseDesktop")),
                    GetProcAddress(user32, s!("SetForegroundWindow")),
                ) {
                    type OpenDesktopFn =
                        unsafe extern "system" fn(
                            u32,
                            windows::core::BOOL,
                            u32,
                        )
                            -> windows::Win32::Foundation::HANDLE;
                    type SetDesktopFn = unsafe extern "system" fn(
                        windows::Win32::Foundation::HANDLE,
                    )
                        -> windows::core::BOOL;
                    type CloseDesktopFn = unsafe extern "system" fn(
                        windows::Win32::Foundation::HANDLE,
                    )
                        -> windows::core::BOOL;
                    type SetFgFn = unsafe extern "system" fn(HWND) -> windows::core::BOOL;
                    let open_f: OpenDesktopFn = std::mem::transmute(open_desk);
                    let set_f: SetDesktopFn = std::mem::transmute(set_desk);
                    let close_f: CloseDesktopFn = std::mem::transmute(close_desk);
                    let fg_f: SetFgFn = std::mem::transmute(set_fg);

                    let desk = open_f(0, windows::core::BOOL(0), 0x01FF);
                    if !desk.is_invalid() {
                        let _ = set_f(desk);
                        let res = fg_f(HWND(h_val as *mut core::ffi::c_void));
                        let _ = close_f(desk);
                        return res.as_bool();
                    }
                }
                false
            })
            .join()
            .unwrap_or(false);

            if success || is_foreground(hwnd) {
                Ok(())
            } else {
                Err(InbriskError::new(
                    ErrorCode::Denied,
                    format!("could not bring {hwnd} to the foreground"),
                )
                .with_hint("Windows blocks background focus stealing; retry or click the window"))
            }
        }
    }
}

/// Post `WM_CLOSE` — the polite path. `force` is a *request*; the caller's
/// policy engine decides whether it is allowed.
pub fn post_close(hwnd: Hwnd) -> Result<()> {
    if !is_alive(hwnd) {
        return Err(InbriskError::not_found(format!("window {hwnd} is gone")));
    }
    unsafe {
        PostMessageW(
            Some(to_hwnd(hwnd)),
            WM_CLOSE,
            Default::default(),
            Default::default(),
        )
        .map_err(|e| InbriskError::internal(format!("PostMessage(WM_CLOSE) failed: {e}")))
    }
}

pub fn show_window(hwnd: Hwnd, cmd: i32) -> Result<()> {
    let _ = cmd;
    unsafe {
        let _ = ShowWindow(to_hwnd(hwnd), SW_RESTORE);
    }
    Ok(())
}

pub fn cursor_pos() -> Option<(i32, i32)> {
    let mut p = windows::Win32::Foundation::POINT::default();
    unsafe {
        match windows::Win32::UI::WindowsAndMessaging::GetCursorPos(&mut p) {
            Ok(()) => Some((p.x, p.y)),
            Err(_) => None,
        }
    }
}

pub fn set_cursor_pos(x: i32, y: i32) -> Result<()> {
    unsafe { SetCursorPos(x, y).map_err(|e| InbriskError::internal(format!("SetCursorPos: {e}"))) }
}

fn get_monitor_dpi(hmonitor: HMONITOR) -> (u32, u32) {
    use windows::core::s;
    use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryA};

    type GetDpiFn =
        unsafe extern "system" fn(HMONITOR, i32, *mut u32, *mut u32) -> windows::core::HRESULT;
    static FN: OnceLock<Option<GetDpiFn>> = OnceLock::new();
    let f = FN.get_or_init(|| unsafe {
        let lib = LoadLibraryA(s!("shcore.dll")).ok()?;
        let proc = GetProcAddress(lib, s!("GetDpiForMonitor"))?;
        Some(std::mem::transmute(proc))
    });

    let mut dpi_x: u32 = 96;
    let mut dpi_y: u32 = 96;
    if let Some(f) = *f {
        unsafe {
            let _ = f(hmonitor, 0, &mut dpi_x, &mut dpi_y);
        }
    }
    (dpi_x, dpi_y)
}

unsafe extern "system" fn monitor_proc(
    hmonitor: HMONITOR,
    _hdc: HDC,
    _rect: *mut RECT,
    data: LPARAM,
) -> windows::core::BOOL {
    let out = &mut *(data.0 as *mut Vec<RawMonitor>);
    let mut info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(hmonitor, &mut info).as_bool() } {
        let (dpi_x, _dpi_y) = get_monitor_dpi(hmonitor);
        out.push(RawMonitor {
            id: hmonitor.0 as usize as u32,
            bounds: Rect::new(
                info.rcMonitor.left,
                info.rcMonitor.top,
                info.rcMonitor.right - info.rcMonitor.left,
                info.rcMonitor.bottom - info.rcMonitor.top,
            ),
            work_area: Rect::new(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right - info.rcWork.left,
                info.rcWork.bottom - info.rcWork.top,
            ),
            primary: info.dwFlags & 1 != 0,
            dpi: dpi_x,
        });
    }
    windows::core::BOOL(1)
}

pub fn monitors() -> Vec<RawMonitor> {
    let mut out: Vec<RawMonitor> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(
            None,
            None,
            Some(monitor_proc),
            LPARAM(&mut out as *mut Vec<RawMonitor> as isize),
        );
    }
    out
}

pub fn monitor_of(hwnd: Hwnd) -> Option<RawMonitor> {
    let hmon = unsafe { MonitorFromWindow(to_hwnd(hwnd), MONITOR_DEFAULTTONEAREST) };
    let mut info = MONITORINFO {
        cbSize: std::mem::size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(hmon, &mut info).as_bool() } {
        Some(RawMonitor {
            id: hmon.0 as usize as u32,
            bounds: Rect::new(
                info.rcMonitor.left,
                info.rcMonitor.top,
                info.rcMonitor.right - info.rcMonitor.left,
                info.rcMonitor.bottom - info.rcMonitor.top,
            ),
            work_area: Rect::new(
                info.rcWork.left,
                info.rcWork.top,
                info.rcWork.right - info.rcWork.left,
                info.rcWork.bottom - info.rcWork.top,
            ),
            primary: info.dwFlags & 1 != 0,
            dpi: 96,
        })
    } else {
        None
    }
}

/// Remember a window title so the event monitor can report "closed: X".
pub fn remember_title(hwnd: Hwnd, title: &str) {
    title_cache()
        .lock()
        .insert(hwnd.0, (inbrisk_core::world::now_ms(), title.to_string()));
}

pub fn last_title(hwnd: Hwnd) -> Option<String> {
    title_cache().lock().get(&hwnd.0).map(|(_, t)| t.clone())
}

pub fn forget_title(hwnd: Hwnd) {
    title_cache().lock().remove(&hwnd.0);
}

#[allow(dead_code)]
fn max_path() -> usize {
    MAX_PATH as usize
}
