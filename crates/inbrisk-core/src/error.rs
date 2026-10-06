use serde::{Deserialize, Serialize};
use thiserror::Error;

/// Stable, machine-readable failure taxonomy.
///
/// Agents branch on these codes, so they are part of the public contract and
/// must not be renamed without a protocol version bump.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorCode {
    /// The submitted `expect.stateVersion` no longer matches the world.
    StaleState,
    /// No element matched the selector.
    TargetNotFound,
    /// More than one element matched and the selector was ambiguous.
    CloseMatchesFound,
    /// The element exists but is outside every visible viewport.
    TargetOffscreen,
    /// The element is disabled / read-only for the requested action.
    TargetDisabled,
    /// The element is disabled (terminal safety error).
    ElementDisabled,
    /// The required UIA pattern is not offered by the provider.
    PatternUnavailable,
    /// A search or plan ran out of its step/time budget.
    BudgetExhausted,
    /// A wait timed out.
    Timeout,
    /// The operation was cancelled by the client or by emergency stop.
    Cancelled,
    /// Policy refused the operation (protected or user-owned target).
    Denied,
    /// The target window is protected and may not be mutated.
    Protected,
    /// Emergency stop is engaged; all mutations are blocked.
    Emergency,
    /// The runtime is paused.
    Paused,
    /// Physical input is currently leased by somebody else.
    InputLeaseTimeout,
    /// No runtime is listening on the IPC channel.
    RuntimeUnavailable,
    /// Protocol or handshake mismatch (stale client, wrong epoch).
    ProtocolMismatch,
    /// The session was closed or expired.
    SessionExpired,
    /// The plan payload was structurally invalid.
    InvalidPlan,
    /// The runtime lacks the required execution capability (e.g. interactive desktop).
    CapabilityUnavailable,
    /// The target window is no longer in foreground or focus changed unexpectedly prior to mutation.
    ForegroundChanged,
    /// Connection to execution backend was lost mid-operation; outcome cannot be verified. Never auto-retry.
    ExecutionOutcomeUnknown,
    /// The backend exists but the feature is not implemented yet.
    NotImplemented,
    /// The target window is no longer valid or has been destroyed.
    TargetGone,
    /// Anything unexpected.
    Internal,
}

impl ErrorCode {
    pub const fn as_str(self) -> &'static str {
        match self {
            ErrorCode::StaleState => "StaleState",
            ErrorCode::TargetNotFound => "TargetNotFound",
            ErrorCode::CloseMatchesFound => "CloseMatchesFound",
            ErrorCode::TargetOffscreen => "TargetOffscreen",
            ErrorCode::TargetDisabled => "TargetDisabled",
            ErrorCode::ElementDisabled => "ElementDisabled",
            ErrorCode::PatternUnavailable => "PatternUnavailable",
            ErrorCode::BudgetExhausted => "BudgetExhausted",
            ErrorCode::Timeout => "Timeout",
            ErrorCode::Cancelled => "Cancelled",
            ErrorCode::Denied => "Denied",
            ErrorCode::Protected => "Protected",
            ErrorCode::Emergency => "Emergency",
            ErrorCode::Paused => "Paused",
            ErrorCode::InputLeaseTimeout => "InputLeaseTimeout",
            ErrorCode::RuntimeUnavailable => "RuntimeUnavailable",
            ErrorCode::ProtocolMismatch => "ProtocolMismatch",
            ErrorCode::SessionExpired => "SessionExpired",
            ErrorCode::InvalidPlan => "InvalidPlan",
            ErrorCode::CapabilityUnavailable => "CapabilityUnavailable",
            ErrorCode::ForegroundChanged => "ForegroundChanged",
            ErrorCode::ExecutionOutcomeUnknown => "ExecutionOutcomeUnknown",
            ErrorCode::NotImplemented => "NotImplemented",
            ErrorCode::TargetGone => "TargetGone",
            ErrorCode::Internal => "Internal",
        }
    }

    /// Whether the client may usefully retry the exact same request.
    pub const fn retryable(self) -> bool {
        matches!(
            self,
            ErrorCode::StaleState
                | ErrorCode::Timeout
                | ErrorCode::InputLeaseTimeout
                | ErrorCode::RuntimeUnavailable
                | ErrorCode::BudgetExhausted
                | ErrorCode::TargetOffscreen
        )
    }
}

/// Error payload shared by every backend and by the protocol edge.
#[derive(Debug, Clone, Serialize, Deserialize, Error)]
#[error("{}: {}", .code.as_str(), .message)]
pub struct InbriskError {
    pub code: ErrorCode,
    pub message: String,
    /// Concrete, actionable next step for the agent.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hint: Option<String>,
    #[serde(default = "default_true")]
    pub retryable: bool,
}

fn default_true() -> bool {
    true
}

impl InbriskError {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        Self {
            retryable: code.retryable(),
            code,
            message: message.into(),
            hint: None,
        }
    }

    pub fn with_hint(mut self, hint: impl Into<String>) -> Self {
        self.hint = Some(hint.into());
        self
    }

    pub fn not_found(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::TargetNotFound, message)
    }

    pub fn internal(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::Internal, message)
    }

    pub fn not_implemented(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::NotImplemented, message)
    }

    pub fn target_gone(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::TargetGone, message)
    }

    pub fn denied(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::Denied, message)
    }

    pub fn invalid_plan(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::InvalidPlan, message)
    }

    pub fn element_disabled(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::ElementDisabled, message)
    }

    pub fn capability_unavailable(
        capability: impl std::fmt::Display,
        detail: impl Into<String>,
    ) -> Self {
        Self::new(
            ErrorCode::CapabilityUnavailable,
            format!("{capability} capability is unavailable: {}", detail.into()),
        )
    }

    pub fn foreground_changed(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::ForegroundChanged, message)
    }

    pub fn execution_outcome_unknown(message: impl Into<String>) -> Self {
        Self::new(ErrorCode::ExecutionOutcomeUnknown, message)
    }
}

impl From<std::io::Error> for InbriskError {
    fn from(e: std::io::Error) -> Self {
        InbriskError::new(ErrorCode::Internal, format!("io error: {e}"))
    }
}

pub type Result<T> = std::result::Result<T, InbriskError>;
