//! `terminal`: persistent AI terminal sessions owned by the runtime.
//!
//! Each invocation is still one-shot: connect, one round trip, exit. The shell
//! (ConPTY + `pwsh`) lives in the runtime, keyed by `session_id`.

use std::io::Read;

use inbrisk_protocol::terminal::{
    TerminalInfo, TerminalOpen, TerminalOutput, TerminalRead, TerminalWrite,
};

use crate::cli::{
    TerminalArgs, TerminalExecArgs, TerminalOpenArgs, TerminalReadArgs, TerminalWriteArgs,
};
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &TerminalArgs) -> CliResult<i32> {
    match args {
        TerminalArgs::Open(a) => open(a),
        TerminalArgs::Write(a) => write(a),
        TerminalArgs::Exec(a) => exec(a),
        TerminalArgs::Read(a) => read(a),
        TerminalArgs::Status { session, json } => status(session.as_deref(), *json),
        TerminalArgs::Close { session, json } => close(session, *json),
    }
}

/// JSON flag of a terminal command, for error rendering in `main`.
pub fn wants_json(args: &TerminalArgs) -> bool {
    match args {
        TerminalArgs::Open(a) => a.json,
        TerminalArgs::Write(a) => a.json,
        TerminalArgs::Exec(a) => a.json,
        TerminalArgs::Read(a) => a.json,
        TerminalArgs::Status { json, .. } | TerminalArgs::Close { json, .. } => *json,
    }
}

/// Decodes the escapes AI callers typically pass on a command line:
/// `\r` `\n` `\e` `\xHH` `\\`. Everything else (`\t`, `\U`, `\Windows`, ...) is
/// left untouched so ordinary Windows paths survive. Use `--literal`,
/// `--stdin` or `exec` for text where `\n`/`\r` must stay literal
/// (`C:\new`, `C:\run`).
pub fn decode_escapes(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        if c != '\\' {
            out.push(c);
            continue;
        }
        match chars.peek().copied() {
            Some('r') => {
                chars.next();
                out.push('\r');
            }
            Some('n') => {
                chars.next();
                out.push('\n');
            }
            Some('e') => {
                chars.next();
                out.push('\x1b');
            }
            Some('\\') => {
                chars.next();
                out.push('\\');
            }
            Some('x') => {
                let mut look = chars.clone();
                look.next();
                let (h, l) = (look.next(), look.next());
                if let (Some(h), Some(l)) = (h, l) {
                    if let (Some(h), Some(l)) = (h.to_digit(16), l.to_digit(16)) {
                        chars.next();
                        chars.next();
                        chars.next();
                        out.push(char::from_u32(h * 16 + l).unwrap_or('\u{FFFD}'));
                        continue;
                    }
                }
                out.push('\\');
            }
            _ => out.push('\\'),
        }
    }
    out
}

fn open(a: &TerminalOpenArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let info = runtime
        .terminal_open(TerminalOpen {
            shell: a.shell.clone(),
            cwd: a.cwd.clone(),
            cols: a.cols,
            rows: a.rows,
            raw: a.raw,
            no_profile: a.no_profile,
            buffer_bytes: a.buffer_bytes,
        })
        .map_err(CliError::from)?;
    if a.json {
        crate::emit_json!(info)?;
    } else {
        output::print_line(&format!("session_id: {}", info.session_id))?;
        output::print_line(&describe(&info))?;
    }
    Ok(EXIT_OK)
}

fn write(a: &TerminalWriteArgs) -> CliResult<i32> {
    let text = if a.stdin {
        let mut s = String::new();
        std::io::stdin()
            .read_to_string(&mut s)
            .map_err(|e| CliError::usage(format!("cannot read stdin: {e}")))?;
        s
    } else {
        let raw = a.text.clone().unwrap_or_default();
        if a.literal {
            raw
        } else {
            decode_escapes(&raw)
        }
    };
    send(&a.session, text, !a.no_normalize, false, a.json)
}

fn exec(a: &TerminalExecArgs) -> CliResult<i32> {
    send(&a.session, a.command.clone(), true, true, a.json)
}

fn send(session: &str, text: String, normalize: bool, enter: bool, json: bool) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let written = runtime
        .terminal_write(TerminalWrite {
            session_id: session.to_string(),
            text,
            normalize_newlines: normalize,
            append_enter: enter,
        })
        .map_err(CliError::from)?;
    if json {
        crate::emit_json!(written)?;
    } else {
        output::print_line(&format!(
            "written: session={} bytes={} alive={} output_seq={}",
            written.session_id,
            written.bytes_written,
            output::yes_no(written.process_alive),
            written.next_seq
        ))?;
    }
    Ok(EXIT_OK)
}

fn read(a: &TerminalReadArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let out = runtime
        .terminal_read(TerminalRead {
            session_id: a.session.clone(),
            after: a.after,
            wait_ms: a.wait_ms,
            settle_ms: a.settle_ms,
            max_bytes: a.max_bytes,
            peek: a.peek,
        })
        .map_err(CliError::from)?;
    if a.json {
        crate::emit_json!(out)?;
    } else {
        print_output(&out)?;
    }
    Ok(EXIT_OK)
}

fn print_output(out: &TerminalOutput) -> CliResult<()> {
    if !out.text.is_empty() {
        let text = out.text.trim_end_matches('\n');
        output::print_line(text)?;
    }
    let exit = out
        .exit_code
        .map(|c| format!(" exit_code={c}"))
        .unwrap_or_default();
    output::print_line(&format!(
        "[terminal {} seq={}..{} more={} truncated={} alive={}{}]",
        out.session_id,
        out.from_seq,
        out.to_seq,
        output::yes_no(out.has_more),
        output::yes_no(out.truncated),
        output::yes_no(out.process_alive),
        exit
    ))
}

fn status(session: Option<&str>, json: bool) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let sessions = runtime
        .terminal_status(session.map(str::to_string))
        .map_err(CliError::from)?;
    if json {
        crate::emit_json!(sessions)?;
    } else if sessions.is_empty() {
        output::print_line("no terminal sessions")?;
    } else {
        for s in &sessions {
            output::print_line(&format!("{}  {}", s.session_id, describe(s)))?;
        }
    }
    Ok(EXIT_OK)
}

fn close(session: &str, json: bool) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let closed = runtime
        .terminal_close(session.to_string())
        .map_err(CliError::from)?;
    if json {
        crate::emit_json!(closed)?;
    } else {
        let code = closed
            .exit_code
            .map(|c| format!(" exit_code={c}"))
            .unwrap_or_default();
        output::print_line(&format!(
            "closed: session={} was_alive={}{}",
            closed.session_id,
            output::yes_no(closed.was_alive),
            code
        ))?;
    }
    Ok(EXIT_OK)
}

fn describe(i: &TerminalInfo) -> String {
    format!(
        "shell={} pid={} size={}x{} alive={} output_seq={}..{} buffered={}/{}B{}",
        i.shell,
        i.pid,
        i.cols,
        i.rows,
        output::yes_no(i.alive),
        i.base_seq,
        i.next_seq,
        i.buffered_bytes,
        i.buffer_capacity,
        i.exit_code
            .map(|c| format!(" exit_code={c}"))
            .unwrap_or_default()
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_enter_and_escape_sequences() {
        assert_eq!(decode_escapes(r"echo hi\r\n"), "echo hi\r\n");
        assert_eq!(decode_escapes(r"a\nb"), "a\nb");
        assert_eq!(decode_escapes(r"\e[A"), "\x1b[A");
        assert_eq!(decode_escapes(r"\x03"), "\x03");
        assert_eq!(decode_escapes(r"a\\b"), "a\\b");
    }

    #[test]
    fn leaves_ordinary_windows_paths_alone() {
        assert_eq!(
            decode_escapes(r"cd C:\Users\Ahmet\Documents\inbrisk\r\n"),
            "cd C:\\Users\\Ahmet\\Documents\\inbrisk\r\n"
        );
        assert_eq!(decode_escapes(r"C:\Windows\System32"), r"C:\Windows\System32");
        assert_eq!(decode_escapes(r"\xZZ \x1"), r"\xZZ \x1");
        assert_eq!(decode_escapes("trailing\\"), "trailing\\");
    }

    #[test]
    fn known_collision_is_documented_behaviour() {
        // `C:\new` decodes `\n`; callers must use --literal / --stdin / exec.
        assert_eq!(decode_escapes(r"C:\new"), "C:\new");
    }
}
