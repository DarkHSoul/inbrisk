use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};
use serde_json::Value;

use inbrisk_core::{
    ActivitySnapshot, ElementRef, Generation, Hwnd, InbriskError, OperationId, OwnershipStamp,
    PlanId, RequestId, RuntimeIdentity, SessionId, StateVersion, WindowRef,
};

use crate::action::OnError;
use crate::caps::Capabilities;
use crate::opcode::Op;

/// Answer to `HELLO`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Welcome {
    pub session_id: SessionId,
    pub client_id: u64,
    /// Changes on every runtime start; a mismatch means "stale runtime".
    pub runtime_epoch: u64,
    pub protocol_version: u16,
    pub runtime: RuntimeIdentity,
    pub capabilities: Capabilities,
    /// Shared-memory section the client should have opened.
    pub region_name: String,
    pub heartbeat_ms: u32,
    pub state_version: StateVersion,
    pub generation: Generation,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Pong {
    pub seq: u64,
    pub client_send_ns: u64,
    pub runtime_recv_ns: u64,
    pub runtime_send_ns: u64,
    pub session_id: SessionId,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MonitorInfo {
    pub id: u32,
    pub bounds: inbrisk_core::Rect,
    pub work_area: inbrisk_core::Rect,
    pub primary: bool,
    pub dpi: u32,
    #[serde(default)]
    pub name: String,
}

/// The answer to `observe`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Observation {
    pub state_version: StateVersion,
    pub generation: Generation,
    pub captured_at_ms: u64,
    pub elapsed_us: u64,
    #[serde(default)]
    pub windows: Vec<WindowRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub foreground: Option<WindowRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub focused_element: Option<ElementRef>,
    /// UIA subtree, present when `include_tree` was requested.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub elements: Vec<ElementRef>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub monitors: Vec<MonitorInfo>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cursor: Option<(i32, i32)>,
    #[serde(default)]
    pub activity: ActivitySnapshot,
    /// Backend that produced the answer (`win32`, `uia`, ...).
    #[serde(default)]
    pub backend: String,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FindResult {
    pub selector: String,
    pub matches: Vec<ElementRef>,
    pub scanned: u32,
    pub truncated: bool,
    pub backend: String,
    pub elapsed_us: u64,
    pub state_version: StateVersion,
    pub generation: Generation,
    /// Candidates reported when `policy = strict` and several matched.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub candidates: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReadResult {
    pub element: ElementRef,
    pub properties: BTreeMap<String, Value>,
    pub backend: String,
    pub cached: bool,
    pub elapsed_us: u64,
    pub state_version: StateVersion,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActionOutcome {
    pub action: String,
    pub backend: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub element: Option<ElementRef>,
    /// True only when the runtime *verified* the effect, never assumed.
    pub verified: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
    pub state_version: StateVersion,
    pub generation: Generation,
    pub elapsed_us: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum StepStatus {
    Ok,
    Failed,
    Skipped,
    Cancelled,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StepOutcome {
    pub index: usize,
    pub action: String,
    pub status: StepStatus,
    pub backend: String,
    pub verified: bool,
    pub elapsed_us: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub element: Option<ElementRef>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<InbriskError>,
    /// Value stored under `store` (element handle, window handle, text, ...).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub output: Option<Value>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RunOutcome {
    pub plan_id: PlanId,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub steps: Vec<StepOutcome>,
    pub succeeded: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub failed_step: Option<usize>,
    pub state_version: StateVersion,
    pub generation: Generation,
    pub elapsed_us: u64,
    pub mode: crate::action::RunMode,
    pub on_error: OnError,
    /// `$0`, `$name` -> value table for follow-up steps and the final answer.
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub outputs: BTreeMap<String, Value>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub notes: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WaitOutcome {
    pub satisfied: bool,
    pub waited_ms: u64,
    pub state_version: StateVersion,
    pub generation: Generation,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub detail: Option<String>,
    /// How the wake happened: `event`, `poll` or `timeout`.
    pub wake_source: String,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Counters {
    pub requests_total: u64,
    pub requests_failed: u64,
    pub plans_total: u64,
    pub steps_total: u64,
    pub actions_total: u64,
    pub uia_calls: u64,
    pub win32_calls: u64,
    pub cache_hits: u64,
    pub cache_misses: u64,
    pub stale_rejections: u64,
    pub denials: u64,
    #[serde(default)]
    pub native_requests_total: u64,
    #[serde(default)]
    pub mcp_requests_total: u64,
    #[serde(default)]
    pub shared_memory_requests_total: u64,
    #[serde(default)]
    pub native_run_plans_total: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct IpcStats {
    pub transport: String,
    pub region_name: String,
    pub request_capacity: u32,
    pub response_capacity: u32,
    pub event_capacity: u32,
    pub arena_bytes: u64,
    pub requests_seen: u64,
    pub responses_sent: u64,
    pub events_published: u64,
    pub events_dropped: u64,
    pub wake_signals: u64,
    pub rtt_p50_us: u64,
    pub rtt_p95_us: u64,
    pub rtt_p99_us: u64,
    pub rtt_max_us: u64,
    pub runtime_pid: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ClientInfo {
    pub session_id: SessionId,
    pub client_id: u64,
    pub name: String,
    pub version: String,
    pub pid: u32,
    pub connected_at_ms: u64,
    pub requests: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct StatusReport {
    pub identity: RuntimeIdentity,
    pub uptime_ms: u64,
    pub state_version: StateVersion,
    pub generation: Generation,
    pub activity: ActivitySnapshot,
    pub capabilities: Capabilities,
    pub sessions: Vec<ClientInfo>,
    #[serde(default)]
    pub counters: Counters,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ipc: Option<IpcStats>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub ownership: Vec<OwnershipStamp>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub windows: Vec<WindowRef>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub log_tail: Vec<String>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub backends: Vec<String>,
    #[serde(default)]
    pub emergency: bool,
    #[serde(default)]
    pub paused: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub desktop_probe: Option<DesktopProbeReport>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub desktop_broker: Option<crate::desktop_broker::DesktopBrokerReport>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub virtual_desktop: Option<VirtualDesktopReport>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VirtualDesktopReport {
    pub left: i32,
    pub top: i32,
    pub width: i32,
    pub height: i32,
    pub monitor_count: u32,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DesktopProbeReport {
    pub interactive_desktop: bool,
    pub physical_input: bool,
    pub window_enumeration: bool,
    pub session_id: u32,
    pub desktop: String,
    pub window_station: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Subscribed {
    pub subscription_id: u64,
    pub kinds: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Cancelled {
    pub cancelled_operations: u32,
    pub cancelled_plans: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "result", rename_all = "snake_case")]
pub enum ResponsePayload {
    Welcome(Welcome),
    Pong(Pong),
    Observation(Box<Observation>),
    Found(FindResult),
    Read(ReadResult),
    Acted(ActionOutcome),
    Ran(Box<RunOutcome>),
    Waited(WaitOutcome),
    Cancelled(Cancelled),
    Status(Box<StatusReport>),
    Subscribed(Subscribed),
    Ack,
    Terminal(Box<crate::terminal::TerminalReply>),
}

impl ResponsePayload {
    pub const fn kind(&self) -> &'static str {
        match self {
            ResponsePayload::Welcome(_) => "welcome",
            ResponsePayload::Pong(_) => "pong",
            ResponsePayload::Observation(_) => "observation",
            ResponsePayload::Found(_) => "found",
            ResponsePayload::Read(_) => "read",
            ResponsePayload::Acted(_) => "acted",
            ResponsePayload::Ran(_) => "ran",
            ResponsePayload::Waited(_) => "waited",
            ResponsePayload::Cancelled(_) => "cancelled",
            ResponsePayload::Status(_) => "status",
            ResponsePayload::Subscribed(_) => "subscribed",
            ResponsePayload::Ack => "ack",
            ResponsePayload::Terminal(_) => "terminal",
        }
    }
}

/// The single response envelope.
///
/// `ok == false` always carries `error`; when `ok == true` the mutation has
/// definitely been applied (or, for reads, the data is complete).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Reply {
    pub request_id: RequestId,
    pub op: Op,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<SessionId>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub operation_id: Option<OperationId>,
    pub ok: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result: Option<ResponsePayload>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<InbriskError>,
    pub elapsed_us: u64,
    pub state_version: StateVersion,
    pub generation: Generation,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<Hwnd>,
}

impl Reply {
    pub fn ok(
        request_id: RequestId,
        op: Op,
        payload: ResponsePayload,
        state_version: StateVersion,
        generation: Generation,
        elapsed_us: u64,
    ) -> Self {
        Self {
            request_id,
            op,
            session_id: None,
            operation_id: None,
            ok: true,
            result: Some(payload),
            error: None,
            elapsed_us,
            state_version,
            generation,
            hwnd: None,
        }
    }

    pub fn err(
        request_id: RequestId,
        op: Op,
        error: InbriskError,
        state_version: StateVersion,
        generation: Generation,
        elapsed_us: u64,
    ) -> Self {
        Self {
            request_id,
            op,
            session_id: None,
            operation_id: None,
            ok: false,
            result: None,
            error: Some(error),
            elapsed_us,
            state_version,
            generation,
            hwnd: None,
        }
    }

    pub fn with_session(mut self, session_id: SessionId) -> Self {
        self.session_id = Some(session_id);
        self
    }

    pub fn with_operation(mut self, operation_id: OperationId) -> Self {
        self.operation_id = Some(operation_id);
        self
    }

    pub fn with_hwnd(mut self, hwnd: Hwnd) -> Self {
        self.hwnd = Some(hwnd);
        self
    }
}
