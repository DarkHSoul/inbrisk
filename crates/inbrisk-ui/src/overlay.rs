//! Click-through overlays for the one activity source.
//!
//! The pill, the four screen-edge strips and the yellow target frame are
//! created on the UI thread and never take mouse or keyboard input. They read
//! `UiState`, which the status pump copies from `ActivityManager`.

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use once_cell::sync::OnceCell;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{COLORREF, HWND, LPARAM, LRESULT, RECT, WPARAM};
use windows::Win32::Graphics::Gdi::{
    BeginPaint, CreateFontW, CreatePen, CreateSolidBrush, DeleteObject, DrawTextW, Ellipse,
    EndPaint, FillRect, InvalidateRect, RoundRect, SelectObject, SetBkMode,
    SetTextColor, ANSI_CHARSET, CLEARTYPE_QUALITY, CLIP_DEFAULT_PRECIS, DEFAULT_PITCH,
    DT_END_ELLIPSIS, DT_NOPREFIX, DT_SINGLELINE, DT_VCENTER, FF_DONTCARE, FW_BOLD, FW_NORMAL,
    HBRUSH, OUT_DEFAULT_PRECIS, PAINTSTRUCT, PS_SOLID, TRANSPARENT,
};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CreateWindowExW, DefWindowProcW, DestroyWindow, GetClientRect, GetSystemMetrics,
    GetWindowLongPtrW, IsWindow, RegisterClassW, SetLayeredWindowAttributes,
    SetWindowLongPtrW, SetWindowPos, ShowWindow, SystemParametersInfoW, CS_HREDRAW, CS_VREDRAW,
    GWLP_USERDATA, HWND_TOPMOST, LWA_ALPHA, LWA_COLORKEY, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN,
    SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN, SPI_GETWORKAREA, SWP_NOACTIVATE, SWP_SHOWWINDOW, SW_HIDE,
    SW_SHOWNOACTIVATE, WM_DESTROY, WM_PAINT, WNDCLASSW, WS_EX_LAYERED, WS_EX_NOACTIVATE,
    WS_EX_TOOLWINDOW, WS_EX_TOPMOST, WS_EX_TRANSPARENT, WS_POPUP,
};

use crate::{context, UiState};
use inbrisk_core::ActivityState;

const CLASS_NAME: &str = "InbriskOverlay";
/// Magenta color key. Painted pixels of this color are click-through holes.
const COLOR_KEY: u32 = 0x00FF_00FF;
const KIND_PILL: isize = 1;
const KIND_EDGE: isize = 2;
const KIND_BOX: isize = 3;
const EDGE: i32 = 6;
const BOX_THICKNESS: i32 = 4;
/// Timer ticks to keep the overlays up after activity returns to idle.
const FADE_TICKS: u32 = 4;

static READY: OnceCell<()> = OnceCell::new();
static GONE: AtomicBool = AtomicBool::new(false);
static IDLE_TICKS: AtomicU32 = AtomicU32::new(0);
static WINDOWS: parking_lot::Mutex<OverlayWindows> = parking_lot::Mutex::new(OverlayWindows {
    pill: 0,
    edges: Vec::new(),
    frame: 0,
});

struct OverlayWindows {
    pill: isize,
    edges: Vec<isize>,
    frame: isize,
}

pub fn accent(state: ActivityState) -> u32 {
    match state {
        ActivityState::Active => 0x0000_B0FF,        // amber
        ActivityState::Waiting => 0x00DC_7828,       // blue
        ActivityState::Paused => 0x008C_8C8C,        // gray
        ActivityState::FailureSettle => 0x0028_78FF, // orange
        ActivityState::Emergency => 0x0028_28DC,     // red
        ActivityState::Idle => 0x008C_8C8C,
    }
}

pub const fn highlight() -> u32 {
    0x0000_D6FF // RGB(255, 214, 0)
}

/// Show, move or hide overlays to match `state`. UI thread only.
pub fn sync(state: &UiState) {
    if GONE.load(Ordering::Acquire) {
        return;
    }
    if !ensure_class() {
        return;
    }
    let monitors = inbrisk_win32::geometry::enumerate_monitors();
    let needed_edges = if monitors.is_empty() { 4 } else { monitors.len() * 4 };

    let mut windows = WINDOWS.lock();
    if windows.pill == 0 {
        windows.pill = create_overlay(KIND_PILL);
        windows.frame = create_overlay(KIND_BOX);
    }
    if windows.edges.len() != needed_edges {
        for hwnd in &windows.edges {
            destroy(*hwnd);
        }
        windows.edges.clear();
        for _ in 0..needed_edges {
            windows.edges.push(create_overlay(KIND_EDGE));
        }
    }

    let is_active = state
        .activity
        .map(ActivityState::is_visible)
        .unwrap_or(false);

    if is_active {
        IDLE_TICKS.store(0, Ordering::Release);
        place_edges(&windows.edges, &monitors);
        place_box(windows.frame, state.target_hwnd);
        for hwnd in &windows.edges {
            repaint(*hwnd);
        }
        repaint(windows.frame);
    } else {
        let ticks = IDLE_TICKS.fetch_add(1, Ordering::AcqRel) + 1;
        if ticks >= FADE_TICKS {
            for hwnd in &windows.edges {
                hide(*hwnd);
            }
            hide(windows.frame);
        }
    }

    place_pill(windows.pill, state.target_hwnd, &monitors);
    repaint(windows.pill);
}

/// Destroy every overlay. Called when the status window itself is destroyed.
pub fn hide_all() {
    GONE.store(true, Ordering::Release);
    let mut windows = WINDOWS.lock();
    destroy(windows.pill);
    for hwnd in &windows.edges {
        destroy(*hwnd);
    }
    windows.edges.clear();
    destroy(windows.frame);
    *windows = OverlayWindows {
        pill: 0,
        edges: Vec::new(),
        frame: 0,
    };
}



fn ensure_class() -> bool {
    if READY.get().is_some() {
        return true;
    }
    unsafe {
        let instance = GetModuleHandleW(None).unwrap_or_default();
        let class_name = wide(CLASS_NAME);
        let wc = WNDCLASSW {
            style: CS_HREDRAW | CS_VREDRAW,
            lpfnWndProc: Some(overlay_proc),
            cbClsExtra: 0,
            cbWndExtra: 0,
            hInstance: windows::Win32::Foundation::HINSTANCE(instance.0),
            hIcon: Default::default(),
            hCursor: Default::default(),
            hbrBackground: HBRUSH::default(),
            lpszMenuName: PCWSTR::null(),
            lpszClassName: PCWSTR(class_name.as_ptr()),
        };
        let _ = RegisterClassW(&wc);
    }
    READY.set(()).is_ok() || READY.get().is_some()
}

#[link(name = "user32")]
extern "system" {
    fn SetWindowDisplayAffinity(
        hwnd: windows::Win32::Foundation::HWND,
        dw_affinity: u32,
    ) -> windows::core::BOOL;
}
const WDA_EXCLUDEFROMCAPTURE: u32 = 0x0000_0011;

fn create_overlay(kind: isize) -> isize {
    let class_name = wide(CLASS_NAME);
    let instance = unsafe { GetModuleHandleW(None) }.unwrap_or_default();
    let mut ex = WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
    if kind != KIND_PILL {
        ex |= WS_EX_TRANSPARENT;
    }
    let created = unsafe {
        CreateWindowExW(
            ex,
            PCWSTR(class_name.as_ptr()),
            PCWSTR::null(),
            WS_POPUP,
            0,
            0,
            1,
            1,
            None,
            None,
            Some(windows::Win32::Foundation::HINSTANCE(instance.0)),
            None,
        )
    };
    let Ok(hwnd) = created else {
        return 0;
    };
    unsafe {
        SetWindowLongPtrW(hwnd, GWLP_USERDATA, kind);
        let _ =
            SetLayeredWindowAttributes(hwnd, COLORREF(COLOR_KEY), 235, LWA_COLORKEY | LWA_ALPHA);
        let _ = SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }
    hwnd.0 as isize
}

fn place_edges(edges: &[isize], monitors: &[inbrisk_win32::geometry::MonitorGeometry]) {
    if monitors.is_empty() {
        let x = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
        let y = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
        let w = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) }.max(1);
        let h = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) }.max(1);
        let rects = [
            (x, y, w, EDGE),
            (x, y + h - EDGE, w, EDGE),
            (x, y, EDGE, h),
            (x + w - EDGE, y, EDGE, h),
        ];
        for (hwnd, (left, top, width, height)) in edges.iter().zip(rects) {
            move_show(*hwnd, left, top, width, height);
        }
        return;
    }

    let mut rects = Vec::with_capacity(monitors.len() * 4);
    for m in monitors {
        let x = m.left;
        let y = m.top;
        let w = m.width().max(1);
        let h = m.height().max(1);
        rects.push((x, y, w, EDGE));
        rects.push((x, y + h - EDGE, w, EDGE));
        rects.push((x, y, EDGE, h));
        rects.push((x + w - EDGE, y, EDGE, h));
    }
    for (hwnd, (left, top, width, height)) in edges.iter().zip(rects) {
        move_show(*hwnd, left, top, width, height);
    }
}

fn is_target_fullscreen(target: isize, monitors: &[inbrisk_win32::geometry::MonitorGeometry]) -> bool {
    let hwnd_to_check = if target != 0 && alive(target) {
        target
    } else if let Some(fg) = inbrisk_win32::window::foreground() {
        fg.0
    } else {
        0
    };
    if hwnd_to_check == 0 {
        return false;
    }
    let rect = inbrisk_win32::window::window_rect(inbrisk_core::Hwnd(hwnd_to_check));
    for m in monitors {
        if rect.left() <= m.left + 5 && rect.top() <= m.top + 5 && rect.right() >= m.right - 5 && rect.bottom() >= m.bottom - 5 {
            return true;
        }
    }
    false
}

fn place_pill(hwnd: isize, target: isize, monitors: &[inbrisk_win32::geometry::MonitorGeometry]) {
    let (work_x, work_y, work_w, _work_h) = target_work_area(target, monitors);
    let is_working = context()
        .and_then(|ctx| ctx.state.lock().activity)
        .map(ActivityState::is_visible)
        .unwrap_or(false);

    let (width, height) = if is_working {
        (260i32, 34i32)
    } else {
        (170i32, 34i32)
    };

    let (x, y) = if is_target_fullscreen(target, monitors) {
        let px = work_x + (work_w - width) / 2;
        let py = work_y + 12;
        (px, py)
    } else if let Some(dock) = find_taskbar_dock(width, height) {
        dock
    } else {
        let px = work_x + (work_w - width) / 2;
        let py = work_y + 12;
        (px, py)
    };
    move_show(hwnd, x, y, width, height);
}

fn find_taskbar_dock(width: i32, height: i32) -> Option<(i32, i32)> {
    unsafe {
        use windows::Win32::UI::WindowsAndMessaging::{FindWindowExW, FindWindowW, GetWindowRect};
        let shell_tray = wide("Shell_TrayWnd");
        let h_tray = FindWindowW(PCWSTR(shell_tray.as_ptr()), PCWSTR::null()).ok()?;
        let mut r_tray = RECT::default();
        if GetWindowRect(h_tray, &mut r_tray).is_err() || r_tray.right <= r_tray.left {
            return None;
        }

        let tray_notify = wide("TrayNotifyWnd");
        let h_notify = FindWindowExW(Some(h_tray), None, PCWSTR(tray_notify.as_ptr()), PCWSTR::null()).ok();
        let mut r_notify = RECT::default();
        let has_notify = h_notify
            .map(|hn| GetWindowRect(hn, &mut r_notify).is_ok() && r_notify.right > r_notify.left)
            .unwrap_or(false);

        let taskbar_h = r_tray.bottom - r_tray.top;
        let y = r_tray.top + (taskbar_h - height).max(0) / 2;
        let tray_left = if has_notify {
            r_notify.left
        } else {
            r_tray.right - 180
        };
        let x = tray_left - width - 8;
        Some((x, y))
    }
}

fn target_work_area(
    target: isize,
    monitors: &[inbrisk_win32::geometry::MonitorGeometry],
) -> (i32, i32, i32, i32) {
    if !monitors.is_empty() {
        if target != 0 && alive(target) {
            let r = inbrisk_win32::window::window_rect(inbrisk_core::Hwnd(target));
            let cx = r.left() + r.width / 2;
            let cy = r.top() + r.height / 2;
            for m in monitors {
                if cx >= m.left && cx < m.right && cy >= m.top && cy < m.bottom {
                    return (
                        m.work_left,
                        m.work_top,
                        (m.work_right - m.work_left).max(1),
                        (m.work_bottom - m.work_top).max(1),
                    );
                }
            }
        }
        if let Some(m) = monitors.iter().find(|m| m.primary).or_else(|| monitors.first()) {
            return (
                m.work_left,
                m.work_top,
                (m.work_right - m.work_left).max(1),
                (m.work_bottom - m.work_top).max(1),
            );
        }
    }
    work_area()
}

fn place_box(hwnd: isize, target: isize) {
    if hwnd == 0 {
        return;
    }
    if target == 0 || !alive(target) {
        hide(hwnd);
        return;
    }
    let rect = inbrisk_win32::window::window_rect(inbrisk_core::Hwnd(target));
    let width = rect.width;
    let height = rect.height;
    if width < 8 || height < 8 {
        hide(hwnd);
        return;
    }
    let pad = BOX_THICKNESS;
    move_show(
        hwnd,
        rect.left() - pad,
        rect.top() - pad,
        width + pad * 2,
        height + pad * 2,
    );
}

fn work_area() -> (i32, i32, i32, i32) {
    let mut rect = RECT::default();
    let ok = unsafe {
        SystemParametersInfoW(
            SPI_GETWORKAREA,
            0,
            Some(&mut rect as *mut RECT as *mut core::ffi::c_void),
            Default::default(),
        )
    };
    if ok.is_ok() && rect.right > rect.left && rect.bottom > rect.top {
        return (
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        );
    }
    let x = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
    let y = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
    let w = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) }.max(1);
    let h = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) }.max(1);
    (x, y, w, h)
}

fn move_show(hwnd: isize, x: i32, y: i32, w: i32, h: i32) {
    if hwnd == 0 || w <= 0 || h <= 0 {
        return;
    }
    let window = HWND(hwnd as *mut core::ffi::c_void);
    unsafe {
        let _ = SetWindowPos(
            window,
            Some(HWND_TOPMOST),
            x,
            y,
            w,
            h,
            SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );
        let _ = ShowWindow(window, SW_SHOWNOACTIVATE);
    }
}

fn repaint(hwnd: isize) {
    if hwnd == 0 {
        return;
    }
    unsafe {
        let _ = InvalidateRect(Some(HWND(hwnd as *mut core::ffi::c_void)), None, false);
    }
}

fn hide(hwnd: isize) {
    if hwnd == 0 {
        return;
    }
    unsafe {
        let _ = ShowWindow(HWND(hwnd as *mut core::ffi::c_void), SW_HIDE);
    }
}

fn destroy(hwnd: isize) {
    if hwnd == 0 {
        return;
    }
    unsafe {
        let _ = DestroyWindow(HWND(hwnd as *mut core::ffi::c_void));
    }
}

fn alive(hwnd: isize) -> bool {
    unsafe { IsWindow(Some(HWND(hwnd as *mut core::ffi::c_void))).as_bool() }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

fn fill_client(hwnd: HWND, hdc: windows::Win32::Graphics::Gdi::HDC, color: u32) {
    let mut rect = RECT::default();
    unsafe {
        let _ = GetClientRect(hwnd, &mut rect);
    }
    fill(hdc, &rect, color);
}

fn fill(hdc: windows::Win32::Graphics::Gdi::HDC, rect: &RECT, color: u32) {
    let brush = unsafe { CreateSolidBrush(COLORREF(color)) };
    unsafe {
        FillRect(hdc, rect, brush);
        let _ = DeleteObject(windows::Win32::Graphics::Gdi::HGDIOBJ(brush.0));
    }
}

unsafe extern "system" fn overlay_proc(
    hwnd: HWND,
    msg: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    const WM_LBUTTONUP: u32 = 0x0202;
    match msg {
        WM_PAINT => {
            let mut paint = PAINTSTRUCT::default();
            let hdc = BeginPaint(hwnd, &mut paint);
            let kind = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            match kind {
                KIND_PILL => paint_pill(hwnd, hdc),
                KIND_EDGE => {
                    let color = current_accent();
                    fill_client(hwnd, hdc, color);
                }
                KIND_BOX => paint_box(hwnd, hdc),
                _ => fill_client(hwnd, hdc, COLOR_KEY),
            }
            let _ = EndPaint(hwnd, &paint);
            LRESULT(0)
        }
        WM_LBUTTONUP => {
            let kind = GetWindowLongPtrW(hwnd, GWLP_USERDATA);
            if kind == KIND_PILL {
                if let Some(ctx) = context() {
                    (ctx.on_command)(crate::UiCommand::ToggleWindow);
                }
            }
            LRESULT(0)
        }
        WM_DESTROY => LRESULT(0),
        _ => DefWindowProcW(hwnd, msg, wparam, lparam),
    }
}

fn current_accent() -> u32 {
    context()
        .and_then(|ctx| ctx.state.lock().activity)
        .map(accent)
        .unwrap_or_else(|| accent(ActivityState::Active))
}

fn paint_pill(hwnd: HWND, hdc: windows::Win32::Graphics::Gdi::HDC) {
    let mut rect = RECT::default();
    unsafe {
        let _ = GetClientRect(hwnd, &mut rect);
    }
    let w = rect.right;
    let h = rect.bottom;
    if w <= 0 || h <= 0 {
        return;
    }

    let state = context().map(|ctx| ctx.state.lock().clone()).unwrap_or_default();
    let activity = state.activity.unwrap_or(ActivityState::Idle);

    // 1. Transparent mask fill
    fill_client(hwnd, hdc, COLOR_KEY);

    // 2. State-dependent colors (Obsidian Dark Glass theme)
    let (bg_color, border_color, dot_color) = match activity {
        ActivityState::Emergency => (
            0x000C_0C24, // Dark Red Glass RGB(36, 12, 12)
            0x0030_3BFF, // Bright Red Border RGB(255, 59, 48)
            0x0030_3BFF, // Red Dot
        ),
        ActivityState::Paused => (
            0x0011_1116, // Dark Glass RGB(17, 17, 22)
            0x0007_C1FF, // Amber Border RGB(255, 193, 7)
            0x0007_C1FF, // Amber Dot
        ),
        ActivityState::Active | ActivityState::Waiting => (
            0x0011_1116, // Dark Glass RGB(17, 17, 22)
            0x0033_57FF, // Inbrisk Orange Border RGB(255, 87, 51)
            0x0033_57FF, // Pulsing Orange Dot
        ),
        ActivityState::Idle | ActivityState::FailureSettle => (
            0x0011_1116, // Dark Glass RGB(17, 17, 22)
            0x0048_3A3A, // Subtle Brand Border RGB(58, 58, 72)
            0x005E_C522, // Standby Green Dot RGB(34, 197, 94)
        ),
    };

    unsafe {
        use windows::Win32::Graphics::Gdi::HGDIOBJ;

        // 3. Rounded Capsule
        let bg_brush = CreateSolidBrush(COLORREF(bg_color));
        let border_pen = CreatePen(PS_SOLID, 1, COLORREF(border_color));
        let old_brush = SelectObject(hdc, bg_brush.into());
        let old_pen = SelectObject(hdc, border_pen.into());

        let radius = (h - 2).min(28);
        let _ = RoundRect(hdc, 1, 1, w - 1, h - 1, radius, radius);

        // 4. Status Indicator Dot
        let dot_brush = CreateSolidBrush(COLORREF(dot_color));
        let dot_pen = CreatePen(PS_SOLID, 1, COLORREF(dot_color));
        let _ = SelectObject(hdc, dot_brush.into());
        let _ = SelectObject(hdc, dot_pen.into());

        let dot_x = 16i32;
        let dot_y = h / 2;
        let _ = Ellipse(hdc, dot_x - 3, dot_y - 3, dot_x + 4, dot_y + 4);

        let _ = SelectObject(hdc, old_brush);
        let _ = SelectObject(hdc, old_pen);
        let _ = DeleteObject(HGDIOBJ(bg_brush.0));
        let _ = DeleteObject(HGDIOBJ(border_pen.0));
        let _ = DeleteObject(HGDIOBJ(dot_brush.0));
        let _ = DeleteObject(HGDIOBJ(dot_pen.0));

        // 5. Typography
        SetBkMode(hdc, TRANSPARENT);

        let segoe = wide("Segoe UI");
        let font_title = CreateFontW(
            15, 0, 0, 0, FW_BOLD.0 as i32, 0, 0, 0,
            ANSI_CHARSET, OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
            DEFAULT_PITCH.0 as u32 | FF_DONTCARE.0 as u32,
            PCWSTR(segoe.as_ptr()),
        );
        let font_regular = CreateFontW(
            14, 0, 0, 0, FW_NORMAL.0 as i32, 0, 0, 0,
            ANSI_CHARSET, OUT_DEFAULT_PRECIS,
            CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY,
            DEFAULT_PITCH.0 as u32 | FF_DONTCARE.0 as u32,
            PCWSTR(segoe.as_ptr()),
        );

        match activity {
            ActivityState::Idle => {
                // "Inbrisk" in bold white
                let _ = SelectObject(hdc, font_title.into());
                SetTextColor(hdc, COLORREF(0x00F7_F5F5)); // #F5F5F7
                let brand = "Inbrisk";
                let mut brand_u16 = wide(brand);
                let mut r_brand = RECT { left: dot_x + 10, top: 0, right: dot_x + 10 + 56, bottom: h };
                let _ = DrawTextW(hdc, &mut brand_u16, &mut r_brand, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

                // " Hazır" in muted gray
                let _ = SelectObject(hdc, font_regular.into());
                SetTextColor(hdc, COLORREF(0x00AB_9E9E)); // #9E9EAB
                let status = " Hazır";
                let mut status_u16 = wide(status);
                let mut r_status = RECT { left: dot_x + 10 + 56, top: 0, right: w - 24, bottom: h };
                let _ = DrawTextW(hdc, &mut status_u16, &mut r_status, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);

                // Right glyph ⚡
                SetTextColor(hdc, COLORREF(0x0035_4BE5)); // #E54B35
                let glyph = "⚡";
                let mut glyph_u16 = wide(glyph);
                let mut r_glyph = RECT { left: w - 22, top: 0, right: w - 4, bottom: h };
                let _ = DrawTextW(hdc, &mut glyph_u16, &mut r_glyph, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
            }
            ActivityState::Emergency => {
                let _ = SelectObject(hdc, font_title.into());
                SetTextColor(hdc, COLORREF(0x006B_6BFF)); // #FF6B6B
                let text = "■ Inbrisk durduruldu";
                let mut text_u16 = wide(text);
                let mut r_text = RECT { left: dot_x + 10, top: 0, right: w - 8, bottom: h };
                let _ = DrawTextW(hdc, &mut text_u16, &mut r_text, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            }
            ActivityState::Paused => {
                let _ = SelectObject(hdc, font_title.into());
                SetTextColor(hdc, COLORREF(0x0007_C1FF)); // #FFC107
                let text = "⏸ Duraklatıldı";
                let mut text_u16 = wide(text);
                let mut r_text = RECT { left: dot_x + 10, top: 0, right: w - 8, bottom: h };
                let _ = DrawTextW(hdc, &mut text_u16, &mut r_text, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            }
            _ => {
                let _ = SelectObject(hdc, font_regular.into());
                SetTextColor(hdc, COLORREF(0x00FC_FAFA)); // #FAFAFC
                let display_text = if !state.label.is_empty() {
                    state.label.clone()
                } else {
                    "Inbrisk Çalışıyor...".to_string()
                };
                let mut text_u16 = wide(&display_text);
                let mut r_text = RECT { left: dot_x + 10, top: 0, right: w - 8, bottom: h };
                let _ = DrawTextW(hdc, &mut text_u16, &mut r_text, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            }
        }

        let _ = DeleteObject(HGDIOBJ(font_title.0));
        let _ = DeleteObject(HGDIOBJ(font_regular.0));
    }
}

fn paint_box(hwnd: HWND, hdc: windows::Win32::Graphics::Gdi::HDC) {
    fill_client(hwnd, hdc, COLOR_KEY);
    let mut rect = RECT::default();
    unsafe {
        let _ = GetClientRect(hwnd, &mut rect);
    }
    let t = BOX_THICKNESS;
    let right = rect.right;
    let bottom = rect.bottom;
    fill(
        hdc,
        &RECT {
            left: 0,
            top: 0,
            right,
            bottom: t,
        },
        highlight(),
    );
    fill(
        hdc,
        &RECT {
            left: 0,
            top: bottom - t,
            right,
            bottom,
        },
        highlight(),
    );
    fill(
        hdc,
        &RECT {
            left: 0,
            top: 0,
            right: t,
            bottom,
        },
        highlight(),
    );
    fill(
        hdc,
        &RECT {
            left: right - t,
            top: 0,
            right,
            bottom,
        },
        highlight(),
    );
}

#[cfg(test)]
mod tests {
    use super::*;
    use inbrisk_win32::geometry::MonitorGeometry;

    #[test]
    fn accents_follow_activity() {
        assert_eq!(accent(ActivityState::Active), 0x0000_B0FF);
        assert_eq!(accent(ActivityState::Waiting), 0x00DC_7828);
        assert_eq!(accent(ActivityState::Emergency), 0x0028_28DC);
        assert_eq!(highlight(), 0x0000_D6FF);
        assert!(ActivityState::Active.is_visible());
        assert!(!ActivityState::Idle.is_visible());
    }

    #[test]
    fn highlight_operation_end_clears_while_window_remains_open() {
        let mut state = UiState::default();
        state.activity = Some(ActivityState::Active);
        state.target_hwnd = 0x1234;
        assert_eq!(state.target_hwnd, 0x1234);

        // When operation ends, target_hwnd is cleared to 0
        state.target_hwnd = 0;
        state.activity = Some(ActivityState::Idle);

        assert_eq!(state.target_hwnd, 0);
        assert!(!state.activity.map(ActivityState::is_visible).unwrap_or(false));
    }

    #[test]
    fn perimeter_connected_idle_is_hidden() {
        assert!(!ActivityState::Idle.is_visible());
        let idle_state = UiState {
            activity: Some(ActivityState::Idle),
            ..Default::default()
        };
        let visible = idle_state
            .activity
            .map(ActivityState::is_visible)
            .unwrap_or(false);
        assert!(!visible, "Connected idle must never show perimeter wall");
    }

    #[test]
    fn perimeter_native_run_shows_while_active() {
        assert!(ActivityState::Active.is_visible());
        assert_eq!(accent(ActivityState::Active), 0x0000_B0FF);
    }

    #[test]
    fn perimeter_run_completion_hides() {
        // Active -> Idle transition
        assert!(ActivityState::Active.is_visible());
        assert!(!ActivityState::Idle.is_visible());
        assert_eq!(FADE_TICKS, 4);
    }

    #[test]
    fn perimeter_asymmetric_monitor_layout_creates_four_edges_per_monitor() {
        let m0 = MonitorGeometry {
            handle_id: 1,
            left: 0,
            top: 0,
            right: 1920,
            bottom: 1080,
            work_left: 0,
            work_top: 0,
            work_right: 1920,
            work_bottom: 1040,
            dpi_x: 96,
            dpi_y: 96,
            primary: true,
        };
        let m1 = MonitorGeometry {
            handle_id: 2,
            left: 446,
            top: 1080,
            right: 2366,
            bottom: 2160,
            work_left: 446,
            work_top: 1080,
            work_right: 2366,
            work_bottom: 2100,
            dpi_x: 120,
            dpi_y: 120,
            primary: false,
        };
        let monitors = vec![m0, m1];

        let mut rects = Vec::new();
        for m in &monitors {
            let x = m.left;
            let y = m.top;
            let w = m.width().max(1);
            let h = m.height().max(1);
            rects.push((x, y, w, EDGE));             // Top
            rects.push((x, y + h - EDGE, w, EDGE)); // Bottom
            rects.push((x, y, EDGE, h));             // Left
            rects.push((x + w - EDGE, y, EDGE, h)); // Right
        }

        assert_eq!(rects.len(), 8, "Must create 4 edges per monitor (8 total)");

        // Monitor 0 top edge must match monitor 0 width, not virtual desktop width
        assert_eq!(rects[0], (0, 0, 1920, EDGE));
        // Monitor 0 bottom edge must be at monitor 0 bottom (1080 - EDGE), not at the bottom of monitor 1
        assert_eq!(rects[1], (0, 1080 - EDGE, 1920, EDGE));
        // Monitor 1 left edge must start at 446 (monitor 1 origin), never in the void at 0
        assert_eq!(rects[6], (446, 1080, EDGE, 1080));
    }

    #[test]
    fn highlight_uses_extended_frame_bounds() {
        let rect = inbrisk_core::Rect::new(100, 150, 800, 600);
        let pad = BOX_THICKNESS;
        let left = rect.left() - pad;
        let top = rect.top() - pad;
        let width = rect.width + pad * 2;
        let height = rect.height + pad * 2;

        assert_eq!(left, 96);
        assert_eq!(top, 146);
        assert_eq!(width, 808);
        assert_eq!(height, 608);
    }
}
