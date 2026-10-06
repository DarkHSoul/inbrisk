//! Stdout/stderr plumbing: JSON encoding, human formatting and error shapes.
//!
//! Every write goes through this module so a closed pipe becomes an error
//! instead of a panic (`println!` panics once the reader of
//! `inbrisk-cli status --json | jq` goes away).

use std::io::Write;

use inbrisk_core::{ErrorCode, InbriskError};

use crate::error::{CliError, CliResult};
use crate::region;

/// Where the human form of a failure is written.
///
/// Diagnostic commands (`status`, `doctor`, `endpoint`) report on stdout: the
/// "runtime is not running" answer *is* their output. Everything else keeps
/// stdout clean and uses stderr.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FailStream {
    Stdout,
    Stderr,
}

/// Prints one line to stdout and flushes it, so streaming commands (`events`)
/// stay live when their output is a pipe.
pub fn print_line(text: &str) -> CliResult<()> {
    let mut out = std::io::stdout().lock();
    writeln!(out, "{text}").map_err(write_error)?;
    out.flush().map_err(write_error)
}

/// Prints one line to stderr.
pub fn eprint_line(text: &str) -> CliResult<()> {
    let mut err = std::io::stderr().lock();
    writeln!(err, "{text}").map_err(write_error)
}

/// Prints a JSON document (pretty, one value per invocation).
pub fn print_json(value: &serde_json::Value) -> CliResult<()> {
    let text = serde_json::to_string_pretty(value).map_err(encode_error)?;
    print_line(&text)
}

/// Prints one compact JSON value per line (JSONL).
pub fn print_json_line(value: &serde_json::Value) -> CliResult<()> {
    let text = serde_json::to_string(value).map_err(encode_error)?;
    print_line(&text)
}

/// Renders a failure and returns the exit code the process should use.
pub fn report(error: &CliError, json: bool, stream: FailStream) -> i32 {
    let code = error.exit_code();
    match error {
        // A usage error is never machine output: the user mistyped something.
        CliError::Usage(message) => {
            let text = format!("error: {message}\nhint: run 'inbrisk-cli help' for usage");
            let _ = eprint_line(&text);
        }
        CliError::Runtime(_) if json => {
            let _ = print_json(&error_json(error));
        }
        CliError::Runtime(_) => {
            let text = human_error(error);
            let _ = match stream {
                FailStream::Stdout => print_line(&text),
                FailStream::Stderr => eprint_line(&text),
            };
        }
    }
    code
}

fn human_error(error: &CliError) -> String {
    match error {
        CliError::Usage(message) => format!("error: {message}"),
        CliError::Runtime(err) if err.code == ErrorCode::RuntimeUnavailable => {
            unavailable_text(err, region::is_published(), &region::expected_region_name())
        }
        CliError::Runtime(err) => generic_text(err),
    }
}

fn generic_text(err: &InbriskError) -> String {
    let mut text = format!("error: {err}");
    if let Some(hint) = &err.hint {
        text.push_str(&format!("\nhint: {hint}"));
    }
    text
}

/// `RuntimeUnavailable` has two very different causes, and pretending they are
/// the same would send the user down the wrong path:
///
/// * nothing is published for this user — the runtime really was never started;
/// * a region exists but refused the handshake (session slots exhausted, or a
///   protocol mismatch) — restarting the runtime is not necessarily the fix.
///
/// Both forms keep the standard remedy sentence (agents match on it) and add the
/// facts that tell the two apart.
fn unavailable_text(err: &InbriskError, published: bool, region_name: &str) -> String {
    let mut text = region::runtime_unavailable_message();
    text.push_str(&format!("\nexpected region: {region_name}"));
    if published {
        text.push_str(
            "\ndetail: a shared region IS published for this user, so this is a handshake \
             failure rather than a missing runtime",
        );
        text.push_str(&format!("\nreason: {err}"));
    } else {
        text.push_str(&format!("\ndetail: {err}"));
    }
    if let Some(hint) = &err.hint {
        text.push_str(&format!("\nhint: {hint}"));
    }
    text
}

fn error_json(error: &CliError) -> serde_json::Value {
    let mut body = serde_json::Map::new();
    match error {
        CliError::Usage(message) => {
            body.insert("code".into(), "UsageError".into());
            body.insert("message".into(), message.clone().into());
            body.insert("retryable".into(), false.into());
        }
        CliError::Runtime(err) => {
            body.insert("code".into(), err.code.as_str().into());
            body.insert("message".into(), err.message.clone().into());
            if let Some(hint) = &err.hint {
                body.insert("hint".into(), hint.clone().into());
            }
            body.insert("retryable".into(), err.retryable.into());
            if err.code == ErrorCode::RuntimeUnavailable {
                body.insert(
                    "expected_region".into(),
                    region::expected_region_name().into(),
                );
            }
        }
    }

    let mut root = serde_json::Map::new();
    root.insert("ok".into(), false.into());
    root.insert("error".into(), serde_json::Value::Object(body));
    serde_json::Value::Object(root)
}

/// Encodes anything `serde_json` can encode. Used by the `emit_json!` macros,
/// because this crate cannot name the `Serialize` bound itself (no `serde`
/// dependency).
pub fn encode_error(error: serde_json::Error) -> CliError {
    CliError::Runtime(InbriskError::new(
        ErrorCode::Internal,
        format!("cannot encode JSON output: {error}"),
    ))
}

/// Prints a value as pretty JSON.
///
/// A macro, not a function: this crate deliberately depends on `serde_json`
/// only, so it can *call* the encoder for protocol types but cannot write the
/// `Serialize` bound in its own signature.
#[macro_export]
macro_rules! emit_json {
    ($value:expr) => {
        $crate::output::print_json(
            &::serde_json::to_value(&$value).map_err($crate::output::encode_error)?,
        )
    };
}

/// Same as `emit_json!`, but compact and one line (JSONL).
#[macro_export]
macro_rules! emit_json_line {
    ($value:expr) => {
        $crate::output::print_json_line(
            &::serde_json::to_value(&$value).map_err($crate::output::encode_error)?,
        )
    };
}

fn write_error(error: std::io::Error) -> CliError {
    CliError::Runtime(InbriskError::new(
        ErrorCode::Internal,
        format!("cannot write output: {error}"),
    ))
}

/// Microseconds as a short duration: `450us`, `1.2ms`, `2.35s`.
pub fn fmt_us(micros: u64) -> String {
    if micros < 1_000 {
        format!("{micros}us")
    } else if micros < 1_000_000 {
        format!("{:.1}ms", micros as f64 / 1_000.0)
    } else {
        format!("{:.2}s", micros as f64 / 1_000_000.0)
    }
}

/// Milliseconds as `12.3s`, `1m 23.4s` or `2h 5m`.
pub fn fmt_uptime(ms: u64) -> String {
    let seconds = ms as f64 / 1_000.0;
    if seconds < 60.0 {
        format!("{seconds:.1}s")
    } else if seconds < 3_600.0 {
        format!("{}m {:.1}s", (seconds / 60.0) as u64, seconds % 60.0)
    } else {
        format!(
            "{}h {}m",
            (seconds / 3_600.0) as u64,
            ((seconds % 3_600.0) / 60.0) as u64
        )
    }
}

pub fn yes_no(value: bool) -> &'static str {
    if value {
        "yes"
    } else {
        "no"
    }
}

/// Short rect form used by the window and element listings.
pub fn fmt_rect(rect: &inbrisk_core::Rect) -> String {
    format!("{},{} {}x{}", rect.x, rect.y, rect.width, rect.height)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn durations_pick_a_readable_unit() {
        assert_eq!(fmt_us(450), "450us");
        assert_eq!(fmt_us(1_200), "1.2ms");
        assert_eq!(fmt_us(2_350_000), "2.35s");
    }

    #[test]
    fn uptime_formats_minutes_and_hours() {
        assert_eq!(fmt_uptime(1_500), "1.5s");
        assert_eq!(fmt_uptime(83_400), "1m 23.4s");
        assert_eq!(fmt_uptime(7_500_000), "2h 5m");
    }

    #[test]
    fn unavailable_without_a_region_reports_the_missing_runtime() {
        let error = InbriskError::new(ErrorCode::RuntimeUnavailable, "region is not present");
        let text = unavailable_text(&error, false, "Local\\Inbrisk.Runtime.S-1-5-21-1");
        assert!(text.starts_with(
            "inbrisk runtime is not running (no shared region for this user). Start inbrisk.exe, then retry."
        ));
        assert!(text.contains("expected region: Local\\Inbrisk.Runtime.S-1-5-21-1"));
        assert!(text.contains("detail: RuntimeUnavailable: region is not present"));
    }

    #[test]
    fn unavailable_with_a_published_region_calls_out_the_handshake() {
        let error = InbriskError::new(
            ErrorCode::RuntimeUnavailable,
            "all 0 runtime sessions are in use",
        );
        let text = unavailable_text(&error, true, "Local\\Inbrisk.Runtime.S-1-5-21-1");
        // Agents match on the remedy sentence, so it stays in both forms.
        assert!(text.starts_with(
            "inbrisk runtime is not running (no shared region for this user). Start inbrisk.exe, then retry."
        ));
        assert!(text.contains("a shared region IS published for this user"));
        assert!(text.contains("all 0 runtime sessions are in use"));
    }

    #[test]
    fn generic_failure_prints_code_message_and_hint() {
        let error = InbriskError::new(ErrorCode::TargetNotFound, "no match")
            .with_hint("relax the selector");
        let text = generic_text(&error);
        assert!(text.starts_with("error: TargetNotFound: no match"));
        assert!(text.contains("hint: relax the selector"));
    }
}
