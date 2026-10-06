use serde::{Deserialize, Serialize};

use crate::geometry::Rect;
use crate::ids::{ElementId, Generation, Hwnd};

/// A resolved automation element handle.
///
/// Handles are only valid for the `generation` they were minted in; the
/// runtime rejects anything older with `StaleState` before any side effect.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ElementRef {
    pub id: ElementId,
    pub hwnd: Hwnd,
    pub process_id: u32,
    pub generation: Generation,
    /// UIA runtime id — survives re-enumeration inside the same generation.
    pub runtime_id: Vec<i32>,
    pub role: String,
    pub name: String,
    #[serde(default)]
    pub automation_id: String,
    #[serde(default)]
    pub class_name: String,
    pub bounds: Rect,
    pub enabled: bool,
    pub offscreen: bool,
    /// Which backend produced this handle (`win32`, `uia`, `cdp`).
    pub backend: String,
}

impl ElementRef {
    pub fn is_stale(&self, current_generation: Generation) -> bool {
        self.generation != current_generation
    }

    /// Short human/agent readable label, e.g. `button "Save"`.
    pub fn label(&self) -> String {
        if self.name.is_empty() {
            format!("{}[{}]", self.role, self.id)
        } else {
            format!("{} \"{}\"", self.role, self.name)
        }
    }
}
