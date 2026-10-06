//! `windows`: the cheap Win32-level window list.

use inbrisk_core::WindowRef;

use crate::cli::WindowsArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &WindowsArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let windows = runtime.windows().map_err(CliError::from)?;

    let filtered: Vec<WindowRef> = match &args.filter {
        Some(needle) => windows
            .into_iter()
            .filter(|window| matches_filter(window, needle))
            .collect(),
        None => windows,
    };

    if args.json {
        crate::emit_json!(filtered)?;
    } else if filtered.is_empty() {
        output::print_line(&match &args.filter {
            Some(needle) => format!("no windows match '{needle}'"),
            None => "no top-level windows".to_string(),
        })?;
    } else {
        print_window_list(&filtered, "  ")?;
    }
    Ok(EXIT_OK)
}

/// Case-insensitive substring match over title, process and class name.
fn matches_filter(window: &WindowRef, needle: &str) -> bool {
    let needle = needle.to_lowercase();
    window.title.to_lowercase().contains(&needle)
        || window.process_name.to_lowercase().contains(&needle)
        || window.class_name.to_lowercase().contains(&needle)
}

/// Shared by `windows` and `status --windows`.
pub(super) fn print_window_list(windows: &[WindowRef], indent: &str) -> CliResult<()> {
    for window in windows {
        output::print_line(&format!("{indent}{}", format_window(window)))?;
    }
    Ok(())
}

fn format_window(window: &WindowRef) -> String {
    format!(
        "{}  pid {:<6}  {:<22}  \"{}\"  class={}  {}{}{}{}  {}/{}",
        window.hwnd,
        window.process_id,
        short(&window.process_name, 22),
        window.title,
        window.class_name,
        output::fmt_rect(&window.bounds),
        if window.visible {
            "  visible"
        } else {
            "  hidden"
        },
        if window.minimized { "  minimized" } else { "" },
        if window.foreground {
            "  foreground"
        } else {
            ""
        },
        window.ownership.as_str(),
        window.intent.as_str()
    )
}

/// Process names are user data: clip them so the columns stay aligned.
fn short(text: &str, max: usize) -> String {
    if text.is_empty() {
        return "-".to_string();
    }
    if text.chars().count() <= max {
        return text.to_string();
    }
    let mut clipped: String = text.chars().take(max.saturating_sub(1)).collect();
    clipped.push('~');
    clipped
}

#[cfg(test)]
mod tests {
    use super::*;
    use inbrisk_core::{Hwnd, LifecycleIntent, Ownership, Rect};

    fn window(title: &str, process: &str, class: &str) -> WindowRef {
        WindowRef {
            hwnd: Hwnd(0x1F),
            process_id: 4242,
            process_name: process.into(),
            title: title.into(),
            class_name: class.into(),
            bounds: Rect::new(0, 0, 800, 600),
            visible: true,
            minimized: false,
            maximized: false,
            foreground: true,
            tool_window: false,
            ownership: Ownership::Agent,
            intent: LifecycleIntent::Ephemeral,
            state_version: 7,
        }
    }

    #[test]
    fn filter_matches_title_process_and_class() {
        let notepad = window("Untitled - Notepad", "notepad.exe", "Notepad");
        assert!(matches_filter(&notepad, "notepad"));
        assert!(matches_filter(&notepad, "NOTEPAD.EXE"));
        assert!(matches_filter(&notepad, "untitled"));
        assert!(!matches_filter(&notepad, "calculator"));
    }

    #[test]
    fn window_line_is_single_line_and_informative() {
        let line = format_window(&window("Untitled - Notepad", "notepad.exe", "Notepad"));
        assert!(!line.contains('\n'));
        assert!(line.contains("0x1F"));
        assert!(line.contains("notepad.exe"));
        assert!(line.contains("\"Untitled - Notepad\""));
        assert!(line.contains("AgentOwned/Ephemeral"));
    }

    #[test]
    fn long_process_names_are_clipped_ascii_safely() {
        assert_eq!(short("", 10), "-");
        assert_eq!(short("notepad.exe", 22), "notepad.exe");
        assert_eq!(short("averyveryverylongprocessname.exe", 10), "averyvery~");
    }
}
