//! UIA runtime ids.
//!
//! A runtime id is the only stable identity Windows gives us for an element
//! inside one generation: it is what makes "resolve, then act" safe.

use windows::Win32::System::Ole::{
    SafeArrayAccessData, SafeArrayDestroy, SafeArrayGetLBound, SafeArrayGetUBound,
    SafeArrayUnaccessData,
};
use windows::Win32::UI::Accessibility::IUIAutomationElement;

/// Extract the runtime id as a plain `Vec<i32>` (Send).
pub fn runtime_id_of(el: &IUIAutomationElement) -> Vec<i32> {
    unsafe {
        let Ok(psa) = el.GetRuntimeId() else {
            return Vec::new();
        };
        if psa.is_null() {
            return Vec::new();
        }
        let mut out = Vec::new();
        let mut data: *mut core::ffi::c_void = std::ptr::null_mut();
        if SafeArrayAccessData(psa, &mut data).is_ok() && !data.is_null() {
            let lower = SafeArrayGetLBound(psa, 1).unwrap_or(0);
            let upper = SafeArrayGetUBound(psa, 1).unwrap_or(-1);
            if upper >= lower {
                let len = (upper - lower + 1) as usize;
                if len <= 64 {
                    out = std::slice::from_raw_parts(data as *const i32, len).to_vec();
                }
            }
            let _ = SafeArrayUnaccessData(psa);
        }
        let _ = SafeArrayDestroy(psa);
        out
    }
}

/// True when two runtime ids describe the same element.
pub fn same_element(a: &[i32], b: &[i32]) -> bool {
    !a.is_empty() && a == b
}
