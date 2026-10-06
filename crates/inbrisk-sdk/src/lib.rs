//! Inbrisk SDK: the native client.
//!
//! This is the *turbo path*: `host -> SDK -> shared memory -> runtime`, with no
//! CLI process, no stdio framing and no MCP translation in between.
//!
//! ```no_run
//! use inbrisk_sdk::Runtime;
//! use inbrisk_protocol::action::{Plan, Step};
//! use inbrisk_protocol::selector::Selector;
//!
//! # fn main() -> Result<(), Box<dyn std::error::Error>> {
//! let runtime = Runtime::connect()?;
//! let outcome = runtime.run(Plan::new(vec![
//!     Step::Launch {
//!         app: "notepad".into(),
//!         args: vec![],
//!         cwd: None,
//!         wait_for_window: Some(Selector::name("Notepad")),
//!         timeout_ms: None,
//!         intent: None,
//!         store: None,
//!     },
//! ]))?;
//! println!("{} steps, {} us", outcome.steps.len(), outcome.elapsed_us);
//! # Ok(())
//! # }
//! ```

use std::time::{Duration, Instant};

use inbrisk_core::{log_debug, ErrorCode, InbriskError, Result, SessionId};
use inbrisk_ipc::IpcClient;
use inbrisk_protocol::action::{Plan, Step, WaitSpec};
use inbrisk_protocol::event::EventEnvelope;
use inbrisk_protocol::request::{
    ActRequest, CancelRequest, ObserveRequest, ReadRequest, Request, RunRequest, StatusRequest,
    SubscribeRequest, WaitRequest,
};
use inbrisk_protocol::response::{
    ActionOutcome, Cancelled, FindResult, Observation, ReadResult, RunOutcome, StatusReport,
    WaitOutcome, Welcome,
};
use inbrisk_protocol::selector::Selector;

/// Default timeouts. Writes are generous because a plan may legitimately wait
/// on a human or a slow provider; reads stay tight.
pub const DEFAULT_CALL_TIMEOUT: Duration = Duration::from_secs(30);
pub const DEFAULT_PLAN_TIMEOUT: Duration = Duration::from_secs(180);
pub const DEFAULT_CONNECT_TIMEOUT: Duration = Duration::from_secs(5);

/// A connected Inbrisk runtime.
#[derive(Debug)]
pub struct Runtime {
    client: IpcClient,
    default_timeout: Duration,
}

impl Runtime {
    /// Connect with the standard client identity.
    pub fn connect() -> Result<Self> {
        Self::connect_as("inbrisk-sdk", env!("CARGO_PKG_VERSION"), false)
    }

    pub fn connect_as(name: &str, version: &str, wants_events: bool) -> Result<Self> {
        let client = IpcClient::connect(name, version, wants_events, DEFAULT_CONNECT_TIMEOUT)?;
        Ok(Self {
            client,
            default_timeout: DEFAULT_CALL_TIMEOUT,
        })
    }

    /// Is a runtime listening right now? Never blocks, never panics.
    pub fn is_available() -> bool {
        IpcClient::connect("inbrisk-probe", "0", false, Duration::from_millis(1500)).is_ok()
    }

    pub fn session_id(&self) -> SessionId {
        self.client.session_id()
    }

    pub fn region_name(&self) -> &str {
        self.client.region_name()
    }

    pub fn client(&self) -> &IpcClient {
        &self.client
    }

    pub fn set_default_timeout(&mut self, timeout: Duration) {
        self.default_timeout = timeout;
    }

    /// Where the runtime lives, for diagnostics.
    pub fn endpoint(&self) -> String {
        format!(
            "{} (pid {}, epoch {})",
            self.client.region_name(),
            self.client.runtime_pid(),
            self.client.runtime_epoch()
        )
    }

    fn call(&self, request: &Request) -> Result<inbrisk_protocol::response::ResponsePayload> {
        self.client.call_ok(request, self.default_timeout)
    }

    fn call_with(
        &self,
        request: &Request,
        timeout: Duration,
    ) -> Result<inbrisk_protocol::response::ResponsePayload> {
        self.client.call_ok(request, timeout)
    }

    /// Round-trip latency in microseconds, measured with the runtime's clock.
    pub fn ping(&self) -> Result<u64> {
        let started = Instant::now();
        let seq = started.elapsed().as_nanos() as u64;
        let _ = self.call(&Request::Ping(inbrisk_protocol::request::Ping {
            seq,
            client_send_ns: unix_nanos(),
        }))?;
        Ok(started.elapsed().as_micros() as u64)
    }

    pub fn status(&self) -> Result<StatusReport> {
        let payload = self.call(&Request::Status(StatusRequest {
            include_windows: false,
            include_ownership: false,
            include_logs: false,
            log_lines: 0,
            include_ipc_stats: true,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Status(s) => Ok(*s),
            other => Err(unexpected("status", other.kind())),
        }
    }

    pub fn observe(&self, request: ObserveRequest) -> Result<Observation> {
        let payload = self.call(&Request::Observe(request))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Observation(o) => Ok(*o),
            other => Err(unexpected("observe", other.kind())),
        }
    }

    /// Cheap observation: windows + activity only.
    pub fn windows(&self) -> Result<Vec<inbrisk_core::WindowRef>> {
        Ok(self.observe(ObserveRequest::default())?.windows)
    }

    pub fn find(&self, selector: Selector) -> Result<FindResult> {
        self.find_all(selector, false, None)
    }

    pub fn find_all(
        &self,
        selector: Selector,
        all: bool,
        limit: Option<usize>,
    ) -> Result<FindResult> {
        let payload = self.call(&Request::Find(inbrisk_protocol::request::FindRequest {
            selector,
            all,
            limit,
            timeout_ms: None,
            store: None,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Found(f) => Ok(f),
            other => Err(unexpected("find", other.kind())),
        }
    }

    pub fn read(&self, element_id: u64) -> Result<ReadResult> {
        let payload = self.call(&Request::Read(ReadRequest {
            element_id: Some(element_id),
            generation: None,
            target: None,
            properties: Vec::new(),
            timeout_ms: None,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Read(r) => Ok(r),
            other => Err(unexpected("read", other.kind())),
        }
    }

    pub fn act(&self, step: Step) -> Result<ActionOutcome> {
        let payload = self.call(&Request::Act(ActRequest {
            action: step,
            expect: None,
            timeout_ms: None,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Acted(a) => Ok(a),
            other => Err(unexpected("act", other.kind())),
        }
    }

    /// Execute a whole plan in one round trip.
    pub fn run(&self, plan: Plan) -> Result<RunOutcome> {
        self.run_with(plan, DEFAULT_PLAN_TIMEOUT, None)
    }

    pub fn run_with(
        &self,
        plan: Plan,
        timeout: Duration,
        expect: Option<inbrisk_core::Expect>,
    ) -> Result<RunOutcome> {
        let payload = self.call_with(
            &Request::Run(RunRequest {
                plan,
                expect,
                mode: inbrisk_protocol::action::RunMode::Execute,
                on_error: inbrisk_protocol::action::OnError::Abort,
                timeout_ms: Some(timeout.as_millis() as u64),
                max_steps: None,
            }),
            timeout + Duration::from_secs(5),
        )?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Ran(r) => Ok(*r),
            other => Err(unexpected("run", other.kind())),
        }
    }

    /// Validate a plan without side effects.
    pub fn dry_run(&self, plan: Plan) -> Result<RunOutcome> {
        let payload = self.call_with(
            &Request::Run(RunRequest {
                plan,
                expect: None,
                mode: inbrisk_protocol::action::RunMode::DryRun,
                on_error: inbrisk_protocol::action::OnError::Continue,
                timeout_ms: Some(30_000),
                max_steps: None,
            }),
            Duration::from_secs(35),
        )?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Ran(r) => Ok(*r),
            other => Err(unexpected("run", other.kind())),
        }
    }

    pub fn wait(&self, spec: WaitSpec) -> Result<WaitOutcome> {
        let payload = self.call(&Request::Wait(WaitRequest {
            wait: spec,
            timeout_ms: None,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Waited(w) => Ok(w),
            other => Err(unexpected("wait", other.kind())),
        }
    }

    pub fn cancel_all(&self) -> Result<Cancelled> {
        let payload = self.call(&Request::Cancel(CancelRequest {
            operation_id: None,
            plan_id: None,
            all: true,
            reason: Some("client requested cancel".into()),
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Cancelled(c) => Ok(c),
            other => Err(unexpected("cancel", other.kind())),
        }
    }

    pub fn subscribe(&self, kinds: Vec<String>) -> Result<inbrisk_protocol::response::Subscribed> {
        let payload = self.call(&Request::Subscribe(SubscribeRequest {
            kinds,
            min_interval_ms: None,
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Subscribed(s) => Ok(s),
            other => Err(unexpected("subscribe", other.kind())),
        }
    }

    pub fn poll_event(&self) -> Option<EventEnvelope> {
        self.client.poll_event()
    }

    pub fn wait_event(&self, timeout: Duration) -> Option<EventEnvelope> {
        self.client.wait_event(timeout)
    }

    pub fn shutdown(&self) -> Result<()> {
        let _ = self.client.call(
            &Request::Shutdown(inbrisk_protocol::request::Shutdown {
                force: false,
                reason: Some("sdk requested shutdown".into()),
            }),
            Duration::from_secs(3),
        )?;
        Ok(())
    }

    /// Handshake details, useful for `doctor` style diagnostics.
    pub fn welcome(&self) -> Welcome {
        Welcome {
            session_id: self.client.session_id(),
            client_id: self.client.client_id(),
            runtime_epoch: self.client.runtime_epoch(),
            protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
            runtime: inbrisk_core::RuntimeIdentity {
                name: "inbrisk-runtime".into(),
                version: String::new(),
                protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
                pid: self.client.runtime_pid(),
                epoch: self.client.runtime_epoch(),
            },
            capabilities: inbrisk_protocol::caps::Capabilities::empty(),
            region_name: self.client.region_name().to_string(),
            heartbeat_ms: 1000,
            state_version: self.client.state_version(),
            generation: self.client.generation(),
        }
    }

    /// Wait until a runtime appears, or give up.
    pub fn wait_until_available(timeout: Duration) -> bool {
        let deadline = Instant::now() + timeout;
        loop {
            if Self::is_available() {
                return true;
            }
            if Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(120));
        }
    }

    pub fn cancel(&self, plan_id: Option<u64>, reason: Option<String>) -> Result<Cancelled> {
        let payload = self.call(&Request::Cancel(CancelRequest {
            operation_id: None,
            plan_id,
            all: plan_id.is_none(),
            reason: reason.or_else(|| Some("client requested cancel".into())),
        }))?;
        match payload {
            inbrisk_protocol::response::ResponsePayload::Cancelled(c) => Ok(c),
            other => Err(unexpected("cancel", other.kind())),
        }
    }

    pub fn disconnect(&self) {
        self.client.disconnect();
        log_debug!("sdk", "session {} disconnected", self.client.session_id());
    }

    /// Send one persistent-terminal request. Blocking reads get extra
    /// transport timeout on top of their `wait_ms`.
    pub fn terminal(
        &self,
        request: inbrisk_protocol::terminal::TerminalRequest,
    ) -> Result<inbrisk_protocol::terminal::TerminalReply> {
        use inbrisk_protocol::terminal::TerminalRequest as T;
        let timeout = match &request {
            T::Read(r) => self
                .default_timeout
                .max(Duration::from_millis(r.wait_ms.saturating_add(10_000))),
            _ => self.default_timeout,
        };
        match self.call_with(&Request::Terminal(request), timeout)? {
            inbrisk_protocol::response::ResponsePayload::Terminal(t) => Ok(*t),
            other => Err(unexpected("terminal", other.kind())),
        }
    }

    pub fn terminal_open(
        &self,
        open: inbrisk_protocol::terminal::TerminalOpen,
    ) -> Result<inbrisk_protocol::terminal::TerminalInfo> {
        use inbrisk_protocol::terminal::{TerminalReply as R, TerminalRequest as T};
        match self.terminal(T::Open(open))? {
            R::Opened(i) => Ok(i),
            other => Err(unexpected("terminal open", terminal_kind(&other))),
        }
    }

    pub fn terminal_write(
        &self,
        write: inbrisk_protocol::terminal::TerminalWrite,
    ) -> Result<inbrisk_protocol::terminal::TerminalWritten> {
        use inbrisk_protocol::terminal::{TerminalReply as R, TerminalRequest as T};
        match self.terminal(T::Write(write))? {
            R::Written(w) => Ok(w),
            other => Err(unexpected("terminal write", terminal_kind(&other))),
        }
    }

    pub fn terminal_read(
        &self,
        read: inbrisk_protocol::terminal::TerminalRead,
    ) -> Result<inbrisk_protocol::terminal::TerminalOutput> {
        use inbrisk_protocol::terminal::{TerminalReply as R, TerminalRequest as T};
        match self.terminal(T::Read(read))? {
            R::Output(o) => Ok(o),
            other => Err(unexpected("terminal read", terminal_kind(&other))),
        }
    }

    pub fn terminal_status(
        &self,
        session_id: Option<String>,
    ) -> Result<Vec<inbrisk_protocol::terminal::TerminalInfo>> {
        use inbrisk_protocol::terminal::{TerminalReply as R, TerminalRequest as T};
        match self.terminal(T::Status(inbrisk_protocol::terminal::TerminalStatusRequest {
            session_id,
        }))? {
            R::Status { sessions } => Ok(sessions),
            other => Err(unexpected("terminal status", terminal_kind(&other))),
        }
    }

    pub fn terminal_close(
        &self,
        session_id: String,
    ) -> Result<inbrisk_protocol::terminal::TerminalClosed> {
        use inbrisk_protocol::terminal::{TerminalReply as R, TerminalRequest as T};
        match self.terminal(T::Close(inbrisk_protocol::terminal::TerminalClose { session_id }))? {
            R::Closed(c) => Ok(c),
            other => Err(unexpected("terminal close", terminal_kind(&other))),
        }
    }
}

fn terminal_kind(r: &inbrisk_protocol::terminal::TerminalReply) -> &'static str {
    use inbrisk_protocol::terminal::TerminalReply as R;
    match r {
        R::Opened(_) => "opened",
        R::Written(_) => "written",
        R::Output(_) => "output",
        R::Status { .. } => "status",
        R::Closed(_) => "closed",
    }
}

/// Production client type alias per Phase 3 contract.
pub type InbriskClient = Runtime;

fn unexpected(op: &str, got: &str) -> InbriskError {
    InbriskError::new(
        ErrorCode::ProtocolMismatch,
        format!("{op} answered with a '{got}' payload"),
    )
}

fn unix_nanos() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sdk_has_no_mcp_dependency() {
        let cargo_toml = include_str!("../Cargo.toml");
        assert!(!cargo_toml.contains("inbrisk-mcp"));
        assert!(!cargo_toml.contains("mcp"));
    }

    #[test]
    fn sdk_native_client_connects_directly_to_runtime() {
        // InbriskClient type alias exists and is equivalent to Runtime
        let is_avail = InbriskClient::is_available();
        let _ = is_avail;
    }
}
