use serde::{Deserialize, Serialize};

use inbrisk_core::{Expect, LifecycleIntent};

use crate::selector::Selector;

/// Canonical coordinate space wire DTO.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CoordinateSpaceDto {
    VirtualScreenPhysical,
    WindowClientPhysical,
    WindowClientLogical,
}

impl From<inbrisk_core::CoordinateSpace> for CoordinateSpaceDto {
    fn from(s: inbrisk_core::CoordinateSpace) -> Self {
        match s {
            inbrisk_core::CoordinateSpace::VirtualScreenPhysical => {
                CoordinateSpaceDto::VirtualScreenPhysical
            }
            inbrisk_core::CoordinateSpace::WindowClientPhysical => {
                CoordinateSpaceDto::WindowClientPhysical
            }
            inbrisk_core::CoordinateSpace::WindowClientLogical => {
                CoordinateSpaceDto::WindowClientLogical
            }
        }
    }
}

impl From<CoordinateSpaceDto> for inbrisk_core::CoordinateSpace {
    fn from(d: CoordinateSpaceDto) -> Self {
        match d {
            CoordinateSpaceDto::VirtualScreenPhysical => {
                inbrisk_core::CoordinateSpace::VirtualScreenPhysical
            }
            CoordinateSpaceDto::WindowClientPhysical => {
                inbrisk_core::CoordinateSpace::WindowClientPhysical
            }
            CoordinateSpaceDto::WindowClientLogical => {
                inbrisk_core::CoordinateSpace::WindowClientLogical
            }
        }
    }
}

fn default_coordinate_space() -> CoordinateSpaceDto {
    CoordinateSpaceDto::VirtualScreenPhysical
}

/// Explicit coordinate point target wire DTO.
#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
pub struct PointTarget {
    pub x: f64,
    pub y: f64,
    #[serde(default = "default_coordinate_space")]
    pub space: CoordinateSpaceDto,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<u64>,
}

impl PointTarget {
    pub fn virtual_screen(x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpaceDto::VirtualScreenPhysical,
            hwnd: None,
        }
    }

    pub fn client_physical(hwnd: u64, x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpaceDto::WindowClientPhysical,
            hwnd: Some(hwnd),
        }
    }

    pub fn window_client_physical(hwnd: u64, x: f64, y: f64) -> Self {
        Self::client_physical(hwnd, x, y)
    }

    pub fn client_logical(hwnd: u64, x: f64, y: f64) -> Self {
        Self {
            x,
            y,
            space: CoordinateSpaceDto::WindowClientLogical,
            hwnd: Some(hwnd),
        }
    }

    pub fn window_client_logical(hwnd: u64, x: f64, y: f64) -> Self {
        Self::client_logical(hwnd, x, y)
    }

    pub fn validate(&self) -> inbrisk_core::Result<()> {
        match self.space {
            CoordinateSpaceDto::VirtualScreenPhysical => {
                if self.hwnd.is_some() {
                    return Err(inbrisk_core::InbriskError::invalid_plan(
                        "VirtualScreenPhysical coordinate must not specify a target HWND",
                    ));
                }
            }
            CoordinateSpaceDto::WindowClientPhysical | CoordinateSpaceDto::WindowClientLogical => {
                match self.hwnd {
                    Some(h) if h != 0 => {}
                    _ => {
                        return Err(inbrisk_core::InbriskError::invalid_plan(format!(
                            "{:?} coordinate requires a valid target HWND",
                            self.space
                        )));
                    }
                }
            }
        }
        Ok(())
    }

    pub fn to_core_point(&self) -> inbrisk_core::Point {
        inbrisk_core::Point {
            x: self.x,
            y: self.y,
            space: self.space.into(),
            hwnd: self.hwnd.map(|h| inbrisk_core::Hwnd(h as isize)),
        }
    }
}

/// What a step points at: an explicit coordinate point, a reference (`$0`),
/// or an inline selector.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum TargetRef {
    Point(PointTarget),
    Reference(String),
    Selector(Box<Selector>),
}

impl TargetRef {
    pub fn reference(name: impl Into<String>) -> Self {
        TargetRef::Reference(name.into())
    }

    pub fn point(x: i32, y: i32) -> Self {
        TargetRef::Point(PointTarget::virtual_screen(x as f64, y as f64))
    }

    pub fn point_target(target: PointTarget) -> Self {
        TargetRef::Point(target)
    }

    pub fn as_reference(&self) -> Option<&str> {
        match self {
            TargetRef::Reference(r) => Some(r.as_str()),
            TargetRef::Selector(_) | TargetRef::Point(_) => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum MouseButton {
    #[default]
    Left,
    Right,
    Middle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ScrollDirection {
    Up,
    Down,
    Left,
    Right,
}

/// Where a drag should end.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum DropTarget {
    Point(PointTarget),
    Reference(String),
    Selector(Box<Selector>),
}

/// How a `select` step picks an option.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum SelectOption {
    /// Option's display text.
    Text(String),
    /// Zero-based index.
    Index(usize),
}

/// Which window state a `wait` step is waiting for.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum WindowWaitState {
    #[default]
    Exists,
    Visible,
    Foreground,
    /// Wait until the window is gone (closed by the user or by us).
    Gone,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ElementWaitState {
    #[default]
    Exists,
    Visible,
    Enabled,
    Gone,
}

/// Event-driven wait specification.
///
/// Sleeps are deliberately not the primary mechanism: the runtime subscribes to
/// UIA/WinEvent/process notifications and wakes the plan the moment the
/// condition becomes true.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "until", rename_all = "snake_case")]
pub enum WaitSpec {
    /// Wait for a top-level window matching the selector.
    Window {
        selector: Selector,
        #[serde(default)]
        state: WindowWaitState,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        timeout_ms: Option<u64>,
    },
    /// Wait for an element inside the tree.
    Element {
        selector: Selector,
        #[serde(default)]
        state: ElementWaitState,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        timeout_ms: Option<u64>,
    },
    /// Wait until an element's value equals the given text.
    Value {
        target: TargetRef,
        equals: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        timeout_ms: Option<u64>,
    },
    /// Wait for a process to exit.
    ProcessExit {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        name: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        process_id: Option<u32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        timeout_ms: Option<u64>,
    },
    /// Explicit quiescence window. Discouraged outside of legacy migrations.
    Idle { ms: u64 },
}

/// Alias kept for readability in agent-facing docs.
pub type WaitUntil = WaitSpec;

/// A single plan step.
///
/// The tag is `action`, matching the wire examples:
/// `{ "action": "launch", "app": "notepad" }`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "action", rename_all = "snake_case")]
pub enum Step {
    /// Start an application and (optionally) wait for its window.
    Launch {
        app: String,
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        args: Vec<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        cwd: Option<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        wait_for_window: Option<Selector>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        timeout_ms: Option<u64>,
        /// Lifecycle contract for the new window. Omitted means `Unknown`.
        /// Cleanup is allowed only for an explicit `ephemeral` window that
        /// this runtime opened. Unknown, reusable, task_artifact and
        /// user_useful are kept.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        intent: Option<LifecycleIntent>,
        /// Store the resulting window handle under this name.
        #[serde(default, skip_serializing_if = "Option::is_none")]
        store: Option<String>,
    },
    /// Bring a window/element to the foreground.
    Focus {
        target: TargetRef,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        expect: Option<Expect>,
    },
    /// Resolve a selector and store the handle(s) without acting.
    Find {
        selector: Selector,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        store: Option<String>,
        #[serde(default)]
        all: bool,
    },
    /// Activate an element through its semantic pattern.
    Invoke {
        target: TargetRef,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        expect: Option<Expect>,
    },
    /// Set a value through `ValuePattern` / `TextPattern` (no keystrokes).
    SetValue {
        target: TargetRef,
        value: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        expect: Option<Expect>,
    },
    /// Physical click at the element's center or screen coordinates.
    Click {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target: Option<TargetRef>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        x: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        y: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        hwnd: Option<u64>,
        #[serde(default)]
        button: MouseButton,
        #[serde(default = "one")]
        clicks: u8,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        expect: Option<Expect>,
    },
    /// Move cursor to coordinates or target without clicking (hover).
    Move {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target: Option<TargetRef>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        x: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        y: Option<i32>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        hwnd: Option<u64>,
    },
    /// Type text; focuses `target` first when given.
    Type {
        text: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target: Option<TargetRef>,
        #[serde(default)]
        clear_first: bool,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        expect: Option<Expect>,
    },
    /// Key chord, e.g. `["ctrl", "shift", "s"]`.
    Key {
        keys: Vec<String>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target: Option<TargetRef>,
    },
    Scroll {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        target: Option<TargetRef>,
        direction: ScrollDirection,
        #[serde(default = "three")]
        amount: i32,
    },
    Select {
        target: TargetRef,
        option: SelectOption,
    },
    Toggle {
        target: TargetRef,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        state: Option<bool>,
    },
    Drag {
        from: TargetRef,
        to: DropTarget,
    },
    /// Event-driven wait (preferred over `sleep`).
    Wait(WaitSpec),
    /// Explicit delay. Supported for compatibility; reported in telemetry so
    /// plans that lean on it are visible.
    Sleep {
        ms: u64,
    },
    /// Close a window. `force` is a *request*: the policy engine still decides.
    Close {
        target: TargetRef,
        #[serde(default)]
        force: bool,
    },
    /// Hand control back to the human and pause the run.
    Human {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        message: Option<String>,
    },
}

fn one() -> u8 {
    1
}

fn three() -> i32 {
    3
}

impl Step {
    /// Short action name used in events, telemetry and error messages.
    pub const fn action_name(&self) -> &'static str {
        match self {
            Step::Launch { .. } => "launch",
            Step::Focus { .. } => "focus",
            Step::Find { .. } => "find",
            Step::Invoke { .. } => "invoke",
            Step::SetValue { .. } => "set_value",
            Step::Click { .. } => "click",
            Step::Move { .. } => "move",
            Step::Type { .. } => "type",
            Step::Key { .. } => "key",
            Step::Scroll { .. } => "scroll",
            Step::Select { .. } => "select",
            Step::Toggle { .. } => "toggle",
            Step::Drag { .. } => "drag",
            Step::Wait(_) => "wait",
            Step::Sleep { .. } => "sleep",
            Step::Close { .. } => "close",
            Step::Human { .. } => "human",
        }
    }

    pub const fn is_mutating(&self) -> bool {
        !matches!(self, Step::Find { .. } | Step::Wait(_) | Step::Sleep { .. } | Step::Move { .. })
    }
}

/// An ordered list of steps executed locally by the runtime.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Plan {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub steps: Vec<Step>,
}

impl Plan {
    pub fn new(steps: Vec<Step>) -> Self {
        Self { name: None, steps }
    }

    pub fn named(name: impl Into<String>, steps: Vec<Step>) -> Self {
        Self {
            name: Some(name.into()),
            steps,
        }
    }
}

/// Execute for real, or resolve targets only.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum RunMode {
    #[default]
    Execute,
    /// Resolve every selector and validate the plan without side effects.
    DryRun,
}

/// How a plan reacts to a failing step.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum OnError {
    #[default]
    Abort,
    Continue,
}
