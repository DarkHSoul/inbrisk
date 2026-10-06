//! CLI error type and the exit-code contract.
//!
//! The exit codes are part of the agent-facing contract, so they are derived in
//! exactly one place. Everything else in the crate returns `CliResult` and lets
//! `main` translate the failure.

use inbrisk_core::{ErrorCode, InbriskError};

/// The command succeeded.
pub const EXIT_OK: i32 = 0;
/// The runtime answered with a failure, or the operation failed.
pub const EXIT_ERROR: i32 = 1;
/// The arguments (or the plan JSON) were not usable.
pub const EXIT_USAGE: i32 = 2;
/// No runtime is listening on the shared region.
pub const EXIT_UNAVAILABLE: i32 = 3;
/// The runtime did not answer in time.
pub const EXIT_TIMEOUT: i32 = 4;

/// Everything a command can fail with.
#[derive(Debug)]
pub enum CliError {
    /// The user asked for something the CLI cannot do.
    Usage(String),
    /// The runtime refused, or could not be reached.
    Runtime(InbriskError),
}

impl CliError {
    pub fn usage(message: impl Into<String>) -> Self {
        CliError::Usage(message.into())
    }

    /// Process exit code for this failure.
    pub fn exit_code(&self) -> i32 {
        match self {
            CliError::Usage(_) => EXIT_USAGE,
            CliError::Runtime(error) => match error.code {
                ErrorCode::Timeout => EXIT_TIMEOUT,
                ErrorCode::RuntimeUnavailable => EXIT_UNAVAILABLE,
                _ => EXIT_ERROR,
            },
        }
    }
}

impl From<InbriskError> for CliError {
    fn from(error: InbriskError) -> Self {
        CliError::Runtime(error)
    }
}

impl std::fmt::Display for CliError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CliError::Usage(message) => write!(f, "{message}"),
            CliError::Runtime(error) => write!(f, "{error}"),
        }
    }
}

pub type CliResult<T> = std::result::Result<T, CliError>;
