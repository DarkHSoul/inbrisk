//! `endpoint`: where the runtime lives.

use crate::cli::EndpointArgs;
use crate::error::{CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &EndpointArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;

    if args.json {
        crate::emit_json!(serde_json::json!({
            "endpoint": runtime.endpoint(),
            "region_name": runtime.region_name(),
            "session_id": runtime.session_id(),
            "runtime_pid": runtime.client().runtime_pid(),
            "runtime_epoch": runtime.client().runtime_epoch(),
        }))?;
    } else {
        output::print_line(&runtime.endpoint())?;
    }
    Ok(EXIT_OK)
}
