//! The status window and its message loop.
//!
//! Everything Win32-shaped about the UI lives here; the runtime only ever sees
//! `UiState` and `UiCommand`.

use std::sync::atomic::Ordering;

use windows::core::{w, PCWSTR};
use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
use windows::Win32::Graphics::Gdi::{GetStockObject, UpdateWindow, DEFAULT_GUI_FONT};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, GetMessageW, LoadCursorW,
    PostMessageW, PostQuitMessage, RegisterClassW, SendMessageW, SetTimer, SetWindowTextW,
    ShowWindow, TranslateMessage, CS_HREDRAW, CS_VREDRAW, CW_USEDEFAULT, HMENU, IDC_ARROW, MSG,
    SW_HIDE, SW_SHOW, WINDOW_EX_STYLE, WINDOW_STYLE, WM_APP, WM_CLOSE, WM_COMMAND, WM_CREATE,
    WM_DESTROY, WM_SETFONT, WM_TIMER, WNDCLASSW, WS_CHILD, WS_OVERLAPPEDWINDOW, WS_TABSTOP,
    WS_VISIBLE, WS_VSCROLL,
};

use crate::{context, tray, UiCommand, UiState};

/// Posted from any thread to re-read `UiState`.
const REFRESH_MSG: u32 = WM_APP + 2;
/// Posted from any thread to show (`wparam != 0`) or hide the status window.
const SHOW_MSG: u32 = WM_APP + 3;
const TIMER_ID: usize = 1;

const ID_PAUSE: usize = 1001;
const ID_RESUME: usize = 1002;
const ID_EMERGENCY: usize = 1003;
const ID_CLEAR: usize = 1004;
const ID_HIDE: usize = 1005;

static CLASS_NAME: &str = "InbriskStatusWindow";

/// Child controls, captured at WM_CREATE.
#[derive(Debug, Clone, Copy, Default)]
struct Controls {
    headline: isize,
    activity: isize,
    body: isize,
    pause: isize,
    resume: isize,
    emergency: isize,
    clear: isize,
    hide: isize,
}

static CONTROLS: parking_lot::Mutex<Controls> = parking_lot::Mutex::new(Controls {
    headline: 0,
    activity: 0,
    body: 0,
    pause: 0,
    resume: 0,
    emergency: 0,
    clear: 0,
    hide: 0,
});

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn set_text(hwnd: isize, text: &str) {
    if hwnd == 0 {
        return;
    }
    let buf = wide(text);
    unsafe {
        let _ = SetWindowTextW(HWND(hwnd as *mut core::ffi::c_void), PCWSTR(buf.as_ptr()));
    }
}

fn make_child(
    parent: HWND,
    class: PCWSTR,
    text: &str,
    style: WINDOW_STYLE,
    x: i32,
    y: i32,
    w: i32,
    h: i32,
    id: usize,
) -> isize {
    let text_buf = wide(text);
    let instance = unsafe { GetModuleHandleW(None) }.unwrap_or_default();
    let result = unsafe {
        CreateWindowExW(
            WINDOW_EX_STYLE(0),
            class,
            PCWSTR(text_buf.as_ptr()),
            style,
            x,
            y,
            w,
            h,
            Some(parent),
            Some(HMENU(id as *mut core::ffi::c_void)),
            Some(windows::Win32::Foundation::HINSTANCE(instance.0)),
            None,
        )
    };
    match result {
        Ok(h) => {
            let font = unsafe { GetStockObject(DEFAULT_GUI_FONT) };
            unsafe {
                SendMessageW(
                    h,
                    WM_SETFONT,
                    Some(WPARAM(font.0 as usize)),
                    Some(LPARAM(1)),
                );
            }
            h.0 as isize
        }
        Err(_) => 0,
    }
}

fn build_controls(hwnd: HWND) -> Controls {
    let label = WINDOW_STYLE(0x5000_0000); // SS_LEFT
    let button = WINDOW_STYLE(0x5001_0000); // BS_PUSHBUTTON | WS_CHILD
    let visible = WS_CHILD | WS_VISIBLE;
    Controls {
        headline: make_child(
            hwnd,
            w!("STATIC"),
            "Inbrisk",
            label | visible,
            16,
            12,
            480,
            24,
            0,
        ),
        activity: make_child(
            hwnd,
            w!("STATIC"),
            "idle",
            label | visible,
            16,
            40,
            480,
            20,
            0,
        ),
        body: make_child(
            hwnd,
            w!("STATIC"),
            "",
            label | visible | WS_VSCROLL,
            16,
            68,
            480,
            290,
            0,
        ),
        pause: make_child(
            hwnd,
            w!("BUTTON"),
            "Pause",
            button | visible | WS_TABSTOP,
            16,
            368,
            96,
            30,
            ID_PAUSE,
        ),
        resume: make_child(
            hwnd,
            w!("BUTTON"),
            "Resume",
            button | visible | WS_TABSTOP,
            116,
            368,
            96,
            30,
            ID_RESUME,
        ),
        emergency: make_child(
            hwnd,
            w!("BUTTON"),
            "Emergency Stop",
            button | visible | WS_TABSTOP,
            216,
            368,
            120,
            30,
            ID_EMERGENCY,
        ),
        clear: make_child(
            hwnd,
            w!("BUTTON"),
            "Clear",
            button | visible | WS_TABSTOP,
            340,
            368,
            80,
            30,
            ID_CLEAR,
        ),
        hide: make_child(
            hwnd,
            w!("BUTTON"),
            "Hide",
            button | visible | WS_TABSTOP,
            424,
            368,
            80,
            30,
            ID_HIDE,
        ),
    }
}

fn render() {
    let Some(ctx) = context() else {
        return;
    };
    let state: UiState = ctx.state.lock().clone();
    let controls = *CONTROLS.lock();
    set_text(controls.headline, &state.headline);
    let activity = state
        .activity
        .map(|a| a.as_str().to_string())
        .unwrap_or_else(|| "unknown".into());
    let flags = match (state.emergency, state.paused) {
        (true, _) => "  [EMERGENCY STOP]",
        (false, true) => "  [PAUSED]",
        _ => "",
    };
    set_text(
        controls.activity,
        &format!("state: {activity}{flags}   clients: {}", state.clients),
    );
    set_text(controls.body, &state.body);
    crate::overlay::sync(&state);
    // Pause/Resume are only meaningful when they would do something, so the
    // buttons reflect the live state instead of accepting nonsense clicks.
    enable(controls.pause, !state.paused && !state.emergency);
    enable(controls.resume, state.paused);
    enable(controls.emergency, !state.emergency);
    enable(controls.clear, state.emergency);
    enable(controls.hide, true);
    tray::set_tip(
        HWND(ctx.hwnd.load(Ordering::Acquire) as *mut core::ffi::c_void),
        &state.tip(),
    );
}

fn enable(hwnd: isize, enabled: bool) {
    if hwnd == 0 {
        return;
    }
    // `EnableWindow` is not exposed by the bindings we build against, so drive
    // the control through its documented `WM_ENABLE` message instead.
    const WM_ENABLE: u32 = 0x000A;
    unsafe {
        SendMessageW(
            HWND(hwnd as *mut core::ffi::c_void),
            WM_ENABLE,
            Some(WPARAM(usize::from(enabled))),
            Some(LPARAM(0)),
        );
    }
}

unsafe extern "system" fn wnd_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match msg {
        WM_CREATE => {
            let controls = build_controls(hwnd);
            *CONTROLS.lock() = controls;
            if let Some(ctx) = context() {
                ctx.hwnd.store(hwnd.0 as isize, Ordering::Release);
                let _ = tray::add(hwnd, "Inbrisk");
            }
            unsafe {
                SetTimer(Some(hwnd), TIMER_ID, 100, None);
            }
            render();
            LRESULT(0)
        }
        WM_TIMER => {
            render();
            LRESULT(0)
        }
        REFRESH_MSG => {
            render();
            LRESULT(0)
        }
        SHOW_MSG => {
            let show = wparam.0 != 0;
            unsafe {
                let _ = ShowWindow(hwnd, if show { SW_SHOW } else { SW_HIDE });
                if show {
                    let _ = UpdateWindow(hwnd);
                }
            }
            LRESULT(0)
        }
        tray::TRAY_CALLBACK => {
            // Low word of lparam carries the mouse message.
            let mouse = (lparam.0 as u32) & 0xFFFF;
            const WM_RBUTTONUP: u32 = 0x0205;
            const WM_LBUTTONUP: u32 = 0x0202;
            const WM_LBUTTONDBLCLK: u32 = 0x0203;
            match mouse {
                WM_RBUTTONUP => {
                    if let Some(command) = tray::show_menu(hwnd) {
                        dispatch(command);
                    }
                }
                WM_LBUTTONUP | WM_LBUTTONDBLCLK => dispatch(UiCommand::ToggleWindow),
                _ => {}
            }
            LRESULT(0)
        }
        WM_COMMAND => {
            let id = (wparam.0 & 0xFFFF) as usize;
            match id {
                ID_PAUSE => dispatch(UiCommand::Pause),
                ID_RESUME => dispatch(UiCommand::Resume),
                ID_EMERGENCY => dispatch(UiCommand::EmergencyStop),
                ID_CLEAR => dispatch(UiCommand::ClearEmergency),
                ID_HIDE => {
                    let _ = unsafe { ShowWindow(hwnd, SW_HIDE) };
                }
                _ => {}
            }
            LRESULT(0)
        }
        WM_CLOSE => {
            // Closing the window must never stop automation: it only hides.
            let shutting_down = context()
                .map(|c| c.shutting_down.load(Ordering::Acquire))
                .unwrap_or(false);
            if shutting_down {
                let _ = unsafe { DestroyWindow(hwnd) };
            } else {
                let _ = unsafe { ShowWindow(hwnd, SW_HIDE) };
            }
            LRESULT(0)
        }
        WM_DESTROY => {
            crate::overlay::hide_all();
            tray::remove(hwnd);
            unsafe { PostQuitMessage(0) };
            LRESULT(0)
        }
        _ => unsafe { DefWindowProcW(hwnd, msg, wparam, lparam) },
    }
}

fn dispatch(command: UiCommand) {
    if let Some(ctx) = context() {
        (ctx.on_command)(command);
    }
}

/// Show or hide the status window.
pub fn show_window(show: bool) {
    let Some(ctx) = context() else {
        return;
    };
    let hwnd = ctx.hwnd.load(Ordering::Acquire);
    if hwnd == 0 {
        return;
    }
    let hwnd = HWND(hwnd as *mut core::ffi::c_void);
    // ShowWindow has to run on the thread that created the window. Posting
    // keeps the status pump from touching the HWND cross-thread.
    unsafe {
        let _ = PostMessageW(Some(hwnd), SHOW_MSG, WPARAM(usize::from(show)), LPARAM(0));
    }
}

pub fn request_refresh() {
    let Some(ctx) = context() else {
        return;
    };
    let hwnd = ctx.hwnd.load(Ordering::Acquire);
    if hwnd == 0 {
        return;
    }
    unsafe {
        let _ = PostMessageW(
            Some(HWND(hwnd as *mut core::ffi::c_void)),
            REFRESH_MSG,
            WPARAM(0),
            LPARAM(0),
        );
    }
}

pub fn request_quit() {
    let Some(ctx) = context() else {
        return;
    };
    let hwnd = ctx.hwnd.load(Ordering::Acquire);
    if hwnd == 0 {
        unsafe { PostQuitMessage(0) };
        return;
    }
    unsafe {
        let _ = PostMessageW(
            Some(HWND(hwnd as *mut core::ffi::c_void)),
            WM_CLOSE,
            WPARAM(0),
            LPARAM(0),
        );
    }
}

/// Register the window class, create the window and pump messages.
///
/// This blocks the calling thread. That thread must be the process main
/// thread: the tray and the menu only stay responsive there.
pub fn run_message_loop(show: bool) {
    let _ = inbrisk_win32::geometry::set_per_monitor_v2_dpi_awareness();
    unsafe {
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let class_name = wide(CLASS_NAME);
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(wnd_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: windows::Win32::Foundation::HINSTANCE(instance.0),
            hIcon: Default::default(),
            hCursor: LoadCursorW(None, IDC_ARROW).unwrap_or_default(),
            hbrBackground: windows::Win32::Graphics::Gdi::HBRUSH(
                GetStockObject(windows::Win32::Graphics::Gdi::WHITE_BRUSH).0,
            ),
            lpszMenuName: PCWSTR::null(),
            lpszClassName: PCWSTR(class_name.as_ptr()),
        };
        let atom = RegisterClassW(&wc);
        if atom == 0 {
            // Already registered by a previous UI instance in this process.
        }

        let title = wide("Inbrisk");
        let hwnd = CreateWindowExW(
            WINDOW_EX_STYLE(0),
            PCWSTR(class_name.as_ptr()),
            PCWSTR(title.as_ptr()),
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT,
            CW_USEDEFAULT,
            532,
            460,
            None,
            None,
            Some(windows::Win32::Foundation::HINSTANCE(instance.0)),
            None,
        );

        let hwnd = match hwnd {
            Ok(h) => h,
            Err(_) => return,
        };

        // Created without WS_VISIBLE, so it stays hidden until Open / `inbrisk ui`.
        // Do not SW_HIDE a window that was never shown: that drops the foreground
        // right the tray menu needs in order to dismiss.
        if show {
            let _ = ShowWindow(hwnd, SW_SHOW);
            let _ = UpdateWindow(hwnd);
        }

        let mut msg = MSG::default();
        while GetMessageW(&mut msg, None, 0, 0).as_bool() {
            let _ = TranslateMessage(&msg);
            let _ = DispatchMessageW(&msg);
        }
    }
}
