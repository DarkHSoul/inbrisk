//! Persistent terminal sessions owned by the runtime.
//!
//! ```text
//! one-shot CLI --IPC--> Dispatcher --> TerminalManager --> TerminalSession
//!                                                              |-- ConPty (pwsh -NoLogo -NoExit)
//!                                                              |-- reader thread  (drains output, never writes)
//!                                                              `-- monitor thread (closes the pseudo console after the shell exits)
//! ```
//!
//! Concurrency rules:
//! * terminal I/O never takes the global engine/UIA lanes — sessions only
//!   share the manager's `HashMap` mutex, which is held for lookups only;
//! * the reader thread is the only consumer of the ConPTY output pipe and
//!   keeps draining even while a write is blocked (ConPTY deadlock rule);
//! * `write` is serialised per session inside [`ConPty::write`];
//! * a `read` with `wait_ms` blocks only its own worker thread, on a
//!   `Condvar` that the reader thread signals.

use std::collections::{HashMap, VecDeque};
use std::path::Path;
use std::sync::atomic::{AtomicBool, AtomicIsize, AtomicU64, Ordering};
use std::sync::mpsc::{sync_channel, SyncSender};
use std::sync::Arc;
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use inbrisk_core::{now_ms, ErrorCode, InbriskError, Result};
use inbrisk_protocol::terminal::*;
use inbrisk_win32::conpty::ConPty;
use parking_lot::{Condvar, Mutex};

/// Pre-flight check run before `open`/`write` (emergency stop / pause).
pub type TerminalGuard = Box<dyn Fn() -> Result<()> + Send + Sync>;

struct WriteReq {
    text: Vec<u8>,
    resp: SyncSender<Result<usize>>,
}

// ---------------------------------------------------------------------------
// Output buffer
// ---------------------------------------------------------------------------

/// Result of [`OutputBuffer::read`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Slice {
    pub from_seq: u64,
    pub to_seq: u64,
    pub text: String,
    pub has_more: bool,
    pub truncated: bool,
}

/// Bounded ring of UTF-8 text with monotonic byte sequence numbers.
///
/// Invariant: the retained bytes are always valid UTF-8 and `data[0]` sits on
/// a char boundary (eviction advances past continuation bytes).
#[derive(Debug)]
pub struct OutputBuffer {
    data: VecDeque<u8>,
    capacity: usize,
    base_seq: u64,
    cursor: u64,
}

fn is_continuation(b: u8) -> bool {
    (b & 0xC0) == 0x80
}

impl OutputBuffer {
    pub fn new(capacity: usize) -> Self {
        Self {
            data: VecDeque::new(),
            capacity: capacity.max(16),
            base_seq: 0,
            cursor: 0,
        }
    }

    pub fn base_seq(&self) -> u64 {
        self.base_seq
    }
    pub fn next_seq(&self) -> u64 {
        self.base_seq + self.data.len() as u64
    }
    pub fn cursor(&self) -> u64 {
        self.cursor
    }
    pub fn len(&self) -> usize {
        self.data.len()
    }
    pub fn is_empty(&self) -> bool {
        self.data.is_empty()
    }
    pub fn capacity(&self) -> usize {
        self.capacity
    }

    /// Append text, evicting the oldest bytes when over capacity.
    pub fn push(&mut self, text: &str) {
        if text.is_empty() {
            return;
        }
        self.data.extend(text.as_bytes());
        if self.data.len() > self.capacity {
            let mut drop_n = self.data.len() - self.capacity;
            while drop_n < self.data.len() && is_continuation(self.data[drop_n]) {
                drop_n += 1;
            }
            self.data.drain(..drop_n);
            self.base_seq += drop_n as u64;
        }
    }

    /// Whether a read from `after` (or the implicit cursor) would return bytes.
    pub fn has_new(&self, after: Option<u64>) -> bool {
        let start = after.unwrap_or(self.cursor).max(self.base_seq);
        self.next_seq() > start
    }

    /// Read at most `max_bytes` (cut on a char boundary).
    ///
    /// `after = Some(n)` never touches the implicit cursor. `after = None`
    /// reads from the implicit cursor and advances it unless `peek`.
    pub fn read(&mut self, after: Option<u64>, max_bytes: usize, peek: bool) -> Slice {
        let requested = after.unwrap_or(self.cursor);
        let truncated = requested < self.base_seq;
        let next = self.next_seq();
        let start = requested.max(self.base_seq).min(next);
        let mut end = (start.saturating_add(max_bytes.max(1) as u64)).min(next);
        // Back up to a char boundary (never past `start`).
        while end > start
            && end < next
            && is_continuation(self.data[(end - self.base_seq) as usize])
        {
            end -= 1;
        }
        let (a, b) = (
            (start - self.base_seq) as usize,
            (end - self.base_seq) as usize,
        );
        let bytes: Vec<u8> = self.data.range(a..b).copied().collect();
        let text = String::from_utf8_lossy(&bytes).into_owned();
        if after.is_none() && !peek {
            self.cursor = end;
        }
        Slice {
            from_seq: start,
            to_seq: end,
            text,
            has_more: end < next,
            truncated,
        }
    }
}

// ---------------------------------------------------------------------------
// VT stripping
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum VtState {
    Ground,
    Esc,
    EscInter,
    Csi,
    /// OSC / DCS / SOS / PM / APC string, terminated by BEL or ST.
    Str,
    StrEsc,
}

/// Stateful (chunk-safe) ANSI/VT escape stripper producing readable text.
#[derive(Debug)]
pub struct VtStripper {
    state: VtState,
}

impl Default for VtStripper {
    fn default() -> Self {
        Self {
            state: VtState::Ground,
        }
    }
}

impl VtStripper {
    pub fn feed(&mut self, input: &str) -> String {
        let mut out = String::with_capacity(input.len());
        for c in input.chars() {
            match self.state {
                VtState::Ground => match c {
                    '\x1b' => self.state = VtState::Esc,
                    '\n' | '\t' => out.push(c),
                    '\r' => {}
                    c if (c as u32) < 0x20 || c == '\x7f' => {}
                    c => out.push(c),
                },
                VtState::Esc => match c {
                    '[' => self.state = VtState::Csi,
                    ']' | 'P' | 'X' | '^' | '_' => self.state = VtState::Str,
                    ' '..='/' => self.state = VtState::EscInter,
                    '\x1b' => {}
                    _ => self.state = VtState::Ground,
                },
                VtState::EscInter => {
                    if ('\u{30}'..='\u{7e}').contains(&c) {
                        self.state = VtState::Ground;
                    }
                }
                VtState::Csi => {
                    if ('\u{40}'..='\u{7e}').contains(&c) {
                        self.state = VtState::Ground;
                    }
                }
                VtState::Str => match c {
                    '\x07' => self.state = VtState::Ground,
                    '\x1b' => self.state = VtState::StrEsc,
                    _ => {}
                },
                VtState::StrEsc => {
                    // ESC \ (ST) ends the string; anything else also ends it.
                    self.state = VtState::Ground;
                }
            }
        }
        out
    }
}

/// Decodes a byte stream into `String`s, carrying incomplete UTF-8 tails
/// across chunk boundaries and replacing invalid bytes.
#[derive(Debug, Default)]
pub struct Utf8Decoder {
    carry: Vec<u8>,
}

impl Utf8Decoder {
    pub fn feed(&mut self, bytes: &[u8]) -> String {
        self.carry.extend_from_slice(bytes);
        let mut out = String::new();
        loop {
            match std::str::from_utf8(&self.carry) {
                Ok(s) => {
                    out.push_str(s);
                    self.carry.clear();
                    return out;
                }
                Err(e) => {
                    let valid = e.valid_up_to();
                    out.push_str(std::str::from_utf8(&self.carry[..valid]).unwrap());
                    match e.error_len() {
                        Some(n) => {
                            out.push('\u{FFFD}');
                            self.carry.drain(..valid + n);
                        }
                        None => {
                            // Incomplete tail: keep for the next chunk.
                            self.carry.drain(..valid);
                            return out;
                        }
                    }
                }
            }
        }
    }
}

/// `\r\n` and lone `\n` -> `\r` (what ConPTY input expects for Enter).
pub fn normalize_enter(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    let mut chars = text.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '\r' => {
                if chars.peek() == Some(&'\n') {
                    chars.next();
                }
                out.push('\r');
            }
            '\n' => out.push('\r'),
            c => out.push(c),
        }
    }
    out
}

// ---------------------------------------------------------------------------
// Session
// ---------------------------------------------------------------------------

struct Shared {
    state: Mutex<SharedState>,
    cv: Condvar,
}

struct SharedState {
    buf: OutputBuffer,
    last_output_at: Instant,
    last_activity_ms: u64,
    reader_done: bool,
}

pub struct TerminalSession {
    id: String,
    shell: String,
    shell_path: String,
    raw: bool,
    pty: Arc<ConPty>,
    shared: Arc<Shared>,
    created_ms: u64,
    closing: Arc<AtomicBool>,
    threads: Mutex<Vec<JoinHandle<()>>>,
    writer_tx: Mutex<Option<SyncSender<WriteReq>>>,
    writer_thread_handle: AtomicIsize,
}

impl std::fmt::Debug for TerminalSession {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TerminalSession")
            .field("id", &self.id)
            .finish()
    }
}

fn quote(path: &str) -> String {
    format!("\"{path}\"")
}

impl TerminalSession {
    fn start(
        id: String,
        shell: &str,
        shell_path: &Path,
        open: &TerminalOpen,
        cols: u16,
        rows: u16,
        capacity: usize,
    ) -> Result<Arc<Self>> {
        let p = shell_path.to_string_lossy().into_owned();
        let cmdline = match shell {
            "pwsh" | "powershell" => {
                let mut c = format!("{} -NoLogo -NoExit", quote(&p));
                if open.no_profile {
                    c.push_str(" -NoProfile");
                }
                c
            }
            _ => quote(&p),
        };
        let cwd = match open.cwd.as_deref().filter(|s| !s.trim().is_empty()) {
            Some(dir) => {
                let path = Path::new(dir);
                if !path.is_dir() {
                    return Err(InbriskError::new(
                        ErrorCode::InvalidPlan,
                        format!("cwd '{dir}' is not an existing directory"),
                    ));
                }
                Some(path.to_path_buf())
            }
            None => None,
        };
        let pty =
            ConPty::spawn(&cmdline, cwd.as_deref(), cols as i16, rows as i16).map_err(|e| {
                InbriskError::internal(format!("failed to start ConPTY shell '{p}': {e}"))
            })?;
        let pty = Arc::new(pty);
        let shared = Arc::new(Shared {
            state: Mutex::new(SharedState {
                buf: OutputBuffer::new(capacity),
                last_output_at: Instant::now(),
                last_activity_ms: now_ms(),
                reader_done: false,
            }),
            cv: Condvar::new(),
        });
        let closing = Arc::new(AtomicBool::new(false));

        // Reader: the ONLY consumer of the output pipe. Never writes.
        let reader = {
            let pty = pty.clone();
            let shared = shared.clone();
            let raw = open.raw;
            std::thread::Builder::new()
                .name(format!("term-read-{id}"))
                .spawn(move || {
                    let mut dec = Utf8Decoder::default();
                    let mut vt = VtStripper::default();
                    let mut buf = vec![0u8; 8192];
                    loop {
                        match pty.read(&mut buf) {
                            Ok(0) | Err(_) => break,
                            Ok(n) => {
                                let text = dec.feed(&buf[..n]);
                                let text = if raw { text } else { vt.feed(&text) };
                                if text.is_empty() {
                                    continue;
                                }
                                let mut g = shared.state.lock();
                                g.buf.push(&text);
                                g.last_output_at = Instant::now();
                                g.last_activity_ms = now_ms();
                                drop(g);
                                shared.cv.notify_all();
                            }
                        }
                    }
                    shared.state.lock().reader_done = true;
                    shared.cv.notify_all();
                })
                .map_err(|e| InbriskError::internal(format!("reader thread: {e}")))?
        };

        // Monitor: once the shell exits, close the pseudo console so the
        // reader sees EOF after the final output is flushed.
        let monitor = {
            let pty = pty.clone();
            let closing = closing.clone();
            std::thread::Builder::new()
                .name(format!("term-mon-{id}"))
                .spawn(move || {
                    loop {
                        if closing.load(Ordering::SeqCst) {
                            return;
                        }
                        if pty.wait_exit(200) {
                            break;
                        }
                    }
                    std::thread::sleep(Duration::from_millis(200));
                    pty.close_pseudo_console();
                })
                .map_err(|e| InbriskError::internal(format!("monitor thread: {e}")))?
        };

        // Writer: bounded queue to ConPTY input pipe
        let (writer_tx, writer_rx) = sync_channel::<WriteReq>(4);
        let writer_pty = pty.clone();
        let writer_id = id.clone();
        let writer_closing = closing.clone();
        let writer = std::thread::Builder::new()
            .name(format!("term-write-{id}"))
            .spawn(move || {
                while let Ok(req) = writer_rx.recv() {
                    if writer_closing.load(Ordering::SeqCst) {
                        let _ = req.resp.send(Err(InbriskError::target_gone("terminal session closed")));
                        continue;
                    }
                    let res = writer_pty.write(&req.text).map_err(|e| {
                        InbriskError::target_gone(format!(
                            "terminal {} input pipe failed: {e}",
                            writer_id
                        ))
                    });
                    let _ = req.resp.send(res);
                }
            })
            .map_err(|e| InbriskError::internal(format!("writer thread: {e}")))?;

        #[cfg(windows)]
        use std::os::windows::io::AsRawHandle;

        let writer_thread_handle = AtomicIsize::new(writer.as_raw_handle() as isize);

        Ok(Arc::new(Self {
            id,
            shell: shell.to_string(),
            shell_path: p,
            raw: open.raw,
            pty,
            shared,
            created_ms: now_ms(),
            closing,
            threads: Mutex::new(vec![reader, monitor, writer]),
            writer_tx: Mutex::new(Some(writer_tx)),
            writer_thread_handle,
        }))
    }

    pub fn id(&self) -> &str {
        &self.id
    }

    pub fn is_alive(&self) -> bool {
        self.pty.is_alive()
    }

    fn exit_code(&self) -> Option<i32> {
        self.pty.exit_code().map(|c| c as i32)
    }

    pub fn info(&self) -> TerminalInfo {
        let g = self.shared.state.lock();
        let (cols, rows) = self.pty.size();
        let code = self.exit_code();
        TerminalInfo {
            session_id: self.id.clone(),
            pid: self.pty.pid(),
            shell: self.shell.clone(),
            shell_path: self.shell_path.clone(),
            cols: cols as u16,
            rows: rows as u16,
            raw: self.raw,
            alive: code.is_none(),
            exit_code: code,
            created_ms: self.created_ms,
            last_activity_ms: g.last_activity_ms,
            base_seq: g.buf.base_seq(),
            next_seq: g.buf.next_seq(),
            buffered_bytes: g.buf.len(),
            buffer_capacity: g.buf.capacity(),
            read_cursor: g.buf.cursor(),
        }
    }

    pub fn write(&self, req: &TerminalWrite) -> Result<TerminalWritten> {
        if self.closing.load(Ordering::SeqCst) {
            return Err(InbriskError::target_gone(format!(
                "terminal session {} is closing",
                self.id
            )));
        }
        if !self.is_alive() {
            return Err(InbriskError::target_gone(format!(
                "terminal session {} has exited (exit code {:?}); read remaining output or close it",
                self.id,
                self.exit_code()
            )));
        }
        let mut text = if req.normalize_newlines {
            normalize_enter(&req.text)
        } else {
            req.text.clone()
        };
        if req.append_enter {
            text.push('\r');
        }
        if text.len() > TERMINAL_MAX_WRITE_BYTES {
            return Err(InbriskError::invalid_plan(format!(
                "terminal write is {} bytes; the maximum is {TERMINAL_MAX_WRITE_BYTES}",
                text.len()
            )));
        }
        // Capture the tail BEFORE writing so `next_seq` lets the caller read
        // exactly what this input produced.
        let before = self.shared.state.lock().buf.next_seq();

        let (res_tx, res_rx) = sync_channel(1);
        let w_req = WriteReq {
            text: text.into_bytes(),
            resp: res_tx,
        };

        {
            let g = self.writer_tx.lock();
            if let Some(tx) = g.as_ref() {
                if let Err(_) = tx.try_send(w_req) {
                    return Err(InbriskError::new(
                        ErrorCode::BudgetExhausted,
                        "terminal session input queue is full",
                    ));
                }
            } else {
                return Err(InbriskError::target_gone(
                    "terminal session writer is closed",
                ));
            }
        }

        let wait = Duration::from_millis(5000); // 5 second max wait for write
        match res_rx.recv_timeout(wait) {
            Ok(Ok(n)) => {
                self.shared.state.lock().last_activity_ms = now_ms();
                Ok(TerminalWritten {
                    session_id: self.id.clone(),
                    bytes_written: n,
                    process_alive: self.is_alive(),
                    next_seq: before,
                })
            }
            Ok(Err(e)) => Err(e),
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                // If the write gets stuck (e.g. ConPTY is full), we must not block
                // the runtime indefinitely. Cancel the writer thread and close session.
                self.shutdown();
                Err(InbriskError::new(
                    ErrorCode::Timeout,
                    "terminal write timed out and the session was closed",
                ))
            }
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => Err(InbriskError::target_gone(
                "terminal writer thread died unexpectedly",
            )),
        }
    }

    pub fn read(&self, req: &TerminalRead) -> TerminalOutput {
        let wait = Duration::from_millis(req.wait_ms.min(TERMINAL_MAX_WAIT_MS));
        let settle = Duration::from_millis(req.settle_ms.min(TERMINAL_MAX_WAIT_MS));
        let max = req
            .max_bytes
            .unwrap_or(TERMINAL_DEFAULT_READ_BYTES)
            .clamp(16, TERMINAL_MAX_READ_BYTES);
        let deadline = Instant::now() + wait;
        let mut g = self.shared.state.lock();
        if !wait.is_zero() {
            while !g.buf.has_new(req.after) && !g.reader_done && Instant::now() < deadline {
                self.shared.cv.wait_until(&mut g, deadline);
            }
            if !settle.is_zero() && g.buf.has_new(req.after) {
                loop {
                    let quiet_at = g.last_output_at + settle;
                    let now = Instant::now();
                    if now >= quiet_at || now >= deadline || g.reader_done {
                        break;
                    }
                    self.shared.cv.wait_until(&mut g, quiet_at.min(deadline));
                }
            }
        }
        let slice = g.buf.read(req.after, max, req.peek);
        drop(g);
        let code = self.exit_code();
        TerminalOutput {
            session_id: self.id.clone(),
            from_seq: slice.from_seq,
            to_seq: slice.to_seq,
            text: slice.text,
            has_more: slice.has_more,
            truncated: slice.truncated,
            process_alive: code.is_none(),
            exit_code: code,
        }
    }

    /// Terminate the shell tree, close the pseudo console and join workers.
    /// Idempotent. Returns whether the shell was alive when called.
    pub fn shutdown(&self) -> bool {
        let was_alive = self.is_alive();
        self.closing.store(true, Ordering::SeqCst);

        // Close the writer channel
        let _ = self.writer_tx.lock().take();

        #[cfg(windows)]
        {
            let th = self.writer_thread_handle.load(Ordering::SeqCst);
            if th != 0 {
                if let Err(e) = ConPty::cancel_sync_io(th) {
                    // If cancellation failed for an unexpected reason, we must not enter an
                    // unbounded join since the writer thread might still be blocked.
                    // Instead of panicking or hanging, we just log/ignore since we also
                    // call close_pseudo_console() which breaks the pipe.
                    eprintln!("terminal session {}: cancel_sync_io failed: {e}", self.id);
                }
            }
        }

        self.pty.terminate();
        self.pty.close_pseudo_console();
        // The reader ends on EOF; give it a bounded time, then detach.
        {
            let mut g = self.shared.state.lock();
            let deadline = Instant::now() + Duration::from_secs(3);
            while !g.reader_done && Instant::now() < deadline {
                self.shared.cv.wait_until(&mut g, deadline);
            }
        }
        let handles: Vec<_> = std::mem::take(&mut *self.threads.lock());
        for h in handles {
            if h.is_finished() || self.shared.state.lock().reader_done {
                let _ = h.join();
            }
        }
        was_alive
    }
}

impl Drop for TerminalSession {
    fn drop(&mut self) {
        self.shutdown();
    }
}

// ---------------------------------------------------------------------------
// Manager
// ---------------------------------------------------------------------------

pub struct TerminalManager {
    sessions: Mutex<HashMap<String, Arc<TerminalSession>>>,
    next_id: AtomicU64,
    max_sessions: usize,
    guard: TerminalGuard,
}

impl std::fmt::Debug for TerminalManager {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("TerminalManager")
    }
}

fn resolve_shell(name: &str) -> Result<(String, std::path::PathBuf)> {
    let key = name.trim().to_ascii_lowercase();
    let canon = match key.as_str() {
        "" | "pwsh" | "pwsh.exe" => "pwsh",
        "powershell" | "powershell.exe" | "winps" => "powershell",
        "cmd" | "cmd.exe" => "cmd",
        other => {
            return Err(InbriskError::invalid_plan(format!(
                "unsupported terminal shell '{other}' (use pwsh, powershell or cmd)"
            )))
        }
    };
    match inbrisk_win32::resolve_app(canon) {
        Some(p) => Ok((canon.to_string(), p)),
        None if canon == "pwsh" => Err(InbriskError::capability_unavailable(
            "terminal",
            "pwsh.exe (PowerShell 7) was not found; Inbrisk does not silently fall back",
        )
        .with_hint("install PowerShell 7, or pass --shell powershell / --shell cmd explicitly")),
        None => Err(InbriskError::capability_unavailable(
            "terminal",
            format!("{canon} was not found"),
        )),
    }
}

impl TerminalManager {
    pub fn new(max_sessions: usize, guard: TerminalGuard) -> Self {
        Self {
            sessions: Mutex::new(HashMap::new()),
            next_id: AtomicU64::new(1),
            max_sessions: max_sessions.max(1),
            guard,
        }
    }

    pub fn session_count(&self) -> usize {
        self.sessions.lock().len()
    }

    fn get(&self, id: &str) -> Result<Arc<TerminalSession>> {
        self.sessions.lock().get(id).cloned().ok_or_else(|| {
            InbriskError::not_found(format!("no terminal session '{id}'"))
                .with_hint("run `inbrisk-cli terminal status` to list sessions")
        })
    }

    pub fn handle(&self, req: &TerminalRequest) -> Result<TerminalReply> {
        if req.is_mutating() {
            (self.guard)()?;
        }
        match req {
            TerminalRequest::Open(o) => self.open(o).map(TerminalReply::Opened),
            TerminalRequest::Write(w) => self
                .get(&w.session_id)?
                .write(w)
                .map(TerminalReply::Written),
            TerminalRequest::Read(r) => Ok(TerminalReply::Output(self.get(&r.session_id)?.read(r))),
            TerminalRequest::Status(s) => self.status(s.session_id.as_deref()),
            TerminalRequest::Close(c) => self.close(&c.session_id).map(TerminalReply::Closed),
        }
    }

    pub fn open(&self, open: &TerminalOpen) -> Result<TerminalInfo> {
        let (shell, path) = resolve_shell(open.shell.as_deref().unwrap_or("pwsh"))?;
        let cols = open.cols.unwrap_or(TERMINAL_DEFAULT_COLS).clamp(20, 500);
        let rows = open.rows.unwrap_or(TERMINAL_DEFAULT_ROWS).clamp(5, 200);
        let cap = open
            .buffer_bytes
            .unwrap_or(TERMINAL_DEFAULT_BUFFER_BYTES)
            .clamp(TERMINAL_MIN_BUFFER_BYTES, TERMINAL_MAX_BUFFER_BYTES);

        // Reserve a slot (evicting exited sessions if needed) before spawning.
        let reaped: Vec<Arc<TerminalSession>>;
        {
            let mut map = self.sessions.lock();
            let mut dead = Vec::new();
            if map.len() >= self.max_sessions {
                let ids: Vec<String> = map
                    .iter()
                    .filter(|(_, s)| !s.is_alive())
                    .map(|(k, _)| k.clone())
                    .collect();
                for id in ids {
                    if let Some(s) = map.remove(&id) {
                        dead.push(s);
                    }
                }
            }
            if map.len() >= self.max_sessions {
                return Err(InbriskError::new(
                    ErrorCode::BudgetExhausted,
                    format!("terminal session limit reached ({})", self.max_sessions),
                )
                .with_hint("close an unused session with `inbrisk-cli terminal close <id>`"));
            }
            reaped = dead;
        }
        for s in reaped {
            s.shutdown();
        }

        let id = format!("term_{:02}", self.next_id.fetch_add(1, Ordering::SeqCst));
        let session = TerminalSession::start(id.clone(), &shell, &path, open, cols, rows, cap)?;
        let info = session.info();
        let mut map = self.sessions.lock();
        if map.len() >= self.max_sessions {
            // Lost a race with a concurrent open.
            drop(map);
            session.shutdown();
            return Err(InbriskError::new(
                ErrorCode::BudgetExhausted,
                format!("terminal session limit reached ({})", self.max_sessions),
            ));
        }
        map.insert(id, session);
        Ok(info)
    }

    pub fn status(&self, id: Option<&str>) -> Result<TerminalReply> {
        let sessions = match id {
            Some(id) => vec![self.get(id)?.info()],
            None => {
                let mut v: Vec<TerminalInfo> =
                    self.sessions.lock().values().map(|s| s.info()).collect();
                v.sort_by(|a, b| a.session_id.cmp(&b.session_id));
                v
            }
        };
        Ok(TerminalReply::Status { sessions })
    }

    pub fn close(&self, id: &str) -> Result<TerminalClosed> {
        let session = self.sessions.lock().remove(id).ok_or_else(|| {
            InbriskError::not_found(format!("no terminal session '{id}' (already closed?)"))
        })?;
        let was_alive = session.shutdown();
        Ok(TerminalClosed {
            session_id: id.to_string(),
            was_alive,
            exit_code: session.exit_code(),
        })
    }

    /// Close every session (runtime shutdown). Deterministic.
    pub fn shutdown_all(&self) {
        let all: Vec<_> = self.sessions.lock().drain().map(|(_, s)| s).collect();
        for s in all {
            s.shutdown();
        }
    }
}

impl Drop for TerminalManager {
    fn drop(&mut self) {
        self.shutdown_all();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn buffer_delta_reads_and_cursor() {
        let mut b = OutputBuffer::new(1024);
        b.push("hello ");
        let s = b.read(None, 100, false);
        assert_eq!((s.from_seq, s.to_seq, s.text.as_str()), (0, 6, "hello "));
        assert!(!s.truncated && !s.has_more);
        b.push("world");
        let s = b.read(None, 100, false);
        assert_eq!((s.from_seq, s.to_seq, s.text.as_str()), (6, 11, "world"));
        // Nothing new.
        let s = b.read(None, 100, false);
        assert_eq!((s.from_seq, s.to_seq, s.text.as_str()), (11, 11, ""));
    }

    #[test]
    fn buffer_explicit_after_does_not_move_cursor_and_peek() {
        let mut b = OutputBuffer::new(1024);
        b.push("abcdef");
        let s = b.read(Some(2), 100, false);
        assert_eq!(s.text, "cdef");
        assert_eq!(b.cursor(), 0);
        let s = b.read(None, 3, true);
        assert_eq!(s.text, "abc");
        assert!(s.has_more);
        assert_eq!(b.cursor(), 0);
        let s = b.read(None, 3, false);
        assert_eq!(s.text, "abc");
        assert_eq!(b.cursor(), 3);
        let s = b.read(None, 100, false);
        assert_eq!(s.text, "def");
        // Cursor beyond tail clamps.
        let s = b.read(Some(999), 100, false);
        assert_eq!((s.from_seq, s.to_seq, s.text.as_str()), (6, 6, ""));
    }

    #[test]
    fn buffer_eviction_marks_truncated_and_keeps_utf8() {
        let mut b = OutputBuffer::new(16);
        b.push("0123456789");
        b.push("ççççç"); // 10 bytes -> 20 total, evict 4
        assert!(b.base_seq() >= 4);
        assert!(b.len() <= 16);
        let s = b.read(None, 100, false);
        assert!(s.truncated);
        assert_eq!(s.from_seq, b.base_seq());
        assert!(std::str::from_utf8(s.text.as_bytes()).is_ok());
        // Reading again is not truncated any more.
        b.push("x");
        let s = b.read(None, 100, false);
        assert!(!s.truncated);
        assert_eq!(s.text, "x");
    }

    #[test]
    fn buffer_eviction_skips_split_char() {
        let mut b = OutputBuffer::new(16);
        b.push("abç"); // 4 bytes
        b.push("0123456789ab"); // total 16
        b.push("Z"); // 17 -> drop 1
        let s = b.read(Some(0), 100, true);
        assert!(s.truncated);
        assert!(!s.text.contains('\u{FFFD}'));
    }

    #[test]
    fn buffer_read_cuts_on_char_boundary() {
        let mut b = OutputBuffer::new(1024);
        b.push("aç"); // a + 2 bytes
        let s = b.read(Some(0), 2, true); // would cut inside ç
        assert_eq!(s.text, "a");
        assert_eq!(s.to_seq, 1);
        assert!(s.has_more);
    }

    #[test]
    fn vt_stripper_basic_and_chunked() {
        let mut v = VtStripper::default();
        assert_eq!(v.feed("\x1b[31mred\x1b[0m ok\r\n"), "red ok\n");
        assert_eq!(v.feed("\x1b]0;title\x07after"), "after");
        assert_eq!(v.feed("\x1b]0;t\x1b\\x"), "x");
        // Split mid-sequence.
        let a = v.feed("pre\x1b[3");
        let b = v.feed("1;1Hpost");
        assert_eq!(format!("{a}{b}"), "prepost");
        assert_eq!(v.feed("\x1b[?25l\x1b[2J\x1b[H"), "");
        assert_eq!(v.feed("a\x07b\tc"), "ab\tc");
    }

    #[test]
    fn utf8_decoder_handles_split_sequences() {
        let mut d = Utf8Decoder::default();
        let bytes = "ç€".as_bytes();
        let mut out = String::new();
        for b in bytes {
            out.push_str(&d.feed(&[*b]));
        }
        assert_eq!(out, "ç€");
        assert_eq!(d.feed(&[0xFF, b'a']), "\u{FFFD}a");
    }

    #[test]
    fn enter_normalisation() {
        assert_eq!(normalize_enter("a\r\nb\nc\rd"), "a\rb\rc\rd");
        assert_eq!(normalize_enter("x"), "x");
    }

    fn mgr(max: usize) -> TerminalManager {
        TerminalManager::new(max, Box::new(|| Ok(())))
    }

    #[test]
    fn invalid_session_and_shell_errors() {
        let m = mgr(2);
        let e = m
            .handle(&TerminalRequest::Close(TerminalClose {
                session_id: "nope".into(),
            }))
            .unwrap_err();
        assert_eq!(e.code, ErrorCode::TargetNotFound);
        let e = m
            .open(&TerminalOpen {
                shell: Some("bash".into()),
                ..Default::default()
            })
            .unwrap_err();
        assert_eq!(e.code, ErrorCode::InvalidPlan);
    }

    #[test]
    fn guard_blocks_mutations_but_not_reads() {
        let m = TerminalManager::new(
            2,
            Box::new(|| Err(InbriskError::new(ErrorCode::Emergency, "stopped"))),
        );
        let e = m
            .handle(&TerminalRequest::Open(TerminalOpen::default()))
            .unwrap_err();
        assert_eq!(e.code, ErrorCode::Emergency);
        let e = m
            .handle(&TerminalRequest::Write(TerminalWrite {
                session_id: "x".into(),
                text: "a".into(),
                normalize_newlines: true,
                append_enter: false,
            }))
            .unwrap_err();
        assert_eq!(e.code, ErrorCode::Emergency);
        // Status is allowed.
        assert!(m
            .handle(&TerminalRequest::Status(Default::default()))
            .is_ok());
    }
}
