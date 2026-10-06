//! Persistent terminal sessions (`op: "terminal"`).
//!
//! The *runtime* owns the session: a ConPTY plus a long-lived shell. Clients
//! (one-shot CLI, SDK, MCP) only hold the `session_id` string, so a client
//! process can exit and the shell keeps living.
//!
//! ## Output cursor model
//!
//! Output is kept in a bounded ring. Every byte of retained output has a
//! monotonically increasing sequence number (`seq`, a byte offset into the
//! session's lifetime output stream). A read returns `[from_seq, to_seq)`.
//!
//! * `after: Some(n)`  – explicit cursor. Returns output from `max(n, base)`.
//!   Never touches the session's implicit cursor, so any number of readers can
//!   use explicit cursors independently.
//! * `after: None`     – implicit cursor. Returns everything since the last
//!   implicit read and advances the cursor to `to_seq` (unless `peek`).
//!   With several implicit readers they *share* one cursor: whoever reads
//!   first consumes the output. Use explicit cursors for multiple readers.
//!
//! If the requested start has already been evicted from the ring the reply
//! carries `truncated: true` and starts at the oldest retained byte.

use serde::{Deserialize, Serialize};

/// Default pseudo-console size. Wide on purpose: ConPTY re-wraps at `cols`,
/// and wrapped lines are much harder for a model to read.
pub const TERMINAL_DEFAULT_COLS: u16 = 200;
pub const TERMINAL_DEFAULT_ROWS: u16 = 50;
/// One `write` request may carry at most this many bytes (IPC request cap is 64 KiB).
pub const TERMINAL_MAX_WRITE_BYTES: usize = 32 * 1024;
/// Default / maximum bytes returned by one `read` (IPC reply cap is 256 KiB and
/// JSON may escape control bytes to 6 bytes each, so keep a wide safety margin).
pub const TERMINAL_DEFAULT_READ_BYTES: usize = 16 * 1024;
pub const TERMINAL_MAX_READ_BYTES: usize = 32 * 1024;
/// Default ring capacity per session.
pub const TERMINAL_DEFAULT_BUFFER_BYTES: usize = 1024 * 1024;
pub const TERMINAL_MIN_BUFFER_BYTES: usize = 4 * 1024;
pub const TERMINAL_MAX_BUFFER_BYTES: usize = 16 * 1024 * 1024;
/// Upper bound for `wait_ms` / `settle_ms` on a read.
pub const TERMINAL_MAX_WAIT_MS: u64 = 60_000;

fn default_true() -> bool {
    true
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct TerminalOpen {
    /// `pwsh` (default), `powershell` or `cmd`. There is **no silent fallback**:
    /// if `pwsh` is not installed the open fails unless another shell is named.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub shell: Option<String>,
    /// Initial working directory.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cwd: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cols: Option<u16>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub rows: Option<u16>,
    /// Keep VT/ANSI escape sequences in the output (default: stripped).
    #[serde(default)]
    pub raw: bool,
    /// Start PowerShell with `-NoProfile`.
    #[serde(default)]
    pub no_profile: bool,
    /// Ring capacity in bytes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub buffer_bytes: Option<usize>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TerminalWrite {
    pub session_id: String,
    /// Bytes written verbatim to the ConPTY input pipe (after the optional
    /// newline normalisation below).
    pub text: String,
    /// ConPTY input expects `\r` for Enter. When true (default) `\r\n` and lone
    /// `\n` are converted to `\r`. Set false for a byte-exact write.
    #[serde(default = "default_true")]
    pub normalize_newlines: bool,
    /// Append `\r` (Enter) after `text`. This is what `exec` uses.
    #[serde(default)]
    pub append_enter: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TerminalRead {
    pub session_id: String,
    /// Explicit cursor (see module docs). `None` = implicit, advancing cursor.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub after: Option<u64>,
    /// Block up to this long for *any* new output (0 = return immediately).
    #[serde(default)]
    pub wait_ms: u64,
    /// After output appears, keep waiting until the stream has been quiet for
    /// this long (bounded by `wait_ms`). Useful to read a command's full result.
    #[serde(default)]
    pub settle_ms: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_bytes: Option<usize>,
    /// Implicit-cursor read that does not advance the cursor.
    #[serde(default)]
    pub peek: bool,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct TerminalStatusRequest {
    /// One session, or every session when `None`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub session_id: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TerminalClose {
    pub session_id: String,
}

/// The `terminal` request: one op, five sub-commands.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "cmd", rename_all = "snake_case")]
pub enum TerminalRequest {
    Open(TerminalOpen),
    Write(TerminalWrite),
    Read(TerminalRead),
    Status(TerminalStatusRequest),
    Close(TerminalClose),
}

impl TerminalRequest {
    /// Open and write run commands inside the user's session, so they are
    /// mutations and are refused during emergency stop / pause. Read, status
    /// and close are always allowed (closing is how you make it safe).
    pub const fn is_mutating(&self) -> bool {
        matches!(self, TerminalRequest::Open(_) | TerminalRequest::Write(_))
    }

    pub const fn name(&self) -> &'static str {
        match self {
            TerminalRequest::Open(_) => "open",
            TerminalRequest::Write(_) => "write",
            TerminalRequest::Read(_) => "read",
            TerminalRequest::Status(_) => "status",
            TerminalRequest::Close(_) => "close",
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TerminalInfo {
    pub session_id: String,
    pub pid: u32,
    pub shell: String,
    pub shell_path: String,
    pub cols: u16,
    pub rows: u16,
    pub raw: bool,
    pub alive: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
    pub created_ms: u64,
    pub last_activity_ms: u64,
    /// Oldest retained output sequence.
    pub base_seq: u64,
    /// Sequence the next output byte will get (= total bytes produced).
    pub next_seq: u64,
    pub buffered_bytes: usize,
    pub buffer_capacity: usize,
    /// Implicit read cursor.
    pub read_cursor: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TerminalWritten {
    pub session_id: String,
    pub bytes_written: usize,
    pub process_alive: bool,
    /// Sequence of the output tail at the moment the write returned. Pass this
    /// as `after` to read only what the written input produced.
    pub next_seq: u64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TerminalOutput {
    pub session_id: String,
    pub from_seq: u64,
    pub to_seq: u64,
    pub text: String,
    /// More retained output exists beyond `to_seq` (read again).
    pub has_more: bool,
    /// Requested output was already evicted from the ring.
    pub truncated: bool,
    pub process_alive: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TerminalClosed {
    pub session_id: String,
    pub was_alive: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exit_code: Option<i32>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum TerminalReply {
    Opened(TerminalInfo),
    Written(TerminalWritten),
    Output(TerminalOutput),
    Status { sessions: Vec<TerminalInfo> },
    Closed(TerminalClosed),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn request_round_trips_through_the_op_envelope() {
        let req = crate::request::Request::Terminal(TerminalRequest::Write(TerminalWrite {
            session_id: "term_01".into(),
            text: "dir\r".into(),
            normalize_newlines: true,
            append_enter: false,
        }));
        let json = serde_json::to_string(&req).unwrap();
        assert!(json.contains("\"op\":\"terminal\""), "{json}");
        assert!(json.contains("\"cmd\":\"write\""), "{json}");
        let back: crate::request::Request = serde_json::from_str(&json).unwrap();
        match back {
            crate::request::Request::Terminal(TerminalRequest::Write(w)) => {
                assert_eq!(w.session_id, "term_01");
                assert!(w.normalize_newlines);
            }
            other => panic!("wrong variant: {other:?}"),
        }
    }

    #[test]
    fn normalize_newlines_defaults_to_true_on_the_wire() {
        let w: TerminalWrite =
            serde_json::from_str(r#"{"session_id":"t","text":"x"}"#).unwrap();
        assert!(w.normalize_newlines);
        assert!(!w.append_enter);
    }

    #[test]
    fn reply_round_trips_through_the_payload_envelope() {
        let payload = crate::response::ResponsePayload::Terminal(Box::new(TerminalReply::Output(
            TerminalOutput {
                session_id: "term_01".into(),
                from_seq: 0,
                to_seq: 5,
                text: "hello".into(),
                has_more: false,
                truncated: false,
                process_alive: true,
                exit_code: None,
            },
        )));
        let json = serde_json::to_string(&payload).unwrap();
        let back: crate::response::ResponsePayload = serde_json::from_str(&json).unwrap();
        match back {
            crate::response::ResponsePayload::Terminal(t) => match *t {
                TerminalReply::Output(o) => assert_eq!(o.text, "hello"),
                other => panic!("wrong reply: {other:?}"),
            },
            other => panic!("wrong payload: {other:?}"),
        }
    }

    #[test]
    fn only_open_and_write_are_mutating() {
        assert!(TerminalRequest::Open(TerminalOpen::default()).is_mutating());
        assert!(!TerminalRequest::Status(TerminalStatusRequest::default()).is_mutating());
        assert!(!TerminalRequest::Close(TerminalClose {
            session_id: "t".into()
        })
        .is_mutating());
    }
}
