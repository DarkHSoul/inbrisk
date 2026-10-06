//! `act`: one step, one round trip.
//!
//! `--json` carries the step payload itself (that is the agent-facing contract),
//! so `--out-json` selects the machine-readable rendering of the outcome.

use crate::cli::ActArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &ActArgs) -> CliResult<i32> {
    // Normalize before connecting: a malformed step is a usage error, not a
    // reason to open a session.
    let step = crate::plan_input::normalize_step_json(&args.step_json)?;

    let runtime = super::connect(false)?;
    let outcome = runtime.act(step).map_err(CliError::from)?;

    if args.out_json {
        crate::emit_json!(outcome)?;
        return Ok(EXIT_OK);
    }

    output::print_line(&format!(
        "act {}  backend={}  verified={}  elapsed={}  state_version={}",
        outcome.action,
        super::backend_name(&outcome.backend),
        output::yes_no(outcome.verified),
        output::fmt_us(outcome.elapsed_us),
        outcome.state_version
    ))?;
    if let Some(element) = &outcome.element {
        output::print_line(&format!("  element: {}", super::format_element(element)))?;
    }
    if let Some(detail) = &outcome.detail {
        output::print_line(&format!("  detail: {detail}"))?;
    }
    Ok(EXIT_OK)
}
