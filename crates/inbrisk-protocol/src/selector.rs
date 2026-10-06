use serde::{Deserialize, Serialize};

use inbrisk_core::Hwnd;

/// Relational element selector.
///
/// Every field is optional; a selector with no fields matches nothing (the
/// runtime rejects it as `InvalidPlan` rather than returning the whole tree).
#[derive(Debug, Clone, Default, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub struct Selector {
    /// UIA control type, e.g. `button`, `edit`, `document`, `menuitem`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub role: Option<String>,
    /// Accessible name; substring match unless `exact` is set.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Regular expression over the accessible name.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name_regex: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub automation_id: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub class_name: Option<String>,
    /// Requires the element's name/value to contain this text.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub text: Option<String>,
    /// Restrict the search to the subtree of another selector.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub within: Option<Box<Selector>>,
    /// Walk up from a match instead of down (find the row/dialog containing it).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ancestor: Option<Box<Selector>>,
    /// Pick the n-th match (0-based) instead of failing on ambiguity.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub index: Option<usize>,
    /// Limit the search to a specific top-level window.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub window: Option<Box<Selector>>,
    /// Limit the search to one window handle.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<Hwnd>,
    /// Limit the search to a process image name, e.g. `notepad.exe`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub process: Option<String>,
    /// Force a backend: `uia`, `win32`, `cdp`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub backend: Option<String>,
    /// Exact instead of substring matching for `name`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exact: Option<bool>,
    /// Upper bound for the search itself.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
    /// What to do when several elements match.
    #[serde(default)]
    pub policy: MatchPolicy,
}

impl Selector {
    pub fn role(role: impl Into<String>) -> Self {
        Self {
            role: Some(role.into()),
            ..Default::default()
        }
    }

    pub fn name(name: impl Into<String>) -> Self {
        Self {
            name: Some(name.into()),
            ..Default::default()
        }
    }

    pub fn role_name(role: impl Into<String>, name: impl Into<String>) -> Self {
        Self {
            role: Some(role.into()),
            name: Some(name.into()),
            ..Default::default()
        }
    }

    pub fn in_window(mut self, window: Selector) -> Self {
        self.window = Some(Box::new(window));
        self
    }

    pub fn with_hwnd(mut self, hwnd: Hwnd) -> Self {
        self.hwnd = Some(hwnd);
        self
    }

    pub fn nth(mut self, index: usize) -> Self {
        self.index = Some(index);
        self
    }

    /// A selector that constrains nothing is a bug, not a wildcard.
    pub fn is_empty(&self) -> bool {
        self.role.is_none()
            && self.name.is_none()
            && self.name_regex.is_none()
            && self.automation_id.is_none()
            && self.class_name.is_none()
            && self.text.is_none()
            && self.within.is_none()
            && self.ancestor.is_none()
            && self.window.is_none()
            && self.hwnd.is_none()
            && self.process.is_none()
    }

    /// Human readable form used in error messages and logs.
    pub fn describe(&self) -> String {
        let mut parts: Vec<String> = Vec::new();
        if let Some(v) = &self.role {
            parts.push(format!("role={v}"));
        }
        if let Some(v) = &self.name {
            parts.push(format!("name~{v}"));
        }
        if let Some(v) = &self.name_regex {
            parts.push(format!("name/{v}/"));
        }
        if let Some(v) = &self.automation_id {
            parts.push(format!("automationId={v}"));
        }
        if let Some(v) = &self.class_name {
            parts.push(format!("class={v}"));
        }
        if let Some(v) = &self.text {
            parts.push(format!("text~{v}"));
        }
        if let Some(v) = &self.process {
            parts.push(format!("process={v}"));
        }
        if let Some(v) = &self.hwnd {
            parts.push(format!("hwnd={v}"));
        }
        if let Some(v) = &self.window {
            parts.push(format!("window({})", v.describe()));
        }
        if let Some(i) = self.index {
            parts.push(format!("#{i}"));
        }
        if parts.is_empty() {
            "<empty selector>".to_string()
        } else {
            parts.join(" ")
        }
    }
}

/// What to do when more than one element matches.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum MatchPolicy {
    /// Fail with `CloseMatchesFound` and list the candidates.
    #[default]
    Strict,
    /// Take the first match in tree order.
    First,
    /// Prefer a visible/enabled match, then the first.
    Best,
}
