use serde::{Deserialize, Serialize};

use inbrisk_core::{Expect, Hwnd, OperationId, PlanId, StateVersion};

use crate::action::{OnError, Plan, RunMode, Step, TargetRef, WaitSpec};
use crate::caps::Capabilities;
use crate::selector::Selector;

/// Client handshake. The runtime answers with `WELCOME` or refuses.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Hello {
    pub protocol_version: u16,
    pub client_name: String,
    #[serde(default)]
    pub client_version: String,
    pub client_pid: u32,
    /// Random per-process value: a client may not reconnect with a stale nonce.
    pub nonce: u64,
    #[serde(default)]
    pub capabilities: Capabilities,
    /// Whether the client intends to consume the event ring.
    #[serde(default)]
    pub wants_events: bool,
    /// If the client already knows the runtime epoch, it can assert it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expected_epoch: Option<u64>,
}

/// How much world to look at.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum ObserveScope {
    /// Top-level windows + foreground + activity. Cheapest useful answer.
    #[default]
    Windows,
    /// One window's UIA subtree.
    Window,
    /// Whole desktop with the interaction tree.
    Desktop,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct ObserveRequest {
    #[serde(default)]
    pub scope: ObserveScope,
    /// Pick the target window by selector.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub window: Option<Selector>,
    /// Pick the target window by handle.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hwnd: Option<Hwnd>,
    /// Include the UIA subtree (implies a `uia` backend call).
    #[serde(default)]
    pub include_tree: bool,
    /// Include a screenshot (needs the capture backend).
    #[serde(default)]
    pub include_screenshot: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_depth: Option<u32>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_elements: Option<usize>,
    /// Include only windows whose title matches this substring.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub title_contains: Option<String>,
    /// Include only windows owned by these processes.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub processes: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct FindRequest {
    pub selector: Selector,
    /// Return every match instead of failing on ambiguity.
    #[serde(default)]
    pub all: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub limit: Option<usize>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
    /// Store the result under this name for later `$name` references.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub store: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ReadRequest {
    /// Handle returned by a previous `find`/`act`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub element_id: Option<u64>,
    /// Generation the handle was minted in; a mismatch is `StaleState`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub generation: Option<u64>,
    /// Or resolve fresh from a selector / `$ref`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub target: Option<TargetRef>,
    /// Properties to read; empty = the default useful set.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub properties: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
}

/// One action. Reuses the same `Step` type as plans so there is exactly one
/// action engine in the system.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActRequest {
    pub action: Step,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expect: Option<Expect>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RunRequest {
    #[serde(flatten)]
    pub plan: Plan,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub expect: Option<Expect>,
    #[serde(default)]
    pub mode: RunMode,
    #[serde(default)]
    pub on_error: OnError,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
    /// Reject plans longer than this many steps (self-protection).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_steps: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct WaitRequest {
    #[serde(flatten)]
    pub wait: WaitSpec,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub timeout_ms: Option<u64>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct CancelRequest {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub operation_id: Option<OperationId>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub plan_id: Option<PlanId>,
    /// Cancel everything this session is running.
    #[serde(default)]
    pub all: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct StatusRequest {
    #[serde(default)]
    pub include_windows: bool,
    #[serde(default)]
    pub include_ownership: bool,
    #[serde(default)]
    pub include_logs: bool,
    #[serde(default = "default_log_lines")]
    pub log_lines: u32,
    #[serde(default)]
    pub include_ipc_stats: bool,
}

fn default_log_lines() -> u32 {
    50
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct SubscribeRequest {
    /// Empty = every event kind.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub kinds: Vec<String>,
    /// Coalescing window for high-frequency events such as activity ticks.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min_interval_ms: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Ping {
    pub seq: u64,
    /// Client clock at send time (nanoseconds, unix epoch).
    #[serde(default)]
    pub client_send_ns: u64,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Shutdown {
    #[serde(default)]
    pub force: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

/// The native protocol request.
///
/// Wire shape (matching the documented examples):
/// `{ "op": "run", "steps": [ { "action": "launch", "app": "notepad" } ] }`
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "snake_case")]
pub enum Request {
    Hello(Hello),
    Observe(ObserveRequest),
    Find(FindRequest),
    Read(ReadRequest),
    Act(ActRequest),
    Run(RunRequest),
    Wait(WaitRequest),
    Cancel(CancelRequest),
    Status(StatusRequest),
    Subscribe(SubscribeRequest),
    Ping(Ping),
    Shutdown(Shutdown),
    Terminal(crate::terminal::TerminalRequest),
}

impl Request {
    pub const fn op(&self) -> crate::opcode::Op {
        use crate::opcode::Op;
        match self {
            Request::Hello(_) => Op::Hello,
            Request::Observe(_) => Op::Observe,
            Request::Find(_) => Op::Find,
            Request::Read(_) => Op::Read,
            Request::Act(_) => Op::Act,
            Request::Run(_) => Op::Run,
            Request::Wait(_) => Op::Wait,
            Request::Cancel(_) => Op::Cancel,
            Request::Status(_) => Op::Status,
            Request::Subscribe(_) => Op::Subscribe,
            Request::Ping(_) => Op::Ping,
            Request::Shutdown(_) => Op::Shutdown,
            Request::Terminal(_) => Op::Terminal,
        }
    }

    /// Whether the operation can change the world (thus: needs guards).
    pub const fn is_mutating(&self) -> bool {
        match self {
            Request::Act(_) | Request::Run(_) | Request::Shutdown(_) => true,
            Request::Terminal(t) => t.is_mutating(),
            _ => false,
        }
    }

    /// Optional per-request timeout.
    pub fn timeout_ms(&self) -> Option<u64> {
        match self {
            Request::Observe(r) => r.timeout_ms,
            Request::Find(r) => r.timeout_ms,
            Request::Read(r) => r.timeout_ms,
            Request::Act(r) => r.timeout_ms,
            Request::Run(r) => r.timeout_ms,
            Request::Wait(r) => r.timeout_ms,
            _ => None,
        }
    }

    /// Optional state precondition.
    pub fn expect(&self) -> Option<Expect> {
        match self {
            Request::Act(r) => r.expect,
            Request::Run(r) => r.expect,
            _ => None,
        }
    }

    pub fn state_version_hint(&self) -> Option<StateVersion> {
        self.expect().and_then(|e| e.state_version)
    }
}
