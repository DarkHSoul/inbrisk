//! Canonical desktop capability probe and context detection.
//!
//! Determines whether the current process/thread has a valid path to
//! the active interactive Windows desktop (`WinSta0\Default`).

use inbrisk_core::{ExecutionCapabilities, Result};

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DesktopContextProbe {
    pub process_session_id: u32,
    pub current_window_station: Option<String>,
    pub current_desktop: Option<String>,
    pub input_desktop_available: bool,
    pub fresh_thread_can_bind_input_desktop: bool,
}

fn get_user_object_name(handle: windows::Win32::Foundation::HANDLE) -> Option<String> {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if user32.is_invalid() {
            return None;
        }
        let proc = GetProcAddress(user32, s!("GetUserObjectInformationW"))?;
        type GetUserObjectInformationWFn = unsafe extern "system" fn(
            windows::Win32::Foundation::HANDLE,
            i32,
            *mut core::ffi::c_void,
            u32,
            *mut u32,
        )
            -> windows::core::BOOL;
        let func: GetUserObjectInformationWFn = std::mem::transmute(proc);

        let mut needed: u32 = 0;
        let _ = func(
            handle,
            2, /* UOI_NAME */
            std::ptr::null_mut(),
            0,
            &mut needed,
        );
        if needed == 0 {
            return None;
        }
        let mut buf = vec![0u16; (needed as usize / 2) + 1];
        let ok = func(
            handle,
            2, /* UOI_NAME */
            buf.as_mut_ptr() as *mut _,
            needed,
            &mut needed,
        );
        if !ok.as_bool() {
            return None;
        }
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        String::from_utf16(&buf[..len]).ok()
    }
}

fn get_process_session_id(pid: u32) -> u32 {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let kernel32 = GetModuleHandleA(s!("kernel32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("kernel32.dll")).unwrap_or_default());
        if !kernel32.is_invalid() {
            if let Some(proc) = GetProcAddress(kernel32, s!("ProcessIdToSessionId")) {
                type ProcessIdToSessionIdFn =
                    unsafe extern "system" fn(u32, *mut u32) -> windows::core::BOOL;
                let func: ProcessIdToSessionIdFn = std::mem::transmute(proc);
                let mut session_id: u32 = 0;
                if func(pid, &mut session_id).as_bool() {
                    return session_id;
                }
            }
        }
        0
    }
}

fn probe_fresh_thread_bind_input_desktop() -> (bool, bool) {
    let handle = std::thread::spawn(|| unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if user32.is_invalid() {
            return (false, false);
        }
        let open_desk = GetProcAddress(user32, s!("OpenInputDesktop"));
        let set_desk = GetProcAddress(user32, s!("SetThreadDesktop"));
        let close_desk = GetProcAddress(user32, s!("CloseDesktop"));

        if let (Some(open_p), Some(set_p), Some(close_p)) = (open_desk, set_desk, close_desk) {
            type OpenDesktopFn = unsafe extern "system" fn(
                u32,
                windows::core::BOOL,
                u32,
            )
                -> windows::Win32::Foundation::HANDLE;
            type SetDesktopFn = unsafe extern "system" fn(
                windows::Win32::Foundation::HANDLE,
            ) -> windows::core::BOOL;
            type CloseDesktopFn = unsafe extern "system" fn(
                windows::Win32::Foundation::HANDLE,
            ) -> windows::core::BOOL;

            let open_f: OpenDesktopFn = std::mem::transmute(open_p);
            let set_f: SetDesktopFn = std::mem::transmute(set_p);
            let close_f: CloseDesktopFn = std::mem::transmute(close_p);

            let desk = open_f(0, windows::core::BOOL(0), 0x01FF);
            if desk.is_invalid() || desk.0.is_null() {
                return (false, false);
            }
            let bound = set_f(desk).as_bool();
            let _ = close_f(desk);
            (true, bound)
        } else {
            (false, false)
        }
    });

    handle.join().unwrap_or((false, false))
}

fn get_current_context() -> (Option<String>, Option<String>) {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{
            GetModuleHandleA, GetProcAddress, LoadLibraryA,
        };
        let user32 = GetModuleHandleA(s!("user32.dll"))
            .unwrap_or_else(|_| LoadLibraryA(s!("user32.dll")).unwrap_or_default());
        if user32.is_invalid() {
            return (None, None);
        }
        let get_winsta = GetProcAddress(user32, s!("GetProcessWindowStation"));
        let get_desk = GetProcAddress(user32, s!("GetThreadDesktop"));
        let get_tid = GetProcAddress(
            GetModuleHandleA(s!("kernel32.dll")).unwrap_or_default(),
            s!("GetCurrentThreadId"),
        );

        let winsta_name = if let Some(p) = get_winsta {
            type GetWinstaFn = unsafe extern "system" fn() -> windows::Win32::Foundation::HANDLE;
            let f: GetWinstaFn = std::mem::transmute(p);
            let hwinsta = f();
            if !hwinsta.is_invalid() && !hwinsta.0.is_null() {
                get_user_object_name(hwinsta)
            } else {
                None
            }
        } else {
            None
        };

        let desk_name = if let (Some(dp), Some(tp)) = (get_desk, get_tid) {
            type GetThreadIdFn = unsafe extern "system" fn() -> u32;
            type GetThreadDesktopFn =
                unsafe extern "system" fn(u32) -> windows::Win32::Foundation::HANDLE;
            let tid_f: GetThreadIdFn = std::mem::transmute(tp);
            let desk_f: GetThreadDesktopFn = std::mem::transmute(dp);
            let hdesk = desk_f(tid_f());
            if !hdesk.is_invalid() && !hdesk.0.is_null() {
                get_user_object_name(hdesk)
            } else {
                None
            }
        } else {
            None
        };

        (winsta_name, desk_name)
    }
}

/// Perform canonical detection of desktop and session context.
pub fn probe_desktop_context() -> Result<DesktopContextProbe> {
    let pid = std::process::id();
    let session_id = get_process_session_id(pid);
    let (winsta, desk) = get_current_context();
    let (input_avail, can_bind) = probe_fresh_thread_bind_input_desktop();

    Ok(DesktopContextProbe {
        process_session_id: session_id,
        current_window_station: winsta,
        current_desktop: desk,
        input_desktop_available: input_avail,
        fresh_thread_can_bind_input_desktop: can_bind,
    })
}

/// Canonical mapping from desktop probe to runtime execution capabilities.
pub fn detect_execution_capabilities() -> Result<ExecutionCapabilities> {
    let probe = probe_desktop_context()?;
    let interactive_desktop =
        probe.input_desktop_available && probe.fresh_thread_can_bind_input_desktop;

    Ok(ExecutionCapabilities {
        interactive_desktop,
        physical_input: interactive_desktop,
        window_enumeration: interactive_desktop,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn probe_returns_valid_structure_without_panicking() {
        let probe = probe_desktop_context().expect("probe_desktop_context should succeed");
        // Verify invariants: session ID is non-zero in user session (or 0 for system/services)
        // Names are either valid strings or None, no panic or memory corruptions occurred.
        if let Some(ref w) = probe.current_window_station {
            assert!(!w.is_empty());
        }
        if let Some(ref d) = probe.current_desktop {
            assert!(!d.is_empty());
        }
    }

    #[test]
    fn probe_returns_native_desktop_context_probe_type() {
        // Assert that probe_desktop_context returns our native domain struct
        let probe: DesktopContextProbe =
            probe_desktop_context().expect("probe_desktop_context should succeed");
        assert!(probe.process_session_id <= 100_000);
    }

    #[test]
    fn capabilities_detection_matches_probe_consistency() {
        let probe = probe_desktop_context().expect("probe should succeed");
        let caps = detect_execution_capabilities().expect("caps should succeed");

        let expected_interactive =
            probe.input_desktop_available && probe.fresh_thread_can_bind_input_desktop;
        assert_eq!(caps.interactive_desktop, expected_interactive);
        assert_eq!(caps.physical_input, expected_interactive);
        assert_eq!(caps.window_enumeration, expected_interactive);
    }
}
