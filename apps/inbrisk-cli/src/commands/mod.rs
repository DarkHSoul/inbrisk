//! One module per CLI command.
//!
//! A command is a thin translation layer: shape a request, call the SDK, render
//! the answer. No automation logic lives here — that all sits in the runtime.

pub mod act;
pub mod cancel;
pub mod doctor;
pub mod endpoint;
pub mod events;
pub mod fast_path;
pub mod find;
pub mod observe;
pub mod ping;
pub mod read;
pub mod run;
pub mod skill;
pub mod status;
pub mod terminal;
pub mod windows;

use inbrisk_core::ElementRef;
use inbrisk_sdk::Runtime;

use crate::error::{CliError, CliResult};
use crate::output;

/// Identity the runtime sees in `status` and `doctor`.
pub const CLIENT_NAME: &str = "inbrisk-cli";
/// The CLI is versioned with the workspace, like every other crate.
pub const CLIENT_VERSION: &str = env!("CARGO_PKG_VERSION");

/// Connects to the runtime, optionally asking for the event ring.
pub fn connect(wants_events: bool) -> CliResult<Runtime> {
    Runtime::connect_as(CLIENT_NAME, CLIENT_VERSION, wants_events).map_err(CliError::from)
}

/// One-line element rendering, shared by `find`, `observe`, `read` and `act`.
pub fn format_element(element: &ElementRef) -> String {
    format!(
        "{}  id={}  hwnd={}  gen={}  {}  {}{}  backend={}",
        element.label(),
        element.id,
        element.hwnd,
        element.generation,
        output::fmt_rect(&element.bounds),
        if element.enabled {
            "enabled"
        } else {
            "disabled"
        },
        if element.offscreen { " offscreen" } else { "" },
        backend_name(&element.backend)
    )
}

/// Backends may leave the field empty for pure-Win32 answers.
pub fn backend_name(backend: &str) -> &str {
    if backend.is_empty() {
        "-"
    } else {
        backend
    }
}

/// Compact one-line JSON, used for arbitrary property values and step outputs.
pub fn compact_json(value: &serde_json::Value) -> String {
    serde_json::to_string(value).unwrap_or_else(|_| "null".to_string())
}

/// `a, b, c`, or `-` when the list is empty.
pub fn list_or_dash(items: &[String]) -> String {
    if items.is_empty() {
        "-".to_string()
    } else {
        items.join(", ")
    }
}

/// Versions are optional in the protocol; never print an empty column.
pub fn version_or_unknown(version: &str) -> &str {
    if version.is_empty() {
        "unknown"
    } else {
        version
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use inbrisk_core::{Hwnd, Rect};

    fn element(backend: &str, name: &str) -> ElementRef {
        ElementRef {
            id: 12,
            hwnd: Hwnd(0x2A),
            process_id: 4,
            generation: 3,
            runtime_id: vec![42],
            role: "button".into(),
            name: name.into(),
            automation_id: String::new(),
            class_name: "Button".into(),
            bounds: Rect::new(10, 20, 80, 24),
            enabled: true,
            offscreen: false,
            backend: backend.into(),
        }
    }

    #[test]
    fn element_line_carries_the_handle_and_generation() {
        let line = format_element(&element("uia", "Save"));
        assert!(line.contains("button \"Save\""));
        assert!(line.contains("id=12"));
        assert!(line.contains("hwnd=0x2A"));
        assert!(line.contains("gen=3"));
        assert!(line.contains("10,20 80x24"));
        assert!(line.contains("backend=uia"));
    }

    #[test]
    fn empty_backend_renders_as_a_dash() {
        assert!(format_element(&element("", "Save")).contains("backend=-"));
        assert_eq!(backend_name("win32"), "win32");
    }

    #[test]
    fn empty_lists_and_versions_are_marked() {
        assert_eq!(list_or_dash(&[]), "-");
        assert_eq!(list_or_dash(&["uia".into(), "win32".into()]), "uia, win32");
        assert_eq!(version_or_unknown(""), "unknown");
        assert_eq!(version_or_unknown("0.1.0"), "0.1.0");
    }
}
