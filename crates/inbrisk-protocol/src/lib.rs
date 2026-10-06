//! Inbrisk native protocol.
//!
//! This is the *only* contract shared by the runtime, the CLI, the SDK and the
//! MCP compatibility adapter. MCP's JSON schema is deliberately **not** the
//! core protocol: MCP tools compile down to these operations.
//!
//! Wire rules
//! ----------
//! * Encoding at the edge: JSON (serde), compact and debuggable.
//! * Tool/CLI shape: `{ "op": "run", "steps": [...] }`.
//! * The runtime deserializes once into native structs — JSON never travels
//!   between runtime subsystems.
//! * Mutating requests may carry `expect: { stateVersion }`; violations are
//!   rejected with `StaleState` and **zero** side effects.

pub mod action;
pub mod caps;
pub mod desktop_broker;
pub mod event;
pub mod opcode;
pub mod request;
pub mod response;
pub mod selector;
pub mod terminal;

pub use action::{OnError, Plan, RunMode, Step, TargetRef, WaitSpec, WaitUntil};
pub use caps::Capabilities;
pub use desktop_broker::{
    BrokerErrorPayload, BrokerRequestEnvelope, BrokerResponseEnvelope, BrokerWindowInfo,
    DesktopBrokerHello, DesktopBrokerReport, DesktopBrokerRequest, DesktopBrokerResponse,
    DesktopBrokerWelcome, PhysicalInputCommand, PhysicalInputGuard, PhysicalInputRequest,
    PhysicalInputResult, DESKTOP_BROKER_PROTOCOL_VERSION,
};
pub use event::{Event, EventEnvelope};
pub use opcode::Op;
pub use request::{
    ActRequest, CancelRequest, Hello, ObserveRequest, Ping, ReadRequest, Request, RunRequest,
    StatusRequest, SubscribeRequest, WaitRequest,
};
pub use response::{
    ActionOutcome, FindResult, Observation, Pong, ReadResult, Reply, ResponsePayload, RunOutcome,
    StatusReport, StepOutcome, StepStatus, Subscribed, WaitOutcome, Welcome,
};
pub use selector::{MatchPolicy, Selector};

/// Version of the protocol implemented by this crate.
pub const PROTOCOL_VERSION: u16 = inbrisk_core::PROTOCOL_VERSION;

/// Magic value written into the shared-memory header.
pub const IPC_MAGIC: u64 = 0x494E_4252_4953_4B31; // "INBRISK1"

/// Codec id for the payload arena.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u16)]
pub enum Codec {
    /// UTF-8 JSON (default, debuggable).
    Json = 1,
}

impl Codec {
    pub const fn id(self) -> u16 {
        self as u16
    }
}
