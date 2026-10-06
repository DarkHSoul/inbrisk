//! `read`: property values for one element handle.

use crate::cli::ReadArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &ReadArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let result = runtime.read(args.element_id).map_err(CliError::from)?;

    if args.json {
        crate::emit_json!(result)?;
        return Ok(EXIT_OK);
    }

    output::print_line(&format!("read  {}", super::format_element(&result.element)))?;
    output::print_line(&format!(
        "  backend={}  cached={}  elapsed={}  state_version={}",
        super::backend_name(&result.backend),
        output::yes_no(result.cached),
        output::fmt_us(result.elapsed_us),
        result.state_version
    ))?;

    if result.properties.is_empty() {
        output::print_line("  properties: none")?;
    } else {
        output::print_line(&format!("  properties: {}", result.properties.len()))?;
        for (key, value) in &result.properties {
            output::print_line(&format!("    {:<20} = {}", key, super::compact_json(value)))?;
        }
    }
    Ok(EXIT_OK)
}
