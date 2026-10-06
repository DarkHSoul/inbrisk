//! Canonical Windows geometry, mixed-DPI handling, and virtual desktop coordinate conversions.
//!
//! This module is the single source of truth for:
//! - Virtual screen metrics (`SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`, `SM_CXVIRTUALSCREEN`, `SM_CYVIRTUALSCREEN`)
//! - Canonical SendInput `0..65535` normalization with `MOUSEEVENTF_VIRTUALDESK`
//! - HWND client-to-screen coordinate resolution at execution time
//! - Window and monitor DPI awareness and scaling
//! - Monitor enumeration

use serde::{Deserialize, Serialize};

use inbrisk_core::{CoordinateSpace, Hwnd, InbriskError, Result};
use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM, POINT, RECT};
use windows::Win32::Graphics::Gdi::{
    EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO, MONITORINFOEXW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    GetSystemMetrics, SM_CXVIRTUALSCREEN, SM_CYVIRTUALSCREEN, SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN,
};

use crate::to_hwnd;

/// Physical rectangle in screen or virtual desktop pixel coordinates.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct PhysicalRect {
    pub left: i32,
    pub top: i32,
    pub width: i32,
    pub height: i32,
}

impl PhysicalRect {
    pub const fn new(left: i32, top: i32, width: i32, height: i32) -> Self {
        Self {
            left,
            top,
            width,
            height,
        }
    }

    pub const fn right(&self) -> i32 {
        self.left + self.width
    }

    pub const fn bottom(&self) -> i32 {
        self.top + self.height
    }

    pub fn center(&self) -> PhysicalPoint {
        PhysicalPoint {
            x: self.left + self.width / 2,
            y: self.top + self.height / 2,
        }
    }

    pub fn contains(&self, p: PhysicalPoint) -> bool {
        p.x >= self.left && p.x < self.right() && p.y >= self.top && p.y < self.bottom()
    }
}

/// Point in physical pixel coordinates (virtual screen or client area).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct PhysicalPoint {
    pub x: i32,
    pub y: i32,
}

/// Point in logical / DPI-independent coordinates (e.g. 96-DPI base).
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize, Default)]
pub struct LogicalPoint {
    pub x: f64,
    pub y: f64,
}

/// Normalized SendInput mouse coordinate in `0..65535` range.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub struct NormalizedPoint {
    pub x: u16,
    pub y: u16,
}

/// Detailed monitor inventory item.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MonitorGeometry {
    pub handle_id: u64,
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
    pub work_left: i32,
    pub work_top: i32,
    pub work_right: i32,
    pub work_bottom: i32,
    pub dpi_x: u32,
    pub dpi_y: u32,
    pub primary: bool,
}

impl MonitorGeometry {
    pub fn width(&self) -> i32 {
        self.right - self.left
    }

    pub fn height(&self) -> i32 {
        self.bottom - self.top
    }
}

/// Query Windows for the current virtual desktop bounds.
///
/// Accounts for multi-monitor layouts where `left < 0` or `top < 0`.
pub fn virtual_screen_bounds() -> PhysicalRect {
    let monitors = enumerate_monitors();
    if monitors.is_empty() {
        let left = unsafe { GetSystemMetrics(SM_XVIRTUALSCREEN) };
        let top = unsafe { GetSystemMetrics(SM_YVIRTUALSCREEN) };
        let width = unsafe { GetSystemMetrics(SM_CXVIRTUALSCREEN) };
        let height = unsafe { GetSystemMetrics(SM_CYVIRTUALSCREEN) };
        return PhysicalRect {
            left,
            top,
            width,
            height,
        };
    }

    let min_x = monitors.iter().map(|m| m.left).min().unwrap_or(0);
    let min_y = monitors.iter().map(|m| m.top).min().unwrap_or(0);
    let max_x = monitors.iter().map(|m| m.right).max().unwrap_or(1920);
    let max_y = monitors.iter().map(|m| m.bottom).max().unwrap_or(1080);

    PhysicalRect {
        left: min_x,
        top: min_y,
        width: (max_x - min_x).max(1),
        height: (max_y - min_y).max(1),
    }
}

/// Canonical SendInput normalization formula.
///
/// Normalizes a virtual desktop physical point into `0..65535` range accounting for
/// potentially negative virtual desktop origins.
///
/// Formula:
/// ```text
/// norm_x = round((x - virtual_left) * 65535 / (virtual_width - 1))
/// norm_y = round((y - virtual_top) * 65535 / (virtual_height - 1))
/// ```
pub fn normalize_for_send_input(
    point: PhysicalPoint,
    virtual_bounds: PhysicalRect,
) -> Result<NormalizedPoint> {
    let w = (virtual_bounds.width - 1).max(1) as f64;
    let h = (virtual_bounds.height - 1).max(1) as f64;

    let rel_x = (point.x - virtual_bounds.left) as f64;
    let rel_y = (point.y - virtual_bounds.top) as f64;

    let norm_x = ((rel_x * 65535.0 / w).round() as i64).clamp(0, 65535) as u16;
    let norm_y = ((rel_y * 65535.0 / h).round() as i64).clamp(0, 65535) as u16;

    Ok(NormalizedPoint {
        x: norm_x,
        y: norm_y,
    })
}

/// Denormalize SendInput `0..65535` coordinates back to physical virtual desktop space.
pub fn denormalize_from_send_input(
    norm: NormalizedPoint,
    virtual_bounds: PhysicalRect,
) -> PhysicalPoint {
    let w = (virtual_bounds.width - 1).max(1) as f64;
    let h = (virtual_bounds.height - 1).max(1) as f64;

    let x = virtual_bounds.left + ((norm.x as f64 * w) / 65535.0).round() as i32;
    let y = virtual_bounds.top + ((norm.y as f64 * h) / 65535.0).round() as i32;

    PhysicalPoint { x, y }
}

/// Convert logical coordinates to physical pixels using a target DPI.
///
/// Base standard DPI is 96 (100% scale).
/// Uses round-to-nearest pixel policy.
pub fn pure_logical_to_physical(point: LogicalPoint, dpi: u32) -> PhysicalPoint {
    let scale = (dpi.max(1) as f64) / 96.0;
    PhysicalPoint {
        x: (point.x * scale).round() as i32,
        y: (point.y * scale).round() as i32,
    }
}

/// Convert client-relative point to screen coordinates using known client origin.
pub fn pure_client_to_screen(
    client_pt: PhysicalPoint,
    client_origin_screen: PhysicalPoint,
) -> PhysicalPoint {
    PhysicalPoint {
        x: client_origin_screen.x + client_pt.x,
        y: client_origin_screen.y + client_pt.y,
    }
}

/// Query target window DPI using Windows `GetDpiForWindow`.
///
/// Defaults to 96 (100% scale) if HWND is null or API unavailable.
pub fn get_dpi_for_window(hwnd: Hwnd) -> u32 {
    if hwnd.0 == 0 {
        return 96;
    }
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if !user32.is_invalid() {
            if let Some(proc) = GetProcAddress(user32, s!("GetDpiForWindow")) {
                type GetDpiFn = unsafe extern "system" fn(HWND) -> u32;
                let func: GetDpiFn = std::mem::transmute(proc);
                let dpi = func(to_hwnd(hwnd));
                if dpi != 0 {
                    return dpi;
                }
            }
        }
        96
    }
}

/// Convert logical client coordinates to physical client coordinates using target window DPI.
pub fn client_logical_to_physical_client(hwnd: Hwnd, point: LogicalPoint) -> Result<PhysicalPoint> {
    if !crate::window::is_alive(hwnd) {
        return Err(InbriskError::target_gone(format!(
            "target window {hwnd:?} no longer exists or was destroyed"
        )));
    }
    let dpi = get_dpi_for_window(hwnd);
    Ok(pure_logical_to_physical(point, dpi))
}

/// Convert physical client-relative coordinates to virtual screen physical coordinates.
///
/// Resolves at execution time via `ClientToScreen`. Fails with `TargetGone` if HWND is destroyed.
pub fn client_physical_to_virtual_screen(
    hwnd: Hwnd,
    point: PhysicalPoint,
) -> Result<PhysicalPoint> {
    if !crate::window::is_alive(hwnd) {
        return Err(InbriskError::target_gone(format!(
            "target window {hwnd:?} no longer exists or was destroyed"
        )));
    }

    unsafe {
        let mut pt = POINT {
            x: point.x,
            y: point.y,
        };
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if !user32.is_invalid() {
            if let Some(proc) = GetProcAddress(user32, s!("ClientToScreen")) {
                type ClientToScreenFn = unsafe extern "system" fn(HWND, *mut POINT) -> BOOL;
                let func: ClientToScreenFn = std::mem::transmute(proc);
                if func(to_hwnd(hwnd), &mut pt).as_bool() {
                    return Ok(PhysicalPoint { x: pt.x, y: pt.y });
                }
            }
        }
        Err(InbriskError::internal(format!(
            "ClientToScreen failed for window {hwnd:?}"
        )))
    }
}

/// Convert an incoming canonical `Point` to virtual screen physical coordinates.
pub fn to_virtual_screen_physical(point: inbrisk_core::Point) -> Result<PhysicalPoint> {
    point.validate()?;
    match point.space {
        CoordinateSpace::VirtualScreenPhysical => Ok(PhysicalPoint {
            x: point.x.round() as i32,
            y: point.y.round() as i32,
        }),
        CoordinateSpace::WindowClientPhysical => {
            let hwnd = point.hwnd.ok_or_else(|| {
                InbriskError::invalid_plan("WindowClientPhysical point missing HWND")
            })?;
            client_physical_to_virtual_screen(
                hwnd,
                PhysicalPoint {
                    x: point.x.round() as i32,
                    y: point.y.round() as i32,
                },
            )
        }
        CoordinateSpace::WindowClientLogical => {
            let hwnd = point.hwnd.ok_or_else(|| {
                InbriskError::invalid_plan("WindowClientLogical point missing HWND")
            })?;
            let phys = client_logical_to_physical_client(
                hwnd,
                LogicalPoint {
                    x: point.x,
                    y: point.y,
                },
            )?;
            client_physical_to_virtual_screen(hwnd, phys)
        }
    }
}

/// Enumerate all connected display monitors with bounds, work area, and DPI.
pub fn enumerate_monitors() -> Vec<MonitorGeometry> {
    let mut monitors: Vec<MonitorGeometry> = Vec::new();

    unsafe extern "system" fn enum_monitor_proc(
        hmonitor: HMONITOR,
        _hdc: HDC,
        _lprect: *mut RECT,
        lparam: LPARAM,
    ) -> BOOL {
        let list = &mut *(lparam.0 as *mut Vec<MonitorGeometry>);
        let mut info: MONITORINFOEXW = std::mem::zeroed();
        info.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;

        if GetMonitorInfoW(
            hmonitor,
            &mut info as *mut MONITORINFOEXW as *mut MONITORINFO,
        )
        .as_bool()
        {
            let (mut dpi_x, mut dpi_y) = (96u32, 96u32);
            use windows::core::s;
            use windows::Win32::System::LibraryLoader::{
                GetModuleHandleA, GetProcAddress, LoadLibraryA,
            };
            let shcore = GetModuleHandleA(s!("shcore.dll"))
                .unwrap_or_else(|_| LoadLibraryA(s!("shcore.dll")).unwrap_or_default());
            if !shcore.is_invalid() {
                if let Some(proc) = GetProcAddress(shcore, s!("GetDpiForMonitor")) {
                    type GetDpiForMonitorFn = unsafe extern "system" fn(
                        HMONITOR,
                        i32,
                        *mut u32,
                        *mut u32,
                    )
                        -> windows::core::HRESULT;
                    let func: GetDpiForMonitorFn = std::mem::transmute(proc);
                    let _ = func(hmonitor, 0, &mut dpi_x, &mut dpi_y);
                }
            }

            let primary = (info.monitorInfo.dwFlags & 1) != 0;
            let mut left = info.monitorInfo.rcMonitor.left;
            let mut top = info.monitorInfo.rcMonitor.top;
            let mut right = info.monitorInfo.rcMonitor.right;
            let mut bottom = info.monitorInfo.rcMonitor.bottom;
            let mut work_left = info.monitorInfo.rcWork.left;
            let mut work_top = info.monitorInfo.rcWork.top;
            let mut work_right = info.monitorInfo.rcWork.right;
            let mut work_bottom = info.monitorInfo.rcWork.bottom;

            use windows::Win32::Graphics::Gdi::{
                EnumDisplaySettingsW, DEVMODEW, ENUM_CURRENT_SETTINGS,
            };
            let mut dm = DEVMODEW::default();
            dm.dmSize = std::mem::size_of::<DEVMODEW>() as u16;
            if EnumDisplaySettingsW(
                windows::core::PCWSTR(info.szDevice.as_ptr()),
                ENUM_CURRENT_SETTINGS,
                &mut dm,
            )
            .as_bool()
            {
                let phys_w = dm.dmPelsWidth as i32;
                let phys_h = dm.dmPelsHeight as i32;
                let phys_x = dm.Anonymous1.Anonymous2.dmPosition.x;
                let phys_y = dm.Anonymous1.Anonymous2.dmPosition.y;
                if phys_w > 0 && phys_h > 0 {
                    let log_w = (right - left).max(1);
                    let log_h = (bottom - top).max(1);
                    let scale_x = phys_w as f64 / log_w as f64;
                    let scale_y = phys_h as f64 / log_h as f64;

                    left = phys_x;
                    top = phys_y;
                    right = phys_x + phys_w;
                    bottom = phys_y + phys_h;

                    work_left = phys_x
                        + ((work_left - info.monitorInfo.rcMonitor.left) as f64 * scale_x).round()
                            as i32;
                    work_top = phys_y
                        + ((work_top - info.monitorInfo.rcMonitor.top) as f64 * scale_y).round()
                            as i32;
                    work_right = phys_x
                        + ((work_right - info.monitorInfo.rcMonitor.left) as f64 * scale_x).round()
                            as i32;
                    work_bottom = phys_y
                        + ((work_bottom - info.monitorInfo.rcMonitor.top) as f64 * scale_y).round()
                            as i32;
                }
            }

            list.push(MonitorGeometry {
                handle_id: hmonitor.0 as usize as u64,
                left,
                top,
                right,
                bottom,
                work_left,
                work_top,
                work_right,
                work_bottom,
                dpi_x,
                dpi_y,
                primary,
            });
        }
        BOOL(1)
    }

    unsafe {
        let _ = EnumDisplayMonitors(
            None,
            None,
            Some(enum_monitor_proc),
            LPARAM(&mut monitors as *mut Vec<MonitorGeometry> as isize),
        );
    }

    monitors
}

/// Set thread/process DPI awareness to PerMonitorV2.
pub fn set_per_monitor_v2_dpi_awareness() -> bool {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if !user32.is_invalid() {
            if let Some(proc) = GetProcAddress(user32, s!("SetProcessDpiAwarenessContext")) {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 is ((HANDLE)-4)
                type SetDpiContextFn = unsafe extern "system" fn(isize) -> BOOL;
                let func: SetDpiContextFn = std::mem::transmute(proc);
                return func(-4).as_bool();
            }
        }
        false
    }
}

/// Query current process DPI awareness context name.
pub fn get_process_dpi_awareness() -> String {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if !user32.is_invalid() {
            if let (Some(get_ctx), Some(get_aware)) = (
                GetProcAddress(user32, s!("GetProcessDpiAwarenessContext")),
                GetProcAddress(user32, s!("GetAwarenessFromDpiAwarenessContext")),
            ) {
                type GetContextFn =
                    unsafe extern "system" fn(windows::Win32::Foundation::HANDLE) -> isize;
                type GetAwareFn = unsafe extern "system" fn(isize) -> i32;
                let get_context: GetContextFn = std::mem::transmute(get_ctx);
                let get_aware_fn: GetAwareFn = std::mem::transmute(get_aware);
                let ctx = get_context(windows::Win32::Foundation::HANDLE(std::ptr::null_mut()));
                let aware = get_aware_fn(ctx);
                return match aware {
                    2 => "PerMonitorV2".to_string(),
                    1 => "SystemAware".to_string(),
                    0 => "Unaware".to_string(),
                    _ => "PerMonitorV2".to_string(),
                };
            }
        }
        "PerMonitorV2".to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use inbrisk_core::ErrorCode;

    // SECTION 24: Pure Geometry Normalization Tests

    #[test]
    fn geometry_virtual_origin_zero_normalizes_correctly() {
        let bounds = PhysicalRect::new(0, 0, 1920, 1080);
        let pt = PhysicalPoint { x: 960, y: 540 };
        let norm = normalize_for_send_input(pt, bounds).expect("normalize");
        let denorm = denormalize_from_send_input(norm, bounds);
        assert!((denorm.x - 960).abs() <= 1);
        assert!((denorm.y - 540).abs() <= 1);
    }

    #[test]
    fn geometry_virtual_origin_negative_x_normalizes_correctly() {
        // Dual monitor: secondary on the left (-1920..0), primary (0..1920)
        // Total virtual bounds: left = -1920, top = 0, width = 3840, height = 1080
        let bounds = PhysicalRect::new(-1920, 0, 3840, 1080);

        // Point on the secondary monitor (left of primary origin)
        let pt_left = PhysicalPoint { x: -960, y: 540 };
        let norm = normalize_for_send_input(pt_left, bounds).expect("normalize");
        assert!(norm.x > 0 && norm.x < 32768);

        let denorm = denormalize_from_send_input(norm, bounds);
        assert!((denorm.x - (-960)).abs() <= 1);
        assert!((denorm.y - 540).abs() <= 1);
    }

    #[test]
    fn geometry_virtual_origin_negative_y_normalizes_correctly() {
        // Monitor placed above primary: top = -1440, height = 2520 (1440 + 1080)
        let bounds = PhysicalRect::new(0, -1440, 1920, 2520);
        let pt_top = PhysicalPoint { x: 960, y: -720 };
        let norm = normalize_for_send_input(pt_top, bounds).expect("normalize");

        let denorm = denormalize_from_send_input(norm, bounds);
        assert!((denorm.x - 960).abs() <= 1);
        assert!((denorm.y - (-720)).abs() <= 1);
    }

    #[test]
    fn geometry_point_at_virtual_top_left_normalizes_to_zero() {
        let bounds = PhysicalRect::new(-1920, -1080, 3840, 2160);
        let top_left = PhysicalPoint {
            x: bounds.left,
            y: bounds.top,
        };
        let norm = normalize_for_send_input(top_left, bounds).expect("normalize");
        assert_eq!(norm.x, 0);
        assert_eq!(norm.y, 0);
    }

    #[test]
    fn geometry_point_at_virtual_bottom_right_normalizes_to_65535() {
        let bounds = PhysicalRect::new(0, 0, 1920, 1080);
        // The last addressable pixel is left + width - 1, top + height - 1
        let bottom_right = PhysicalPoint {
            x: bounds.left + bounds.width - 1,
            y: bounds.top + bounds.height - 1,
        };
        let norm = normalize_for_send_input(bottom_right, bounds).expect("normalize");
        assert_eq!(norm.x, 65535);
        assert_eq!(norm.y, 65535);
    }

    #[test]
    fn geometry_center_normalizes_near_midpoint() {
        let bounds = PhysicalRect::new(100, 200, 2000, 1000);
        let center = bounds.center();
        let norm = normalize_for_send_input(center, bounds).expect("normalize");
        assert!((norm.x as i32 - 32767).abs() <= 100);
        assert!((norm.y as i32 - 32767).abs() <= 100);
    }

    #[test]
    fn geometry_out_of_bounds_rejected_before_final_clamp_or_handled_per_contract() {
        let bounds = PhysicalRect::new(0, 0, 1920, 1080);
        // Clamping happens at final SendInput normalization boundary
        let out_left = PhysicalPoint { x: -500, y: 500 };
        let norm_left = normalize_for_send_input(out_left, bounds).expect("normalize");
        assert_eq!(norm_left.x, 0);

        let out_right = PhysicalPoint { x: 3000, y: 500 };
        let norm_right = normalize_for_send_input(out_right, bounds).expect("normalize");
        assert_eq!(norm_right.x, 65535);
    }

    // SECTION 25: Mixed-DPI Scaling Tests

    #[test]
    fn geometry_client_logical_100_percent_to_physical() {
        let pt = LogicalPoint { x: 100.0, y: 200.0 };
        let phys = pure_logical_to_physical(pt, 96); // 100% scale
        assert_eq!(phys.x, 100);
        assert_eq!(phys.y, 200);
    }

    #[test]
    fn geometry_client_logical_150_percent_to_physical() {
        let pt = LogicalPoint { x: 100.0, y: 200.0 };
        let phys = pure_logical_to_physical(pt, 144); // 150% scale
        assert_eq!(phys.x, 150);
        assert_eq!(phys.y, 300);
    }

    #[test]
    fn geometry_client_logical_200_percent_to_physical() {
        let pt = LogicalPoint { x: 100.0, y: 200.0 };
        let phys = pure_logical_to_physical(pt, 192); // 200% scale
        assert_eq!(phys.x, 200);
        assert_eq!(phys.y, 400);
    }

    #[test]
    fn geometry_mixed_dpi_window_target_uses_target_window_dpi() {
        let pt = LogicalPoint { x: 50.0, y: 80.0 };
        let target_dpi_1 = 120; // 125%
        let target_dpi_2 = 168; // 175%

        let phys_1 = pure_logical_to_physical(pt, target_dpi_1);
        let phys_2 = pure_logical_to_physical(pt, target_dpi_2);

        assert_eq!(phys_1.x, (50.0f64 * 1.25f64).round() as i32);
        assert_eq!(phys_2.x, (50.0f64 * 1.75f64).round() as i32);
        assert_ne!(phys_1.x, phys_2.x);
    }

    #[test]
    fn geometry_window_moved_to_different_dpi_reevaluates_dpi() {
        let pt = LogicalPoint { x: 100.0, y: 100.0 };
        // Simulated window moving from 100% monitor to 150% monitor
        let dpi_on_mon1 = 96;
        let dpi_on_mon2 = 144;

        let phys_mon1 = pure_logical_to_physical(pt, dpi_on_mon1);
        let phys_mon2 = pure_logical_to_physical(pt, dpi_on_mon2);

        assert_eq!(phys_mon1, PhysicalPoint { x: 100, y: 100 });
        assert_eq!(phys_mon2, PhysicalPoint { x: 150, y: 150 });
    }

    // SECTION 26: Client / Screen Tests

    #[test]
    fn geometry_window_client_physical_to_virtual_screen() {
        let client_pt = PhysicalPoint { x: 50, y: 50 };
        let window_client_origin_screen = PhysicalPoint { x: 300, y: 200 };
        let screen_pt = pure_client_to_screen(client_pt, window_client_origin_screen);
        assert_eq!(screen_pt, PhysicalPoint { x: 350, y: 250 });
    }

    #[test]
    fn geometry_window_client_with_negative_screen_origin() {
        // Window located on a secondary monitor with negative coordinates
        let client_pt = PhysicalPoint { x: 100, y: 100 };
        let window_origin_negative = PhysicalPoint { x: -1500, y: 200 };
        let screen_pt = pure_client_to_screen(client_pt, window_origin_negative);
        assert_eq!(screen_pt, PhysicalPoint { x: -1400, y: 300 });
    }

    #[test]
    fn geometry_destroyed_hwnd_returns_target_gone() {
        let fake_destroyed_hwnd = Hwnd(0xDEADBEEF);
        let res =
            client_physical_to_virtual_screen(fake_destroyed_hwnd, PhysicalPoint { x: 10, y: 10 });
        assert!(res.is_err());
        let err = res.unwrap_err();
        assert_eq!(err.code, ErrorCode::TargetGone);
    }

    // SECTION 30: Current User Dual-Monitor Bug Regression Test

    #[test]
    fn geometry_regression_virtual_desk_flag_multi_monitor_y_does_not_map_to_primary_height() {
        // Scenario from user incident:
        // Virtual desktop height = 1944 (e.g. secondary monitor stacked or mixed resolution)
        // Primary monitor height = 1080
        // Target physical coordinate: y = 523
        let virtual_bounds = PhysicalRect::new(0, 0, 3840, 1944);
        let target = PhysicalPoint { x: 1920, y: 523 };

        // 1. Correct virtual desktop normalization
        let norm = normalize_for_send_input(target, virtual_bounds).expect("normalize");

        // 2. Denormalize with virtual desktop height: exactly preserves 523!
        let denorm_virtual = denormalize_from_send_input(norm, virtual_bounds);
        assert_eq!(
            denorm_virtual.y, 523,
            "Target y=523 must be preserved exactly through virtual desktop normalization"
        );

        // 3. Prove that the OLD bug (which mapped via primary monitor height 1080) would produce y ≈ 290
        let old_primary_height = 1080.0;
        let old_erroneous_y =
            ((norm.y as f64 * (old_primary_height - 1.0)) / 65535.0).round() as i32;
        assert_eq!(
            old_erroneous_y, 290,
            "Demonstrates the old bug mapped target y=523 to y=290 when using primary monitor height"
        );

        // 4. Assert current behavior does NOT produce the broken 290 coordinate
        assert_ne!(
            denorm_virtual.y, old_erroneous_y,
            "Virtual desktop normalization must not collapse to primary monitor height"
        );
    }

    #[test]
    fn live_read_only_geometry_probe() {
        let bounds = virtual_screen_bounds();
        let monitors = enumerate_monitors();
        let awareness = get_process_dpi_awareness();
        println!("\n=== LIVE GEOMETRY PROBE ===");
        println!(
            "Virtual Screen Bounds: left={}, top={}, width={}, height={}",
            bounds.left, bounds.top, bounds.width, bounds.height
        );
        println!("Monitor Count: {}", monitors.len());
        for (i, m) in monitors.iter().enumerate() {
            println!(
                "  Monitor {}: bounds=[left={}, top={}, width={}, height={}], dpi=({}, {}), primary={}",
                i,
                m.left,
                m.top,
                m.width(),
                m.height(),
                m.dpi_x,
                m.dpi_y,
                m.primary
            );
        }
        println!("Process DPI Awareness: {}", awareness);
        let windows = crate::window::enumerate_top_level();
        if let Some(w) = windows.iter().find(|w| w.visible && w.hwnd.0 != 0) {
            let dpi = get_dpi_for_window(w.hwnd);
            let pt_res =
                to_virtual_screen_physical(inbrisk_core::Point::client_logical(w.hwnd, 10.0, 10.0));
            println!(
                "Sample Window HWND: {:?} ('{}'), DPI: {}, ClientPoint(10, 10) -> ScreenPhysical: {:?}",
                w.hwnd, w.title, dpi, pt_res
            );
        }
        println!("=== END PROBE ===\n");
    }
}
