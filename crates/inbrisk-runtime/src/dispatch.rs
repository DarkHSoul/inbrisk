//! Request dispatch: protocol request in, protocol reply out.
//!
//! Every op is handled here and nowhere else, so the runtime has exactly one
//! entry point. Long operations (`run`, `wait`, `act`) are executed on worker
//! threads by the runtime loop; this type is `Sync` and stateless apart from
//! counters.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use inbrisk_core::{log_info, now_ms, ErrorCode, InbriskError, Result, RuntimeIdentity, SessionId};
use inbrisk_ipc::{IncomingRequest, IpcServer};
use inbrisk_protocol::caps::Capabilities;
use inbrisk_protocol::event::Event;
use inbrisk_protocol::request::{
    ActRequest, CancelRequest, ObserveRequest, ReadRequest, Request, RunRequest, StatusRequest,
    SubscribeRequest, WaitRequest,
};
use inbrisk_protocol::response::{
    ActionOutcome, Cancelled, FindResult, Observation, Pong, ReadResult, Reply, ResponsePayload,
    RunOutcome, StatusReport, Subscribed, WaitOutcome, Welcome,
};
use inbrisk_protocol::selector::{MatchPolicy, Selector};
use inbrisk_protocol::PROTOCOL_VERSION;
use serde_json::{json, Value};

use crate::engine::{Engine, PlanContext, RunningOperation};
use crate::state::Backend;

/// Everything the dispatcher needs to answer a request.
pub struct Dispatcher {
    pub engine: Arc<Engine>,
    pub ipc: Arc<IpcServer>,
    pub identity: RuntimeIdentity,
    pub capabilities: Capabilities,
    pub shutting_down: Arc<AtomicBool>,
    pub read_timeout: Duration,
    /// Persistent terminal sessions (ConPTY shells) owned by the runtime.
    pub terminals: Arc<crate::terminal::TerminalManager>,
}

impl std::fmt::Debug for Dispatcher {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Dispatcher")
    }
}

/// Ops that may run for a long time and therefore get their own thread.
pub fn is_long_running(op: inbrisk_protocol::opcode::Op) -> bool {
    use inbrisk_protocol::opcode::Op;
    // Terminal reads may block (wait_ms) and writes may block on a full pipe:
    // keep them off the main loop.
    matches!(op, Op::Run | Op::Wait | Op::Act | Op::Terminal)
}

impl Dispatcher {
    /// Compute dynamic capabilities taking desktop broker connection and permissions into account.
    pub fn effective_capabilities(&self) -> Capabilities {
        let mut caps = self.capabilities;
        let broker_connected = self.engine.is_broker_connected();
        let engine_caps = self.engine.capabilities();
        if !broker_connected || !engine_caps.physical_input {
            caps.0 &= !Capabilities::PHYSICAL_INPUT;
        } else {
            caps.0 |= Capabilities::PHYSICAL_INPUT;
        }
        caps
    }

    /// Handle one request and return the reply. Never panics on bad input.
    pub fn handle(&self, incoming: IncomingRequest) -> Reply {
        let started = Instant::now();
        let op = incoming.request.op();
        let state = &self.engine.state;
        state.counters.requests.fetch_add(1, Ordering::Relaxed);
        state
            .counters
            .shared_memory_requests
            .fetch_add(1, Ordering::Relaxed);
        let client_name = self
            .ipc
            .session_name(incoming.session_id)
            .unwrap_or_default();
        if client_name.eq_ignore_ascii_case("inbrisk-mcp") {
            state.counters.mcp_requests.fetch_add(1, Ordering::Relaxed);
        } else {
            state
                .counters
                .native_requests
                .fetch_add(1, Ordering::Relaxed);
        }
        self.ipc.touch(incoming.session_id);

        let result =
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| self.dispatch(&incoming)));

        let elapsed_us = started.elapsed().as_micros() as u64;
        let (state_version, generation) = (state.state_version(), state.generation());
        match result {
            Ok(Ok(payload)) => Reply::ok(
                incoming.request_id,
                op,
                payload,
                state_version,
                generation,
                elapsed_us,
            )
            .with_session(incoming.session_id)
            .with_operation(incoming.request_id),
            Ok(Err(e)) => {
                if e.code == ErrorCode::StaleState {
                    state.counters.stale.fetch_add(1, Ordering::Relaxed);
                }
                if e.code == ErrorCode::Denied || e.code == ErrorCode::Protected {
                    state.counters.denials.fetch_add(1, Ordering::Relaxed);
                }
                state.counters.failures.fetch_add(1, Ordering::Relaxed);
                Reply::err(
                    incoming.request_id,
                    op,
                    e,
                    state_version,
                    generation,
                    elapsed_us,
                )
                .with_session(incoming.session_id)
                .with_operation(incoming.request_id)
            }
            Err(_) => {
                state.counters.failures.fetch_add(1, Ordering::Relaxed);
                Reply::err(
                    incoming.request_id,
                    op,
                    InbriskError::internal("the runtime panicked while handling this request"),
                    state_version,
                    generation,
                    elapsed_us,
                )
                .with_session(incoming.session_id)
            }
        }
    }

    fn dispatch(&self, incoming: &IncomingRequest) -> Result<ResponsePayload> {
        let session = incoming.session_id;
        match &incoming.request {
            Request::Hello(hello) => self.handle_hello(session, hello),
            Request::Ping(ping) => Ok(ResponsePayload::Pong(Pong {
                seq: ping.seq,
                client_send_ns: ping.client_send_ns,
                runtime_recv_ns: now_ns(),
                runtime_send_ns: now_ns(),
                session_id: session,
            })),
            Request::Status(request) => self.handle_status(request),
            Request::Observe(request) => self.handle_observe(request),
            Request::Find(request) => self.handle_find(session, request),
            Request::Read(request) => self.handle_read(session, request),
            Request::Act(request) => self.handle_act(session, incoming.request_id, request),
            Request::Run(request) => self.handle_run(session, incoming.request_id, request),
            Request::Wait(request) => self.handle_wait(session, incoming.request_id, request),
            Request::Cancel(request) => Ok(ResponsePayload::Cancelled(
                self.handle_cancel(session, request),
            )),
            Request::Subscribe(request) => {
                Ok(ResponsePayload::Subscribed(self.handle_subscribe(request)))
            }
            Request::Terminal(request) => self
                .terminals
                .handle(request)
                .map(|r| ResponsePayload::Terminal(Box::new(r))),
            Request::Shutdown(request) => {
                let reason = request
                    .reason
                    .clone()
                    .unwrap_or_else(|| "client requested shutdown".into());
                log_info!("runtime", "shutdown requested: {reason}");
                self.shutting_down.store(true, Ordering::Release);
                let _ = self.ipc.publish_event(&Event::RuntimeShutdown {
                    reason,
                    epoch: self.ipc.epoch(),
                });
                Ok(ResponsePayload::Ack)
            }
        }
    }

    fn handle_hello(
        &self,
        session: SessionId,
        hello: &inbrisk_protocol::request::Hello,
    ) -> Result<ResponsePayload> {
        if hello.protocol_version != PROTOCOL_VERSION {
            return Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                format!(
                    "client speaks protocol {}, runtime speaks {PROTOCOL_VERSION}",
                    hello.protocol_version
                ),
            ));
        }
        if let Some(expected) = hello.expected_epoch {
            if expected != self.ipc.epoch() {
                return Err(InbriskError::new(
                    ErrorCode::ProtocolMismatch,
                    "client is bound to an older runtime epoch",
                )
                .with_hint("reconnect; the runtime restarted"));
            }
        }
        let client_id = (session as u64) << 32 | (hello.nonce & 0xFFFF_FFFF);
        self.ipc.activate(session, client_id)?;
        log_info!(
            "runtime",
            "session {session} connected: {} {} (pid {})",
            hello.client_name,
            hello.client_version,
            hello.client_pid
        );

        let welcome = Welcome {
            session_id: session,
            client_id,
            runtime_epoch: self.ipc.epoch(),
            protocol_version: PROTOCOL_VERSION,
            runtime: self.identity.clone(),
            capabilities: self.effective_capabilities(),
            region_name: self.ipc.name().to_string(),
            heartbeat_ms: 1000,
            state_version: self.engine.state.state_version(),
            generation: self.engine.state.generation(),
        };
        let _ = self.ipc.publish_event(&Event::Log {
            level: "info".into(),
            target: "runtime".into(),
            message: format!("{} connected", hello.client_name),
        });
        Ok(ResponsePayload::Welcome(welcome))
    }

    fn handle_status(&self, request: &StatusRequest) -> Result<ResponsePayload> {
        let state = &self.engine.state;
        let activity = state.activity.snapshot();
        let windows = if request.include_windows {
            self.window_refs(None).unwrap_or_default()
        } else {
            Vec::new()
        };
        let ownership = if request.include_ownership {
            state
                .ownership
                .read()
                .iter()
                .map(|(_, s)| s.clone())
                .collect()
        } else {
            Vec::new()
        };
        let log_tail = if request.include_logs {
            inbrisk_core::logging::recent(request.log_lines.max(1) as usize)
        } else {
            Vec::new()
        };
        let ipc = if request.include_ipc_stats {
            Some(self.ipc.stats())
        } else {
            None
        };
        let counters = state
            .counters
            .snapshot(state.elements.hits(), state.elements.misses());
        let probe = inbrisk_win32::desktop::probe_desktop_context()
            .ok()
            .map(|p| inbrisk_protocol::response::DesktopProbeReport {
                interactive_desktop: p.input_desktop_available,
                physical_input: p.input_desktop_available && p.fresh_thread_can_bind_input_desktop,
                window_enumeration: p.input_desktop_available,
                session_id: p.process_session_id,
                desktop: p.current_desktop.unwrap_or_else(|| "unknown".into()),
                window_station: p.current_window_station.unwrap_or_else(|| "unknown".into()),
            });
        let desktop_broker = self
            .engine
            .broker
            .read()
            .as_ref()
            .map(|b| b.to_status_report());
        let virtual_desktop = {
            let bounds = inbrisk_win32::geometry::virtual_screen_bounds();
            let count = inbrisk_win32::geometry::enumerate_monitors().len() as u32;
            Some(inbrisk_protocol::response::VirtualDesktopReport {
                left: bounds.left,
                top: bounds.top,
                width: bounds.width,
                height: bounds.height,
                monitor_count: count.max(1),
            })
        };
        Ok(ResponsePayload::Status(Box::new(StatusReport {
            identity: self.identity.clone(),
            uptime_ms: state.uptime_ms(),
            state_version: state.state_version(),
            generation: state.generation(),
            activity,
            capabilities: self.effective_capabilities(),
            sessions: self.ipc.client_infos(),
            counters,
            ipc,
            ownership,
            windows,
            log_tail,
            backends: vec![
                Backend::Win32.as_str().into(),
                Backend::Uia.as_str().into(),
                Backend::Input.as_str().into(),
                if self.capabilities.contains(Capabilities::CDP) {
                    Backend::Cdp.as_str().into()
                } else {
                    "cdp (not built in this phase)".into()
                },
            ],
            emergency: state.activity.is_emergency(),
            paused: state.activity.is_paused(),
            desktop_probe: probe,
            desktop_broker,
            virtual_desktop,
        })))
    }

    /// All windows, decorated with ownership, foreground first.
    pub fn window_refs(&self, filter: Option<&Selector>) -> Result<Vec<inbrisk_core::WindowRef>> {
        let raws = self.engine.enumerate_windows()?;
        let mut out: Vec<inbrisk_core::WindowRef> = raws
            .iter()
            .filter(|raw| match filter {
                Some(sel) => crate::engine::window_matches(sel, raw),
                None => true,
            })
            .map(|raw| self.engine.state.decorate(raw))
            .collect();
        out.sort_by_key(|w| (!w.foreground, w.process_name.clone(), w.hwnd.0));
        Ok(out)
    }

    fn handle_observe(&self, request: &ObserveRequest) -> Result<ResponsePayload> {
        let started = Instant::now();
        let state = &self.engine.state;
        let mut notes = Vec::new();

        let filter = request.window.clone();
        let mut windows = self.window_refs(filter.as_ref())?;
        if let Some(needle) = &request.title_contains {
            windows.retain(|w| w.title.to_lowercase().contains(&needle.to_lowercase()));
        }
        if !request.processes.is_empty() {
            windows.retain(|w| {
                request
                    .processes
                    .iter()
                    .any(|p| w.process_name.eq_ignore_ascii_case(p))
            });
        }
        let foreground = windows.iter().find(|w| w.foreground).cloned().or_else(|| {
            inbrisk_win32::window::foreground()
                .and_then(|hwnd| inbrisk_win32::window::describe(hwnd))
                .map(|raw| state.decorate(&raw))
        });

        let target_window = request.hwnd.or_else(|| {
            filter
                .as_ref()
                .and_then(crate::engine::find_window)
                .map(|w| w.hwnd)
        });

        let mut elements = Vec::new();
        let mut focused_element = None;
        let mut backend = "win32".to_string();

        if request.include_tree {
            match target_window.or(foreground.as_ref().map(|w| w.hwnd)) {
                Some(hwnd) if hwnd.0 != 0 => {
                    let max_depth = request.max_depth.unwrap_or(12);
                    let max_elements = request.max_elements.unwrap_or(400);
                    let generation = state.generation();
                    let result = self
                        .engine
                        .uia
                        .reads
                        .submit(Some(self.read_timeout), move |ap| {
                            ap.tree(hwnd, max_depth, max_elements)
                        });
                    match result {
                        Ok(Ok(tree)) => {
                            elements = tree
                                .iter()
                                .map(|d| d.to_element_ref(0, generation, "uia"))
                                .collect();
                            backend = "uia".to_string();
                        }
                        Ok(Err(e)) => {
                            notes.push(format!("UIA tree read failed: {e}"));
                        }
                        Err(e) => {
                            notes.push(format!("UIA read pool refused the tree read: {e}"));
                        }
                    }
                }
                _ => notes.push("include_tree requested but no target window".into()),
            }
        }

        if request.scope != inbrisk_protocol::request::ObserveScope::Windows {
            let focus = self
                .engine
                .uia
                .reads
                .submit(Some(self.read_timeout), |ap| ap.focused_element())
                .unwrap_or(None);
            if let Some(f) = focus {
                focused_element = Some(f.to_element_ref(0, state.generation(), "uia"));
            }
        }

        let monitors = inbrisk_win32::window::monitors()
            .into_iter()
            .map(|m| inbrisk_protocol::response::MonitorInfo {
                id: m.id,
                bounds: m.bounds,
                work_area: m.work_area,
                primary: m.primary,
                dpi: m.dpi,
                name: String::new(),
            })
            .collect();

        Ok(ResponsePayload::Observation(Box::new(Observation {
            state_version: state.state_version(),
            generation: state.generation(),
            captured_at_ms: now_ms(),
            elapsed_us: started.elapsed().as_micros() as u64,
            windows,
            foreground,
            focused_element,
            elements,
            monitors,
            cursor: inbrisk_win32::window::cursor_pos(),
            activity: state.activity.snapshot(),
            backend,
            notes,
        })))
    }

    fn handle_find(
        &self,
        session: SessionId,
        request: &inbrisk_protocol::request::FindRequest,
    ) -> Result<ResponsePayload> {
        let started = Instant::now();
        let matches = self.engine.find_matches(
            session,
            &request.selector,
            request.all,
            request.limit.unwrap_or(64),
            None,
        )?;
        Ok(ResponsePayload::Found(FindResult {
            selector: request.selector.describe(),
            matches: matches.elements,
            scanned: matches.scanned,
            truncated: matches.truncated,
            backend: matches.backend,
            elapsed_us: started.elapsed().as_micros() as u64,
            state_version: self.engine.state.state_version(),
            generation: self.engine.state.generation(),
            candidates: matches.candidates,
        }))
    }

    fn handle_read(&self, session: SessionId, request: &ReadRequest) -> Result<ResponsePayload> {
        let started = Instant::now();
        let state = &self.engine.state;

        // Path 1: a cached handle.
        if let Some(id) = request.element_id {
            let handle = state.elements.get(id)?;
            if let Some(expected) = request.generation {
                if expected != handle.element.generation {
                    return Err(InbriskError::new(
                        ErrorCode::StaleState,
                        format!(
                            "handle {id} was minted in generation {}",
                            handle.element.generation
                        ),
                    ));
                }
            }
            if handle.element.backend.eq_ignore_ascii_case("cdp") {
                let element = self.engine.refresh_cdp(&handle.element)?;
                let properties = read_properties(&element, request);
                return Ok(ResponsePayload::Read(ReadResult {
                    element,
                    properties,
                    backend: "cdp".into(),
                    cached: true,
                    elapsed_us: started.elapsed().as_micros() as u64,
                    state_version: state.state_version(),
                }));
            }
            let properties = read_properties(&handle.element, request);
            return Ok(ResponsePayload::Read(ReadResult {
                element: handle.element.clone(),
                properties,
                backend: handle.element.backend.clone(),
                cached: true,
                elapsed_us: started.elapsed().as_micros() as u64,
                state_version: state.state_version(),
            }));
        }

        // Path 2: resolve fresh.
        let target = request
            .target
            .clone()
            .ok_or_else(|| InbriskError::invalid_plan("read needs element_id or target"))?;
        let selector = match &target {
            inbrisk_protocol::action::TargetRef::Selector(s) => (**s).clone(),
            inbrisk_protocol::action::TargetRef::Reference(_) => {
                return Err(InbriskError::invalid_plan(
                    "read by reference needs the plan's output table; use element_id",
                ))
            }
            inbrisk_protocol::action::TargetRef::Point(_) => {
                return Err(InbriskError::invalid_plan(
                    "read cannot target screen coordinates; use element_id or selector",
                ))
            }
        };
        if selector.is_empty() {
            return Err(InbriskError::invalid_plan("read selector is empty"));
        }
        let window = self.engine.window_for_selector(
            &selector,
            &PlanContext::new(session, 0, Arc::new(AtomicBool::new(false))),
        )?;
        let proc =
            inbrisk_win32::process::process_name(inbrisk_win32::window::process_id_of(window));
        if crate::engine::content_requires_cdp(&selector, proc.as_deref()) {
            let found = self
                .engine
                .find_matches(session, &selector, false, 1, Some(window))?;
            let element =
                found.elements.into_iter().next().ok_or_else(|| {
                    InbriskError::not_found("no CDP page matched the read selector")
                })?;
            let properties = read_properties(&element, request);
            return Ok(ResponsePayload::Read(ReadResult {
                element,
                properties,
                backend: "cdp".into(),
                cached: false,
                elapsed_us: started.elapsed().as_micros() as u64,
                state_version: state.state_version(),
            }));
        }
        let generation = state.generation();
        let sel = selector.clone();
        let data = self
            .engine
            .uia
            .reads
            .submit(Some(self.read_timeout), move |ap| ap.find_one(window, &sel))??;
        let element = data.to_element_ref(0, generation, "uia");
        let properties = read_properties(&element, request);
        Ok(ResponsePayload::Read(ReadResult {
            element,
            properties,
            backend: "uia".into(),
            cached: false,
            elapsed_us: started.elapsed().as_micros() as u64,
            state_version: state.state_version(),
        }))
    }

    fn handle_act(
        &self,
        session: SessionId,
        request_id: u64,
        request: &ActRequest,
    ) -> Result<ResponsePayload> {
        let state = &self.engine.state;
        if let Some(expect) = &request.expect {
            inbrisk_core::StateGuard::check(expect, &state.world)?;
        }
        let plan_id = self.engine.cancels.next_plan_id();
        let cancel = self.engine.cancels.register(session, plan_id);
        let mut ctx = PlanContext::new(session, plan_id, cancel);
        ctx.deadline = request
            .timeout_ms
            .map(|ms| Instant::now() + Duration::from_millis(ms));
        let outcome = self.engine.execute_step(&request.action, &mut ctx);
        self.engine.cancels.finish(session, plan_id);
        let _ = request_id;
        match outcome.status {
            inbrisk_protocol::response::StepStatus::Ok => {
                Ok(ResponsePayload::Acted(ActionOutcome {
                    action: outcome.action,
                    backend: outcome.backend,
                    element: outcome.element,
                    verified: outcome.verified,
                    detail: None,
                    state_version: state.state_version(),
                    generation: state.generation(),
                    elapsed_us: outcome.elapsed_us,
                }))
            }
            _ => Err(outcome
                .error
                .unwrap_or_else(|| InbriskError::internal("action failed without an error"))),
        }
    }

    fn handle_run(
        &self,
        session: SessionId,
        request_id: u64,
        request: &RunRequest,
    ) -> Result<ResponsePayload> {
        let plan_id = self.engine.cancels.next_plan_id();
        let cancel = self.engine.cancels.register(session, plan_id);
        let mut ctx = PlanContext::new(session, plan_id, Arc::clone(&cancel));
        ctx = ctx.with_deadline(request.timeout_ms.map(Duration::from_millis));
        let _ = request_id;
        let outcome: RunOutcome =
            self.engine
                .run_plan(&request.plan, &mut ctx, request.mode, request.on_error);
        self.engine.cancels.finish(session, plan_id);
        let client_name = self.ipc.session_name(session).unwrap_or_default();
        if !client_name.eq_ignore_ascii_case("inbrisk-mcp") {
            self.engine
                .state
                .counters
                .native_run_plans
                .fetch_add(1, Ordering::Relaxed);
        }
        Ok(ResponsePayload::Ran(Box::new(outcome)))
    }

    fn handle_wait(
        &self,
        session: SessionId,
        request_id: u64,
        request: &WaitRequest,
    ) -> Result<ResponsePayload> {
        let plan_id = self.engine.cancels.next_plan_id();
        let cancel = self.engine.cancels.register(session, plan_id);
        let mut ctx = PlanContext::new(session, plan_id, Arc::clone(&cancel));
        ctx.deadline = request
            .timeout_ms
            .map(|ms| Instant::now() + Duration::from_millis(ms));
        let _ = request_id;
        let step = inbrisk_protocol::action::Step::Wait(request.wait.clone());
        let outcome = self.engine.execute_step(&step, &mut ctx);
        self.engine.cancels.finish(session, plan_id);
        match outcome.status {
            inbrisk_protocol::response::StepStatus::Ok => {
                let output = outcome.output.unwrap_or(Value::Null);
                let satisfied = output
                    .get("satisfied")
                    .and_then(|v| v.as_bool())
                    .unwrap_or(false);
                let source = output
                    .get("wakeSource")
                    .and_then(|v| v.as_str())
                    .unwrap_or("unknown");
                if !satisfied {
                    return Err(InbriskError::new(
                        ErrorCode::Timeout,
                        format!("wait not satisfied ({source})"),
                    )
                    .with_hint("increase timeout_ms or re-observe"));
                }
                Ok(ResponsePayload::Waited(WaitOutcome {
                    satisfied,
                    waited_ms: output.get("waitedMs").and_then(|v| v.as_u64()).unwrap_or(0),
                    state_version: self.engine.state.state_version(),
                    generation: self.engine.state.generation(),
                    detail: None,
                    wake_source: source.to_string(),
                }))
            }
            _ => Err(outcome
                .error
                .unwrap_or_else(|| InbriskError::internal("wait failed"))),
        }
    }

    fn handle_cancel(&self, session: SessionId, request: &CancelRequest) -> Cancelled {
        use std::sync::atomic::Ordering as O;
        let _ = O::Relaxed;
        let mut cancelled_plans = 0u32;
        if request.all || (request.operation_id.is_none() && request.plan_id.is_none()) {
            cancelled_plans = self.engine.cancels.cancel_session(session);
        } else if let Some(plan_id) = request.plan_id {
            if self.engine.cancels.cancel_plan(session, plan_id) {
                cancelled_plans = 1;
            }
        } else if let Some(op) = request.operation_id {
            if self.engine.cancels.cancel_plan(session, op) {
                cancelled_plans = 1;
            }
        }
        // Physical input held by a cancelled session must be released.
        self.engine.input.release_session(session);
        let _: RunningOperation = (session, request.operation_id.unwrap_or(0));
        Cancelled {
            cancelled_operations: cancelled_plans,
            cancelled_plans,
        }
    }

    fn handle_subscribe(&self, request: &SubscribeRequest) -> Subscribed {
        let kinds = if request.kinds.is_empty() {
            vec![
                "activity".into(),
                "window_opened".into(),
                "window_closed".into(),
                "window_focus_changed".into(),
                "state_changed".into(),
                "plan_progress".into(),
                "plan_finished".into(),
                "emergency".into(),
                "log".into(),
            ]
        } else {
            request.kinds.clone()
        };
        Subscribed {
            subscription_id: now_ms(),
            kinds,
        }
    }
}

fn now_ns() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(0)
}

/// Property bag for `read`.
fn read_properties(
    element: &inbrisk_core::ElementRef,
    request: &ReadRequest,
) -> std::collections::BTreeMap<String, Value> {
    let mut map = std::collections::BTreeMap::new();
    let wanted: Vec<String> = if request.properties.is_empty() {
        vec![
            "name".into(),
            "role".into(),
            "automation_id".into(),
            "class_name".into(),
            "bounds".into(),
            "enabled".into(),
            "offscreen".into(),
            "process_id".into(),
            "backend".into(),
        ]
    } else {
        request.properties.clone()
    };
    for key in wanted {
        let value = match key.as_str() {
            "name" => json!(element.name),
            "role" => json!(element.role),
            "automation_id" | "automationId" => json!(element.automation_id),
            "class_name" | "className" => json!(element.class_name),
            "bounds" => json!({
                "x": element.bounds.x,
                "y": element.bounds.y,
                "width": element.bounds.width,
                "height": element.bounds.height,
            }),
            "enabled" => json!(element.enabled),
            "offscreen" => json!(element.offscreen),
            "process_id" | "processId" => json!(element.process_id),
            "hwnd" => json!(element.hwnd.0),
            "backend" => json!(element.backend),
            "runtime_id" | "runtimeId" => json!(element.runtime_id),
            "generation" => json!(element.generation),
            // Unknown property: report it explicitly instead of pretending.
            _ => Value::Null,
        };
        map.insert(key, value);
    }
    map
}

/// Selector used when the caller only wants "everything".
pub fn everything() -> Selector {
    Selector {
        policy: MatchPolicy::Best,
        ..Default::default()
    }
}
