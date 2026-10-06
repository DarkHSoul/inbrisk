//! `cancel`: stop whatever this client has in flight.
//!
//! The SDK exposes session-wide cancellation only, which is why `--all` is
//! accepted as a no-op rather than pretending to offer finer scope.

use crate::cli::CancelArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &CancelArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let cancelled = runtime.cancel_all().map_err(CliError::from)?;

    if args.json {
        crate::emit_json!(cancelled)?;
    } else {
        output::print_line(&format!(
            "cancelled: operations={} plans={} (session scope)",
            cancelled.cancelled_operations, cancelled.cancelled_plans
        ))?;
    }
    Ok(EXIT_OK)
}
