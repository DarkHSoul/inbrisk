//! Inbrisk core domain model.
//!
//! This crate is the brain of the system and must stay free of Windows APIs.
//! Everything that knows about HWNDs, UIA, COM or DWM lives in the platform
//! crates (`inbrisk-win32`, `inbrisk-uia`, `inbrisk-input`, `inbrisk-cdp`).

pub mod activity;
pub mod budget;
pub mod capability;
pub mod element;
pub mod error;
pub mod geometry;
pub mod guard;
pub mod ids;
pub mod logging;
pub mod ownership;
pub mod window;
pub mod world;

pub use activity::{
    ActivityManager, ActivitySnapshot, ActivityState, ActivityToken, WindowActivityToken,
};
pub use budget::OperationBudget;
pub use capability::{ExecutionCapabilities, ExecutionCapability};
pub use element::ElementRef;
pub use error::{ErrorCode, InbriskError, Result};
pub use geometry::{CoordinateSpace, Point, Rect};
pub use guard::{Expect, GuardOutcome, StateGuard};
pub use ids::{
    ClientId, ElementId, Generation, Hwnd, OperationId, PlanId, RequestId, SessionId, StateVersion,
};
pub use ownership::{
    eligible_for_cleanup, mutable, LifecycleIntent, Ownership, OwnershipRegistry, OwnershipStamp,
};
pub use window::WindowRef;
pub use world::{now_ms, WorldState};

/// Protocol version spoken by the native IPC layer.
pub const PROTOCOL_VERSION: u16 = 1;

/// Build/runtime identity, surfaced through `STATUS` and the handshake.
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct RuntimeIdentity {
    pub name: String,
    pub version: String,
    pub protocol_version: u16,
    pub pid: u32,
    pub epoch: u64,
}
