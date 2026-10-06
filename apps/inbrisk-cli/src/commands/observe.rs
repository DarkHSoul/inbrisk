//! `observe`: windows plus an optional UIA subtree.

use inbrisk_protocol::request::{ObserveRequest, ObserveScope};
use inbrisk_protocol::response::Observation;
use inbrisk_protocol::selector::Selector;

use crate::cli::ObserveArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &ObserveArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;

    let request = ObserveRequest {
        // Asking for one window is cheaper than the whole desktop, and asking
        // for the tree without a window only makes sense on the desktop scope.
        scope: if args.window.is_some() {
            ObserveScope::Window
        } else if args.tree {
            ObserveScope::Desktop
        } else {
            ObserveScope::Windows
        },
        window: args
            .window
            .as_ref()
            .map(|name| Selector::name(name.clone())),
        include_tree: args.tree,
        max_depth: args.depth,
        ..Default::default()
    };

    let observation = runtime.observe(request).map_err(CliError::from)?;
    if args.json {
        crate::emit_json!(observation)?;
        return Ok(EXIT_OK);
    }
    print_human(&observation)?;
    Ok(EXIT_OK)
}

fn print_human(observation: &Observation) -> CliResult<()> {
    output::print_line(&format!(
        "observation  backend={}  elapsed={}  state_version={}  generation={}  captured_at={}",
        super::backend_name(&observation.backend),
        output::fmt_us(observation.elapsed_us),
        observation.state_version,
        observation.generation,
        observation.captured_at_ms
    ))?;

    output::print_line(&format!("windows: {}", observation.windows.len()))?;
    super::windows::print_window_list(&observation.windows, "  ")?;

    match &observation.foreground {
        Some(window) => output::print_line(&format!(
            "foreground: {}  pid {}  \"{}\"",
            window.hwnd, window.process_id, window.title
        ))?,
        None => output::print_line("foreground: -")?,
    }

    match &observation.focused_element {
        Some(element) => {
            output::print_line(&format!("focused: {}", super::format_element(element)))?
        }
        None => output::print_line("focused: -")?,
    }

    if !observation.monitors.is_empty() {
        output::print_line(&format!("monitors: {}", observation.monitors.len()))?;
        for monitor in &observation.monitors {
            output::print_line(&format!(
                "  #{} {}{}  {}  dpi={}{}",
                monitor.id,
                if monitor.primary { "primary " } else { "" },
                output::fmt_rect(&monitor.bounds),
                output::fmt_rect(&monitor.work_area),
                monitor.dpi,
                if monitor.name.is_empty() {
                    String::new()
                } else {
                    format!("  name={}", monitor.name)
                }
            ))?;
        }
    }

    if !observation.elements.is_empty() {
        output::print_line(&format!("tree: {} element(s)", observation.elements.len()))?;
        for (index, element) in observation.elements.iter().enumerate() {
            output::print_line(&format!("  [{index}] {}", super::format_element(element)))?;
        }
    }

    if !observation.notes.is_empty() {
        output::print_line("notes:")?;
        for note in &observation.notes {
            output::print_line(&format!("  - {note}"))?;
        }
    }
    Ok(())
}
