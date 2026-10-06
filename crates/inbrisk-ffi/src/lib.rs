//! C ABI over the Rust SDK.
//!
//! `inbrisk_status` and `inbrisk_run_json` connect for the call, the same way
//! the CLI does. They do not start a second runtime and they do not keep a
//! process-global session. A return value of `-1` means the buffer was too
//! small, the pointer was null, or the runtime rejected the call. On success
//! the return value is the number of JSON bytes written. The buffer is not
//! NUL-terminated.

use std::os::raw::{c_char, c_int};

use inbrisk_protocol::action::Plan;
use serde_json::Value;

/// Write a status report. Returns the byte count, or -1.
///
/// # Safety
/// `buf` must point at `cap` writable bytes when it is non-null.
#[no_mangle]
pub unsafe extern "C" fn inbrisk_status(buf: *mut u8, cap: usize) -> isize {
    if buf.is_null() || cap == 0 {
        return -1;
    }
    let runtime =
        match inbrisk_sdk::Runtime::connect_as("inbrisk-ffi", env!("CARGO_PKG_VERSION"), false) {
            Ok(runtime) => runtime,
            Err(_) => return -1,
        };
    match runtime.status() {
        Ok(report) => write_json(buf, cap, &report),
        Err(_) => -1,
    }
}

/// Run a plan JSON object. `dry_run != 0` validates without side effects.
/// Returns the byte count of the outcome JSON, or -1.
///
/// Accepted shapes match the CLI: `{"steps":[...]}`, `{"op":"run","steps":[...]}`
/// and `{"plan":{"steps":[...]}}`.
///
/// # Safety
/// `plan_json` must be a NUL-terminated string. `buf` must point at `cap`
/// writable bytes when it is non-null.
#[no_mangle]
pub unsafe extern "C" fn inbrisk_run_json(
    plan_json: *const c_char,
    dry_run: c_int,
    buf: *mut u8,
    cap: usize,
) -> isize {
    if plan_json.is_null() || buf.is_null() || cap == 0 {
        return -1;
    }
    let raw = match std::ffi::CStr::from_ptr(plan_json).to_str() {
        Ok(raw) => raw,
        Err(_) => return -1,
    };
    let plan = match parse_plan(raw) {
        Ok(plan) => plan,
        Err(_) => return -1,
    };
    let runtime =
        match inbrisk_sdk::Runtime::connect_as("inbrisk-ffi", env!("CARGO_PKG_VERSION"), false) {
            Ok(runtime) => runtime,
            Err(_) => return -1,
        };
    let outcome = if dry_run != 0 {
        runtime.dry_run(plan)
    } else {
        runtime.run(plan)
    };
    match outcome {
        Ok(outcome) => write_json(buf, cap, &outcome),
        Err(_) => -1,
    }
}

fn write_json<T: serde::Serialize>(buf: *mut u8, cap: usize, value: &T) -> isize {
    let bytes = match serde_json::to_vec(value) {
        Ok(bytes) => bytes,
        Err(_) => return -1,
    };
    if bytes.len() > cap {
        return -1;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), buf, bytes.len());
    }
    bytes.len() as isize
}

pub fn parse_plan(raw: &str) -> Result<Plan, String> {
    let value: Value =
        serde_json::from_str(raw).map_err(|e| format!("plan is not valid JSON: {e}"))?;
    let mut outer = match value {
        Value::Object(map) => map,
        _ => return Err("plan must be a JSON object with a steps array".into()),
    };
    let outer_name = match outer.get("name") {
        Some(Value::String(name)) => Some(name.clone()),
        _ => None,
    };
    if let Some(inner) = outer.remove("plan") {
        outer = match inner {
            Value::Object(map) => map,
            _ => return Err("'plan' must be an object".into()),
        };
    }
    outer.remove("op");
    if !outer.contains_key("steps") {
        return Err("plan JSON has no steps array".into());
    }
    if !outer.contains_key("name") {
        if let Some(name) = outer_name {
            outer.insert("name".into(), Value::String(name));
        }
    }
    serde_json::from_value(Value::Object(outer))
        .map_err(|e| format!("plan is not a valid plan: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_the_cli_plan_shapes() {
        let bare = parse_plan(r#"{"steps":[{"action":"sleep","ms":5}]}"#).unwrap();
        assert_eq!(bare.steps.len(), 1);
        let wire = parse_plan(r#"{"op":"run","name":"boot","steps":[{"action":"sleep","ms":1}]}"#)
            .unwrap();
        assert_eq!(wire.name.as_deref(), Some("boot"));
        let wrapped =
            parse_plan(r#"{"plan":{"name":"inner","steps":[{"action":"sleep","ms":1}]}}"#).unwrap();
        assert_eq!(wrapped.name.as_deref(), Some("inner"));
    }
}
