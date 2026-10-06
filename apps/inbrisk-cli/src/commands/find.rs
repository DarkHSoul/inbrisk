//! `find`: resolve a selector to element handles.

use inbrisk_protocol::response::FindResult;
use inbrisk_protocol::selector::Selector;

use crate::cli::FindArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &FindArgs) -> CliResult<i32> {
    let selector = build_selector(args)?;
    let runtime = super::connect(false)?;
    let found = runtime
        .find_all(selector, args.all, args.limit)
        .map_err(CliError::from)?;

    if args.json {
        crate::emit_json!(found)?;
    } else {
        print_human(&found)?;
    }
    Ok(EXIT_OK)
}

/// A selector can be given as flags, as `SELECTOR_TEXT`, or as selector JSON.
fn build_selector(args: &FindArgs) -> CliResult<Selector> {
    let json_text = args
        .selector_text
        .as_deref()
        .filter(|text| text.trim_start().starts_with('{'));

    let mut selector = match json_text {
        Some(text) => serde_json::from_str::<Selector>(text).map_err(|error| {
            CliError::usage(format!("SELECTOR_TEXT is not valid selector JSON: {error}"))
        })?,
        None => Selector::default(),
    };

    if let Some(role) = &args.role {
        selector.role = Some(role.clone());
    }
    if let Some(name) = &args.name {
        selector.name = Some(name.clone());
    }
    if let Some(text) = &args.selector_text {
        if json_text.is_none() {
            // Plain text is the accessible name, unless `--name` claimed that
            // field: then it must be a value/text match instead.
            if selector.name.is_none() {
                selector.name = Some(text.clone());
            } else {
                selector.text = Some(text.clone());
            }
        }
    }
    if let Some(window) = &args.window {
        selector = selector.in_window(Selector::name(window.clone()));
    }

    if selector.is_empty() {
        return Err(CliError::usage(
            "find needs a selector: pass --role, --name, --selector or SELECTOR_TEXT",
        ));
    }
    Ok(selector)
}

fn print_human(found: &FindResult) -> CliResult<()> {
    output::print_line(&format!("selector: {}", found.selector))?;
    output::print_line(&format!(
        "  matches={}  scanned={}  truncated={}  backend={}  elapsed={}",
        found.matches.len(),
        found.scanned,
        output::yes_no(found.truncated),
        super::backend_name(&found.backend),
        output::fmt_us(found.elapsed_us)
    ))?;

    for (index, element) in found.matches.iter().enumerate() {
        output::print_line(&format!("  [{index}] {}", super::format_element(element)))?;
    }
    if found.matches.is_empty() {
        output::print_line("  (no match)")?;
    }

    if !found.candidates.is_empty() {
        output::print_line(&format!("candidates: {}", found.candidates.len()))?;
        for candidate in &found.candidates {
            output::print_line(&format!("  - {candidate}"))?;
        }
    }
    Ok(())
}
