use serde::{Deserialize, Serialize};

use crate::geometry::Rect;
use crate::ids::Hwnd;
use crate::ownership::{LifecycleIntent, Ownership};

/// Cheap Win32-level view of a top-level window.
///
/// This is the *only* window representation the core knows about: it costs a
/// handful of user32 calls and never touches UIA.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WindowRef {
    pub hwnd: Hwnd,
    pub process_id: u32,
    #[serde(default)]
    pub process_name: String,
    pub title: String,
    pub class_name: String,
    pub bounds: Rect,
    pub visible: bool,
    pub minimized: bool,
    pub maximized: bool,
    pub foreground: bool,
    pub tool_window: bool,
    pub ownership: Ownership,
    pub intent: LifecycleIntent,
    pub state_version: u64,
}

impl WindowRef {
    pub fn matches_title(&self, needle: &str) -> bool {
        self.title.to_lowercase().contains(&needle.to_lowercase())
    }
}
