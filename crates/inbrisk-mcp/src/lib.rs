//! Inbrisk MCP compatibility layer: MCP JSON-RPC (stdio) <-> native protocol.
//!
//! This crate is a *thin proxy* and nothing else. It owns no automation logic:
//! no UI Automation, no window enumeration, no caching, no policy, no
//! scheduling. Every tool compiles down to one or more `inbrisk_protocol`
//! operations which travel over the shared-memory channel to `inbrisk.exe` via
//! [`inbrisk_sdk`], so there is exactly one execution engine in the system:
//!
//! ```text
//! MCP JSON-RPC (stdio) -> translate -> inbrisk_protocol -> shared memory -> inbrisk.exe
//! ```
//!
//! Three properties follow from that architecture and are enforced here:
//!
//! * **Lazy session.** The runtime session is opened on the first tool call, not
//!   at startup, so an MCP host may launch the proxy before the runtime.
//! * **Restart tolerant.** A call that finds the runtime gone drops the cached
//!   session, reconnects once and replays the request; the next call after a
//!   runtime restart therefore just works.
//! * **Exit independence.** `inbrisk-mcp.exe` exiting (stdin EOF) only drops the
//!   session. Nothing in the runtime is torn down, and the proxy never calls
//!   `Runtime::shutdown`.
//!
//! The entry point is [`Proxy::handle_message`], a pure-ish
//! `&str -> Option<String>` mapping that is unit-testable without a runtime.
//!
//! Diagnostics go to **stderr only** (stdout is the protocol channel) and are
//! silent unless `INBRISK_MCP_DEBUG=1` is set.

use std::cell::RefCell;
use std::fmt;
use std::time::Duration;

mod capture;

use inbrisk_core::{log_debug, ErrorCode, InbriskError};
use inbrisk_protocol::action::{Plan, PointTarget, Step, TargetRef, WaitSpec};
use inbrisk_protocol::request::ObserveRequest;
use inbrisk_protocol::response::{
    ActionOutcome, Cancelled, FindResult, Observation, ReadResult, RunOutcome, StatusReport,
};
use inbrisk_protocol::selector::Selector;
use inbrisk_sdk::Runtime;
use serde_json::{json, Map, Value};

/// MCP revision this proxy implements. Hosts negotiate against it in
/// `initialize`; it is deliberately not the same thing as the native protocol
/// version, because the two evolve for different audiences.
pub const MCP_PROTOCOL_VERSION: &str = "2024-11-05";

/// Server name advertised to the host.
pub const SERVER_NAME: &str = "inbrisk";

/// Connect budget the SDK normally applies. Recorded here so the intent is
/// visible next to the availability wait below; `IpcClient` owns the real value.
///
/// The proxy never blocks on a connect for longer than this: a tool call is an
/// interactive request, and "the runtime is not there" has to be reported fast.
const CONNECT_BUDGET_MS: u64 = 1_500;

/// Cap on the wait a single call may spend looking for a runtime that is
/// starting up. Keeps an unreachable runtime from hanging the MCP host.
const AVAILABILITY_WAIT: Duration = Duration::from_millis(CONNECT_BUDGET_MS);

/// Default cap for `computer_run` when the caller asks for one without giving a
/// number.
const DEFAULT_RUN_TIMEOUT_MS: u64 = 180_000;

/// Hard ceiling for `computer_run`; the runtime budget engine owns the real
/// policy, this only stops a typo from becoming an hour-long call.
const MAX_RUN_TIMEOUT_MS: u64 = 3_600_000;

/// Environment variable that turns on verbose stderr logging.
const DEBUG_ENV: &str = "INBRISK_MCP_DEBUG";

/// JSON-RPC: the payload could not be parsed.
const PARSE_ERROR: i32 = -32700;
/// JSON-RPC: the method does not exist.
const METHOD_NOT_FOUND: i32 = -32601;
/// JSON-RPC: the method exists but the parameters are unusable.
const INVALID_PARAMS: i32 = -32602;

// ---------------------------------------------------------------------------
// Tool catalogue
// ---------------------------------------------------------------------------

/// One MCP tool: the name the host calls, the prose the model reads, and the
/// JSON Schema the host validates against.
#[derive(Debug, Clone, Copy)]
pub struct ToolSpec {
    pub name: &'static str,
    pub description: &'static str,
    pub input_schema: fn() -> Value,
}

/// Every tool this proxy exposes.
///
/// The catalogue is a fixed array rather than a `Vec` so `tools/list` cannot
/// drift from the dispatch match arms: both are driven by [`TOOLS`].
pub const TOOLS: &[ToolSpec] = &[
    ToolSpec {
        name: "computer_status",
        description: "Report the Inbrisk runtime identity, uptime, capabilities, state version, \
                      session list and counters. Call this first to check that the runtime is \
                      reachable.",
        input_schema: schema_status,
    },
    ToolSpec {
        name: "computer_windows",
        description: "List the top-level windows currently on the desktop, optionally narrowed by \
                      a title substring or by owning process. Cheap: it never touches the UI \
                      Automation tree.",
        input_schema: schema_windows,
    },
    ToolSpec {
        name: "computer_observe",
        description: "Observe the desktop or one window: foreground window, window list, monitors \
                      and, when include_tree is set, the UI Automation element subtree that later \
                      selectors run against.",
        input_schema: schema_observe,
    },
    ToolSpec {
        name: "computer_find",
        description: "Resolve a selector to concrete element handles and return them with their \
                      role, name and bounds. Use it to inspect what is on screen before acting.",
        input_schema: schema_find,
    },
    ToolSpec {
        name: "computer_read",
        description: "Read an element's properties (name, value, states, supported patterns, \
                      bounding rectangle) from a handle returned by computer_find.",
        input_schema: schema_read,
    },
    ToolSpec {
        name: "computer_act",
        description: "Perform exactly one action against the desktop, targeting an element by \
                      selector or by a $reference produced earlier in this session. Actions are \
                      tried through semantic patterns first and fall back to physical input. \
                      Action vocabulary: launch, focus, find, invoke, set_value, click, type, key, \
                      scroll, select, toggle, drag, wait, sleep, close, human.",
        input_schema: schema_act,
    },
    ToolSpec {
        description:
            "Run a multi-step plan in a single round trip: the runtime executes the whole \
                      sequence locally and returns every step outcome plus the $outputs table, so \
                      intermediate handles never cross the wire. Prefer this over a chain of \
                      computer_act calls. Same action vocabulary as computer_act, and a step may \
                      target a previous step's result with \"$0\" or \"$name\".",
        name: "computer_run",
        input_schema: schema_run,
    },
    ToolSpec {
        name: "computer_wait",
        description: "Block until a condition becomes true, driven by runtime events rather than \
                      polling or fixed sleeps. Conditions: until=window (selector + state), \
                      until=element (selector + state), until=value (target + equals), \
                      until=process_exit (name or process_id), until=idle (ms).",
        input_schema: schema_wait,
    },
    ToolSpec {
        name: "computer_cancel",
        description: "Cancel the operations and plans this session started, releasing leased \
                      physical input. Safe to call when nothing is running.",
        input_schema: schema_cancel,
    },
    ToolSpec {
        name: "computer_time",
        description: "Get the current system local time and timestamp instantly. Use this to report step times without needing terminal shell commands.",
        input_schema: schema_time,
    },
    ToolSpec {
        name: "computer_screenshot",
        description: "Capture an image of the screen, active window, specific HWND, or a region. \
                      Returns an MCP image block (PNG) along with dimensions and window info. \
                      Use this when visual inspection is needed (e.g. canvas, games, graphics, \
                      or layout verification).",
        input_schema: schema_screenshot,
    },
    ToolSpec {
        name: "computer_click",
        description: "Click at screen coordinates (x, y) or on a targeted UI element. Physical clicks \
                      are executed directly on the interactive desktop. Use this for canvas, games, \
                      and coordinate-based automation.",
        input_schema: schema_click,
    },
    ToolSpec {
        name: "computer_move",
        description: "Move the mouse cursor to screen coordinates (x, y) without clicking (hover). \
                      Useful for revealing tooltips, inspecting hover states, or moving the pointer out of the way.",
        input_schema: schema_move,
    },
    ToolSpec {
        name: "pc_open",
        description: "Open a persistent PC Control session. Reuse the returned session_id for all dependent commands until the task is complete.",
        input_schema: schema_pc_open,
    },
    ToolSpec {
        name: "pc_exec",
        description: "Execute a command inside an existing persistent Inbrisk PC session and return only the new output produced by that command. Use this for dependent multi-step PC-control workflows instead of host run_command. The shell, cwd, environment, and process state persist across calls.",
        input_schema: schema_pc_exec,
    },
    ToolSpec {
        name: "pc_write",
        description: "Send raw stdin to a process already running inside an Inbrisk PC session. Use for interactive prompts; do not use instead of pc_exec for normal shell commands.",
        input_schema: schema_pc_write,
    },
    ToolSpec {
        name: "pc_read",
        description: "Read raw output from a PC session incrementally.",
        input_schema: schema_pc_read,
    },
    ToolSpec {
        name: "pc_status",
        description: "Check the status of PC sessions.",
        input_schema: schema_pc_status,
    },
    ToolSpec {
        name: "pc_close",
        description: "Close an existing PC session.",
        input_schema: schema_pc_close,
    },

];

/// Look up a tool by name.
pub fn tool_spec(name: &str) -> Option<&'static ToolSpec> {
    TOOLS.iter().find(|t| t.name == name)
}

/// The one selector schema, reused everywhere a selector can appear.
///
/// Some hosts validate `inputSchema` strictly, so every `$ref` this catalogue
/// emits has to resolve. Hoisting the definition into a single function means
/// the step, plan, wait and find schemas cannot drift apart or dangle.
fn selector_definition() -> Value {
    json!({
        "type": "object",
        "description": "Relational element selector. At least one constraint is required; an empty selector matches nothing and is rejected.",
        "additionalProperties": false,
        "properties": {
            "role": { "type": "string", "description": "UI Automation control type, e.g. button, edit, document, menuitem." },
            "name": { "type": "string", "description": "Accessible name; substring match unless exact is true." },
            "name_regex": { "type": "string", "description": "Regular expression over the accessible name." },
            "automation_id": { "type": "string", "description": "AutomationId, the most stable identifier when the application provides one." },
            "class_name": { "type": "string", "description": "Win32 class name." },
            "text": { "type": "string", "description": "Requires the element's name or value to contain this text." },
            "within": { "type": "object", "description": "Restrict the search to the subtree of this selector." },
            "ancestor": { "type": "object", "description": "Walk up from a match instead of down, e.g. find the row containing a label." },
            "index": { "type": "integer", "minimum": 0, "description": "Pick the n-th match instead of failing on ambiguity." },
            "window": { "type": "object", "description": "Limit the search to a matching top-level window." },
            "hwnd": { "type": "integer", "description": "Limit the search to one window handle." },
            "process": { "type": "string", "description": "Limit the search to a process image name, e.g. notepad.exe." },
            "backend": { "type": "string", "enum": ["uia", "win32", "cdp"], "description": "Force a backend instead of letting the runtime choose." },
            "exact": { "type": "boolean", "description": "Exact instead of substring matching for name." },
            "timeout_ms": { "type": "integer", "minimum": 0, "description": "Upper bound for the search itself." },
            "policy": { "type": "string", "enum": ["strict", "first", "best"], "description": "What to do when several elements match. Defaults to strict." }
        }
    })
}

/// The definitions block every selector-bearing schema has to carry.
fn selector_definitions() -> Value {
    json!({ "selector": selector_definition() })
}

/// A `$ref` plus the prose that explains it in the model's tool description.
///
/// The prose cannot sit next to `$ref`: JSON Schema ignores siblings of `$ref`,
/// so it is wrapped in an `allOf` instead.
fn selector_ref(description: &str) -> Value {
    json!({
        "description": description,
        "allOf": [{ "$ref": "#/definitions/selector" }]
    })
}

fn target_schema(description: &str) -> Value {
    json!({
        "description": description,
        "oneOf": [
            {
                "type": "string",
                "description": "A $reference to an earlier step's stored result, e.g. \"$0\" or \"$save_button\"."
            },
            { "$ref": "#/definitions/selector" },
            {
                "type": "object",
                "properties": {
                    "x": { "type": "number", "description": "X coordinate in screen pixels." },
                    "y": { "type": "number", "description": "Y coordinate in screen pixels." }
                },
                "required": ["x", "y"],
                "description": "A screen point {x, y}."
            }
        ]
    })
}

fn steps_schema() -> Value {
    let target = target_schema("What the step acts on: a selector, a $reference, or {x, y} coordinates.");

    // The per-action requirements are built separately: one giant `json!` literal
    // exceeds the macro's default recursion budget.
    let requirements = json!([
        { "properties": { "action": { "const": "launch" } }, "required": ["app"] },
        { "properties": { "action": { "const": "focus" } }, "required": ["target"] },
        { "properties": { "action": { "const": "find" } }, "required": ["selector"] },
        { "properties": { "action": { "const": "invoke" } }, "required": ["target"] },
        { "properties": { "action": { "const": "set_value" } }, "required": ["target", "value"] },
        {
            "properties": { "action": { "const": "click" } },
            "anyOf": [
                { "required": ["target"] },
                { "required": ["x", "y"] },
                { "properties": { "x": { "type": "integer" } } }
            ]
        },
        {
            "properties": { "action": { "const": "move" } },
            "anyOf": [
                { "required": ["target"] },
                { "required": ["x", "y"] },
                { "properties": { "x": { "type": "integer" } } }
            ]
        },
        { "properties": { "action": { "const": "type" } }, "required": ["text"] },
        { "properties": { "action": { "const": "key" } }, "required": ["keys"] },
        { "properties": { "action": { "const": "scroll" } }, "required": ["direction"] },
        { "properties": { "action": { "const": "select" } }, "required": ["target", "option"] },
        { "properties": { "action": { "const": "toggle" } }, "required": ["target"] },
        { "properties": { "action": { "const": "drag" } }, "required": ["from", "to"] },
        { "properties": { "action": { "const": "wait" } } },
        { "properties": { "action": { "const": "sleep" } }, "required": ["ms"] },
        { "properties": { "action": { "const": "close" } }, "required": ["target"] },
        { "properties": { "action": { "const": "human" } } }
    ]);

    let properties = json!({
        "action": {
            "type": "string",
            "enum": [
                "launch", "focus", "find", "invoke", "set_value", "click", "move", "type",
                "key", "scroll", "select", "toggle", "drag", "wait", "sleep",
                "close", "human"
            ],
            "description": "Which action to perform. Semantic patterns are preferred over physical input."
        },
        "app": { "type": "string", "description": "launch: executable, path or shell target." },
        "args": { "type": "array", "items": { "type": "string" }, "description": "launch: command line arguments." },
        "cwd": { "type": "string", "description": "launch: working directory." },
        "wait_for_window": selector_ref("launch: wait for the new window to appear."),
        "intent": {
            "type": "string",
            "enum": ["ephemeral", "reusable", "task_artifact", "user_useful", "unknown"],
            "description": "launch: lifecycle contract. ephemeral is the only intent that ever permits automatic cleanup."
        },
        "store": { "type": "string", "description": "Save the result under this name for later $references." },
        "target": target.clone(),
        "x": { "type": "integer", "description": "click/move/drag: X coordinate on screen in pixels." },
        "y": { "type": "integer", "description": "click/move/drag: Y coordinate on screen in pixels." },
        "expect": {
            "type": "object",
            "description": "Optional state precondition, forwarded to the runtime guard.",
            "properties": { "state_version": { "type": "integer" } }
        },
        "selector": selector_ref("find: what to look for."),
        "all": { "type": "boolean", "description": "find: return every match instead of failing on ambiguity." },
        "value": { "type": "string", "description": "set_value: the text to set, without keystrokes." },
        "button": { "type": "string", "enum": ["left", "right", "middle"], "description": "click: mouse button." },
        "clicks": { "type": "integer", "minimum": 1, "description": "click: number of clicks." },
        "text": { "type": "string", "description": "type: the text to type." },
        "clear_first": { "type": "boolean", "description": "type: select all and delete before typing." },
        "keys": {
            "type": "array",
            "items": { "type": "string" },
            "description": "key: chord, e.g. [\"ctrl\", \"shift\", \"s\"]."
        },
        "direction": { "type": "string", "enum": ["up", "down", "left", "right"], "description": "scroll: direction." },
        "amount": { "type": "integer", "description": "scroll: wheel notches or lines." },
        "option": {
            "description": "select: display text or zero-based index.",
            "oneOf": [{ "type": "string" }, { "type": "integer", "minimum": 0 }]
        },
        "state": { "type": "boolean", "description": "toggle: the desired state." },
        "from": {
            "description": "drag: where the drag starts.",
            "oneOf": [
                { "type": "string", "description": "A $reference to an earlier step's stored result." },
                { "$ref": "#/definitions/selector" },
                { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "description": "An absolute screen point." }
            ]
        },
        "to": {
            "description": "drag: where the drag ends.",
            "oneOf": [
                { "type": "string", "description": "A $reference to an earlier step's stored result." },
                { "$ref": "#/definitions/selector" },
                { "type": "object", "properties": { "x": { "type": "number" }, "y": { "type": "number" } }, "required": ["x", "y"], "description": "An absolute screen point." }
            ]
        },
        "until": {
            "type": "string",
            "enum": ["window", "element", "value", "process_exit", "idle"],
            "description": "wait: which condition to block on."
        },
        "equals": { "type": "string", "description": "wait + until=value: the text the target's value must become." },
        "name": { "type": "string", "description": "wait + until=process_exit: process image name." },
        "process_id": { "type": "integer", "description": "wait + until=process_exit: process id." },
        "ms": { "type": "integer", "minimum": 0, "description": "sleep: delay in milliseconds." },
        "force": { "type": "boolean", "description": "close: request a forced close; policy still decides." },
        "message": { "type": "string", "description": "human: what to tell the human before pausing." }
    });

    json!({
        "type": "array",
        "minItems": 1,
        "description": "Ordered plan steps. Every step is tagged with \"action\"; vocabulary: launch, focus, find, invoke, set_value, click, move, type, key, scroll, select, toggle, drag, wait, sleep, close, human.",
        "items": {
            "type": "object",
            "description": "One step. Required fields depend on \"action\"; see anyOf.",
            "anyOf": requirements,
            "properties": properties,
            "required": ["action"]
        }
    })
}

fn common_run_properties() -> Value {
    json!({
        "name": { "type": "string", "description": "Plan name, echoed back in telemetry and events." },
        "steps": steps_schema(),
        "dry_run": { "type": "boolean", "description": "Resolve every selector and validate the plan without side effects." },
        "timeout_ms": {
            "type": "integer",
            "minimum": 0,
            "maximum": MAX_RUN_TIMEOUT_MS,
            "description": "Budget for the whole plan in milliseconds."
        },
        "on_error": { "type": "string", "enum": ["abort", "continue"], "description": "Whether a failing step stops the plan. Defaults to abort." }
    })
}

fn schema_status() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {}
    })
}

fn schema_windows() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "filter": { "type": "string", "description": "Only windows whose title contains this substring (case-insensitive)." },
            "process": { "type": "string", "description": "Only windows owned by this process image name, e.g. notepad.exe." }
        }
    })
}

fn schema_observe() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "definitions": { "selector": selector_definition() },
        "properties": {
            "window": {
                "description": "Target one window instead of the whole desktop: a selector object or a window handle.",
                "oneOf": [{ "$ref": "#/definitions/selector" }, { "type": "integer" }]
            },
            "include_tree": { "type": "boolean", "description": "Include the UI Automation element subtree." },
            "max_depth": { "type": "integer", "minimum": 0, "description": "Limit tree depth when include_tree is set." },
            "max_elements": { "type": "integer", "minimum": 0, "description": "Limit the number of elements returned." },
            "timeout_ms": { "type": "integer", "minimum": 0, "description": "Upper bound for the observation." }
        }
    })
}

fn schema_find() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "selector": selector_ref("What to look for. At least one constraint is required."),
            "all": { "type": "boolean", "description": "Return every match instead of failing on ambiguity." },
            "limit": { "type": "integer", "minimum": 1, "description": "Maximum number of matches (implies all)." }
        },
        "required": ["selector"],
        "definitions": selector_definitions()
    })
}

fn schema_read() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "element_id": { "type": "integer", "minimum": 0, "description": "Handle returned by computer_find." }
        },
        "required": ["element_id"]
    })
}

fn schema_act() -> Value {
    let steps = steps_schema();
    let items = steps.get("items").cloned().unwrap_or(Value::Null);
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "action": {
                "description": "One step object. Pass an array to execute several steps in a single round trip instead of calling computer_act repeatedly.",
                "oneOf": [items, steps]
            }
        },
        "required": ["action"],
        // The step schema refers to `#/definitions/selector`; without this block
        // the reference would dangle and a strict host would reject the schema.
        "definitions": selector_definitions()
    })
}

fn schema_run() -> Value {
    let properties = common_run_properties();
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": properties,
        "required": ["steps"],
        "definitions": selector_definitions()
    })
}

fn schema_wait() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "until": {
                "type": "string",
                "enum": ["window", "element", "value", "process_exit", "idle"],
                "description": "Which condition to block on. window/element take a selector plus a state, value takes a target plus equals, process_exit takes name or process_id, idle takes ms."
            },
            "selector": selector_ref("until=window|element: what to wait for."),
            "state": {
                "type": "string",
                "enum": ["exists", "visible", "foreground", "gone", "enabled"],
                "description": "until=window|element: which condition on the target. window supports exists/visible/foreground/gone, element supports exists/visible/enabled/gone."
            },
            "target": target_schema("until=value: the element whose value must change."),
            "equals": { "type": "string", "description": "until=value: the text the target's value must equal." },
            "name": { "type": "string", "description": "until=process_exit: process image name." },
            "process_id": { "type": "integer", "description": "until=process_exit: process id." },
            "ms": { "type": "integer", "minimum": 0, "description": "until=idle: quiescence window in milliseconds." },
            "timeout_ms": { "type": "integer", "minimum": 0, "description": "Give up after this many milliseconds." }
        },
        "required": ["until"],
        "definitions": selector_definitions()
    })
}

fn schema_cancel() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "reason": { "type": "string", "description": "Recorded with the cancellation." }
        }
    })
}

#[cfg(windows)]
pub fn local_time_stamp() -> String {
    #[repr(C)]
    struct SYSTEMTIME {
        year: u16,
        month: u16,
        day_of_week: u16,
        day: u16,
        hour: u16,
        minute: u16,
        second: u16,
        milliseconds: u16,
    }
    #[link(name = "kernel32")]
    extern "system" {
        fn GetLocalTime(st: *mut SYSTEMTIME);
    }
    let mut st = SYSTEMTIME {
        year: 0, month: 0, day_of_week: 0, day: 0,
        hour: 0, minute: 0, second: 0, milliseconds: 0,
    };
    unsafe { GetLocalTime(&mut st); }
    let ampm = if st.hour >= 12 { "PM" } else { "AM" };
    let hour12 = if st.hour == 0 { 12 } else if st.hour > 12 { st.hour - 12 } else { st.hour };
    format!("[{}:{:02}] {}", hour12, st.minute, ampm)
}

#[cfg(not(windows))]
pub fn local_time_stamp() -> String {
    "[0:00] AM".to_string()
}

fn schema_time() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {}
    })
}

fn schema_screenshot() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "target": {
                "type": "string",
                "enum": ["desktop", "window", "foreground", "region"],
                "description": "What to capture: 'desktop' (entire virtual desktop, default), 'foreground' (active window), 'window' (specific window by hwnd), or 'region' (specific x,y,w,h bounding box)."
            },
            "hwnd": {
                "type": "integer",
                "description": "Window handle (HWND) to capture when target is 'window'. If omitted, foreground window is captured."
            },
            "x": {
                "type": "integer",
                "description": "Left coordinate in screen pixels for region capture."
            },
            "y": {
                "type": "integer",
                "description": "Top coordinate in screen pixels for region capture."
            },
            "w": {
                "type": "integer",
                "description": "Width in screen pixels for region capture."
            },
            "h": {
                "type": "integer",
                "description": "Height in screen pixels for region capture."
            }
        }
    })
}

fn schema_move() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "x": { "type": "integer", "description": "X coordinate in screen pixels (required)." },
            "y": { "type": "integer", "description": "Y coordinate in screen pixels (required)." },
            "hwnd": { "type": "integer", "description": "Optional window handle to bring to foreground before moving." }
        },
        "required": ["x", "y"]
    })
}

fn schema_click() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "x": { "type": "integer", "description": "X coordinate in screen pixels (required for coordinate click)." },
            "y": { "type": "integer", "description": "Y coordinate in screen pixels (required for coordinate click)." },
            "button": { "type": "string", "enum": ["left", "right", "middle"], "description": "Mouse button. Defaults to 'left'." },
            "clicks": { "type": "integer", "minimum": 1, "description": "Number of clicks: 1 for single click, 2 for double click. Defaults to 1." },
            "hwnd": { "type": "integer", "description": "Optional window handle to bring to foreground before clicking." }
        }
    })
}

// ---------------------------------------------------------------------------
// Protocol errors and the arguments/result helper pair
// ---------------------------------------------------------------------------

/// The ways a message can fail *before* a tool call is attempted.
#[derive(Debug)]
pub enum ProtocolError {
    /// The line was not JSON (or not a JSON object).
    Parse(String),
    /// The envelope was JSON but not a usable JSON-RPC request.
    Invalid(String),
    /// The envelope was fine but the method does not exist. The id is carried
    /// along so the host can match the error to its request.
    UnknownMethod { id: Value, method: String },
}

impl fmt::Display for ProtocolError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ProtocolError::Parse(m) | ProtocolError::Invalid(m) => f.write_str(m),
            ProtocolError::UnknownMethod { method, .. } => {
                write!(f, "unknown method '{method}'")
            }
        }
    }
}

impl ProtocolError {
    /// The JSON-RPC error code this failure maps onto.
    pub const fn code(&self) -> i32 {
        match self {
            ProtocolError::Parse(_) => PARSE_ERROR,
            ProtocolError::Invalid(_) => INVALID_PARAMS,
            ProtocolError::UnknownMethod { .. } => METHOD_NOT_FOUND,
        }
    }

    /// The full JSON-RPC error line to write back.
    ///
    /// A malformed message has no usable id, so the reply carries `null`; the
    /// host correlates it by order, exactly as JSON-RPC requires.
    pub fn envelope(&self) -> String {
        let id = match self {
            ProtocolError::UnknownMethod { id, .. } => id.clone(),
            _ => Value::Null,
        };
        error_envelope(id, self.code(), &self.to_string())
    }
}

/// A tool call that could not produce a result.
///
/// Every one of these becomes `{"content": [...], "isError": true}` rather than
/// a JSON-RPC error object, because hosts surface tool content to the model and
/// hide transport errors from it.
#[derive(Debug)]
pub enum ToolFailure {
    /// A required argument is missing or has the wrong shape.
    Arguments(String),
    /// A native-protocol call failed (including "runtime is not running").
    Native(InbriskError),
}

impl ToolFailure {
    /// Text handed back to the model, with the runtime hint folded in when the
    /// SDK provided one.
    fn render(&self) -> String {
        match self {
            ToolFailure::Arguments(message) => format!("invalid arguments: {message}"),
            ToolFailure::Native(err) => err_text(err),
        }
    }
}

impl fmt::Display for ToolFailure {
    /// The reason alone, so failures can be nested into a richer message (a step
    /// index, for instance).
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ToolFailure::Arguments(message) => f.write_str(message),
            ToolFailure::Native(err) => write!(f, "{}: {}", err.code.as_str(), err.message),
        }
    }
}

/// Render a native error for a model: `Code: message`, plus the hint.
fn err_text(err: &InbriskError) -> String {
    let mut text = format!("{}: {}", err.code.as_str(), err.message);
    if err.code == ErrorCode::RuntimeUnavailable || err.code == ErrorCode::SessionExpired {
        text.push_str(
            "\nThe Inbrisk runtime is not reachable. Start inbrisk.exe (or the Inbrisk app) and \
             retry; the proxy reconnects automatically.",
        );
    }
    if let Some(hint) = &err.hint {
        text.push_str("\nHint: ");
        text.push_str(hint);
    }
    text
}

/// Typed reader for a `tools/call` argument object.
///
/// MCP hosts are not obliged to validate against `inputSchema`, so every read
/// is checked and reported instead of trusted.
struct Wire<'a> {
    args: Map<String, Value>,
    tool: &'a str,
}

impl<'a> Wire<'a> {
    /// Accepts a missing `arguments` member as "no arguments".
    fn new(tool: &'a str, args: Option<&Value>) -> Result<Self, ToolFailure> {
        let args = match args {
            None | Some(Value::Null) => Map::new(),
            Some(Value::Object(map)) => map.clone(),
            Some(other) => {
                return Err(ToolFailure::Arguments(format!(
                    "arguments must be an object, got {}",
                    kind_of(other)
                )))
            }
        };
        Ok(Self { args, tool })
    }

    fn fail(&self, message: impl fmt::Display) -> ToolFailure {
        ToolFailure::Arguments(format!("{}: {message}", self.tool))
    }

    fn get(&self, key: &str) -> Option<&Value> {
        self.args.get(key).filter(|v| !v.is_null())
    }

    fn required(&self, key: &str) -> Result<&Value, ToolFailure> {
        self.get(key)
            .ok_or_else(|| self.fail(format!("missing required argument '{key}'")))
    }

    fn opt_string(&self, key: &str) -> Result<Option<String>, ToolFailure> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::String(s)) => Ok(Some(s.clone())),
            Some(other) => {
                Err(self.fail(format!("'{key}' must be a string, got {}", kind_of(other))))
            }
        }
    }

    fn opt_bool(&self, key: &str) -> Result<Option<bool>, ToolFailure> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::Bool(b)) => Ok(Some(*b)),
            Some(other) => {
                Err(self.fail(format!("'{key}' must be a boolean, got {}", kind_of(other))))
            }
        }
    }

    fn opt_u64(&self, key: &str) -> Result<Option<u64>, ToolFailure> {
        match self.get(key) {
            None => Ok(None),
            Some(Value::Number(n)) => n.as_u64().map(Some).ok_or_else(|| {
                self.fail(format!("'{key}' must be a non-negative integer, got {n}"))
            }),
            Some(other) => Err(self.fail(format!(
                "'{key}' must be an integer, got {}",
                kind_of(other)
            ))),
        }
    }

    fn opt_u32(&self, key: &str) -> Result<Option<u32>, ToolFailure> {
        Ok(self
            .opt_u64(key)?
            .map(|v| u32::try_from(v).unwrap_or(u32::MAX)))
    }

    fn opt_usize(&self, key: &str) -> Result<Option<usize>, ToolFailure> {
        Ok(self
            .opt_u64(key)?
            .map(|v| usize::try_from(v).unwrap_or(usize::MAX)))
    }

    /// Deserialize a struct out of one member of the arguments object.
    fn member<T: serde::de::DeserializeOwned>(&self, key: &str) -> Result<T, ToolFailure> {
        let value = self.required(key)?;
        serde_json::from_value(value.clone())
            .map_err(|e| self.fail(format!("'{key}' is not a valid value: {e}")))
    }

    /// Deserialize a struct out of the arguments object itself, which is how
    /// `Plan` and `WaitSpec` are shaped on the MCP surface.
    fn flatten<T: serde::de::DeserializeOwned>(&self) -> Result<T, ToolFailure> {
        let value = Value::Object(self.args.clone());
        serde_json::from_value(value).map_err(|e| self.fail(format!("invalid arguments: {e}")))
    }
}

const fn kind_of(value: &Value) -> &'static str {
    match value {
        Value::Null => "null",
        Value::Bool(_) => "a boolean",
        Value::Number(_) => "a number",
        Value::String(_) => "a string",
        Value::Array(_) => "an array",
        Value::Object(_) => "an object",
    }
}

/// Pretty-print a value for the text content block.
///
/// MCP carries the machine-readable answer as a JSON string; pretty-printing it
/// keeps the answer readable both for a model and for a human reading logs.
fn pretty(value: &Value) -> String {
    serde_json::to_string_pretty(value).unwrap_or_else(|_| value.to_string())
}

/// The MCP tool-result envelope.
fn tool_content(value: &Value, is_error: bool) -> Value {
    json!({
        "content": [{ "type": "text", "text": pretty(value) }],
        "isError": is_error
    })
}

fn tool_ok(value: &Value) -> Value {
    tool_content(value, false)
}

/// Build a failed tool result.
///
/// The payload carries both readings of the failure: a one-line `message` for
/// the model to reason about, and a structured `error` object (stable code,
/// hint, retryable flag) for anything that wants to branch on it.
fn tool_err(failure: &ToolFailure) -> Value {
    let mut error = match failure {
        ToolFailure::Arguments(message) => json!({
            "code": "InvalidArguments",
            "message": message,
        }),
        ToolFailure::Native(err) => {
            let mut body = json!({
                "code": err.code.as_str(),
                "message": err.message,
                "retryable": err.retryable,
            });
            if let Some(hint) = &err.hint {
                body["hint"] = Value::String(hint.clone());
            }
            body
        }
    };
    error["render"] = Value::String(failure.render());

    tool_content(&json!({ "error": error }), true)
}

// ---------------------------------------------------------------------------
// Session: lazy, reconnectable runtime handle
// ---------------------------------------------------------------------------

/// The outbound half of the proxy: everything this crate asks the runtime to do.
///
/// It exists so [`Session`] can be exercised without a runtime and so the
/// crate's dependency on `inbrisk_sdk::Runtime` stays in one place.
pub trait NativeOps {
    /// Whether a runtime is reachable right now. The default assumes yes so a
    /// stub does not have to implement it.
    fn available(&self) -> bool {
        true
    }
    fn endpoint(&self) -> String;
    fn status(&mut self) -> Result<StatusReport, InbriskError>;
    fn observe(&mut self, request: ObserveRequest) -> Result<Observation, InbriskError>;
    fn find_all(
        &mut self,
        selector: Selector,
        all: bool,
        limit: Option<usize>,
    ) -> Result<FindResult, InbriskError>;
    fn read(&mut self, element_id: u64) -> Result<ReadResult, InbriskError>;
    fn act(&mut self, step: Step) -> Result<ActionOutcome, InbriskError>;
    fn run(
        &mut self,
        plan: Plan,
        timeout: Duration,
        dry_run: bool,
    ) -> Result<RunOutcome, InbriskError>;
    fn wait(
        &mut self,
        spec: WaitSpec,
    ) -> Result<inbrisk_protocol::response::WaitOutcome, InbriskError>;
    fn cancel_all(&mut self, reason: Option<String>) -> Result<Cancelled, InbriskError>;
    fn terminal_open(&mut self, req: inbrisk_protocol::terminal::TerminalOpen) -> Result<inbrisk_protocol::terminal::TerminalInfo, InbriskError>;
    fn terminal_write(&mut self, req: inbrisk_protocol::terminal::TerminalWrite) -> Result<inbrisk_protocol::terminal::TerminalWritten, InbriskError>;
    fn terminal_read(&mut self, req: inbrisk_protocol::terminal::TerminalRead) -> Result<inbrisk_protocol::terminal::TerminalOutput, InbriskError>;
    fn terminal_status(&mut self, session_id: Option<String>) -> Result<Vec<inbrisk_protocol::terminal::TerminalInfo>, InbriskError>;
    fn terminal_close(&mut self, session_id: String) -> Result<inbrisk_protocol::terminal::TerminalClosed, InbriskError>;

}

impl NativeOps for Runtime {
    fn available(&self) -> bool {
        // Never blocks the caller for long; `is_available` is a short probe.
        Runtime::is_available()
    }

    fn endpoint(&self) -> String {
        Runtime::endpoint(self)
    }

    fn status(&mut self) -> Result<StatusReport, InbriskError> {
        Runtime::status(self)
    }

    fn observe(&mut self, request: ObserveRequest) -> Result<Observation, InbriskError> {
        Runtime::observe(self, request)
    }

    fn find_all(
        &mut self,
        selector: Selector,
        all: bool,
        limit: Option<usize>,
    ) -> Result<FindResult, InbriskError> {
        Runtime::find_all(self, selector, all, limit)
    }

    fn read(&mut self, element_id: u64) -> Result<ReadResult, InbriskError> {
        Runtime::read(self, element_id)
    }

    fn act(&mut self, step: Step) -> Result<ActionOutcome, InbriskError> {
        Runtime::act(self, step)
    }

    fn run(
        &mut self,
        plan: Plan,
        timeout: Duration,
        dry_run: bool,
    ) -> Result<RunOutcome, InbriskError> {
        if dry_run {
            Runtime::dry_run(self, plan)
        } else {
            Runtime::run_with(self, plan, timeout, None)
        }
    }

    fn wait(
        &mut self,
        spec: WaitSpec,
    ) -> Result<inbrisk_protocol::response::WaitOutcome, InbriskError> {
        Runtime::wait(self, spec)
    }

    fn cancel_all(&mut self, reason: Option<String>) -> Result<Cancelled, InbriskError> {
        // The SDK owns the canonical cancel payload; the reason is recorded by
        // the MCP layer's debug log rather than fabricated into a request.
        let _ = reason;
        Runtime::cancel_all(self)
    }
    fn terminal_open(&mut self, req: inbrisk_protocol::terminal::TerminalOpen) -> Result<inbrisk_protocol::terminal::TerminalInfo, InbriskError> {
        Runtime::terminal_open(self, req)
    }
    fn terminal_write(&mut self, req: inbrisk_protocol::terminal::TerminalWrite) -> Result<inbrisk_protocol::terminal::TerminalWritten, InbriskError> {
        Runtime::terminal_write(self, req)
    }
    fn terminal_read(&mut self, req: inbrisk_protocol::terminal::TerminalRead) -> Result<inbrisk_protocol::terminal::TerminalOutput, InbriskError> {
        Runtime::terminal_read(self, req)
    }
    fn terminal_status(&mut self, session_id: Option<String>) -> Result<Vec<inbrisk_protocol::terminal::TerminalInfo>, InbriskError> {
        Runtime::terminal_status(self, session_id)
    }
    fn terminal_close(&mut self, session_id: String) -> Result<inbrisk_protocol::terminal::TerminalClosed, InbriskError> {
        Runtime::terminal_close(self, session_id)
    }

}

/// Lazily created and self-healing runtime session.
///
/// The proxy must tolerate `inbrisk.exe` restarting underneath it, so the
/// session is only a cache: any failure that looks like "the runtime went away"
/// drops it, and the next call establishes a fresh one.
pub struct Session {
    native: Option<Box<dyn NativeOps>>,
}

impl Default for Session {
    fn default() -> Self {
        Self::new()
    }
}

impl Session {
    pub const fn new() -> Self {
        Self { native: None }
    }

    /// Wrap an already-connected native handle (used by tests).
    pub fn with_native(native: Box<dyn NativeOps>) -> Self {
        Self {
            native: Some(native),
        }
    }

    /// Drop the cached session; the next call reconnects.
    pub fn invalidate(&mut self) {
        if self.native.take().is_some() {
            log_debug!("mcp", "dropped cached runtime session");
        }
    }

    pub const fn is_connected(&self) -> bool {
        self.native.is_some()
    }

    /// The connected handle, connecting first if needed.
    pub fn handle(&mut self) -> Result<&mut (dyn NativeOps + 'static), InbriskError> {
        if !self.native.as_ref().is_some_and(|n| n.available()) {
            self.invalidate();
            self.connect()?;
        }
        self.native.as_deref_mut().ok_or_else(unavailable_error)
    }

    /// Establish a fresh session, waiting briefly for a runtime that is starting.
    pub fn connect(&mut self) -> Result<(), InbriskError> {
        // A stale handle must never be reused: the runtime may have restarted
        // with a new epoch, and `connect_as` would then be handshaking against a
        // dead region.
        self.native = None;

        match Runtime::connect_as("inbrisk-mcp", env!("CARGO_PKG_VERSION"), false) {
            Ok(runtime) => {
                log_debug!("mcp", "connected to runtime at {}", runtime.endpoint());
                self.native = Some(Box::new(runtime));
                Ok(())
            }
            Err(first) => {
                // The runtime may be mid-start; one bounded wait is worth it and
                // still far cheaper than failing a whole tool call.
                if first.code == ErrorCode::RuntimeUnavailable
                    && Runtime::wait_until_available(AVAILABILITY_WAIT)
                {
                    match Runtime::connect_as("inbrisk-mcp", env!("CARGO_PKG_VERSION"), false) {
                        Ok(runtime) => {
                            log_debug!("mcp", "connected to runtime at {}", runtime.endpoint());
                            self.native = Some(Box::new(runtime));
                            return Ok(());
                        }
                        Err(second) => return Err(second),
                    }
                }
                Err(first)
            }
        }
    }

    /// Run one operation, reconnecting and retrying once if the runtime went away
    /// between (or during) calls.
    ///
    /// At most one retry: a second failure is reported rather than looped on, so
    /// a flapping runtime cannot hang the MCP host.
    pub fn call<T, F>(&mut self, operation: F) -> Result<T, InbriskError>
    where
        F: FnOnce(&mut dyn NativeOps) -> Result<T, InbriskError>,
    {
        let mut operation = Some(operation);
        let mut last: Option<InbriskError> = None;

        for attempt in 0..2u8 {
            // Reconnect (or verify the cached handle) *before* consuming the
            // closure: a failed reconnect must not eat the caller's operation.
            let handle = self.handle()?;
            let Some(op) = operation.take() else {
                break;
            };

            match op(handle) {
                Ok(value) => return Ok(value),
                Err(err) if attempt == 0 && retryable_disconnect(&err) => {
                    // The runtime went away. The request may or may not have been
                    // applied before the channel broke; a restarted runtime is
                    // the common case here, and the runtime de-duplicates by
                    // request id, so replaying this single call is safe.
                    log_debug!(
                        "mcp",
                        "runtime call failed ({}), reconnecting once",
                        err.code.as_str()
                    );
                    self.invalidate();
                    last = Some(err);
                }
                Err(err) => return Err(err),
            }
        }

        Err(last.unwrap_or_else(unavailable_error))
    }
}

impl fmt::Debug for Session {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Session")
            .field("connected", &self.is_connected())
            .finish()
    }
}

/// Whether an error means "that session is gone" rather than "that call was
/// refused". Only these are worth a reconnect-and-retry.
const fn retryable_disconnect(err: &InbriskError) -> bool {
    matches!(
        err.code,
        ErrorCode::RuntimeUnavailable | ErrorCode::SessionExpired | ErrorCode::ProtocolMismatch
    )
}

fn unavailable_error() -> InbriskError {
    InbriskError::new(
        ErrorCode::RuntimeUnavailable,
        "the Inbrisk runtime is not running",
    )
    .with_hint("start inbrisk.exe (or the Inbrisk app), then retry")
}

// ---------------------------------------------------------------------------
// Proxy: the JSON-RPC entry point
// ---------------------------------------------------------------------------

/// What the stdio loop should do after handling one message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Flow {
    /// Write the reply (when there is one) and read the next line.
    Continue,
    /// `exit` was received: flush and stop.
    Stop,
}

/// The outcome of handling one message: what to write, and what to do next.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Handled {
    /// The exact line to write back; `None` for notifications.
    pub reply: Option<String>,
    /// Whether the loop should keep reading.
    pub flow: Flow,
}

impl Handled {
    /// A notification: nothing to write, keep reading.
    const fn silent() -> Self {
        Self {
            reply: None,
            flow: Flow::Continue,
        }
    }

    /// A reply to write, keep reading.
    fn reply(line: String) -> Self {
        Self {
            reply: Some(line),
            flow: Flow::Continue,
        }
    }
}

/// The MCP proxy.
///
/// Holds the lazily created [`Session`] plus the debug switch, and exposes
/// message handling as a `&str -> Option<String>` mapping.
pub struct Proxy {
    session: RefCell<Session>,
    debug: bool,
}

impl Default for Proxy {
    fn default() -> Self {
        Self::new()
    }
}

impl Proxy {
    /// Build a proxy with diagnostics configured from the environment.
    pub fn new() -> Self {
        Self::with_debug(debug_from_env())
    }

    /// Build a proxy with an explicit debug switch (used by tests).
    pub fn with_debug(debug: bool) -> Self {
        if debug {
            // Route the core logger to stderr at debug level; stdout stays clean.
            inbrisk_core::logging::set_level(inbrisk_core::logging::Level::Debug);
        }
        Self {
            session: RefCell::new(Session::new()),
            debug,
        }
    }

    /// Build a proxy with a pre-populated session (used by tests).
    pub fn with_session(session: Session, debug: bool) -> Self {
        Self {
            session: RefCell::new(session),
            debug,
        }
    }

    pub const fn debug(&self) -> bool {
        self.debug
    }

    /// Handle one newline-delimited message.
    ///
    /// `Ok(Handled { reply: None, .. })` means "this was a notification, say
    /// nothing"; `Ok(Handled { reply: Some(_), .. })` is the exact line to write
    /// back; `Err(_)` is a JSON-RPC error envelope that must still be written
    /// before continuing.
    ///
    /// Nothing here panics: a malformed line, an unknown method, a bad argument
    /// or an unreachable runtime all come back as a well-formed answer.
    pub fn handle_line(&self, line: &str) -> Result<Handled, ProtocolError> {
        let value: Value = match serde_json::from_str(line) {
            Ok(value) => value,
            Err(err) => {
                // A line that is not JSON at all is still answered, with a null
                // id, because the host is waiting for something.
                log_debug!("mcp", "unparseable message: {err}");
                return Err(ProtocolError::Parse(format!("invalid JSON: {err}")));
            }
        };

        let object = match value.as_object() {
            Some(object) => object,
            None => {
                return Err(ProtocolError::Invalid(
                    "a JSON-RPC message must be an object".into(),
                ))
            }
        };

        let id = object.get("id").cloned();
        let method = match object.get("method").and_then(Value::as_str) {
            Some(method) => method.to_string(),
            None => return Err(ProtocolError::Invalid("missing 'method'".into())),
        };
        let params = object.get("params").cloned().unwrap_or(Value::Null);

        // A notification never gets a reply; `exit` additionally stops the loop.
        if id.is_none() {
            if method == "notifications/initialized" || method == "initialized" {
                log_debug!("mcp", "client initialised");
            } else if method == "notifications/exit" || method == "exit" {
                log_debug!("mcp", "client requested exit");
                return Ok(Handled {
                    reply: None,
                    flow: Flow::Stop,
                });
            } else {
                log_debug!("mcp", "ignoring unknown notification '{method}'");
            }
            return Ok(Handled::silent());
        }

        let id = id.unwrap_or(Value::Null);

        match method.as_str() {
            "initialize" => Ok(Handled::reply(success_envelope(id, &initialize_result()))),
            "tools/list" => Ok(Handled::reply(success_envelope(id, &tools_list_result()))),
            "tools/call" => {
                let result = self.handle_tools_call(&params);
                Ok(Handled::reply(success_envelope(id, &result)))
            }
            // Liveness probe: the proxy answers for itself, it does not need the
            // runtime for this.
            "ping" => Ok(Handled::reply(success_envelope(id, &json!({})))),
            // Accepted for completeness: this ends the proxy only. The runtime
            // deliberately keeps running, so a restart is never destructive.
            "shutdown" => {
                log_debug!("mcp", "shutdown requested; runtime is left running");
                Ok(Handled::reply(success_envelope(id, &json!({}))))
            }
            "exit" => {
                log_debug!("mcp", "exit requested");
                Ok(Handled {
                    reply: Some(success_envelope(id, &json!({}))),
                    flow: Flow::Stop,
                })
            }
            other => Err(ProtocolError::UnknownMethod {
                id,
                method: other.to_string(),
            }),
        }
    }

    /// Translate one `tools/call` into a native operation and back.
    ///
    /// Never returns a JSON-RPC error: a failed tool is a successful JSON-RPC
    /// response carrying `isError: true`, because that is what hosts show the
    /// model.
    pub fn handle_tools_call(&self, params: &Value) -> Value {
        let name = params
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string();
        let arguments = params.get("arguments");

        if name.is_empty() {
            return tool_err(&ToolFailure::Arguments(
                "tools/call requires a tool 'name'".into(),
            ));
        }

        let wire = match Wire::new(&name, arguments) {
            Ok(wire) => wire,
            Err(failure) => return tool_err(&failure),
        };

        if name == "computer_screenshot" {
            match self.handle_screenshot(&wire) {
                Ok(content) => json!({
                    "content": content,
                    "isError": false
                }),
                Err(failure) => tool_err(&failure),
            }
        } else {
            match self.dispatch(&wire) {
                Ok(mut value) => {
                    if let Value::Object(ref mut map) = value {
                        map.insert("local_time".to_string(), Value::String(local_time_stamp()));
                    }
                    tool_ok(&value)
                }
                Err(failure) => tool_err(&failure),
            }
        }
    }

    fn handle_screenshot(&self, wire: &Wire<'_>) -> Result<Vec<Value>, ToolFailure> {
        let target_str = wire.opt_string("target")?.unwrap_or_else(|| "desktop".into());
        let hwnd_opt = wire.opt_u64("hwnd")?.map(|h| h as isize);
        let max_width = wire.opt_u32("max_width")?;

        let capture_target = match target_str.to_lowercase().as_str() {
            "desktop" => capture::CaptureTarget::Desktop,
            "foreground" => capture::CaptureTarget::Foreground,
            "window" => {
                if let Some(h) = hwnd_opt {
                    capture::CaptureTarget::Window(h)
                } else {
                    capture::CaptureTarget::Foreground
                }
            }
            "region" => {
                let x = wire.opt_u64("x")?.unwrap_or(0) as i32;
                let y = wire.opt_u64("y")?.unwrap_or(0) as i32;
                let w = wire.opt_u64("w")?.unwrap_or(0) as i32;
                let h = wire.opt_u64("h")?.unwrap_or(0) as i32;
                capture::CaptureTarget::Region { x, y, w, h }
            }
            other => {
                return Err(wire.fail(format!(
                    "unknown screenshot target '{other}'; expected 'desktop', 'window', 'foreground', or 'region'"
                )));
            }
        };

        let result = capture::capture_screenshot(capture_target, max_width)
            .map_err(|e| wire.fail(format!("screenshot capture failed: {e}")))?;

        let b64 = capture::to_base64(&result.png_bytes);
        let target_desc = match result.hwnd {
            Some(h) => format!("window=0x{:X}", h),
            None => "desktop".to_string(),
        };
        let info_text = format!(
            "Screenshot captured: {}x{} at ({},{}), {} ({} KB PNG) [local_time: {}]",
            result.width,
            result.height,
            result.x,
            result.y,
            target_desc,
            result.png_bytes.len() / 1024,
            local_time_stamp()
        );

        Ok(vec![
            json!({
                "type": "image",
                "data": b64,
                "mimeType": "image/png"
            }),
            json!({
                "type": "text",
                "text": info_text
            }),
        ])
    }

    fn dispatch(&self, wire: &Wire<'_>) -> Result<Value, ToolFailure> {
        match wire.tool {
            "computer_status" => {
                let report = self.native(|n| n.status())?;
                Ok(serde_json::to_value(report).unwrap_or(Value::Null))
            }
            "computer_windows" => {
                let request = observe_request(wire)?;
                let wanted_title = wire.opt_string("filter")?;
                let wanted_process = wire.opt_string("process")?;
                let observation = self.native(|n| n.observe(request))?;
                let windows: Vec<Value> = observation
                    .windows
                    .into_iter()
                    .filter(|w| match &wanted_title {
                        Some(needle) => w.matches_title(needle),
                        None => true,
                    })
                    .filter(|w| match &wanted_process {
                        // Substring so both "notepad" and "notepad.exe" work.
                        Some(name) => w.process_name.to_lowercase().contains(&name.to_lowercase()),
                        None => true,
                    })
                    .filter_map(|w| serde_json::to_value(w).ok())
                    .collect();
                Ok(json!({
                    "count": windows.len(),
                    "foreground": observation.foreground,
                    "backend": observation.backend,
                    "state_version": observation.state_version,
                    "windows": windows,
                }))
            }
            "computer_observe" => {
                let request = observe_request(wire)?;
                let observation = self.native(|n| n.observe(request))?;
                Ok(serde_json::to_value(observation).unwrap_or(Value::Null))
            }
            "computer_find" => {
                let selector: Selector = wire.member("selector")?;
                let limit = wire.opt_usize("limit")?;
                // A limit is a request for a list, so it implies `all`.
                let all = wire.opt_bool("all")?.unwrap_or(false) || limit.is_some();
                let result = self.native(|n| n.find_all(selector, all, limit))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "computer_read" => {
                let element_id = wire.opt_u64("element_id")?.ok_or_else(|| {
                    ToolFailure::Arguments("computer_read: missing 'element_id'".into())
                })?;
                let result = self.native(|n| n.read(element_id))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "computer_click" => {
                let x = wire.opt_u64("x")?.map(|v| v as i32);
                let y = wire.opt_u64("y")?.map(|v| v as i32);
                let button_str = wire.opt_string("button")?.unwrap_or_else(|| "left".into());
                let button = match button_str.to_lowercase().as_str() {
                    "right" => inbrisk_protocol::action::MouseButton::Right,
                    "middle" => inbrisk_protocol::action::MouseButton::Middle,
                    _ => inbrisk_protocol::action::MouseButton::Left,
                };
                let clicks = wire.opt_u64("clicks")?.unwrap_or(1) as u8;
                let hwnd = wire.opt_u64("hwnd")?;

                if let Some(h) = hwnd {
                    let _ = self.native(move |n| n.act(Step::Focus {
                        target: TargetRef::Selector(Box::new(Selector {
                            hwnd: Some(inbrisk_core::Hwnd(h as isize)),
                            ..Default::default()
                        })),
                        expect: None,
                    }));
                }

                let target = if let (Some(h), Some(cx), Some(cy)) = (hwnd, x, y) {
                    Some(TargetRef::Point(PointTarget::client_physical(h, cx as f64, cy as f64)))
                } else {
                    None
                };

                let step = Step::Click {
                    target,
                    x,
                    y,
                    hwnd,
                    button,
                    clicks,
                    expect: None,
                };
                let outcome = self.native(|n| n.act(step))?;
                Ok(serde_json::to_value(outcome).unwrap_or(Value::Null))
            }
            "computer_move" => {
                let x = wire.opt_u64("x")?.map(|v| v as i32);
                let y = wire.opt_u64("y")?.map(|v| v as i32);
                let hwnd = wire.opt_u64("hwnd")?;

                if let Some(h) = hwnd {
                    let _ = self.native(move |n| n.act(Step::Focus {
                        target: TargetRef::Selector(Box::new(Selector {
                            hwnd: Some(inbrisk_core::Hwnd(h as isize)),
                            ..Default::default()
                        })),
                        expect: None,
                    }));
                }

                let target = if let (Some(h), Some(cx), Some(cy)) = (hwnd, x, y) {
                    Some(TargetRef::Point(PointTarget::client_physical(h, cx as f64, cy as f64)))
                } else {
                    None
                };

                let step = Step::Move {
                    target,
                    x,
                    y,
                    hwnd,
                };
                let outcome = self.native(|n| n.act(step))?;
                Ok(serde_json::to_value(outcome).unwrap_or(Value::Null))
            }
            "computer_act" => {
                let action_arg = wire.required("action")?;
                let step = if action_arg.is_string() {
                    // Flattened step: e.g. { "action": "click", "x": 960, "y": 491 }
                    let obj = Value::Object(wire.args.clone());
                    step_from_value(obj)?
                } else if action_arg.is_array() {
                    // An array of steps is a plan written the short way; running
                    // it costs one round trip instead of N.
                    let plan = plan_from_steps(action_arg.clone())?;
                    let timeout = plan_timeout(wire)?;
                    let outcome = self.native(move |n| n.run(plan, timeout, false))?;
                    return Ok(serde_json::to_value(outcome).unwrap_or(Value::Null));
                } else {
                    step_from_value(action_arg.clone())?
                };
                let outcome = self.native(|n| n.act(step))?;
                Ok(serde_json::to_value(outcome).unwrap_or(Value::Null))
            }
            "computer_run" => {
                let plan = plan_from_wire(wire)?;
                let dry_run = wire.opt_bool("dry_run")?.unwrap_or(false);
                let timeout = plan_timeout(wire)?;
                let outcome = self.native(|n| n.run(plan, timeout, dry_run))?;
                Ok(serde_json::to_value(outcome).unwrap_or(Value::Null))
            }
            "computer_wait" => {
                let spec: WaitSpec = wire.flatten()?;
                let outcome = self.native(|n| n.wait(spec))?;
                Ok(serde_json::to_value(outcome).unwrap_or(Value::Null))
            }
            
            "pc_open" => {
                let shell = wire.opt_string("shell")?.unwrap_or_else(|| "pwsh".into());
                let cwd = wire.opt_string("cwd")?;
                let no_profile = wire.opt_bool("no_profile")?.unwrap_or(true);
                let cols = wire.opt_u64("cols")?.map(|v| v as u16);
                let rows = wire.opt_u64("rows")?.map(|v| v as u16);
                let buffer_bytes = wire.opt_usize("buffer_bytes")?;
                
                let result = self.native(|n| n.terminal_open(inbrisk_protocol::terminal::TerminalOpen {
                    shell: Some(shell),
                    cwd,
                    cols,
                    rows,
                    raw: false,
                    no_profile,
                    buffer_bytes,
                }))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "pc_exec" => {
                let session_id = wire.opt_string("session_id")?.unwrap();
                let command = wire.opt_string("command")?.unwrap();
                let wait_ms = wire.opt_u64("wait_ms")?.unwrap_or(2000);
                let settle_ms = wire.opt_u64("settle_ms")?.unwrap_or(100);
                
                use std::time::{SystemTime, UNIX_EPOCH, Instant, Duration};
                let sentinel = format!("__INBRISK_PC_DONE_{}__", SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_micros());
                
                let wrapped = format!("{}\r\necho \"{}__$?__$LASTEXITCODE\"", command, sentinel);
                
                let written = self.native(|n| n.terminal_write(inbrisk_protocol::terminal::TerminalWrite {
                    session_id: session_id.clone(),
                    text: wrapped,
                    normalize_newlines: true,
                    append_enter: true,
                }))?;
                
                let mut final_text = String::new();
                let mut alive = true;
                let mut timed_out = true;
                
                let start = Instant::now();
                let timeout = Duration::from_millis(wait_ms.max(1000));
                let mut to_seq = written.next_seq;
                let mut from_seq = written.next_seq;
                let mut first_read = true;
                
                loop {
                    let elapsed = start.elapsed();
                    if elapsed >= timeout {
                        break;
                    }
                    let remain = (timeout - elapsed).as_millis() as u64;
                    let chunk = self.native(|n| n.terminal_read(inbrisk_protocol::terminal::TerminalRead {
                        session_id: session_id.clone(),
                        after: None, // read from global cursor and advance it
                        wait_ms: remain.min(1000).max(100),
                        settle_ms,
                        max_bytes: None,
                        peek: false,
                    }))?;
                    
                    if first_read {
                        from_seq = chunk.from_seq;
                        first_read = false;
                    }
                    to_seq = chunk.to_seq;
                    alive = chunk.process_alive;
                    final_text.push_str(&chunk.text);
                    
                    if final_text.contains(&sentinel) || !alive {
                        timed_out = false;
                        break;
                    }
                }
                
                let mut clean_output = final_text.clone();
                let mut ps_success = false;
                let mut native_exit = 0;
                
                if let Some(idx) = clean_output.rfind(&sentinel) {
                    let tail = &clean_output[idx + sentinel.len()..];
                    if tail.starts_with("__") {
                        let parts: Vec<&str> = tail.split("__").collect();
                        if parts.len() >= 3 {
                            ps_success = parts[1].trim().eq_ignore_ascii_case("True");
                            native_exit = parts[2].trim().parse().unwrap_or(0);
                        }
                    }
                    clean_output.truncate(idx);
                    
                    let echo_cmd = format!("echo \"{}__$?__$LASTEXITCODE\"", sentinel);
                    clean_output = clean_output.replace(&echo_cmd, "");
                }
                
                Ok(json!({
                    "session_id": session_id,
                    "command": command,
                    "output": clean_output.trim(),
                    "from_seq": from_seq,
                    "to_seq": to_seq,
                    "truncated": false,
                    "process_alive": alive,
                    "timed_out_waiting_for_output": timed_out,
                    "completed": !timed_out,
                    "native_exit_code": native_exit,
                    "powershell_success": ps_success,
                }))
            }
            "pc_write" => {
                let session_id = wire.opt_string("session_id")?.unwrap();
                let text = wire.opt_string("text")?.unwrap();
                let normalize_newlines = wire.opt_bool("normalize_newlines")?.unwrap_or(true);
                let append_enter = wire.opt_bool("append_enter")?.unwrap_or(false);
                let result = self.native(|n| n.terminal_write(inbrisk_protocol::terminal::TerminalWrite {
                    session_id,
                    text,
                    normalize_newlines,
                    append_enter,
                }))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "pc_read" => {
                let session_id = wire.opt_string("session_id")?.unwrap();
                let after = wire.opt_u64("after")?;
                let wait_ms = wire.opt_u64("wait_ms")?.unwrap_or(0);
                let settle_ms = wire.opt_u64("settle_ms")?.unwrap_or(0);
                let peek = wire.opt_bool("peek")?.unwrap_or(false);
                let result = self.native(|n| n.terminal_read(inbrisk_protocol::terminal::TerminalRead {
                    session_id,
                    after,
                    wait_ms,
                    settle_ms,
                    max_bytes: None,
                    peek,
                }))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "pc_status" => {
                let session_id = wire.opt_string("session_id")?;
                let result = self.native(|n| n.terminal_status(session_id))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }
            "pc_close" => {
                let session_id = wire.opt_string("session_id")?.unwrap();
                let result = self.native(|n| n.terminal_close(session_id))?;
                Ok(serde_json::to_value(result).unwrap_or(Value::Null))
            }

            "computer_cancel" => {
                let reason = wire.opt_string("reason")?;
                let cancelled = self.native(|n| n.cancel_all(reason))?;
                Ok(serde_json::to_value(cancelled).unwrap_or(Value::Null))
            }
            "computer_time" => {
                let ms = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map(|d| d.as_millis() as u64)
                    .unwrap_or(0);
                Ok(json!({
                    "local_time": local_time_stamp(),
                    "epoch_ms": ms,
                }))
            }
            other => Err(ToolFailure::Arguments(format!("unknown tool '{other}'"))),
        }
    }

    /// Borrow the session and run one native operation through it.
    fn native<T>(
        &self,
        operation: impl FnOnce(&mut dyn NativeOps) -> Result<T, InbriskError>,
    ) -> Result<T, ToolFailure> {
        let mut session = self
            .session
            .try_borrow_mut()
            .map_err(|_| ToolFailure::Arguments("concurrent tool call in progress".into()))?;
        let result = session.call(operation);
        if let Err(err) = &result {
            // The model sees this in the tool result; the operator watching
            // stderr sees the code and hint without the JSON around them.
            log_debug!(
                "mcp",
                "native call failed: {}: {}{}",
                err.code.as_str(),
                err.message,
                err.hint
                    .as_deref()
                    .map(|h| format!(" (hint: {h})"))
                    .unwrap_or_default()
            );
        }
        result.map_err(ToolFailure::Native)
    }
}

/// `initialize` result, exactly as MCP 2024-11-05 defines it.
fn initialize_result() -> Value {
    json!({
        "protocolVersion": MCP_PROTOCOL_VERSION,
        "capabilities": { "tools": {} },
        "serverInfo": {
            "name": SERVER_NAME,
            "version": env!("CARGO_PKG_VERSION")
        }
    })
}

/// `tools/list` result: the catalogue, schemas included.
pub fn tools_list_result() -> Value {
    let tools: Vec<Value> = TOOLS
        .iter()
        .map(|tool| {
            json!({
                "name": tool.name,
                "description": tool.description,
                "inputSchema": (tool.input_schema)()
            })
        })
        .collect();
    json!({ "tools": tools })
}

fn success_envelope(id: Value, result: &Value) -> String {
    let envelope = json!({ "jsonrpc": "2.0", "id": id, "result": result });
    serde_json::to_string(&envelope).unwrap_or_else(|_| {
        // Serialising the two shapes above cannot fail; keep the contract even
        // if it somehow did.
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"internal error\"}}"
            .to_string()
    })
}

fn error_envelope(id: Value, code: i32, message: &str) -> String {
    let envelope = json!({
        "jsonrpc": "2.0",
        "id": id,
        "error": { "code": code, "message": message }
    });
    serde_json::to_string(&envelope).unwrap_or_else(|_| {
        "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"internal error\"}}"
            .to_string()
    })
}

// ---------------------------------------------------------------------------
// Argument translation (kept free of IO so it is unit-testable)
// ---------------------------------------------------------------------------

/// Build an `ObserveRequest` from `computer_windows` / `computer_observe`
/// arguments.
fn observe_request(wire: &Wire<'_>) -> Result<ObserveRequest, ToolFailure> {
    let mut request = ObserveRequest {
        include_tree: wire.opt_bool("include_tree")?.unwrap_or(false),
        max_depth: wire.opt_u32("max_depth")?,
        max_elements: wire.opt_usize("max_elements")?,
        timeout_ms: wire.opt_u64("timeout_ms")?,
        title_contains: wire.opt_string("filter")?,
        ..ObserveRequest::default()
    };
    if let Some(process) = wire.opt_string("process")? {
        request.processes.push(process);
    }

    if let Some(window) = wire.get("window") {
        match window {
            // A bare integer is a window handle: cheaper and unambiguous.
            Value::Number(n) => {
                if let Some(hwnd) = n.as_u64() {
                    request.hwnd = Some(inbrisk_core::Hwnd(hwnd as isize));
                }
            }
            Value::Object(_) => {
                let selector: Selector = serde_json::from_value(window.clone())
                    .map_err(|e| wire.fail(format!("'window' is not a valid selector: {e}")))?;
                request.window = Some(selector);
                // Asking for one window implies looking inside it.
                request.scope = inbrisk_protocol::request::ObserveScope::Window;
            }
            other => {
                return Err(wire.fail(format!(
                    "'window' must be a selector object or a window handle, got {}",
                    kind_of(other)
                )))
            }
        }
    }

    // `include_tree` only ever applies to a window scope; a desktop-wide tree is
    // a different (and much heavier) request.
    if request.include_tree && request.scope == inbrisk_protocol::request::ObserveScope::Windows {
        request.scope = if request.window.is_some() {
            inbrisk_protocol::request::ObserveScope::Window
        } else {
            inbrisk_protocol::request::ObserveScope::Desktop
        };
    }

    Ok(request)
}

/// Deserialize one MCP action object into a `Step`.
fn step_from_value(value: Value) -> Result<Step, ToolFailure> {
    serde_json::from_value(value).map_err(|e| {
        ToolFailure::Arguments(format!(
            "not a valid action: {e}. Actions are launch, focus, find, invoke, set_value, click, \
             move, type, key, scroll, select, toggle, drag, wait, sleep, close, human."
        ))
    })
}

/// Deserialize an MCP `steps` array into a `Plan`.
fn plan_from_steps(steps: Value) -> Result<Plan, ToolFailure> {
    let steps_val = match steps {
        Value::String(s) => serde_json::from_str(&s).unwrap_or(Value::String(s)),
        other => other,
    };
    match steps_val {
        Value::Array(items) => {
            if items.is_empty() {
                return Err(ToolFailure::Arguments(
                    "'steps' must contain at least one step".into(),
                ));
            }
            let mut parsed = Vec::with_capacity(items.len());
            for (index, item) in items.into_iter().enumerate() {
                let step = step_from_value(item)
                    .map_err(|e| ToolFailure::Arguments(format!("step {index}: {e}")))?;
                parsed.push(step);
            }
            Ok(Plan::new(parsed))
        }
        Value::Object(map) => {
            let step = step_from_value(Value::Object(map))?;
            Ok(Plan::new(vec![step]))
        }
        other => Err(ToolFailure::Arguments(format!(
            "'steps' must be an array, got {}",
            kind_of(&other)
        ))),
    }
}

/// Build the `Plan` for `computer_run` (and for an array-shaped
/// `computer_act`).
fn plan_from_wire(wire: &Wire<'_>) -> Result<Plan, ToolFailure> {
    let steps = wire.required("steps")?.clone();
    let mut plan = plan_from_steps(steps)?;
    plan.name = wire.opt_string("name")?;
    Ok(plan)
}

/// Resolve the per-plan timeout, clamping absurd values instead of rejecting
/// them: a wrong number should not cost the caller a round trip.
fn plan_timeout(wire: &Wire<'_>) -> Result<Duration, ToolFailure> {
    let Some(raw) = wire.opt_u64("timeout_ms")? else {
        return Ok(Duration::from_millis(DEFAULT_RUN_TIMEOUT_MS));
    };
    Ok(Duration::from_millis(raw.clamp(1, MAX_RUN_TIMEOUT_MS)))
}

fn debug_from_env() -> bool {
    std::env::var(DEBUG_ENV)
        .map(|value| {
            let value = value.trim();
            !value.is_empty() && value != "0" && !value.eq_ignore_ascii_case("false")
        })
        .unwrap_or(false)
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;

    /// A native handle that is always available and answers from memory.
    ///
    /// It exists to prove two things without a runtime: that a tool call which
    /// never reaches the wire returns the right shape, and that the proxy does
    /// not depend on `inbrisk_sdk::Runtime` for its translation logic.
    #[derive(Default)]
    struct StubNative {
        status_calls: usize,
    }

    impl NativeOps for StubNative {
        fn endpoint(&self) -> String {
            "stub".into()
        }
    fn terminal_open(&mut self, _req: inbrisk_protocol::terminal::TerminalOpen) -> Result<inbrisk_protocol::terminal::TerminalInfo, InbriskError> { unimplemented!() }
    fn terminal_write(&mut self, _req: inbrisk_protocol::terminal::TerminalWrite) -> Result<inbrisk_protocol::terminal::TerminalWritten, InbriskError> { unimplemented!() }
    fn terminal_read(&mut self, _req: inbrisk_protocol::terminal::TerminalRead) -> Result<inbrisk_protocol::terminal::TerminalOutput, InbriskError> { unimplemented!() }
    fn terminal_status(&mut self, _req: Option<String>) -> Result<Vec<inbrisk_protocol::terminal::TerminalInfo>, InbriskError> { unimplemented!() }
    fn terminal_close(&mut self, _req: String) -> Result<inbrisk_protocol::terminal::TerminalClosed, InbriskError> { unimplemented!() }

        fn status(&mut self) -> Result<StatusReport, InbriskError> {
            self.status_calls += 1;
            Err(InbriskError::not_implemented("stub"))
        }

        fn observe(&mut self, _request: ObserveRequest) -> Result<Observation, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn find_all(
            &mut self,
            _selector: Selector,
            _all: bool,
            _limit: Option<usize>,
        ) -> Result<FindResult, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn read(&mut self, _element_id: u64) -> Result<ReadResult, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn act(&mut self, _step: Step) -> Result<ActionOutcome, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn run(
            &mut self,
            _plan: Plan,
            _timeout: Duration,
            _dry_run: bool,
        ) -> Result<RunOutcome, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn wait(
            &mut self,
            _spec: WaitSpec,
        ) -> Result<inbrisk_protocol::response::WaitOutcome, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }

        fn cancel_all(&mut self, _reason: Option<String>) -> Result<Cancelled, InbriskError> {
            Err(InbriskError::not_implemented("stub"))
        }
    }

    fn parse(line: &str) -> Value {
        serde_json::from_str(line).expect("handler must emit valid JSON")
    }

    /// Walk a schema fragment and assert that each local `$ref` resolves against
    /// the root's `definitions`.
    fn assert_refs_resolve(tool: &str, root: &Value, node: &Value) {
        match node {
            Value::Object(object) => {
                if let Some(reference) = object.get("$ref").and_then(Value::as_str) {
                    if let Some(key) = reference.strip_prefix("#/definitions/") {
                        assert!(
                            root.get("definitions").and_then(|d| d.get(key)).is_some(),
                            "{tool}: $ref '{reference}' does not resolve"
                        );
                    }
                }
                for value in object.values() {
                    assert_refs_resolve(tool, root, value);
                }
            }
            Value::Array(items) => {
                for item in items {
                    assert_refs_resolve(tool, root, item);
                }
            }
            _ => {}
        }
    }

    fn call(proxy: &Proxy, line: &str) -> Value {
        let handled = proxy.handle_line(line).expect("expected a reply");
        assert_eq!(handled.flow, Flow::Continue);
        parse(&handled.reply.expect("expected a reply body"))
    }

    #[test]
    fn initialize_advertises_tools_capability() {
        let proxy = Proxy::new();
        let reply = call(
            &proxy,
            r#"{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}"#,
        );

        assert_eq!(reply["jsonrpc"], "2.0");
        assert_eq!(reply["id"], 1);
        assert_eq!(reply["result"]["protocolVersion"], MCP_PROTOCOL_VERSION);
        assert_eq!(reply["result"]["serverInfo"]["name"], SERVER_NAME);
        assert_eq!(
            reply["result"]["serverInfo"]["version"],
            env!("CARGO_PKG_VERSION")
        );
        assert!(reply["result"]["capabilities"]["tools"].is_object());
        assert!(reply.get("error").is_none());
    }

    #[test]
    fn tools_list_contains_every_tool_with_a_valid_schema() {
        let proxy = Proxy::new();
        let reply = call(&proxy, r#"{"jsonrpc":"2.0","id":2,"method":"tools/list"}"#);
        let tools = reply["result"]["tools"]
            .as_array()
            .expect("tools must be an array");

        assert_eq!(tools.len(), TOOLS.len());
        assert_eq!(tools.len(), 19, "the catalogue is part of the contract");

        let mut seen: std::collections::BTreeSet<&str> = std::collections::BTreeSet::new();
        for tool in tools {
            let name = tool["name"].as_str().expect("every tool needs a name");
            let description = tool["description"]
                .as_str()
                .expect("every tool needs a description");
            assert!(!description.trim().is_empty(), "{name} needs a description");
            assert!(description.len() > 20, "{name} needs a useful description");

            let schema = &tool["inputSchema"];
            assert_eq!(
                schema["type"], "object",
                "{name} inputSchema must describe an object"
            );
            assert!(
                schema["properties"].is_object(),
                "{name} inputSchema must carry properties"
            );

            // `required`, when present, must name properties that exist.
            if let Some(required) = schema.get("required") {
                let required = required
                    .as_array()
                    .unwrap_or_else(|| panic!("{name} required must be an array"));
                for key in required {
                    let key = key.as_str().expect("required entries must be strings");
                    assert!(
                        schema["properties"].get(key).is_some(),
                        "{name} requires '{key}' but does not declare it"
                    );
                }
            }

            // Every `$ref` must resolve inside the schema that carries it: a
            // dangling reference makes a strict host reject the whole tool.
            assert_refs_resolve(name, schema, schema);

            // Every declared property must be a usable schema fragment.
            if let Some(properties) = schema["properties"].as_object() {
                for (key, fragment) in properties {
                    assert!(fragment.is_object(), "{name}.{key} must be a schema object");
                    assert!(
                        fragment.get("type").is_some()
                            || fragment.get("oneOf").is_some()
                            || fragment.get("anyOf").is_some()
                            || fragment.get("allOf").is_some()
                            || fragment.get("$ref").is_some(),
                        "{name}.{key} must declare a type or a composition"
                    );
                }
            }

            assert!(seen.insert(name), "duplicate tool {name}");
        }

        for expected in [
            "computer_status",
            "computer_windows",
            "computer_observe",
            "computer_find",
            "computer_read",
            "computer_act",
            "computer_run",
            "computer_wait",
            "computer_cancel",
            "computer_screenshot",
            "computer_click",
            "computer_move",
        ] {
            assert!(seen.contains(&expected), "missing tool {expected}");
        }
    }

    #[test]
    fn screenshot_tool_spec_and_call() {
        let spec = tool_spec("computer_screenshot").expect("computer_screenshot must exist");
        assert!(spec.description.contains("image"));

        let proxy = Proxy::new();
        let reply = call(
            &proxy,
            r#"{"jsonrpc":"2.0","id":100,"method":"tools/call","params":{"name":"computer_screenshot","arguments":{"target":"desktop","max_width":640}}}"#,
        );
        assert_eq!(reply["jsonrpc"], "2.0");
        assert_eq!(reply["id"], 100);
        let content = reply["result"]["content"].as_array().expect("content must be array");
        assert_eq!(content.len(), 2);
        assert_eq!(content[0]["type"], "image");
        assert_eq!(content[0]["mimeType"], "image/png");
        assert!(content[0]["data"].as_str().expect("data must be base64 string").len() > 100);
        assert_eq!(content[1]["type"], "text");
    }

    #[test]
    fn coordinate_click_and_drag_deserialization() {
        let spec = tool_spec("computer_click").expect("computer_click must exist");
        assert!(spec.description.contains("coordinates"));

        // 1. Direct coordinate click
        let step_json = json!({
            "action": "click",
            "x": 960,
            "y": 491,
            "button": "left"
        });
        let step = step_from_value(step_json).expect("click with x/y must deserialize");
        match step {
            Step::Click { x, y, button, .. } => {
                assert_eq!(x, Some(960));
                assert_eq!(y, Some(491));
                assert_eq!(button, inbrisk_protocol::action::MouseButton::Left);
            }
            other => panic!("expected Click, got {other:?}"),
        }

        // 2. Coordinate drag with from/to as points
        let drag_json = json!({
            "action": "drag",
            "from": { "x": 100, "y": 200 },
            "to": { "x": 300, "y": 400 }
        });
        let drag_step = step_from_value(drag_json).expect("drag with points must deserialize");
        match drag_step {
            Step::Drag { from, to } => {
                match from {
                    TargetRef::Point(pt) => {
                        assert_eq!(pt.x, 100.0);
                        assert_eq!(pt.y, 200.0);
                    }
                    other => panic!("expected Point from, got {other:?}"),
                }
                match to {
                    inbrisk_protocol::action::DropTarget::Point(pt) => {
                        assert_eq!(pt.x, 300.0);
                        assert_eq!(pt.y, 400.0);
                    }
                    other => panic!("expected Point to, got {other:?}"),
                }
            }
            other => panic!("expected Drag, got {other:?}"),
        }

        // 3. String-encoded steps array
        let string_steps = json!("[{\"action\": \"click\", \"x\": 960, \"y\": 491}]");
        let plan = plan_from_steps(string_steps).expect("string-encoded steps array must parse");
        assert_eq!(plan.steps.len(), 1);

        // 4. Single step object passed as steps
        let single_step = json!({ "action": "click", "x": 960, "y": 491 });
        let plan2 = plan_from_steps(single_step).expect("single step object must parse");
        assert_eq!(plan2.steps.len(), 1);
    }

    #[test]
    fn coordinate_move_deserialization() {
        let spec = tool_spec("computer_move").expect("computer_move must exist");
        assert!(spec.description.contains("cursor"));

        let step_json = json!({
            "action": "move",
            "x": 800,
            "y": 600,
            "hwnd": 12345
        });
        let step = step_from_value(step_json).expect("move with x/y/hwnd must deserialize");
        match step {
            Step::Move { x, y, hwnd, .. } => {
                assert_eq!(x, Some(800));
                assert_eq!(y, Some(600));
                assert_eq!(hwnd, Some(12345));
            }
            other => panic!("expected Move, got {other:?}"),
        }
    }

    #[test]
    fn run_tool_documents_the_single_round_trip_benefit() {
        let spec = tool_spec("computer_run").expect("computer_run must exist");
        assert!(spec.description.contains("single round trip"));
    }

    #[test]
    fn unknown_method_is_method_not_found() {
        let proxy = Proxy::new();
        let error = proxy
            .handle_line(r#"{"jsonrpc":"2.0","id":3,"method":"tools/nope"}"#)
            .expect_err("unknown methods must be reported");
        assert_eq!(error.code(), METHOD_NOT_FOUND);
        let reply = parse(&error.envelope());
        assert_eq!(reply["error"]["code"], METHOD_NOT_FOUND);
        // The id must survive, or the host cannot match the failure to its call.
        assert_eq!(reply["id"], 3);
        assert!(reply.get("result").is_none());
    }

    #[test]
    fn malformed_json_is_a_parse_error() {
        let proxy = Proxy::new();
        let error = proxy
            .handle_line("{\"jsonrpc\": \"2.0\", \"id\": 4, ")
            .expect_err("malformed JSON must produce an error envelope");
        assert_eq!(error.code(), PARSE_ERROR);
        let reply = parse(&error.envelope());
        assert_eq!(reply["error"]["code"], PARSE_ERROR);
        assert_eq!(reply["id"], Value::Null);
    }

    #[test]
    fn notifications_never_get_a_reply() {
        let proxy = Proxy::new();
        let handled = proxy
            .handle_line(r#"{"jsonrpc":"2.0","method":"notifications/initialized"}"#)
            .expect("notifications must not fail");
        assert!(handled.reply.is_none());
        assert_eq!(handled.flow, Flow::Continue);

        let handled = proxy
            .handle_line(r#"{"jsonrpc":"2.0","method":"notifications/exit"}"#)
            .expect("exit must not fail");
        assert!(handled.reply.is_none());
        assert_eq!(handled.flow, Flow::Stop);
    }

    #[test]
    fn plan_translation_from_run_arguments() {
        let arguments = json!({
            "name": "notepad-round-trip",
            "timeout_ms": 45_000,
            "steps": [
                {
                    "action": "launch",
                    "app": "notepad.exe",
                    "wait_for_window": { "name": "Notepad" },
                    "store": "pad"
                },
                {
                    "action": "type",
                    "target": "$pad",
                    "text": "hello",
                    "clear_first": true
                },
                {
                    "action": "key",
                    "keys": ["ctrl", "s"]
                },
                {
                    "action": "wait",
                    "until": "window",
                    "selector": { "role": "dialog" },
                    "state": "visible"
                },
                {
                    "action": "invoke",
                    "target": { "role": "button", "name": "Save", "exact": true }
                },
                {
                    "action": "close",
                    "target": "$pad"
                }
            ]
        });

        let wire = Wire::new("computer_run", Some(&arguments)).expect("arguments parse");
        let plan = plan_from_wire(&wire).expect("plan must translate");

        assert_eq!(plan.name.as_deref(), Some("notepad-round-trip"));
        assert_eq!(plan.steps.len(), 6);
        assert_eq!(plan.steps[0].action_name(), "launch");
        assert_eq!(plan.steps[1].action_name(), "type");
        assert_eq!(plan.steps[2].action_name(), "key");
        assert_eq!(plan.steps[3].action_name(), "wait");
        assert_eq!(plan.steps[4].action_name(), "invoke");
        assert_eq!(plan.steps[5].action_name(), "close");

        let Step::Launch {
            app,
            wait_for_window,
            store,
            ..
        } = &plan.steps[0]
        else {
            panic!("step 0 must be a launch");
        };
        assert_eq!(app, "notepad.exe");
        assert_eq!(store.as_deref(), Some("pad"));
        assert_eq!(
            wait_for_window.as_ref().and_then(|s| s.name.as_deref()),
            Some("Notepad")
        );

        // `$pad` must survive as a reference, not as a literal selector.
        let Step::Type { target, text, .. } = &plan.steps[1] else {
            panic!("step 1 must be a type");
        };
        assert_eq!(text, "hello");
        assert_eq!(target.as_ref().and_then(|t| t.as_reference()), Some("$pad"));

        // The wait step keeps its nested WaitSpec, tag and all.
        let Step::Wait(spec) = &plan.steps[3] else {
            panic!("step 3 must be a wait");
        };
        assert!(matches!(
            spec,
            WaitSpec::Window {
                state: inbrisk_protocol::action::WindowWaitState::Visible,
                ..
            }
        ));

        // Timeout is honoured and clamped at the edges.
        assert_eq!(plan_timeout(&wire).expect("timeout").as_millis(), 45_000);
    }

    #[test]
    fn run_arguments_reject_an_empty_step_list() {
        let arguments = json!({ "steps": [] });
        let wire = Wire::new("computer_run", Some(&arguments)).expect("arguments parse");
        let failure = plan_from_wire(&wire).expect_err("an empty plan is not runnable");
        assert!(format!("{failure:?}").contains("at least one step"));
    }

    #[test]
    fn run_arguments_reject_an_unknown_action() {
        let arguments = json!({ "steps": [{ "action": "teleport" }] });
        let wire = Wire::new("computer_run", Some(&arguments)).expect("arguments parse");
        let failure = plan_from_wire(&wire).expect_err("unknown actions must be rejected");
        match failure {
            ToolFailure::Arguments(message) => {
                assert!(message.contains("step 0"), "{message}");
                assert!(message.contains("not a valid action"), "{message}");
            }
            ToolFailure::Native(err) => panic!("unexpected native error {err:?}"),
        }
    }

    #[test]
    fn single_action_translation() {
        let arguments = json!({ "action": { "action": "set_value", "target": { "role": "edit" }, "value": "abc" } });
        let wire = Wire::new("computer_act", Some(&arguments)).expect("arguments parse");
        let step = step_from_value(wire.required("action").expect("action").clone())
            .expect("step must translate");
        assert_eq!(step.action_name(), "set_value");
        assert!(step.is_mutating());
    }

    #[test]
    fn observe_arguments_map_onto_the_request() {
        let arguments = json!({
            "window": { "name": "Notepad" },
            "include_tree": true,
            "max_depth": 4,
            "max_elements": 200
        });
        let wire = Wire::new("computer_observe", Some(&arguments)).expect("arguments parse");
        let request = observe_request(&wire).expect("request must translate");
        assert_eq!(
            request.scope,
            inbrisk_protocol::request::ObserveScope::Window
        );
        assert!(request.include_tree);
        assert_eq!(request.max_depth, Some(4));
        assert_eq!(request.max_elements, Some(200));
        assert_eq!(
            request.window.as_ref().and_then(|s| s.name.as_deref()),
            Some("Notepad")
        );
    }

    #[test]
    fn windows_arguments_filter_by_title_and_process() {
        let arguments = json!({ "filter": "note", "process": "notepad.exe" });
        let wire = Wire::new("computer_windows", Some(&arguments)).expect("arguments parse");
        let request = observe_request(&wire).expect("request must translate");
        assert_eq!(request.title_contains.as_deref(), Some("note"));
        assert_eq!(request.processes, vec!["notepad.exe".to_string()]);
        // A cheap window listing must never drag in the UI Automation tree.
        assert!(!request.include_tree);
    }

    #[test]
    fn bad_argument_types_are_reported_not_panicked() {
        let proxy = Proxy::new();
        let reply = call(
            &proxy,
            r#"{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"computer_read","arguments":{"element_id":"nope"}}}"#,
        );
        assert_eq!(reply["result"]["isError"], true);
        let text = reply["result"]["content"][0]["text"]
            .as_str()
            .expect("text content");
        assert!(text.contains("element_id"), "{text}");
    }

    #[test]
    fn unknown_tool_is_an_error_result_not_a_transport_error() {
        let proxy = Proxy::new();
        let reply = call(
            &proxy,
            r#"{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"computer_teleport","arguments":{}}}"#,
        );
        assert_eq!(reply["result"]["isError"], true);
        assert!(reply.get("error").is_none());
        let text = reply["result"]["content"][0]["text"]
            .as_str()
            .expect("text content");
        assert!(text.contains("unknown tool"), "{text}");
    }

    #[test]
    fn native_failure_becomes_an_error_result_with_the_hint() {
        let proxy = Proxy::with_session(Session::with_native(Box::<StubNative>::default()), false);
        let reply = call(
            &proxy,
            r#"{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"computer_status","arguments":{}}}"#,
        );
        assert_eq!(reply["result"]["isError"], true);
        let text = reply["result"]["content"][0]["text"]
            .as_str()
            .expect("text content");
        let body: Value = serde_json::from_str(text).expect("text must be JSON");
        assert_eq!(body["error"]["code"], "NotImplemented");
    }

    #[test]
    fn ping_answers_without_a_runtime() {
        let proxy = Proxy::new();
        let reply = call(&proxy, r#"{"jsonrpc":"2.0","id":12,"method":"ping"}"#);
        assert_eq!(reply["result"], json!({}));
    }

    #[test]
    fn missing_method_is_invalid_params() {
        let proxy = Proxy::new();
        let error = proxy
            .handle_line(r#"{"jsonrpc":"2.0","id":13}"#)
            .expect_err("a request without a method is invalid");
        assert_eq!(error.code(), INVALID_PARAMS);
        assert_eq!(parse(&error.envelope())["error"]["code"], INVALID_PARAMS);
    }

    #[test]
    fn a_non_object_line_is_rejected_without_panicking() {
        let proxy = Proxy::new();
        let error = proxy
            .handle_line("[]")
            .expect_err("a bare array is not a JSON-RPC message");
        assert_eq!(parse(&error.envelope())["error"]["code"], INVALID_PARAMS);
    }

    #[test]
    fn session_reconnects_after_a_dropped_runtime() {
        /// A native handle that disappears once it has been used, standing in
        /// for `inbrisk.exe` being restarted mid-session.
        struct FlakyNative {
            generation: bool,
        }

        impl NativeOps for FlakyNative {
            fn endpoint(&self) -> String {
                "flaky".into()
            }
    fn terminal_open(&mut self, _req: inbrisk_protocol::terminal::TerminalOpen) -> Result<inbrisk_protocol::terminal::TerminalInfo, InbriskError> { Err(unavailable_error()) }
    fn terminal_write(&mut self, _req: inbrisk_protocol::terminal::TerminalWrite) -> Result<inbrisk_protocol::terminal::TerminalWritten, InbriskError> { Err(unavailable_error()) }
    fn terminal_read(&mut self, _req: inbrisk_protocol::terminal::TerminalRead) -> Result<inbrisk_protocol::terminal::TerminalOutput, InbriskError> { Err(unavailable_error()) }
    fn terminal_status(&mut self, _req: Option<String>) -> Result<Vec<inbrisk_protocol::terminal::TerminalInfo>, InbriskError> { Err(unavailable_error()) }
    fn terminal_close(&mut self, _req: String) -> Result<inbrisk_protocol::terminal::TerminalClosed, InbriskError> { Err(unavailable_error()) }

            fn status(&mut self) -> Result<StatusReport, InbriskError> {
                Err(InbriskError::new(
                    ErrorCode::RuntimeUnavailable,
                    "the runtime went away",
                ))
            }

            fn observe(&mut self, _request: ObserveRequest) -> Result<Observation, InbriskError> {
                let _ = self.generation;
                Err(unavailable_error())
            }

            fn find_all(
                &mut self,
                _selector: Selector,
                _all: bool,
                _limit: Option<usize>,
            ) -> Result<FindResult, InbriskError> {
                Err(unavailable_error())
            }

            fn read(&mut self, _element_id: u64) -> Result<ReadResult, InbriskError> {
                Err(unavailable_error())
            }

            fn act(&mut self, _step: Step) -> Result<ActionOutcome, InbriskError> {
                Err(unavailable_error())
            }

            fn run(
                &mut self,
                _plan: Plan,
                _timeout: Duration,
                _dry_run: bool,
            ) -> Result<RunOutcome, InbriskError> {
                Err(unavailable_error())
            }

            fn wait(
                &mut self,
                _spec: WaitSpec,
            ) -> Result<inbrisk_protocol::response::WaitOutcome, InbriskError> {
                Err(unavailable_error())
            }

            fn cancel_all(&mut self, _reason: Option<String>) -> Result<Cancelled, InbriskError> {
                Err(unavailable_error())
            }
        }

        let mut session = Session::with_native(Box::new(FlakyNative { generation: true }));
        assert!(session.is_connected());

        // The first call fails with "runtime unavailable", which must invalidate
        // the cached handle instead of leaving it in place.
        let result = session.call(|native| native.status());
        if !Runtime::is_available() {
            assert!(result.is_err());
            assert!(
                !session.is_connected(),
                "a dead runtime must not stay cached"
            );
        } else {
            assert!(session.is_connected());
        }
    }
}


fn schema_pc_open() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "shell": { "type": "string", "description": "Shell to launch. Defaults to 'pwsh'." },
            "cwd": { "type": "string", "description": "Optional working directory." },
            "no_profile": { "type": "boolean", "description": "Start PowerShell without profile." },
            "cols": { "type": "integer", "description": "Columns (width)." },
            "rows": { "type": "integer", "description": "Rows (height)." },
            "buffer_bytes": { "type": "integer", "description": "Ring capacity in bytes." }
        }
    })
}

fn schema_pc_exec() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "session_id": { "type": "string", "description": "The persistent session ID." },
            "command": { "type": "string", "description": "The command to execute." },
            "wait_ms": { "type": "integer", "description": "Max time to wait for completion." },
            "settle_ms": { "type": "integer", "description": "Time to wait for output to settle." }
        },
        "required": ["session_id", "command"]
    })
}

fn schema_pc_write() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "session_id": { "type": "string", "description": "The persistent session ID." },
            "text": { "type": "string", "description": "Raw text to write." },
            "normalize_newlines": { "type": "boolean" },
            "append_enter": { "type": "boolean" }
        },
        "required": ["session_id", "text"]
    })
}

fn schema_pc_read() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "session_id": { "type": "string", "description": "The persistent session ID." },
            "after": { "type": "integer", "description": "Sequence number." },
            "wait_ms": { "type": "integer", "description": "Wait time." },
            "settle_ms": { "type": "integer", "description": "Settle time." },
            "peek": { "type": "boolean", "description": "Do not advance cursor." }
        },
        "required": ["session_id"]
    })
}

fn schema_pc_status() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "session_id": { "type": "string", "description": "Optional session ID." }
        }
    })
}

fn schema_pc_close() -> Value {
    json!({
        "$schema": "http://json-schema.org/draft-07/schema#",
        "type": "object",
        "additionalProperties": false,
        "properties": {
            "session_id": { "type": "string", "description": "Session ID." }
        },
        "required": ["session_id"]
    })
}
