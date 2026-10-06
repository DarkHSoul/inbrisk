use serde::{Deserialize, Serialize};

/// Session identifier handed out by the runtime during `HELLO`/`WELCOME`.
pub type SessionId = u32;
/// Monotonic per-connection request identifier.
pub type RequestId = u64;
/// Identifier of a single in-flight operation (plan run, observe, ...).
pub type OperationId = u64;
/// Identifier of a submitted plan.
pub type PlanId = u64;
/// Identifier of a cached element handle.
pub type ElementId = u64;
/// World-state generation.
pub type StateVersion = u64;
/// Element-cache generation used to reject stale handles.
pub type Generation = u64;

/// Opaque owner identity for a connected client (agent host, CLI, MCP proxy).
pub type ClientId = u64;

/// A raw window handle, kept as a plain integer so the core stays FFI-free.
#[derive(
    Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize, Default,
)]
#[serde(transparent)]
pub struct Hwnd(pub isize);

impl Hwnd {
    pub const NULL: Hwnd = Hwnd(0);

    #[inline]
    pub fn is_null(self) -> bool {
        self.0 == 0
    }

    #[inline]
    pub fn raw(self) -> isize {
        self.0
    }
}

impl std::fmt::Display for Hwnd {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "0x{:X}", self.0)
    }
}

impl From<isize> for Hwnd {
    fn from(v: isize) -> Self {
        Hwnd(v)
    }
}
