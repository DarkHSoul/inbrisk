//! Windows platform layer.
//!
//! Everything here is *cheap first*: Win32 calls that answer a question in
//! microseconds must never be routed through UIA. The execution router in
//! `inbrisk-runtime` depends on this: window discovery, focus, process
//! identity, monitors, launching and raw input all live here.

pub mod conpty;
pub mod desktop;
pub mod event;
pub mod geometry;
pub mod input_raw;
pub mod launch;
pub mod process;
pub mod window;

use inbrisk_core::Hwnd;
use windows::Win32::Foundation::HWND;

pub use desktop::{detect_execution_capabilities, probe_desktop_context, DesktopContextProbe};
pub use event::{WinEvent, WinEventKind, WinEventMonitor};
pub use launch::{launch, resolve_app, LaunchMethod, LaunchResult};
pub use process::{ProcessInfo, ProcessTable};
pub use window::RawWindow;

/// Convert the core's FFI-free handle into a real `HWND`.
#[inline]
pub fn to_hwnd(h: Hwnd) -> HWND {
    HWND(h.0 as *mut core::ffi::c_void)
}

/// Convert a real `HWND` into the core's FFI-free handle.
#[inline]
pub fn from_hwnd(h: HWND) -> Hwnd {
    Hwnd(h.0 as isize)
}

/// Opt the process into per-monitor DPI awareness so that screen coordinates
/// reported to agents match physical pixels.
pub fn init_dpi_awareness() {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{GetModuleHandleA, GetProcAddress};
        if let Ok(user32) = GetModuleHandleA(s!("user32.dll")) {
            if let Some(proc) = GetProcAddress(user32, s!("SetProcessDpiAwarenessContext")) {
                let func: unsafe extern "system" fn(isize) -> windows::core::BOOL =
                    std::mem::transmute(proc);
                let _ = func(-4); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            }
        }
    }
}
