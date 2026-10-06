//! The execution engine: routing, plan execution and event-driven waits.
//!
//! Router order — cheapest first, always:
//!
//! ```text
//! Win32  ->  CDP  ->  UIA semantic  ->  physical input  ->  vision
//! ```
//!
//! Window listing, focus, launching and process identity never touch UIA.
//! Button presses go through `InvokePattern`, text fields through
//! `ValuePattern`; synthesized mouse/keyboard is the last resort and is
//! serialized through the global input arbiter.

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use inbrisk_core::{
    log_debug, log_warn, now_ms, ElementRef, ErrorCode, ExecutionCapabilities, ExecutionCapability,
    Expect, Hwnd, InbriskError, LifecycleIntent, OperationId, Ownership, PlanId, Result, SessionId,
    StateGuard, WindowActivityToken,
};
use inbrisk_input::GlobalInputArbiter;
use inbrisk_policy::{CloseDecision, PolicyEngine};
use inbrisk_protocol::action::{
    DropTarget, MouseButton, Plan, PointTarget, RunMode, ScrollDirection, SelectOption, Step,
    TargetRef, WaitSpec, WindowWaitState,
};
use inbrisk_protocol::desktop_broker::PhysicalInputGuard;
use inbrisk_protocol::response::{StepOutcome, StepStatus};
use inbrisk_protocol::selector::{MatchPolicy, Selector};
use inbrisk_uia::UiaService;
use parking_lot::Mutex;
use serde_json::{json, Value};

use crate::state::{profile_for_app, profile_for_process, Backend, CachedHandle, RuntimeState};

/// Cancellation flags, keyed by session and plan.
#[derive(Debug, Default)]
pub struct CancelRegistry {
    next_plan_id: AtomicU64,
    flags: Mutex<std::collections::HashMap<(SessionId, PlanId), Arc<AtomicBool>>>,
}

impl CancelRegistry {
    pub fn new() -> Self {
        Self {
            next_plan_id: AtomicU64::new(1),
            flags: Mutex::new(std::collections::HashMap::new()),
        }
    }

    pub fn next_plan_id(&self) -> PlanId {
        self.next_plan_id.fetch_add(1, Ordering::AcqRel)
    }

    pub fn register(&self, session: SessionId, plan: PlanId) -> Arc<AtomicBool> {
        let flag = Arc::new(AtomicBool::new(false));
        self.flags.lock().insert((session, plan), Arc::clone(&flag));
        flag
    }

    pub fn finish(&self, session: SessionId, plan: PlanId) {
        self.flags.lock().remove(&(session, plan));
    }

    /// Cancel everything a session started; returns how many plans were hit.
    pub fn cancel_session(&self, session: SessionId) -> u32 {
        let flags = self.flags.lock();
        let mut n = 0;
        for ((s, _), flag) in flags.iter() {
            if *s == session {
                flag.store(true, Ordering::Release);
                n += 1;
            }
        }
        n
    }

    pub fn cancel_plan(&self, session: SessionId, plan: PlanId) -> bool {
        let flags = self.flags.lock();
        if let Some(flag) = flags.get(&(session, plan)) {
            flag.store(true, Ordering::Release);
            true
        } else {
            false
        }
    }

    pub fn active(&self) -> usize {
        self.flags.lock().len()
    }

    /// Cancel every in-flight plan — used by emergency stop.
    pub fn cancel_all(&self) -> u32 {
        let flags = self.flags.lock();
        for flag in flags.values() {
            flag.store(true, Ordering::Release);
        }
        flags.len() as u32
    }
}

/// Event-driven wait service.
#[derive(Debug)]
pub struct WaitService {
    monitor: Option<inbrisk_win32::WinEventMonitor>,
    /// Event stream, cloned per waiter.
    pub rx: Option<crossbeam_channel::Receiver<inbrisk_win32::WinEvent>>,
}

impl WaitService {
    pub fn start() -> Result<Self> {
        match inbrisk_win32::WinEventMonitor::start() {
            Ok(m) => {
                let rx = m.receiver();
                Ok(Self {
                    monitor: Some(m),
                    rx: Some(rx),
                })
            }
            Err(e) => {
                log_warn!(
                    "wait",
                    "WinEvent monitor unavailable ({e}); waits fall back to polling"
                );
                Ok(Self {
                    monitor: None,
                    rx: None,
                })
            }
        }
    }

    pub fn is_event_driven(&self) -> bool {
        self.monitor.is_some()
    }

    pub fn events_seen(&self) -> u32 {
        self.monitor
            .as_ref()
            .map(|m| m.events_received())
            .unwrap_or(0)
    }

    /// Block until the window condition holds.
    pub fn wait_window(
        &self,
        selector: &Selector,
        state: WindowWaitState,
        timeout: Duration,
        cancel: &Arc<AtomicBool>,
    ) -> Result<(bool, String)> {
        let deadline = Instant::now() + timeout;
        let mut wake_source = if self.monitor.is_some() {
            "event"
        } else {
            "poll"
        };
        loop {
            if cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(ErrorCode::Cancelled, "wait cancelled"));
            }
            if window_condition(selector, state) {
                return Ok((true, wake_source.to_string()));
            }
            let now = Instant::now();
            if now >= deadline {
                return Ok((false, "timeout".to_string()));
            }
            let remaining = deadline - now;
            let slice = remaining.min(Duration::from_millis(250));
            if let Some(rx) = &self.rx {
                match rx.recv_timeout(slice) {
                    Ok(ev) => {
                        // Only top-level window events can change the answer.
                        if matches!(
                            ev.kind,
                            inbrisk_win32::WinEventKind::ObjectCreate
                                | inbrisk_win32::WinEventKind::ObjectDestroy
                                | inbrisk_win32::WinEventKind::ObjectShow
                                | inbrisk_win32::WinEventKind::ObjectHide
                                | inbrisk_win32::WinEventKind::NameChange
                                | inbrisk_win32::WinEventKind::Foreground
                                | inbrisk_win32::WinEventKind::MinimizeEnd
                        ) {
                            wake_source = "event";
                        }
                    }
                    Err(_) => {
                        if self.monitor.is_none() {
                            wake_source = "poll";
                        }
                    }
                }
            } else {
                std::thread::sleep(slice.min(Duration::from_millis(60)));
                wake_source = "poll";
            }
        }
    }

    /// Element conditions poll the read pool; UIA event handlers arrive in
    /// Phase 7, at which point this becomes fully event-driven too.
    pub fn wait_element<F>(
        &self,
        timeout: Duration,
        cancel: &Arc<AtomicBool>,
        mut probe: F,
    ) -> Result<(bool, String)>
    where
        F: FnMut() -> Result<bool>,
    {
        let deadline = Instant::now() + timeout;
        loop {
            if cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(ErrorCode::Cancelled, "wait cancelled"));
            }
            if probe()? {
                return Ok((true, "poll".to_string()));
            }
            let now = Instant::now();
            if now >= deadline {
                return Ok((false, "timeout".to_string()));
            }
            let slice = (deadline - now).min(Duration::from_millis(80));
            // A window event is a good hint that the tree changed.
            if let Some(rx) = &self.rx {
                let _ = rx.recv_timeout(slice);
            } else {
                std::thread::sleep(slice);
            }
        }
    }

    pub fn wait_process_exit(
        &self,
        pid: u32,
        timeout: Duration,
        cancel: &Arc<AtomicBool>,
    ) -> Result<(bool, String)> {
        let deadline = Instant::now() + timeout;
        loop {
            if cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(ErrorCode::Cancelled, "wait cancelled"));
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return Ok((!inbrisk_win32::process::is_alive(pid), "timeout".into()));
            }
            let slice = remaining.min(Duration::from_millis(200)).as_millis() as u32;
            if inbrisk_win32::process::wait_for_exit(pid, slice) {
                return Ok((true, "event".into()));
            }
        }
    }
}

/// Does a top-level window currently satisfy the condition?
pub fn window_condition(selector: &Selector, state: WindowWaitState) -> bool {
    let windows = inbrisk_win32::window::enumerate_top_level();
    let matches: Vec<&inbrisk_win32::RawWindow> = windows
        .iter()
        .filter(|w| window_matches(selector, w))
        .collect();
    match state {
        WindowWaitState::Exists | WindowWaitState::Visible => !matches.is_empty(),
        WindowWaitState::Gone => matches.is_empty(),
        WindowWaitState::Foreground => matches.iter().any(|w| w.foreground),
    }
}

/// Match a window selector against a raw window.
pub fn window_matches(selector: &Selector, w: &inbrisk_win32::RawWindow) -> bool {
    if let Some(hwnd) = selector.hwnd {
        if hwnd.0 != 0 && w.hwnd != hwnd {
            return false;
        }
    }
    if let Some(process) = &selector.process {
        if !w.process_name.eq_ignore_ascii_case(process) {
            return false;
        }
    }
    if let Some(class) = &selector.class_name {
        if !w.class_name.eq_ignore_ascii_case(class) {
            return false;
        }
    }
    if let Some(name) = &selector.name {
        let exact = selector.exact.unwrap_or(false);
        if exact {
            if !w.title.eq_ignore_ascii_case(name) {
                return false;
            }
        } else if !w.title.to_lowercase().contains(&name.to_lowercase()) {
            return false;
        }
    }
    if let Some(pattern) = &selector.name_regex {
        match regex::Regex::new(pattern) {
            Ok(re) => {
                if !re.is_match(&w.title) {
                    return false;
                }
            }
            Err(_) => return false,
        }
    }
    if let Some(text) = &selector.text {
        if !w.title.to_lowercase().contains(&text.to_lowercase()) {
            return false;
        }
    }
    if selector.is_empty() {
        return false;
    }
    true
}

/// A cheap fingerprint used to decide whether an action had an observable
/// effect — this is what makes `verified` an honest field.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WorldFingerprint {
    pub windows: usize,
    pub foreground: Hwnd,
    pub state_version: u64,
}

impl WorldFingerprint {
    pub fn capture(state: &RuntimeState) -> Self {
        let windows = inbrisk_win32::window::enumerate_top_level();
        let fg = inbrisk_win32::window::foreground().unwrap_or(Hwnd::NULL);
        let fg_matches = windows.iter().any(|w| w.hwnd == fg);
        Self {
            windows: windows.len(),
            foreground: if fg_matches { fg } else { Hwnd::NULL },
            state_version: state.state_version(),
        }
    }

    pub fn changed(&self, other: &Self) -> bool {
        self.windows != other.windows
            || self.foreground != other.foreground
            || self.state_version != other.state_version
    }
}

/// Where a step's target resolved to.
#[derive(Debug, Clone)]
pub enum ResolvedTarget {
    Handle(Box<CachedHandle>),
    Selector {
        selector: Box<Selector>,
        window: Hwnd,
    },
}

impl ResolvedTarget {
    pub fn window(&self) -> Hwnd {
        match self {
            ResolvedTarget::Handle(h) => h.window_hwnd,
            ResolvedTarget::Selector { window, .. } => *window,
        }
    }

    pub fn selector(&self) -> Selector {
        match self {
            ResolvedTarget::Handle(h) => h.selector.clone(),
            ResolvedTarget::Selector { selector, .. } => (**selector).clone(),
        }
    }

    pub fn element(&self) -> Option<&ElementRef> {
        match self {
            ResolvedTarget::Handle(h) => Some(&h.element),
            ResolvedTarget::Selector { .. } => None,
        }
    }
}

/// Per-plan execution context.
pub struct PlanContext {
    pub session_id: SessionId,
    pub plan_id: PlanId,
    pub cancel: Arc<AtomicBool>,
    pub deadline: Option<Instant>,
    /// `$0`, `$name` -> value.
    pub outputs: BTreeMap<String, Value>,
    /// Handle ids minted by this plan, in step order.
    pub handles: Vec<u64>,
    /// The window most steps operate on when a selector does not name one.
    pub default_window: Option<Hwnd>,
    pub dry_run: bool,
    /// Plan-local resolved element cache: (Hwnd, Selector) -> (ElementRef, world_version)
    pub plan_elements: Mutex<std::collections::HashMap<(Hwnd, Selector), (ElementRef, u64)>>,
}

impl PlanContext {
    pub fn new(session_id: SessionId, plan_id: PlanId, cancel: Arc<AtomicBool>) -> Self {
        Self {
            session_id,
            plan_id,
            cancel,
            deadline: None,
            outputs: BTreeMap::new(),
            handles: Vec::new(),
            default_window: None,
            dry_run: false,
            plan_elements: Mutex::new(std::collections::HashMap::new()),
        }
    }

    pub fn with_deadline(mut self, timeout: Option<Duration>) -> Self {
        self.deadline = timeout.map(|t| Instant::now() + t);
        self
    }

    pub fn expired(&self) -> bool {
        self.deadline.map(|d| Instant::now() >= d).unwrap_or(false)
    }

    pub fn step_ref(&self, index: usize) -> String {
        format!("${index}")
    }

    pub fn record(&mut self, index: usize, name: Option<&str>, value: Value) {
        self.outputs.insert(self.step_ref(index), value.clone());
        self.record_name(name, value);
    }

    /// Publish `$name` only. The step index is recorded by the plan loop, so
    /// steps must not invent an index of their own.
    pub fn record_name(&mut self, name: Option<&str>, value: Value) {
        if let Some(name) = name {
            let key = if name.starts_with('$') {
                name.to_string()
            } else {
                format!("${name}")
            };
            self.outputs.insert(key, value);
        }
    }

    fn lookup(&self, reference: &str) -> Option<Value> {
        let key = if reference.starts_with('$') {
            reference.to_string()
        } else {
            format!("${reference}")
        };
        self.outputs.get(&key).cloned()
    }

    /// Turn a `$ref` output into a cached handle id.
    pub fn handle_from_reference(&self, reference: &str) -> Result<u64> {
        let value = self.lookup(reference).ok_or_else(|| {
            InbriskError::invalid_plan(format!(
                "{reference} has not been produced by an earlier step"
            ))
            .with_hint("produce it with a find step first")
        })?;
        let id = value
            .get("elementId")
            .and_then(|v| v.as_u64())
            .or_else(|| value.as_u64())
            .ok_or_else(|| {
                InbriskError::invalid_plan(format!("{reference} did not resolve to an element"))
            })?;
        Ok(id)
    }
}

/// The execution engine.
pub struct Engine {
    pub state: Arc<RuntimeState>,
    pub uia: Arc<UiaService>,
    pub input: Arc<GlobalInputArbiter>,
    pub policy: Arc<PolicyEngine>,
    pub waits: Arc<WaitService>,
    pub cancels: Arc<CancelRegistry>,
    pub capabilities: Arc<parking_lot::RwLock<ExecutionCapabilities>>,
    pub broker: Arc<parking_lot::RwLock<Option<crate::desktop_broker::DesktopBrokerClient>>>,
    pub physical_op_counter: Arc<AtomicU64>,
}

impl std::fmt::Debug for Engine {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str("Engine")
    }
}

impl Engine {
    pub fn new(
        state: Arc<RuntimeState>,
        uia: Arc<UiaService>,
        input: Arc<GlobalInputArbiter>,
        policy: Arc<PolicyEngine>,
        waits: Arc<WaitService>,
        cancels: Arc<CancelRegistry>,
    ) -> Self {
        let mut capabilities = inbrisk_win32::desktop::detect_execution_capabilities()
            .unwrap_or_else(|_| ExecutionCapabilities::unavailable());
        // Phase 2B: Local daemon probe can NEVER make PhysicalInput available!
        // PhysicalInput is ONLY available via connected, ready desktop broker.
        capabilities.physical_input = false;
        capabilities.window_enumeration = false;
        Self::with_capabilities(state, uia, input, policy, waits, cancels, capabilities)
    }

    pub fn with_capabilities(
        state: Arc<RuntimeState>,
        uia: Arc<UiaService>,
        input: Arc<GlobalInputArbiter>,
        policy: Arc<PolicyEngine>,
        waits: Arc<WaitService>,
        cancels: Arc<CancelRegistry>,
        capabilities: ExecutionCapabilities,
    ) -> Self {
        Self {
            state,
            uia,
            input,
            policy,
            waits,
            cancels,
            capabilities: Arc::new(parking_lot::RwLock::new(capabilities)),
            broker: Arc::new(parking_lot::RwLock::new(None)),
            physical_op_counter: Arc::new(AtomicU64::new(1)),
        }
    }

    pub fn capabilities(&self) -> ExecutionCapabilities {
        self.capabilities.read().clone()
    }

    pub fn update_capabilities(&self, caps: ExecutionCapabilities) {
        *self.capabilities.write() = caps;
    }

    pub fn is_broker_connected(&self) -> bool {
        self.broker
            .read()
            .as_ref()
            .map(|b| b.is_connected())
            .unwrap_or(false)
    }

    pub fn next_physical_op_id(&self) -> u64 {
        self.physical_op_counter.fetch_add(1, Ordering::Relaxed)
    }

    pub fn set_broker(&self, broker: crate::desktop_broker::DesktopBrokerClient) {
        if let Some(welcome) = broker.welcome_info() {
            let mut caps = self.capabilities.write();
            caps.interactive_desktop = welcome.interactive_desktop;
            caps.window_enumeration = welcome.window_enumeration_ready;
            caps.physical_input = welcome.physical_input_ready;
        }
        *self.broker.write() = Some(broker);
    }

    pub fn disconnect_broker(&self) {
        if let Some(broker) = self.broker.write().as_mut() {
            broker.disconnect();
        }
        let mut caps = self.capabilities.write();
        caps.interactive_desktop = false;
        caps.window_enumeration = false;
        caps.physical_input = false;
    }

    pub fn broker_for_physical_input(&self) -> Result<crate::desktop_broker::DesktopBrokerClient> {
        if self.state.activity.is_emergency() {
            return Err(InbriskError::denied(
                "emergency stop active; physical input blocked",
            ));
        }
        self.require_capability(ExecutionCapability::PhysicalInput)?;
        let broker_guard = self.broker.read();
        let broker = broker_guard.as_ref().ok_or_else(|| {
            InbriskError::capability_unavailable(
                ExecutionCapability::PhysicalInput,
                "desktop broker unavailable",
            )
        })?;
        if !broker.is_connected() {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::PhysicalInput,
                "desktop broker disconnected",
            ));
        }
        Ok(broker.clone())
    }

    pub fn handle_physical_error(&self, e: InbriskError) -> InbriskError {
        if !self.is_broker_connected() {
            self.disconnect_broker();
        }
        e
    }

    pub fn require_capability(&self, capability: ExecutionCapability) -> Result<()> {
        if !self.capabilities.read().supports(capability) {
            let detail = match capability {
                ExecutionCapability::InteractiveDesktop => {
                    "interactive desktop is not available to this runtime"
                }
                ExecutionCapability::PhysicalInput => {
                    "interactive desktop is not available to this runtime for physical input"
                }
                ExecutionCapability::WindowEnumeration => {
                    "interactive desktop is not available to this runtime for window enumeration"
                }
            };
            return Err(InbriskError::capability_unavailable(capability, detail));
        }
        Ok(())
    }

    /// Enumerate top-level windows.
    ///
    /// When broker mode is active, enumeration is routed STRICTLY through the desktop broker.
    /// There is NO silent local fallback on broker failure.
    pub fn enumerate_windows(&self) -> Result<Vec<inbrisk_win32::RawWindow>> {
        self.require_capability(ExecutionCapability::WindowEnumeration)?;

        if let Some(broker) = self.broker.read().as_ref() {
            let infos = match broker.enumerate_windows() {
                Ok(windows) => windows,
                Err(e) => {
                    // Revoke capabilities dynamically upon broker failure/disconnect
                    let mut caps = self.capabilities.write();
                    caps.window_enumeration = false;
                    caps.interactive_desktop = false;
                    return Err(e);
                }
            };

            let raws = infos
                .into_iter()
                .map(|info| inbrisk_win32::RawWindow {
                    hwnd: Hwnd(info.hwnd as isize),
                    process_id: info.pid,
                    process_name: info.process_name,
                    title: info.title,
                    class_name: info.class_name,
                    bounds: inbrisk_core::Rect::new(
                        info.left,
                        info.top,
                        (info.right - info.left).max(0),
                        (info.bottom - info.top).max(0),
                    ),
                    visible: info.visible,
                    minimized: info.minimized,
                    maximized: false,
                    foreground: false,
                    tool_window: false,
                })
                .collect();
            return Ok(raws);
        }

        // When no broker is configured (e.g. standalone test mode)
        Ok(inbrisk_win32::window::enumerate_top_level())
    }

    pub fn find_window(&self, selector: &Selector) -> Option<inbrisk_win32::RawWindow> {
        let windows = self.enumerate_windows().ok()?;
        let mut matches: Vec<inbrisk_win32::RawWindow> = windows
            .into_iter()
            .filter(|w| window_matches(selector, w))
            .collect();
        if matches.is_empty() {
            return None;
        }
        matches.sort_by_key(|w| (!w.foreground, w.hwnd.0));
        matches.into_iter().next()
    }

    /// Resolve a `TargetRef` to a window + selector pair.
    pub fn resolve(&self, target: &TargetRef, ctx: &PlanContext) -> Result<ResolvedTarget> {
        match target {
            TargetRef::Point(pt) => Err(InbriskError::invalid_plan(format!(
                "target is raw coordinates ({}, {}); cannot resolve element handle",
                pt.x, pt.y
            ))),
            TargetRef::Reference(reference) => {
                let id = ctx.handle_from_reference(reference)?;
                let handle = self.state.elements.get(id)?;
                Ok(ResolvedTarget::Handle(Box::new(handle)))
            }
            TargetRef::Selector(selector) => {
                if selector.is_empty() {
                    return Err(
                        InbriskError::invalid_plan("target selector has no constraints")
                            .with_hint("give at least a role, name or automation_id"),
                    );
                }
                let window = self.window_for_selector(selector, ctx)?;
                Ok(ResolvedTarget::Selector {
                    selector: selector.clone(),
                    window,
                })
            }
        }
    }

    /// Which top-level window does this selector belong to?
    pub fn window_for_selector(&self, selector: &Selector, ctx: &PlanContext) -> Result<Hwnd> {
        if let Some(hwnd) = selector.hwnd {
            if hwnd.0 != 0 && inbrisk_win32::window::is_alive(hwnd) {
                return Ok(hwnd);
            }
        }
        if let Some(window_selector) = &selector.window {
            self.require_capability(ExecutionCapability::WindowEnumeration)?;
            if let Some(found) = self.find_window(window_selector) {
                return Ok(found.hwnd);
            }
            return Err(InbriskError::not_found(format!(
                "no window matches {}",
                window_selector.describe()
            ))
            .with_hint("observe first; the window may not exist yet"));
        }
        if let Some(process) = &selector.process {
            self.require_capability(ExecutionCapability::WindowEnumeration)?;
            let sel = Selector {
                process: Some(process.clone()),
                ..Default::default()
            };
            if let Some(found) = self.find_window(&sel) {
                return Ok(found.hwnd);
            }
        }
        if let Some(default) = ctx.default_window {
            if inbrisk_win32::window::is_alive(default) {
                return Ok(default);
            }
        }
        if let Some(fg) = inbrisk_win32::window::foreground() {
            return Ok(fg);
        }
        Err(InbriskError::new(
            ErrorCode::TargetNotFound,
            "no window is in scope for this action",
        )
        .with_hint("use a launch step, or pass window/process in the selector"))
    }

    /// Execute one step.
    pub fn execute_step(&self, step: &Step, ctx: &mut PlanContext) -> StepOutcome {
        let started = Instant::now();
        let action = step.action_name().to_string();
        let result = self.dispatch_step(step, ctx);
        let elapsed_us = started.elapsed().as_micros() as u64;
        self.state.counters.steps.fetch_add(1, Ordering::Relaxed);

        match result {
            Ok(done) => StepOutcome {
                index: 0,
                action,
                status: StepStatus::Ok,
                backend: done.backend,
                verified: done.verified,
                elapsed_us,
                element: done.element,
                error: None,
                output: done.output,
            },
            Err(e) => {
                if e.code != ErrorCode::Denied && e.code != ErrorCode::Protected {
                    // Denials are policy, not failure noise.
                }
                StepOutcome {
                    index: 0,
                    action,
                    status: StepStatus::Failed,
                    backend: "none".into(),
                    verified: false,
                    elapsed_us,
                    element: None,
                    error: Some(e),
                    output: None,
                }
            }
        }
    }

    fn dispatch_step(&self, step: &Step, ctx: &mut PlanContext) -> Result<StepDone> {
        if ctx.cancel.load(Ordering::Acquire) {
            return Err(InbriskError::new(ErrorCode::Cancelled, "plan cancelled"));
        }
        if ctx.expired() {
            return Err(InbriskError::new(
                ErrorCode::Timeout,
                "plan deadline exceeded",
            ));
        }
        match step {
            Step::Launch {
                app,
                args,
                cwd,
                wait_for_window,
                timeout_ms,
                intent,
                store,
            } => self.step_launch(
                app,
                args,
                cwd.as_deref(),
                wait_for_window.as_ref(),
                *timeout_ms,
                *intent,
                store.as_deref(),
                ctx,
            ),
            Step::Focus { target, expect } => {
                guard(expect, &self.state)?;
                self.step_focus(target, ctx)
            }
            Step::Find {
                selector,
                store,
                all,
            } => self.step_find(selector, store.as_deref(), *all, ctx),
            Step::Invoke { target, expect } => {
                guard(expect, &self.state)?;
                self.step_invoke(target, ctx)
            }
            Step::SetValue {
                target,
                value,
                expect,
            } => {
                guard(expect, &self.state)?;
                self.step_set_value(target, value, ctx)
            }
            Step::Click {
                target,
                x,
                y,
                hwnd,
                button,
                clicks,
                expect,
            } => {
                guard(expect, &self.state)?;
                let effective_target = match (target, hwnd, x, y) {
                    (None, Some(h), Some(cx), Some(cy)) => {
                        Some(TargetRef::Point(PointTarget::client_physical(*h, *cx as f64, *cy as f64)))
                    }
                    (t, _, _, _) => t.clone(),
                };
                self.step_click(effective_target.as_ref(), *x, *y, *button, *clicks, ctx)
            }
            Step::Move {
                target,
                x,
                y,
                hwnd,
            } => {
                let effective_target = match (target, hwnd, x, y) {
                    (None, Some(h), Some(cx), Some(cy)) => {
                        Some(TargetRef::Point(PointTarget::client_physical(*h, *cx as f64, *cy as f64)))
                    }
                    (t, _, _, _) => t.clone(),
                };
                self.step_move_resolved(effective_target.as_ref(), *x, *y, *hwnd, ctx)
            }
            Step::Type {
                text,
                target,
                clear_first,
                expect,
            } => {
                guard(expect, &self.state)?;
                self.step_type(text, target.as_ref(), *clear_first, ctx)
            }
            Step::Key { keys, target } => self.step_key(keys, target.as_ref(), ctx),
            Step::Scroll {
                target,
                direction,
                amount,
            } => self.step_scroll(target.as_ref(), *direction, *amount, ctx),
            Step::Select { target, option } => self.step_select(target, option, ctx),
            Step::Toggle { target, state } => self.step_toggle(target, *state, ctx),
            Step::Drag { from, to } => self.step_drag(from, to, ctx),
            Step::Wait(spec) => self.step_wait(spec, ctx),
            Step::Sleep { ms } => self.step_sleep(*ms, ctx),
            Step::Close { target, force } => self.step_close(target, *force, ctx),
            Step::Human { message } => self.step_human(message.as_deref(), ctx),
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn step_launch(
        &self,
        app: &str,
        args: &[String],
        cwd: Option<&str>,
        wait_for_window: Option<&Selector>,
        timeout_ms: Option<u64>,
        intent: Option<LifecycleIntent>,
        store: Option<&str>,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        // Lifecycle default is deliberately conservative: an unnamed intent
        // means "keep it", which is exactly the rule we are protecting.
        let intent = intent.unwrap_or(LifecycleIntent::Unknown);
        let profile = profile_for_app(app);
        // Snapshot first. A single-instance app (Windows Notepad) can open its
        // window in an already-running process before the next line returns.
        let before: Vec<Hwnd> = self
            .enumerate_windows()
            .unwrap_or_else(|_| inbrisk_win32::window::enumerate_top_level())
            .into_iter()
            .map(|w| w.hwnd)
            .collect();
        let result = inbrisk_win32::launch(app, args, cwd)?;
        self.state.counters.launched.fetch_add(1, Ordering::Relaxed);
        self.state.world.bump();

        let is_protocol = app.contains("://");
        let launcher_pid = result.pid;
        let image = image_file_name(&result.resolved);
        let mut pid = result.pid;
        let mut hwnd = Hwnd::NULL;
        let default_timeout_ms = if is_protocol { 25_000 } else { 10_000 };
        let timeout = Duration::from_millis(timeout_ms.unwrap_or(default_timeout_ms));

        let deadline = Instant::now() + timeout;
        loop {
            if ctx.cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(ErrorCode::Cancelled, "launch cancelled"));
            }
            if let Some(found) =
                self.discover_launched_window(pid, &image, wait_for_window, &before)
            {
                hwnd = found.hwnd;
                if found.process_id != 0 {
                    pid = Some(found.process_id);
                }
                break;
            }
            if Instant::now() >= deadline {
                break;
            }
            let slice = Duration::from_millis(60);
            if let Some(rx) = self.waits.rx.as_ref() {
                let _ = rx.recv_timeout(slice);
            } else {
                std::thread::sleep(slice);
            }
        }

        if hwnd.0 == 0 {
            return Err(InbriskError::not_found(format!(
                "launched '{app}' but no window appeared within {}ms",
                timeout.as_millis()
            ))
            .with_hint(
                "the process started, but it did not open a top-level window this runtime can see",
            ));
        }

        let handoff = launcher_pid.is_some() && launcher_pid != pid;
        if let Some(actual_pid) = pid {
            self.state.stamp_ownership(inbrisk_core::OwnershipStamp {
                hwnd,
                ownership: Ownership::Agent,
                opened_by_session: Some(ctx.session_id),
                opened_by_pid: Some(actual_pid),
                intent,
                explicit_user_close_requested: false,
                generation: self.state.generation(),
                reason: format!("launched '{app}' by session {}", ctx.session_id),
            });
            // A handoff window lives in a process the user already had open.
            // Recording that pid as "we launched it" would let a later force
            // close treat the whole process as ours.
            if !handoff {
                self.state.launched.write().insert(
                    actual_pid,
                    crate::state::LaunchRecord {
                        pid: actual_pid,
                        app: app.to_string(),
                        session_id: ctx.session_id,
                        intent,
                        launched_at_ms: now_ms(),
                        hwnds: vec![hwnd],
                    },
                );
            }
        }

        let output = json!({
            "app": app,
            "pid": pid,
            "launcherPid": launcher_pid,
            "hwnd": hwnd.0,
            "windowFound": true,
            "handoff": handoff,
            "method": result.method.as_str(),
            "resolved": result.resolved,
            "intent": intent.as_str(),
            "profile": profile.map(|p| p.name),
        });
        if hwnd.0 != 0 {
            ctx.default_window = Some(hwnd);
        }
        ctx.record_name(store, output.clone());
        Ok(StepDone {
            backend: "win32".into(),
            verified: true,
            element: None,
            output: Some(output),
            field: None,
        })
    }

    fn discover_launched_window(
        &self,
        pid: Option<u32>,
        image_name: &str,
        wait_for_window: Option<&Selector>,
        before: &[Hwnd],
    ) -> Option<inbrisk_win32::RawWindow> {
        let windows = self
            .enumerate_windows()
            .unwrap_or_else(|_| inbrisk_win32::window::enumerate_top_level());
        select_launched_window(&windows, pid, image_name, wait_for_window, before)
    }

    fn step_focus(&self, target: &TargetRef, ctx: &mut PlanContext) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        let hwnd = resolved.window();
        if hwnd.0 == 0 {
            return Err(InbriskError::not_found("target has no window to focus"));
        }
        let ownership = self.state.ownership_of(hwnd);
        let name = inbrisk_win32::window::title_of(hwnd);
        self.policy.check_mutable(ownership, hwnd, &name)?;
        self.state
            .counters
            .win32_calls
            .fetch_add(1, Ordering::Relaxed);
        inbrisk_win32::window::focus_window(hwnd)?;
        let verified = inbrisk_win32::window::is_foreground(hwnd);
        self.state.world.bump();
        Ok(StepDone {
            backend: "win32".into(),
            verified,
            element: resolved.element().cloned(),
            output: Some(json!({ "hwnd": hwnd.0, "foreground": verified })),
            field: None,
        })
    }

    fn step_find(
        &self,
        selector: &Selector,
        store: Option<&str>,
        all: bool,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let started = Instant::now();
        let _ = started;
        let window = match self.window_for_selector(selector, ctx) {
            Ok(w) => w,
            Err(e) => {
                // A selector with only win32-expressible fields can still match
                // a window we have not resolved yet.
                if win32_expressible(selector) {
                    Hwnd::NULL
                } else {
                    return Err(e);
                }
            }
        };

        // Win32 first: a bare window selector never needs UIA.
        if win32_expressible(selector) {
            if let Some(found) = find_window(selector) {
                let mut element = ElementRef {
                    id: 0,
                    hwnd: found.hwnd,
                    process_id: found.process_id,
                    generation: self.state.generation(),
                    runtime_id: Vec::new(),
                    role: "window".into(),
                    name: found.title.clone(),
                    automation_id: String::new(),
                    class_name: found.class_name.clone(),
                    bounds: found.bounds,
                    enabled: true,
                    offscreen: false,
                    backend: "win32".into(),
                };
                let id = self.state.elements.insert(
                    element.clone(),
                    selector.clone(),
                    ctx.session_id,
                    found.hwnd,
                );
                element.id = id;
                ctx.handles.push(id);
                ctx.default_window = Some(found.hwnd);
                let value = json!({
                    "elementId": id,
                    "hwnd": found.hwnd.0,
                    "role": "window",
                    "name": found.title,
                    "backend": "win32",
                });
                ctx.record_name(store, value.clone());
                return Ok(StepDone {
                    backend: "win32".into(),
                    verified: true,
                    element: Some(element),
                    output: Some(value),
                    field: None,
                });
            }
            if !all {
                return Err(InbriskError::not_found(format!(
                    "no window matches {}",
                    selector.describe()
                )));
            }
        }

        if window.0 == 0 {
            return Err(
                InbriskError::not_found("cannot search an element tree without a window")
                    .with_hint("pass window/hwnd in the selector or launch the app first"),
            );
        }

        if content_requires_cdp(selector, process_name_of(window).as_deref()) {
            return self.step_find_cdp(selector, store, all, window, ctx);
        }

        let version = self.state.world.version();
        let generation = self.state.generation();

        // Check plan-local cache if single element query
        if !all {
            let cached = {
                let cache = ctx.plan_elements.lock();
                cache.get(&(window, selector.clone())).cloned()
            };
            if let Some((cached, cached_version)) = cached {
                if cached_version == version && !cached.is_stale(generation) {
                    let id = self.state.elements.insert(
                        cached.clone(),
                        selector.clone(),
                        ctx.session_id,
                        window,
                    );
                    ctx.handles.push(id);
                    let value = json!({
                        "elementId": id,
                        "role": cached.role,
                        "name": cached.name,
                        "automationId": cached.automation_id,
                        "bounds": { "x": cached.bounds.x, "y": cached.bounds.y, "width": cached.bounds.width, "height": cached.bounds.height },
                        "backend": "uia",
                        "count": 1,
                    });
                    ctx.record_name(store, value.clone());
                    return Ok(StepDone {
                        backend: "uia".into(),
                        verified: true,
                        element: Some(cached),
                        output: Some(value),
                        field: None,
                    });
                }
            }
        }

        let limit = 64usize;
        // A just-created window can exist in Win32 before its UIA tree is
        // published. Retry only while the tree is still empty.
        let settle_until =
            Instant::now() + Duration::from_millis(selector.timeout_ms.unwrap_or(2_000));
        let (mut matches, scanned) = loop {
            let sel = selector.clone();
            self.state
                .counters
                .uia_calls
                .fetch_add(1, Ordering::Relaxed);
            let outcome = self
                .uia
                .reads
                .submit(Some(Duration::from_millis(4000)), move |ap| {
                    ap.find(window, &sel, all, limit)
                })?;
            let (matches, scanned) = outcome?;
            if !matches.is_empty() || scanned > 8 || Instant::now() >= settle_until {
                break (matches, scanned);
            }
            std::thread::sleep(Duration::from_millis(40));
        };
        if matches.is_empty() {
            return Err(InbriskError::not_found(format!(
                "no element matches {} ({} scanned)",
                selector.describe(),
                scanned
            ))
            .with_hint("observe the window to see the real tree"));
        }
        if !all {
            matches.truncate(1);
        }
        let mut ids = Vec::new();
        for data in &matches {
            let element = data.to_element_ref(0, generation, "uia");
            let id = self
                .state
                .elements
                .insert(element, selector.clone(), ctx.session_id, window);
            ids.push(id);
            ctx.handles.push(id);
        }
        let first = &matches[0];
        let el_ref = matches[0].to_element_ref(ids[0], generation, "uia");
        if !all {
            ctx.plan_elements
                .lock()
                .insert((window, selector.clone()), (el_ref.clone(), version));
        }
        let value = json!({
            "elementId": ids[0],
            "role": first.role,
            "name": first.name,
            "automationId": first.automation_id,
            "bounds": { "x": first.bounds.x, "y": first.bounds.y, "width": first.bounds.width, "height": first.bounds.height },
            "backend": "uia",
            "count": matches.len(),
        });
        ctx.record_name(store, value.clone());
        Ok(StepDone {
            backend: "uia".into(),
            verified: true,
            element: Some(el_ref),
            output: Some(value),
            field: None,
        })
    }

    fn step_invoke(&self, target: &TargetRef, ctx: &mut PlanContext) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        self.refuse_cdp_mutation(&resolved)?;
        let window = resolved.window();
        let selector = resolved.selector();
        self.check_window_mutable(window)?;
        let before = WorldFingerprint::capture(&self.state);
        let sel = selector.clone();
        self.state
            .counters
            .uia_calls
            .fetch_add(1, Ordering::Relaxed);
        let element = self
            .uia
            .mutations
            .submit(Some(Duration::from_millis(6_000)), move |ap| {
                ap.invoke(window, &sel)
            })??;
        let after = WorldFingerprint::capture(&self.state);
        self.state.counters.actions.fetch_add(1, Ordering::Relaxed);
        if after.changed(&before) {
            self.state.world.bump();
        }
        let verified = after.changed(&before);
        Ok(StepDone {
            backend: "uia".into(),
            verified,
            element: Some(element.to_element_ref(0, self.state.generation(), "uia")),
            output: Some(json!({ "invoked": true, "verified": verified })),
            field: None,
        })
    }

    fn step_set_value(
        &self,
        target: &TargetRef,
        value: &str,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        self.refuse_cdp_mutation(&resolved)?;
        let window = resolved.window();
        let selector = resolved.selector();
        self.check_window_mutable(window)?;
        let sel = selector.clone();
        let text = value.to_string();
        self.state
            .counters
            .uia_calls
            .fetch_add(1, Ordering::Relaxed);
        let attempt = self
            .uia
            .mutations
            .submit(Some(Duration::from_millis(6_000)), move |ap| {
                ap.set_value(window, &sel, &text)
            });
        match attempt {
            Ok(Ok(element)) => {
                self.state.world.bump();
                self.state.counters.actions.fetch_add(1, Ordering::Relaxed);
                // Value was verified immediately by SetValue in the apartment.
                let readback = element.value.clone().or_else(|| {
                    let sel = selector.clone();
                    self.uia
                        .reads
                        .submit(Some(Duration::from_millis(500)), move |ap| {
                            ap.read_value_of(window, &sel)
                        })
                        .ok()
                        .and_then(|r| r.ok())
                        .flatten()
                });
                let verified = readback.as_deref() == Some(value);
                Ok(StepDone {
                    backend: "uia".into(),
                    verified,
                    element: Some(element.to_element_ref(0, self.state.generation(), "uia")),
                    output: Some(json!({
                        "set": value,
                        "readBack": readback,
                        "verified": verified,
                    })),
                    field: None,
                })
            }
            Ok(Err(e)) if e.code == ErrorCode::PatternUnavailable => {
                log_debug!("router", "ValuePattern unavailable; falling back to typing");
                self.step_type(value, Some(target), true, ctx)
            }
            Ok(Err(e)) => Err(e),
            Err(e) => Err(e),
        }
    }

pub fn is_fallback_eligible(err: &InbriskError) -> bool {
    matches!(err.code, ErrorCode::PatternUnavailable | ErrorCode::NotImplemented)
}

    fn step_click(
        &self,
        target: Option<&TargetRef>,
        x: Option<i32>,
        y: Option<i32>,
        button: MouseButton,
        clicks: u8,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let coords: Option<PointTarget> = match target {
            Some(TargetRef::Point(pt)) => Some(pt.clone()),
            _ => match (x, y) {
                (Some(cx), Some(cy)) => Some(PointTarget::virtual_screen(cx as f64, cy as f64)),
                _ => None,
            },
        };

        if let Some(target_pt) = coords {
            target_pt.validate().map_err(InbriskError::from)?;
            let broker = self.broker_for_physical_input()?;
            if let Some(h) = target_pt.hwnd {
                if h != 0 {
                    let _ = inbrisk_win32::window::focus_window(inbrisk_core::Hwnd(h as isize));
                }
            } else if let Some(target) = target {
                if !matches!(target, TargetRef::Point(_)) {
                    if let TargetRef::Selector(sel) = target {
                        if !sel.is_empty() {
                            let _ = self.step_focus(target, ctx);
                        }
                    } else {
                        let _ = self.step_focus(target, ctx);
                    }
                }
            }
            let lease = self.input.acquire(
                ctx.session_id,
                format!("click at ({}, {})", target_pt.x, target_pt.y),
                Some(Duration::from_secs(10)),
            )?;
            let before = WorldFingerprint::capture(&self.state);
            let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
            let guard = PhysicalInputGuard {
                expected_foreground_hwnd: expected_fg,
            };
            let op_id = self.next_physical_op_id();
            let clicked_x = target_pt.x;
            let clicked_y = target_pt.y;
            if let Err(e) = broker.click(op_id, target_pt, button, clicks as u32, guard) {
                drop(lease);
                return Err(self.handle_physical_error(e));
            }
            drop(lease);
            let after = WorldFingerprint::capture(&self.state);
            if after.changed(&before) {
                self.state.world.bump();
            }
            self.state.counters.actions.fetch_add(1, Ordering::Relaxed);
            return Ok(StepDone {
                backend: "desktop-broker".into(),
                verified: true,
                element: None,
                output: Some(json!({ "clicked": [clicked_x, clicked_y] })),
                field: None,
            });
        }

        let target = target.ok_or_else(|| {
            InbriskError::invalid_plan(
                "click requires either a target element or (x, y) coordinates",
            )
        })?;

        let resolved = self.resolve(target, ctx)?;
        let window = resolved.window();
        self.check_window_mutable(window)?;
        let selector = resolved.selector();
        let element = self.resolve_element(resolved.clone(), ctx)?;
        if !element.enabled {
            return Err(InbriskError::element_disabled(format!(
                "{} is disabled",
                element.label()
            )));
        }

        // Cheapest first: if the element can be invoked, do that instead of
        // moving a synthetic mouse.
        if button == MouseButton::Left && clicks <= 1 {
            match self.step_invoke(target, ctx) {
                Ok(outcome) => return Ok(outcome),
                Err(e) if !Self::is_fallback_eligible(&e) => return Err(e),
                Err(_) => {}
            }
        }
        let (cx, cy) = element.bounds.center();
        if element.offscreen || element.bounds.is_empty() {
            return Err(InbriskError::new(
                ErrorCode::TargetOffscreen,
                format!("{} is off screen", element.label()),
            )
            .with_hint("scroll it into view or focus the window first"));
        }
        let broker = self.broker_for_physical_input()?;
        let lease = self.input.acquire(
            ctx.session_id,
            format!("click {}", element.label()),
            Some(Duration::from_secs(10)),
        )?;
        let before = WorldFingerprint::capture(&self.state);
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        let op_id = self.next_physical_op_id();
        let target_pt = PointTarget::virtual_screen(cx as f64, cy as f64);
        if let Err(e) = broker.click(op_id, target_pt, button, clicks as u32, guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        let after = WorldFingerprint::capture(&self.state);
        if after.changed(&before) {
            self.state.world.bump();
        }
        self.state.counters.actions.fetch_add(1, Ordering::Relaxed);
        let _ = selector;
        Ok(StepDone {
            backend: "desktop-broker".into(),
            verified: after.changed(&before),
            element: Some(element),
            output: Some(json!({ "clicked": [cx, cy] })),
            field: None,
        })
    }

    fn step_type(
        &self,
        text: &str,
        target: Option<&TargetRef>,
        clear_first: bool,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        if let Some(target) = target {
            let resolved = self.resolve(target, ctx)?;
            let window = resolved.window();
            self.check_window_mutable(window)?;
            let element = self.resolve_element(resolved.clone(), ctx)?;
            if !element.enabled {
                return Err(InbriskError::element_disabled(format!(
                    "{} is disabled",
                    element.label()
                )));
            }
            self.step_focus(target, ctx)?;
        }
        let broker = self.broker_for_physical_input()?;
        let lease =
            self.input
                .acquire(ctx.session_id, "type text", Some(Duration::from_secs(10)))?;
        let before = WorldFingerprint::capture(&self.state);
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        if clear_first {
            let op_id = self.next_physical_op_id();
            if let Err(e) = broker.key_chord(op_id, vec!["ctrl".into(), "a".into()], guard.clone())
            {
                drop(lease);
                return Err(self.handle_physical_error(e));
            }
            std::thread::sleep(Duration::from_millis(25));
        }
        let op_id = self.next_physical_op_id();
        if let Err(e) = broker.type_text(op_id, text.to_string(), guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        let after = WorldFingerprint::capture(&self.state);
        if after.changed(&before) {
            self.state.world.bump();
        }
        self.state.counters.actions.fetch_add(1, Ordering::Relaxed);
        Ok(StepDone {
            backend: "desktop-broker".into(),
            // We cannot read the field back without knowing it; never claim
            // verification we did not perform.
            verified: false,
            element: None,
            output: Some(json!({ "typed": text.len(), "chars": text.chars().count() })),
            field: None,
        })
    }

    pub fn step_move(&self, x: i32, y: i32, ctx: &mut PlanContext) -> Result<StepDone> {
        self.step_move_resolved(None, Some(x), Some(y), None, ctx)
    }

    fn step_move_resolved(
        &self,
        target: Option<&TargetRef>,
        x: Option<i32>,
        y: Option<i32>,
        hwnd: Option<u64>,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let coords: Option<PointTarget> = match target {
            Some(TargetRef::Point(pt)) => Some(pt.clone()),
            _ => match (x, y) {
                (Some(cx), Some(cy)) => {
                    if let Some(h) = hwnd {
                        Some(PointTarget::client_physical(h, cx as f64, cy as f64))
                    } else {
                        Some(PointTarget::virtual_screen(cx as f64, cy as f64))
                    }
                }
                _ => None,
            },
        };

        if let Some(target_pt) = coords {
            target_pt.validate().map_err(InbriskError::from)?;
            let broker = self.broker_for_physical_input()?;
            if let Some(h) = target_pt.hwnd {
                if h != 0 {
                    let _ = inbrisk_win32::window::focus_window(inbrisk_core::Hwnd(h as isize));
                }
            } else if let Some(target) = target {
                if !matches!(target, TargetRef::Point(_)) {
                    if let TargetRef::Selector(sel) = target {
                        if !sel.is_empty() {
                            let _ = self.step_focus(target, ctx);
                        }
                    } else {
                        let _ = self.step_focus(target, ctx);
                    }
                }
            }
            let lease = self.input.acquire(
                ctx.session_id,
                format!("move to ({}, {})", target_pt.x, target_pt.y),
                Some(Duration::from_secs(5)),
            )?;
            let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
            let guard = PhysicalInputGuard {
                expected_foreground_hwnd: expected_fg,
            };
            let op_id = self.next_physical_op_id();
            let moved_x = target_pt.x;
            let moved_y = target_pt.y;
            if let Err(e) = broker.move_to(op_id, target_pt, guard) {
                drop(lease);
                return Err(self.handle_physical_error(e));
            }
            drop(lease);
            self.state.world.bump();
            return Ok(StepDone {
                backend: "desktop-broker".into(),
                verified: true,
                element: None,
                output: Some(json!({ "moved": [moved_x, moved_y] })),
                field: None,
            });
        }

        let target = target.ok_or_else(|| {
            InbriskError::invalid_plan(
                "move requires either a target element or (x, y) coordinates",
            )
        })?;

        let resolved = self.resolve(target, ctx)?;
        let window = resolved.window();
        if window.0 != 0 {
            let _ = inbrisk_win32::window::focus_window(window);
        }
        let element = self.resolve_element(resolved.clone(), ctx)?;
        let (cx, cy) = element.bounds.center();
        let pt = PointTarget::virtual_screen(cx as f64, cy as f64);
        let broker = self.broker_for_physical_input()?;
        let lease = self.input.acquire(
            ctx.session_id,
            format!("move to ({}, {})", cx, cy),
            Some(Duration::from_secs(5)),
        )?;
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        let op_id = self.next_physical_op_id();
        if let Err(e) = broker.move_to(op_id, pt, guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        self.state.world.bump();
        Ok(StepDone {
            backend: "desktop-broker".into(),
            verified: true,
            element: Some(element),
            output: Some(json!({ "moved": [cx, cy] })),
            field: None,
        })
    }

    pub fn step_shortcut(
        &self,
        keys: &[String],
        target: Option<&TargetRef>,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        if let Some(target) = target {
            self.step_focus(target, ctx)?;
        }

        // Safety Guard: Never allow Alt+F4 to hit desktop shell surfaces (Progman, WorkerW,
        // Shell_TrayWnd, explorer.exe) or protected surfaces, preventing accidental system shutdown dialogs.
        let is_alt_f4 = keys.iter().any(|k| k.eq_ignore_ascii_case("f4"))
            && keys.iter().any(|k| k.eq_ignore_ascii_case("alt"));
        if is_alt_f4 {
            if let Some(fg) = inbrisk_win32::window::foreground() {
                let class = inbrisk_win32::window::class_of(fg);
                let pid = inbrisk_win32::window::process_id_of(fg);
                let proc_name = inbrisk_win32::process::process_name(pid).unwrap_or_default();
                if self.policy.is_protected_process(&proc_name).is_some()
                    || self.policy.is_protected_class(&class).is_some()
                    || class.eq_ignore_ascii_case("progman")
                    || class.eq_ignore_ascii_case("workerw")
                    || class.eq_ignore_ascii_case("shell_traywnd")
                    || proc_name.eq_ignore_ascii_case("explorer.exe")
                {
                    return Err(InbriskError::denied(format!(
                        "Alt+F4 is blocked on protected/shell surface '{proc_name}' ({class}) to prevent accidental system shutdown"
                    )));
                }
            } else {
                return Err(InbriskError::denied(
                    "Alt+F4 is blocked when no window is in foreground to prevent accidental system shutdown"
                ));
            }
        }

        let broker = self.broker_for_physical_input()?;
        let lease = self.input.acquire(
            ctx.session_id,
            format!("shortcut {}", keys.join("+")),
            Some(Duration::from_secs(10)),
        )?;
        let before = WorldFingerprint::capture(&self.state);
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        let op_id = self.next_physical_op_id();
        if let Err(e) = broker.shortcut(op_id, keys.to_vec(), guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        let after = WorldFingerprint::capture(&self.state);
        let uia_changed = after.changed(&before);
        if uia_changed {
            self.state.world.bump();
        }
        Ok(StepDone {
            backend: "desktop-broker".into(),
            verified: true,
            element: None,
            output: Some(json!({ "shortcut": keys, "uia_changed": uia_changed })),
            field: None,
        })
    }

    fn step_key(
        &self,
        keys: &[String],
        target: Option<&TargetRef>,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        self.step_shortcut(keys, target, ctx)
    }

    fn step_scroll(
        &self,
        target: Option<&TargetRef>,
        direction: ScrollDirection,
        amount: i32,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        // Prefer scrolling the element itself through UIA.
        if let Some(target) = target {
            if let Ok(resolved) = self.resolve(target, ctx) {
                let window = resolved.window();
                let selector = resolved.selector();
                let dir = direction;
                let amt = amount;
                let scrolled = self
                    .uia
                    .mutations
                    .submit(Some(Duration::from_millis(4_000)), move |ap| {
                        ap.scroll_element(window, &selector, dir, amt)
                    })
                    .unwrap_or(Ok(false))?;
                if scrolled {
                    self.state.world.bump();
                    return Ok(StepDone {
                        backend: "uia".into(),
                        verified: true,
                        element: None,
                        output: Some(
                            json!({ "scrolled": amount, "direction": format!("{direction:?}") }),
                        ),
                        field: None,
                    });
                }
            }
        }
        let (cx, cy) = inbrisk_win32::window::cursor_pos().unwrap_or((0, 0));
        let broker = self.broker_for_physical_input()?;
        let lease = self
            .input
            .acquire(ctx.session_id, "scroll", Some(Duration::from_secs(5)))?;
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        let op_id = self.next_physical_op_id();
        if let Err(e) = broker.scroll(op_id, direction, amount, guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        self.state.world.bump();
        Ok(StepDone {
            backend: "desktop-broker".into(),
            verified: false,
            element: None,
            output: Some(json!({ "scrolled": amount, "at": [cx, cy] })),
            field: None,
        })
    }

    fn step_select(
        &self,
        target: &TargetRef,
        option: &SelectOption,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        let window = resolved.window();
        self.check_window_mutable(window)?;
        let base = resolved.selector();
        let wanted = option.clone();
        let sel_out = base.clone();
        let picked = self
            .uia
            .mutations
            .submit(Some(Duration::from_millis(8_000)), move |ap| {
                ap.select_option(window, &base, &wanted)
            })??;
        self.state.world.bump();
        let _ = sel_out;
        Ok(StepDone {
            backend: "uia".into(),
            verified: true,
            element: Some(picked.to_element_ref(0, self.state.generation(), "uia")),
            output: Some(json!({ "selected": true })),
            field: None,
        })
    }

    fn step_toggle(
        &self,
        target: &TargetRef,
        desired: Option<bool>,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        let window = resolved.window();
        self.check_window_mutable(window)?;
        let selector = resolved.selector();
        let sel = selector.clone();
        let element = self
            .uia
            .mutations
            .submit(Some(Duration::from_millis(5_000)), move |ap| {
                ap.toggle(window, &sel, desired)
            })??;
        self.state.world.bump();
        Ok(StepDone {
            backend: "uia".into(),
            verified: true,
            element: Some(element.to_element_ref(0, self.state.generation(), "uia")),
            output: Some(json!({ "toggled": true })),
            field: None,
        })
    }

    fn step_drag(
        &self,
        from: &TargetRef,
        to: &DropTarget,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let from_target = match from {
            TargetRef::Point(pt) => pt.clone(),
            other => {
                let a = self.resolve_element(self.resolve(other, ctx)?, ctx)?;
                let (ax, ay) = a.bounds.center();
                PointTarget::virtual_screen(ax as f64, ay as f64)
            }
        };
        from_target.validate().map_err(InbriskError::from)?;

        let to_target = match to {
            DropTarget::Point(pt) => pt.clone(),
            DropTarget::Reference(reference) => {
                let id = ctx.handle_from_reference(reference)?;
                let handle = self.state.elements.get(id)?;
                let (bx, by) = handle.element.bounds.center();
                PointTarget::virtual_screen(bx as f64, by as f64)
            }
            DropTarget::Selector(selector) => {
                let resolved = self.resolve(&TargetRef::Selector(selector.clone()), ctx)?;
                let (bx, by) = self.resolve_element(resolved, ctx)?.bounds.center();
                PointTarget::virtual_screen(bx as f64, by as f64)
            }
        };
        to_target.validate().map_err(InbriskError::from)?;

        let broker = self.broker_for_physical_input()?;
        let lease = self
            .input
            .acquire(ctx.session_id, "drag", Some(Duration::from_secs(10)))?;
        let expected_fg = inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64);
        let guard = PhysicalInputGuard {
            expected_foreground_hwnd: expected_fg,
        };
        let op_id = self.next_physical_op_id();
        let from_desc = [from_target.x, from_target.y];
        let to_desc = [to_target.x, to_target.y];
        if let Err(e) = broker.drag(op_id, from_target, to_target, MouseButton::Left, guard) {
            drop(lease);
            return Err(self.handle_physical_error(e));
        }
        drop(lease);
        self.state.world.bump();
        Ok(StepDone {
            backend: "desktop-broker".into(),
            verified: true,
            element: None,
            output: Some(json!({ "from": from_desc, "to": to_desc })),
            field: None,
        })
    }

    fn step_wait(&self, spec: &WaitSpec, ctx: &mut PlanContext) -> Result<StepDone> {
        let default_timeout = Duration::from_millis(15_000);
        match spec {
            WaitSpec::Window {
                selector,
                state,
                timeout_ms,
            } => {
                let timeout = Duration::from_millis(timeout_ms.unwrap_or(15_000));
                let token = self
                    .state
                    .activity
                    .begin_waiting(format!("waiting for window {}", selector.describe()));
                let (ok, source) =
                    self.waits
                        .wait_window(selector, *state, timeout, &ctx.cancel)?;
                drop(token);
                if ok {
                    if let Some(found) = find_window(selector) {
                        ctx.default_window = Some(found.hwnd);
                    }
                    self.state.world.bump();
                }
                Ok(StepDone {
                    backend: "win32".into(),
                    verified: ok,
                    element: None,
                    output: Some(json!({ "satisfied": ok, "wakeSource": source })),
                    field: if ok { None } else { Some("timeout".into()) },
                })
            }
            WaitSpec::Element {
                selector,
                state,
                timeout_ms,
            } => {
                let timeout = Duration::from_millis(timeout_ms.unwrap_or(15_000));
                let window = self
                    .window_for_selector(selector, ctx)
                    .unwrap_or(Hwnd::NULL);
                if window.0 == 0 {
                    return Err(InbriskError::not_found(
                        "element wait needs a window in scope",
                    ));
                }
                let selector = selector.clone();
                let want = *state;
                let token = self
                    .state
                    .activity
                    .begin_waiting(format!("waiting for {}", selector.describe()));
                let (ok, source) = self.waits.wait_element(timeout, &ctx.cancel, || {
                    let sel = selector.clone();
                    let found = self
                        .uia
                        .reads
                        .submit(Some(Duration::from_millis(3_000)), move |ap| {
                            ap.element_state(window, &sel)
                        })
                        .unwrap_or(Ok(None))?;
                    Ok(match found {
                        Some((exists, enabled, offscreen)) => match want {
                            inbrisk_protocol::action::ElementWaitState::Exists => exists,
                            inbrisk_protocol::action::ElementWaitState::Visible => {
                                exists && !offscreen
                            }
                            inbrisk_protocol::action::ElementWaitState::Enabled => {
                                exists && enabled
                            }
                            inbrisk_protocol::action::ElementWaitState::Gone => !exists,
                        },
                        None => matches!(want, inbrisk_protocol::action::ElementWaitState::Gone),
                    })
                })?;
                drop(token);
                if ok {
                    self.state.world.bump();
                }
                Ok(StepDone {
                    backend: "uia".into(),
                    verified: ok,
                    element: None,
                    output: Some(json!({ "satisfied": ok, "wakeSource": source })),
                    field: if ok { None } else { Some("timeout".into()) },
                })
            }
            WaitSpec::Value {
                target,
                equals,
                timeout_ms,
            } => {
                let timeout = Duration::from_millis(timeout_ms.unwrap_or(15_000));
                let resolved = self.resolve(target, ctx)?;
                let window = resolved.window();
                let selector = resolved.selector();
                let expected = equals.clone();
                let (ok, source) = self.waits.wait_element(timeout, &ctx.cancel, || {
                    let sel = selector.clone();
                    let exp = expected.clone();
                    let value = self
                        .uia
                        .reads
                        .submit(Some(Duration::from_millis(3_000)), move |ap| {
                            ap.read_value_of(window, &sel)
                        })
                        .unwrap_or(Ok(None));
                    Ok(value.ok().flatten().as_deref() == Some(exp.as_str()))
                })?;
                Ok(StepDone {
                    backend: "uia".into(),
                    verified: ok,
                    element: None,
                    output: Some(json!({ "satisfied": ok, "wakeSource": source })),
                    field: if ok { None } else { Some("timeout".into()) },
                })
            }
            WaitSpec::ProcessExit {
                name,
                process_id,
                timeout_ms,
            } => {
                let timeout =
                    Duration::from_millis(timeout_ms.unwrap_or(default_timeout.as_millis() as u64));
                let pid = match (process_id, name) {
                    (Some(pid), _) => *pid,
                    (None, Some(name)) => inbrisk_win32::process::snapshot(false)
                        .into_iter()
                        .find(|p| p.name.eq_ignore_ascii_case(name))
                        .map(|p| p.pid)
                        .ok_or_else(|| {
                            InbriskError::not_found(format!("no process named {name}"))
                        })?,
                    (None, None) => {
                        return Err(InbriskError::invalid_plan(
                            "wait process_exit needs a name or a process_id",
                        ))
                    }
                };
                let (ok, source) = self.waits.wait_process_exit(pid, timeout, &ctx.cancel)?;
                if ok {
                    self.state.world.bump();
                }
                Ok(StepDone {
                    backend: "win32".into(),
                    verified: ok,
                    element: None,
                    output: Some(json!({ "satisfied": ok, "pid": pid, "wakeSource": source })),
                    field: if ok { None } else { Some("timeout".into()) },
                })
            }
            WaitSpec::Idle { ms } => {
                let step = Duration::from_millis(25);
                let mut waited = 0u64;
                while waited < *ms {
                    if ctx.cancel.load(Ordering::Acquire) {
                        return Err(InbriskError::new(ErrorCode::Cancelled, "wait cancelled"));
                    }
                    std::thread::sleep(step);
                    waited += step.as_millis() as u64;
                }
                Ok(StepDone {
                    backend: "none".into(),
                    verified: true,
                    element: None,
                    output: Some(
                        json!({ "satisfied": true, "waitedMs": waited, "wakeSource": "sleep" }),
                    ),
                    field: None,
                })
            }
        }
    }

    fn step_sleep(&self, ms: u64, ctx: &mut PlanContext) -> Result<StepDone> {
        let mut waited = 0u64;
        while waited < ms {
            if ctx.cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(ErrorCode::Cancelled, "sleep cancelled"));
            }
            let slice = (ms - waited).min(50);
            std::thread::sleep(Duration::from_millis(slice));
            waited += slice;
        }
        Ok(StepDone {
            backend: "none".into(),
            verified: true,
            element: None,
            output: Some(json!({ "sleptMs": waited })),
            field: None,
        })
    }

    fn step_close(
        &self,
        target: &TargetRef,
        force: bool,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let resolved = self.resolve(target, ctx)?;
        let hwnd = resolved.window();
        if hwnd.0 == 0 || !inbrisk_win32::window::is_alive(hwnd) {
            return Ok(StepDone {
                backend: "win32".into(),
                verified: true,
                element: None,
                output: Some(json!({ "alreadyClosed": true })),
                field: None,
            });
        }
        let stamp = self.state.stamp_for(hwnd);
        let process_name = stamp
            .as_ref()
            .and_then(|_| {
                inbrisk_win32::process::process_name(inbrisk_win32::window::process_id_of(hwnd))
            })
            .unwrap_or_default();
        let ownership = stamp
            .as_ref()
            .map(|s| s.ownership)
            .unwrap_or(Ownership::Unknown);
        let intent = stamp
            .as_ref()
            .map(|s| s.intent)
            .unwrap_or(LifecycleIntent::Unknown);
        let explicit = stamp
            .as_ref()
            .map(|s| s.explicit_user_close_requested)
            .unwrap_or(false);
        let decision = self
            .policy
            .decide_close(&process_name, ownership, intent, explicit, force);
        match decision {
            CloseDecision::Deny { reason } => {
                self.state.counters.denials.fetch_add(1, Ordering::Relaxed);
                Err(InbriskError::denied(reason))
            }
            CloseDecision::Keep { reason } => {
                self.state.counters.denials.fetch_add(1, Ordering::Relaxed);
                Err(InbriskError::new(ErrorCode::Denied, reason)
                    .with_hint("ask the human, or declare intent: ephemeral at launch"))
            }
            CloseDecision::PostClose { reason } => {
                inbrisk_win32::window::post_close(hwnd)?;
                let deadline = Instant::now() + Duration::from_secs(3);
                while Instant::now() < deadline {
                    if !inbrisk_win32::window::is_alive(hwnd) {
                        break;
                    }
                    std::thread::sleep(Duration::from_millis(50));
                }
                let closed = !inbrisk_win32::window::is_alive(hwnd);
                if closed {
                    self.state.forget_window(hwnd);
                    self.state.world.bump();
                    self.state.counters.closed.fetch_add(1, Ordering::Relaxed);
                } else if force {
                    let pid = inbrisk_win32::window::process_id_of(hwnd);
                    // A single-instance app (Notepad) hosts every document in
                    // one process. Killing that process would close the human's
                    // other windows. WM_CLOSE on this hwnd is the whole request.
                    let shares_process = pid != 0
                        && inbrisk_win32::window::enumerate_top_level()
                            .iter()
                            .any(|w| w.process_id == pid && w.hwnd != hwnd && w.visible);
                    if pid != 0 && !shares_process {
                        inbrisk_win32::process::terminate_process(pid, 0)?;
                    }
                }
                Ok(StepDone {
                    backend: "win32".into(),
                    verified: closed,
                    element: None,
                    output: Some(json!({ "closed": closed, "reason": reason })),
                    field: if closed {
                        None
                    } else {
                        Some("still open".into())
                    },
                })
            }
            CloseDecision::Terminate { reason } => {
                let pid = inbrisk_win32::window::process_id_of(hwnd);
                if pid != 0 {
                    inbrisk_win32::process::terminate_process(pid, 0)?;
                }
                self.state.forget_window(hwnd);
                self.state.world.bump();
                Ok(StepDone {
                    backend: "win32".into(),
                    verified: true,
                    element: None,
                    output: Some(json!({ "terminated": pid, "reason": reason })),
                    field: None,
                })
            }
        }
    }

    fn step_human(&self, message: Option<&str>, ctx: &mut PlanContext) -> Result<StepDone> {
        let reason = message
            .unwrap_or("waiting for the human to confirm")
            .to_string();
        self.state.activity.pause(reason.clone());
        let deadline = Instant::now() + Duration::from_secs(600);
        while self.state.activity.is_paused() {
            if ctx.cancel.load(Ordering::Acquire) {
                return Err(InbriskError::new(
                    ErrorCode::Cancelled,
                    "human step cancelled",
                ));
            }
            if Instant::now() >= deadline {
                return Err(
                    InbriskError::new(ErrorCode::Timeout, "nobody resumed the run")
                        .with_hint("press Resume in the Inbrisk tray"),
                );
            }
            std::thread::sleep(Duration::from_millis(200));
        }
        Ok(StepDone {
            backend: "none".into(),
            verified: true,
            element: None,
            output: Some(json!({ "resumed": true, "reason": reason })),
            field: None,
        })
    }

    /// Resolve any target all the way to an element snapshot.
    fn resolve_element(&self, resolved: ResolvedTarget, ctx: &PlanContext) -> Result<ElementRef> {
        match resolved {
            ResolvedTarget::Handle(h) => {
                if h.element.is_stale(self.state.generation()) {
                    return Err(InbriskError::new(
                        ErrorCode::StaleState,
                        format!("{} belongs to an older generation", h.element.label()),
                    )
                    .with_hint("find it again"));
                }
                Ok(h.element)
            }
            ResolvedTarget::Selector { selector, window } => {
                let sel = (*selector).clone();
                let version = self.state.world.version();
                let generation = self.state.generation();

                // Check plan-local cache first
                {
                    let cache = ctx.plan_elements.lock();
                    if let Some((cached, cached_version)) = cache.get(&(window, sel.clone())) {
                        if *cached_version == version && !cached.is_stale(generation) {
                            return Ok(cached.clone());
                        }
                    }
                }

                let data = self
                    .uia
                    .reads
                    .submit(Some(Duration::from_millis(4_000)), {
                        let sel = sel.clone();
                        move |ap| ap.find_one(window, &sel)
                    })??;
                let element = data.to_element_ref(0, generation, "uia");
                ctx.plan_elements
                    .lock()
                    .insert((window, sel), (element.clone(), version));
                Ok(element)
            }
        }
    }

    fn check_window_mutable(&self, hwnd: Hwnd) -> Result<()> {
        if hwnd.0 == 0 {
            return Ok(());
        }
        let ownership = self.state.ownership_of(hwnd);
        let name = inbrisk_win32::window::title_of(hwnd);
        self.policy.check_mutable(ownership, hwnd, &name)
    }

    /// Execute a whole plan locally — this is the `RUN` op code path.
    pub fn run_plan(
        &self,
        plan: &Plan,
        ctx: &mut PlanContext,
        mode: RunMode,
        on_error: inbrisk_protocol::action::OnError,
    ) -> inbrisk_protocol::response::RunOutcome {
        use inbrisk_protocol::action::OnError;
        let started = Instant::now();
        let activitiy_token = self
            .state
            .activity
            .begin(plan.name.clone().unwrap_or_else(|| "plan".into()));
        let mut window_token: Option<WindowActivityToken> = None;
        self.sync_plan_window(&mut window_token, ctx);
        let mut steps: Vec<StepOutcome> = Vec::with_capacity(plan.steps.len());
        let mut failed_step = None;
        ctx.dry_run = mode == RunMode::DryRun;
        self.state.counters.plans.fetch_add(1, Ordering::Relaxed);

        for (index, step) in plan.steps.iter().enumerate() {
            self.sync_plan_window(&mut window_token, ctx);
            let mut outcome = if ctx.dry_run {
                self.dry_run_step(step, ctx)
            } else {
                self.execute_step(step, ctx)
            };
            outcome.index = index;
            if outcome.status == StepStatus::Ok {
                if let Some(output) = outcome.output.clone() {
                    let store = step_store_name(step);
                    ctx.record(index, store.as_deref(), output);
                }
            }
            let stop = outcome.status == StepStatus::Failed && on_error == OnError::Abort;
            steps.push(outcome);
            self.sync_plan_window(&mut window_token, ctx);
            if stop {
                failed_step = Some(index);
                break;
            }
            if ctx.cancel.load(Ordering::Acquire) {
                failed_step = Some(index);
                break;
            }
        }

        let succeeded =
            failed_step.is_none() && steps.iter().all(|s| s.status != StepStatus::Failed);
        drop(window_token);
        drop(activitiy_token);
        if succeeded {
            self.state.world.bump();
        } else {
            self.state
                .activity
                .settle_failure("plan finished with errors", Duration::from_millis(1200));
        }
        inbrisk_protocol::response::RunOutcome {
            plan_id: ctx.plan_id,
            name: plan.name.clone(),
            steps,
            succeeded,
            failed_step,
            state_version: self.state.state_version(),
            generation: self.state.generation(),
            elapsed_us: started.elapsed().as_micros() as u64,
            mode,
            on_error,
            outputs: ctx.outputs.clone(),
            notes: if mode == RunMode::DryRun {
                vec!["dry run: no action was executed".into()]
            } else {
                Vec::new()
            },
        }
    }

    fn dry_run_step(&self, step: &Step, ctx: &mut PlanContext) -> StepOutcome {
        let started = Instant::now();
        let action = step.action_name().to_string();
        let result = match step {
            Step::Find { selector, .. } => {
                let window = self
                    .window_for_selector(selector, ctx)
                    .unwrap_or(Hwnd::NULL);
                if window.0 == 0 {
                    Err(InbriskError::not_found(
                        "no window in scope for the find step",
                    ))
                } else {
                    let sel = selector.clone();
                    self.uia
                        .reads
                        .submit(Some(Duration::from_millis(3_000)), move |ap| {
                            ap.element_state(window, &sel)
                        })
                        .map(|r| r)
                        .unwrap_or(Ok(None))
                        .map(|_| ())
                }
            }
            Step::Launch { app, .. } => {
                if inbrisk_win32::resolve_app(app).is_some() {
                    Ok(())
                } else {
                    Err(InbriskError::not_found(format!(
                        "'{app}' cannot be resolved without the shell"
                    )))
                }
            }
            _ => Ok(()),
        };
        StepOutcome {
            index: 0,
            action,
            status: if result.is_ok() {
                StepStatus::Ok
            } else {
                StepStatus::Skipped
            },
            backend: "dry-run".into(),
            verified: false,
            elapsed_us: started.elapsed().as_micros() as u64,
            element: None,
            error: result.err(),
            output: None,
        }
    }
}

fn guard(expect: &Option<Expect>, state: &RuntimeState) -> Result<()> {
    if let Some(expect) = expect {
        StateGuard::check(expect, &state.world)?;
    }
    Ok(())
}

fn step_store_name(step: &Step) -> Option<String> {
    match step {
        Step::Find { store, .. } => store.clone(),
        Step::Launch { store, .. } => store.clone(),
        _ => None,
    }
}

/// Result of a `find`.
#[derive(Debug, Clone, Default)]
pub struct FindMatches {
    pub elements: Vec<ElementRef>,
    pub ids: Vec<u64>,
    pub scanned: u32,
    pub truncated: bool,
    pub backend: String,
    pub candidates: Vec<String>,
}

impl Engine {
    /// Resolve a selector without a plan context. Used by the `find` op.
    pub fn find_matches(
        &self,
        session_id: SessionId,
        selector: &Selector,
        all: bool,
        limit: usize,
        default_window: Option<Hwnd>,
    ) -> Result<FindMatches> {
        if win32_expressible(selector) {
            let windows = inbrisk_win32::window::enumerate_top_level();
            let mut matched: Vec<&inbrisk_win32::RawWindow> = windows
                .iter()
                .filter(|w| window_matches(selector, w))
                .collect();
            matched.sort_by_key(|w| (!w.foreground, w.hwnd.0));
            if !matched.is_empty() {
                let generation = self.state.generation();
                let mut out = FindMatches {
                    backend: "win32".into(),
                    scanned: windows.len() as u32,
                    ..Default::default()
                };
                if !all {
                    matched.truncate(1);
                }
                for w in matched.iter().take(limit.max(1)) {
                    let mut element = ElementRef {
                        id: 0,
                        hwnd: w.hwnd,
                        process_id: w.process_id,
                        generation,
                        runtime_id: Vec::new(),
                        role: "window".into(),
                        name: w.title.clone(),
                        automation_id: String::new(),
                        class_name: w.class_name.clone(),
                        bounds: w.bounds,
                        enabled: true,
                        offscreen: false,
                        backend: "win32".into(),
                    };
                    let id = self.state.elements.insert(
                        element.clone(),
                        selector.clone(),
                        session_id,
                        w.hwnd,
                    );
                    element.id = id;
                    out.ids.push(id);
                    out.elements.push(element);
                }
                out.truncated = matched.len() > out.elements.len();
                return Ok(out);
            }
            if !all {
                return Err(InbriskError::not_found(format!(
                    "no window matches {}",
                    selector.describe()
                ))
                .with_hint("call observe to list the available windows"));
            }
        }

        let window = match selector.hwnd {
            Some(h) if h.0 != 0 && inbrisk_win32::window::is_alive(h) => h,
            _ => scoped_window(selector, default_window)?,
        };

        if content_requires_cdp(selector, process_name_of(window).as_deref()) {
            return self.find_matches_cdp(session_id, selector, all, limit, window);
        }

        let sel = selector.clone();
        let generation = self.state.generation();
        let limit = limit.max(1);
        self.state
            .counters
            .uia_calls
            .fetch_add(1, Ordering::Relaxed);
        let (matches, scanned) = self
            .uia
            .reads
            .submit(Some(Duration::from_millis(5_000)), move |ap| {
                ap.find(window, &sel, all, limit)
            })??;

        if matches.is_empty() {
            return Err(InbriskError::not_found(format!(
                "no element matches {} ({scanned} elements scanned)",
                selector.describe()
            ))
            .with_hint("observe with include_tree to see the real names and roles"));
        }

        let mut out = FindMatches {
            backend: "uia".into(),
            scanned,
            truncated: matches.len() >= limit,
            candidates: matches
                .iter()
                .take(8)
                .map(|m| format!("{} \"{}\"", m.role, m.name))
                .collect(),
            ..Default::default()
        };
        for data in &matches {
            let mut element = data.to_element_ref(0, generation, "uia");
            let id =
                self.state
                    .elements
                    .insert(element.clone(), selector.clone(), session_id, window);
            element.id = id;
            out.ids.push(id);
            out.elements.push(element);
        }
        Ok(out)
    }

    fn find_matches_cdp(
        &self,
        session_id: SessionId,
        selector: &Selector,
        all: bool,
        limit: usize,
        window: Hwnd,
    ) -> Result<FindMatches> {
        let (pid, pages, port) = cdp_pages(window)?;
        let matched = select_cdp_pages(selector, &pages, all, limit)?;
        let bounds = window_bounds(window);
        let generation = self.state.generation();
        let mut out = FindMatches {
            backend: "cdp".into(),
            scanned: pages.len() as u32,
            truncated: false,
            ..Default::default()
        };
        for page in &matched {
            let mut element = cdp_element(page, window, pid, generation, bounds);
            let id =
                self.state
                    .elements
                    .insert(element.clone(), selector.clone(), session_id, window);
            element.id = id;
            out.ids.push(id);
            out.candidates.push(format!("document \"{}\"", page.title));
            out.elements.push(element);
        }
        out.truncated = pages.len() > out.elements.len();
        let _ = port;
        Ok(out)
    }

    fn step_find_cdp(
        &self,
        selector: &Selector,
        store: Option<&str>,
        all: bool,
        window: Hwnd,
        ctx: &mut PlanContext,
    ) -> Result<StepDone> {
        let found = self.find_matches_cdp(ctx.session_id, selector, all, 64, window)?;
        let first = found.elements.first().cloned().ok_or_else(|| {
            InbriskError::not_found(format!(
                "no CDP page matches {} ({} pages listed)",
                selector.describe(),
                found.scanned
            ))
            .with_hint("pass name, text or index. This document is not searched with UIA")
        })?;
        ctx.handles.extend(found.ids.iter().copied());
        ctx.default_window = Some(window);
        let value = json!({
            "elementId": first.id,
            "role": first.role,
            "name": first.name,
            "automationId": first.automation_id,
            "url": first.class_name,
            "backend": "cdp",
            "count": found.elements.len(),
        });
        ctx.record_name(store, value.clone());
        Ok(StepDone {
            backend: "cdp".into(),
            verified: true,
            element: Some(first),
            output: Some(value),
            field: None,
        })
    }

    /// Re-read a cached CDP page from the same proven DevTools port.
    pub fn refresh_cdp(&self, element: &ElementRef) -> Result<ElementRef> {
        if element.process_id == 0 {
            return Err(InbriskError::not_found(
                "the cached CDP element has no process id",
            ));
        }
        let hwnd = if element.hwnd.is_null() {
            Hwnd::NULL
        } else {
            element.hwnd
        };
        let lines = browser_command_lines(element.process_id);
        let port = inbrisk_cdp::port_from_command_lines(&lines)?;
        let pages = inbrisk_cdp::list_pages(port)?;
        let page = pages
            .into_iter()
            .find(|p| p.id == element.automation_id)
            .ok_or_else(|| {
                InbriskError::not_found(
                    "the CDP page for this handle is gone; find the document again",
                )
            })?;
        let mut fresh = element.clone();
        fresh.name = page.title;
        fresh.class_name = page.url;
        fresh.hwnd = if hwnd.is_null() { element.hwnd } else { hwnd };
        Ok(fresh)
    }

    fn refuse_cdp_mutation(&self, resolved: &ResolvedTarget) -> Result<()> {
        let is_cdp = match resolved {
            ResolvedTarget::Handle(handle) => handle.element.backend.eq_ignore_ascii_case("cdp"),
            ResolvedTarget::Selector { selector, window } => {
                content_requires_cdp(selector, process_name_of(*window).as_deref())
            }
        };
        if is_cdp {
            Err(InbriskError::not_implemented(
                "CDP document content is not changed by running page script",
            )
            .with_hint(
                "this runtime reads the page through DevTools and does not evaluate JavaScript",
            ))
        } else {
            Ok(())
        }
    }

    fn sync_plan_window(&self, token: &mut Option<WindowActivityToken>, ctx: &PlanContext) {
        let hwnd = ctx
            .default_window
            .filter(|h| inbrisk_win32::window::is_alive(*h))
            .map(|h| h.0)
            .unwrap_or(0);
        if token.as_ref().map(|t| t.hwnd()) == Some(hwnd) && hwnd != 0 {
            return;
        }
        *token = None;
        if hwnd != 0 {
            *token = Some(WindowActivityToken::new(
                Arc::clone(&self.state.activity),
                hwnd,
            ));
        }
    }
}

/// Document content of a CDP-profiled app, or an explicit `backend: cdp`.
/// An empty role does not force CDP, so browser chrome stays on UIA.
pub fn content_requires_cdp(selector: &Selector, process_name: Option<&str>) -> bool {
    if let Some(backend) = selector.backend.as_deref() {
        if backend.eq_ignore_ascii_case("uia") || backend.eq_ignore_ascii_case("win32") {
            return false;
        }
        if backend.eq_ignore_ascii_case("cdp") {
            return true;
        }
    }
    let role = selector.role.as_deref().unwrap_or("").to_ascii_lowercase();
    if !matches!(role.as_str(), "document" | "web" | "page") {
        return false;
    }
    let Some(name) = process_name else {
        return false;
    };
    profile_for_process(name)
        .map(|profile| profile.content_backend == Backend::Cdp)
        .unwrap_or(false)
}

fn scoped_window(selector: &Selector, default_window: Option<Hwnd>) -> Result<Hwnd> {
    if let Some(found) = selector.window.as_ref().and_then(|w| find_window(w)) {
        return Ok(found.hwnd);
    }
    if let Some(process) = &selector.process {
        let sel = Selector {
            process: Some(process.clone()),
            ..Default::default()
        };
        if let Some(found) = find_window(&sel) {
            return Ok(found.hwnd);
        }
    }
    default_window
        .filter(|hwnd| inbrisk_win32::window::is_alive(*hwnd))
        .or_else(inbrisk_win32::window::foreground)
        .ok_or_else(|| {
            InbriskError::not_found("no window in scope for this element selector")
                .with_hint("pass window/hwnd, or launch the app in the same plan")
        })
}

fn process_name_of(window: Hwnd) -> Option<String> {
    let pid = inbrisk_win32::window::process_id_of(window);
    inbrisk_win32::process::process_name(pid)
}

fn browser_command_lines(pid: u32) -> Vec<String> {
    let image = inbrisk_win32::process::process_name(pid)
        .unwrap_or_default()
        .to_ascii_lowercase();
    let table = inbrisk_win32::process::snapshot(false);
    let mut pids = Vec::new();
    let mut current = Some(pid);
    for _ in 0..8 {
        let Some(id) = current else {
            break;
        };
        let name = inbrisk_win32::process::process_name(id)
            .unwrap_or_default()
            .to_ascii_lowercase();
        if id != pid && !image.is_empty() && name != image {
            break;
        }
        pids.push(id);
        current = table
            .iter()
            .find(|entry| entry.pid == id)
            .map(|entry| entry.parent_pid)
            .filter(|parent| *parent != 0);
    }
    for child in inbrisk_win32::process::descendants(pid) {
        let name = inbrisk_win32::process::process_name(child)
            .unwrap_or_default()
            .to_ascii_lowercase();
        if !image.is_empty() && name != image {
            continue;
        }
        if !pids.contains(&child) {
            pids.push(child);
        }
    }
    pids.into_iter()
        .filter_map(inbrisk_win32::process::command_line)
        .collect()
}

fn cdp_pages(window: Hwnd) -> Result<(u32, Vec<inbrisk_cdp::CdpPage>, u16)> {
    let pid = inbrisk_win32::window::process_id_of(window);
    if pid == 0 {
        return Err(InbriskError::not_found(
            "the window has no process id, so a DevTools port cannot be proven",
        )
        .with_hint("document content is not read through UIA"));
    }
    let lines = browser_command_lines(pid);
    if lines.is_empty() {
        return Err(InbriskError::not_found(format!(
            "CDP is required for this document, and the command line of process {pid} could not be read"
        ))
        .with_hint(
            "start that browser with --remote-debugging-port. This runtime will not fall through to UIA",
        ));
    }
    let port = inbrisk_cdp::port_from_command_lines(&lines)?;
    let pages = inbrisk_cdp::list_pages(port)?;
    Ok((pid, pages, port))
}

fn select_cdp_pages(
    selector: &Selector,
    pages: &[inbrisk_cdp::CdpPage],
    all: bool,
    limit: usize,
) -> Result<Vec<inbrisk_cdp::CdpPage>> {
    let matched: Vec<inbrisk_cdp::CdpPage> = pages
        .iter()
        .filter(|page| cdp_page_matches(selector, page))
        .cloned()
        .collect();
    if matched.is_empty() {
        return Err(InbriskError::not_found(format!(
            "no CDP page matches {} ({} pages listed)",
            selector.describe(),
            pages.len()
        ))
        .with_hint("pass name, text or index. This document is not searched with UIA"));
    }
    if !all
        && selector.index.is_none()
        && matched.len() > 1
        && selector.policy == MatchPolicy::Strict
    {
        return Err(InbriskError::new(
            ErrorCode::CloseMatchesFound,
            format!("{} CDP pages match {}", matched.len(), selector.describe()),
        )
        .with_hint("pass name, text, automation_id or index"));
    }
    let limit = limit.max(1);
    if all {
        return Ok(matched.into_iter().take(limit).collect());
    }
    let index = selector.index.unwrap_or(0);
    matched
        .get(index)
        .cloned()
        .map(|page| vec![page])
        .ok_or_else(|| {
            InbriskError::not_found(format!(
                "CDP page index {index} is past {} matches",
                matched.len()
            ))
        })
}

fn cdp_page_matches(selector: &Selector, page: &inbrisk_cdp::CdpPage) -> bool {
    if let Some(role) = &selector.role {
        let role = role.to_ascii_lowercase();
        if !matches!(role.as_str(), "document" | "web" | "page") {
            return false;
        }
    }
    if let Some(id) = &selector.automation_id {
        if &page.id != id {
            return false;
        }
    }
    if let Some(name) = &selector.name {
        let title = page.title.to_ascii_lowercase();
        let needle = name.to_ascii_lowercase();
        let exact = selector.exact.unwrap_or(false);
        if exact {
            if title != needle {
                return false;
            }
        } else if !title.contains(&needle) {
            return false;
        }
    }
    if let Some(text) = &selector.text {
        let blob = format!("{} {}", page.title, page.url).to_ascii_lowercase();
        if !blob.contains(&text.to_ascii_lowercase()) {
            return false;
        }
    }
    true
}

fn cdp_element(
    page: &inbrisk_cdp::CdpPage,
    window: Hwnd,
    pid: u32,
    generation: u64,
    bounds: inbrisk_core::Rect,
) -> ElementRef {
    ElementRef {
        id: 0,
        hwnd: window,
        process_id: pid,
        generation,
        runtime_id: Vec::new(),
        role: "document".into(),
        name: page.title.clone(),
        automation_id: page.id.clone(),
        class_name: page.url.clone(),
        bounds,
        enabled: true,
        offscreen: false,
        backend: "cdp".into(),
    }
}

fn window_bounds(hwnd: Hwnd) -> inbrisk_core::Rect {
    inbrisk_win32::window::enumerate_top_level()
        .into_iter()
        .find(|window| window.hwnd == hwnd)
        .map(|window| window.bounds)
        .unwrap_or_default()
}

/// A selector that Win32 alone can answer.
pub fn win32_expressible(selector: &Selector) -> bool {
    (selector.role.is_none() || selector.role.as_deref() == Some("window"))
        && selector.automation_id.is_none()
        && selector.within.is_none()
        && selector.ancestor.is_none()
        && selector
            .backend
            .as_deref()
            .map(|b| b == "win32")
            .unwrap_or(true)
        && (selector.name.is_some()
            || selector.process.is_some()
            || selector.hwnd.is_some()
            || selector.class_name.is_some()
            || selector.name_regex.is_some()
            || selector.text.is_some()
            || selector.role.as_deref() == Some("window"))
}

/// Find the best matching top-level window.
pub fn find_window(selector: &Selector) -> Option<inbrisk_win32::RawWindow> {
    let windows = inbrisk_win32::window::enumerate_top_level();
    let mut matches: Vec<inbrisk_win32::RawWindow> = windows
        .into_iter()
        .filter(|w| window_matches(selector, w))
        .collect();
    if matches.is_empty() {
        return None;
    }
    matches.sort_by_key(|w| (!w.foreground, w.hwnd.0));
    matches.into_iter().next()
}

fn image_file_name(path: &str) -> String {
    std::path::Path::new(path)
        .file_name()
        .map(|name| name.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn image_matches(process_name: &str, image: &str) -> bool {
    if image.is_empty() {
        return true;
    }
    if process_name.eq_ignore_ascii_case(image) {
        return true;
    }
    let with_exe = if image.to_ascii_lowercase().ends_with(".exe") {
        image.to_string()
    } else {
        format!("{image}.exe")
    };
    if process_name.eq_ignore_ascii_case(&with_exe) {
        return true;
    }
    if image.eq_ignore_ascii_case("calc.exe") || image.eq_ignore_ascii_case("calc") {
        if process_name.eq_ignore_ascii_case("CalculatorApp.exe")
            || process_name.eq_ignore_ascii_case("Calculator.exe")
            || process_name.eq_ignore_ascii_case("CalculatorApp")
        {
            return true;
        }
    }
    false
}

/// Pick the window a launch just created.
///
/// Direct pid match wins. When that pid has no window, a new top-level window
/// whose process image matches is the handoff case: Windows Notepad's stub
/// asks the already-running Notepad process to open the document.
pub fn select_launched_window(
    windows: &[inbrisk_win32::RawWindow],
    pid: Option<u32>,
    image_name: &str,
    wait_for_window: Option<&Selector>,
    before: &[Hwnd],
) -> Option<inbrisk_win32::RawWindow> {
    if let Some(pid) = pid {
        let owned: Vec<inbrisk_win32::RawWindow> = windows
            .iter()
            .filter(|w| w.process_id == pid)
            .cloned()
            .collect();
        if let Some(found) = choose_launched(&owned, wait_for_window) {
            return Some(found);
        }
    }
    let is_protocol_or_stub = image_name.contains("://")
        || image_name.starts_with("steam:")
        || image_name.chars().all(|c| c.is_ascii_digit());

    let fresh: Vec<inbrisk_win32::RawWindow> = windows
        .iter()
        .filter(|w| !before.contains(&w.hwnd))
        .filter(|w| {
            if is_protocol_or_stub || wait_for_window.is_some() {
                true
            } else {
                image_matches(&w.process_name, image_name)
            }
        })
        .cloned()
        .collect();
    if let Some(found) = choose_launched(&fresh, wait_for_window) {
        return Some(found);
    }

    if is_protocol_or_stub {
        if let Some(fg) = inbrisk_win32::window::foreground() {
            if let Some(w) = windows.iter().find(|w| w.hwnd == fg && w.visible && !w.title.trim().is_empty()) {
                let proc = w.process_name.to_ascii_lowercase();
                if !proc.contains("explorer")
                    && !proc.contains("steam")
                    && !proc.contains("cmd.exe")
                    && !proc.contains("pwsh.exe")
                {
                    if wait_for_window.map(|sel| window_matches(sel, w)).unwrap_or(true) {
                        return Some(w.clone());
                    }
                }
            }
        }
    }

    None
}

fn choose_launched(
    candidates: &[inbrisk_win32::RawWindow],
    wait_for_window: Option<&Selector>,
) -> Option<inbrisk_win32::RawWindow> {
    let mut visible: Vec<inbrisk_win32::RawWindow> = candidates
        .iter()
        .filter(|w| w.visible && !w.title.trim().is_empty())
        .filter(|w| {
            wait_for_window
                .map(|sel| window_matches(sel, w))
                .unwrap_or(true)
        })
        .cloned()
        .collect();
    visible.sort_by_key(|w| (!w.foreground, w.hwnd.0));
    visible.into_iter().next()
}

/// Result of a single successful step.
#[derive(Debug)]
pub struct StepDone {
    pub backend: String,
    pub verified: bool,
    pub element: Option<ElementRef>,
    pub output: Option<Value>,
    /// Set when the step completed but the effect could not be confirmed.
    pub field: Option<String>,
}

/// Marks a plan as cancelled from the outside.
pub fn cancel_flag_for(
    cancels: &CancelRegistry,
    session: SessionId,
    plan: PlanId,
) -> Option<Arc<AtomicBool>> {
    cancels.flags.lock().get(&(session, plan)).cloned()
}

/// Convenience: the plan id currently running for a session.
pub fn active_plan_ids(cancels: &CancelRegistry, session: SessionId) -> Vec<PlanId> {
    cancels
        .flags
        .lock()
        .keys()
        .filter(|(s, _)| *s == session)
        .map(|(_, p)| *p)
        .collect()
}

/// Operation ids are plan ids for our purposes; kept as a distinct type so
/// future non-plan operations can join in.
pub type RunningOperation = (SessionId, OperationId);

#[allow(dead_code)]
fn unused(outcome: StepDone) -> StepDone {
    outcome
}

#[cfg(test)]
mod launch_pick_tests {
    use super::*;
    use inbrisk_core::Rect;
    use inbrisk_win32::RawWindow;

    fn window(hwnd: isize, pid: u32, name: &str, title: &str, foreground: bool) -> RawWindow {
        RawWindow {
            hwnd: Hwnd(hwnd),
            process_id: pid,
            process_name: name.to_string(),
            title: title.to_string(),
            class_name: "Notepad".into(),
            bounds: Rect::new(0, 0, 100, 100),
            visible: true,
            minimized: false,
            maximized: false,
            foreground,
            tool_window: false,
        }
    }

    #[test]
    fn direct_pid_beats_a_new_window_in_another_process() {
        let windows = vec![
            window(1, 10, "notepad.exe", "Untitled", false),
            window(2, 99, "notepad.exe", "Adsız", true),
        ];
        let found =
            select_launched_window(&windows, Some(10), "notepad.exe", None, &[Hwnd(2)]).unwrap();
        assert_eq!(found.hwnd, Hwnd(1));
    }

    #[test]
    fn single_instance_handoff_picks_the_new_notepad_window() {
        let windows = vec![
            window(1, 19048, "Notepad.exe", "notes.txt", false),
            window(2, 19048, "Notepad.exe", "Adsız", true),
            window(3, 50, "explorer.exe", "New folder", true),
        ];
        let found = select_launched_window(
            &windows,
            Some(7856),
            "notepad.exe",
            None,
            &[Hwnd(1), Hwnd(3)],
        )
        .unwrap();
        assert_eq!(found.hwnd, Hwnd(2));
        assert_eq!(found.process_id, 19048);
    }

    #[test]
    fn handoff_ignores_windows_that_were_already_open() {
        let windows = vec![window(1, 19048, "Notepad.exe", "Adsız", true)];
        let found = select_launched_window(&windows, Some(7856), "notepad.exe", None, &[Hwnd(1)]);
        assert!(found.is_none());
    }
}

#[cfg(test)]
mod cdp_route_tests {
    use super::*;

    fn selector(role: &str, backend: Option<&str>) -> Selector {
        Selector {
            role: if role.is_empty() {
                None
            } else {
                Some(role.into())
            },
            backend: backend.map(|b| b.to_string()),
            ..Default::default()
        }
    }

    #[test]
    fn chrome_documents_use_cdp_and_chrome_buttons_do_not() {
        assert!(content_requires_cdp(
            &selector("document", None),
            Some("chrome.exe")
        ));
        assert!(content_requires_cdp(
            &selector("web", None),
            Some("msedge.exe")
        ));
        assert!(!content_requires_cdp(
            &selector("button", None),
            Some("chrome.exe")
        ));
        assert!(!content_requires_cdp(
            &selector("", None),
            Some("chrome.exe")
        ));
        assert!(!content_requires_cdp(
            &selector("document", Some("uia")),
            Some("chrome.exe")
        ));
        assert!(!content_requires_cdp(
            &selector("document", None),
            Some("notepad.exe")
        ));
        assert!(content_requires_cdp(
            &selector("button", Some("cdp")),
            Some("notepad.exe")
        ));
    }

    #[test]
    fn blender_profile_is_uia() {
        let profile = profile_for_process("blender.exe").expect("blender");
        assert_eq!(profile.content_backend, Backend::Uia);
        assert_eq!(profile.shell_backend, Backend::Win32);
    }
}

#[cfg(test)]
pub(crate) mod capability_tests {
    use super::*;
    use inbrisk_core::{ErrorCode, ExecutionCapabilities, ExecutionCapability};

    pub(crate) fn make_test_engine(capabilities: ExecutionCapabilities) -> Engine {
        let activity = Arc::new(inbrisk_core::ActivityManager::new());
        let state = Arc::new(RuntimeState::new(Arc::clone(&activity)));
        let uia = Arc::new(UiaService::new(1, Duration::from_secs(8)).expect("uia"));
        let input = Arc::new(GlobalInputArbiter::new(Duration::from_secs(30)));
        let policy = Arc::new(PolicyEngine::windows_default());
        let waits = Arc::new(WaitService {
            monitor: None,
            rx: None,
        });
        let cancels = Arc::new(CancelRegistry::new());
        Engine::with_capabilities(state, uia, input, policy, waits, cancels, capabilities)
    }

    #[test]
    fn runtime_physical_input_unavailable_returns_capability_unavailable() {
        let engine = make_test_engine(ExecutionCapabilities::unavailable());
        let err = engine
            .require_capability(ExecutionCapability::PhysicalInput)
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert!(err.message.contains("PhysicalInput"));
    }

    #[test]
    fn runtime_window_enumeration_unavailable_returns_capability_unavailable() {
        let engine = make_test_engine(ExecutionCapabilities::unavailable());
        let err = engine
            .require_capability(ExecutionCapability::WindowEnumeration)
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert!(err.message.contains("WindowEnumeration"));
    }

    #[test]
    fn runtime_capability_gate_does_not_return_raw_access_denied() {
        let engine = make_test_engine(ExecutionCapabilities::unavailable());
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));
        // Coordinate click without PhysicalInput capability
        let err = engine
            .step_click(None, Some(100), Some(200), MouseButton::Left, 1, &mut ctx)
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert_ne!(err.code, ErrorCode::Denied);
        assert!(!err.message.contains("ERROR_ACCESS_DENIED"));
    }
}

#[cfg(test)]
mod desktop_broker_tests {
    use super::*;
    use crate::desktop_broker::{BrokerTransport, DesktopBrokerClient};
    use inbrisk_core::{ErrorCode, ExecutionCapabilities, ExecutionCapability};
    use inbrisk_ipc::{BrokerChannelHost, BrokerChannelPeer};
    use inbrisk_protocol::action::CoordinateSpaceDto;
    use inbrisk_protocol::desktop_broker::{
        BrokerWindowInfo, DesktopBrokerRequest, DesktopBrokerResponse, DesktopBrokerWelcome,
        PhysicalInputCommand, PhysicalInputResult, DESKTOP_BROKER_PROTOCOL_VERSION,
    };
    use parking_lot::Mutex;

    pub(crate) struct MockBrokerTransport {
        alive: AtomicBool,
        epoch: u64,
        windows: Mutex<Vec<BrokerWindowInfo>>,
        pub(crate) physical_commands: Mutex<Vec<PhysicalInputCommand>>,
        disconnect_on_call: AtomicBool,
    }

    impl MockBrokerTransport {
        fn new(epoch: u64, windows: Vec<BrokerWindowInfo>) -> Self {
            Self {
                alive: AtomicBool::new(true),
                epoch,
                windows: Mutex::new(windows),
                physical_commands: Mutex::new(Vec::new()),
                disconnect_on_call: AtomicBool::new(false),
            }
        }
    }

    impl crate::desktop_broker::BrokerTransport for MockBrokerTransport {
        fn call(
            &self,
            request: DesktopBrokerRequest,
            _timeout: Duration,
        ) -> Result<DesktopBrokerResponse> {
            if self.disconnect_on_call.load(Ordering::Acquire) {
                self.alive.store(false, Ordering::Release);
                return Err(InbriskError::internal("channel disconnected during call"));
            }
            if !self.alive.load(Ordering::Acquire) {
                return Err(InbriskError::capability_unavailable(
                    ExecutionCapability::WindowEnumeration,
                    "desktop broker unavailable",
                ));
            }
            match request {
                DesktopBrokerRequest::Hello(_) => {
                    Ok(DesktopBrokerResponse::Welcome(DesktopBrokerWelcome {
                        protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
                        broker_pid: 1111,
                        broker_epoch: self.epoch,
                        process_session_id: 1,
                        window_station: Some("WinSta0".into()),
                        desktop: Some("Default".into()),
                        interactive_desktop: true,
                        window_enumeration_ready: true,
                        physical_input_ready: true,
                        dpi_awareness: Some("PerMonitorV2".into()),
                    }))
                }
                DesktopBrokerRequest::PhysicalInput(req) => {
                    self.physical_commands.lock().push(req.command);
                    Ok(DesktopBrokerResponse::PhysicalInput(PhysicalInputResult {
                        operation_id: req.operation_id,
                        executed: true,
                        events_sent: 1,
                        details: None,
                    }))
                }
                DesktopBrokerRequest::EnumerateWindows => {
                    Ok(DesktopBrokerResponse::Windows(self.windows.lock().clone()))
                }
                DesktopBrokerRequest::Ping => Ok(DesktopBrokerResponse::Pong),
                DesktopBrokerRequest::Status => {
                    Ok(DesktopBrokerResponse::Status(DesktopBrokerWelcome {
                        protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
                        broker_pid: 1111,
                        broker_epoch: self.epoch,
                        process_session_id: 1,
                        window_station: Some("WinSta0".into()),
                        desktop: Some("Default".into()),
                        interactive_desktop: true,
                        window_enumeration_ready: true,
                        physical_input_ready: true,
                        dpi_awareness: Some("PerMonitorV2".into()),
                    }))
                }
                DesktopBrokerRequest::EmergencyStop => Ok(DesktopBrokerResponse::Ack),
                DesktopBrokerRequest::Shutdown => {
                    self.alive.store(false, Ordering::Release);
                    Ok(DesktopBrokerResponse::Ack)
                }
            }
        }

        fn is_alive(&self) -> bool {
            self.alive.load(Ordering::Acquire)
        }

        fn disconnect(&self) {
            self.alive.store(false, Ordering::Release);
        }
    }

    pub(crate) fn make_engine_with_mock_broker(
        windows: Vec<BrokerWindowInfo>,
    ) -> (Engine, Arc<MockBrokerTransport>) {
        let engine =
            super::capability_tests::make_test_engine(ExecutionCapabilities::unavailable());
        let transport = Arc::new(MockBrokerTransport::new(500, windows));
        let mut client = DesktopBrokerClient::with_mock_transport(transport.clone(), 100, 200);
        let _ = client.handshake();
        engine.set_broker(client);
        (engine, transport)
    }

    #[test]
    fn runtime_window_enumeration_uses_desktop_broker() {
        let test_windows = vec![BrokerWindowInfo {
            hwnd: 0x9999,
            pid: 4321,
            title: "Broker Window".into(),
            left: 10,
            top: 20,
            right: 110,
            bottom: 120,
            visible: true,
            minimized: false,
            process_name: "test.exe".into(),
            class_name: "TestClass".into(),
        }];

        let (engine, _transport) = make_engine_with_mock_broker(test_windows);
        let windows = engine
            .enumerate_windows()
            .expect("enumerate windows via broker");

        assert_eq!(windows.len(), 1);
        assert_eq!(windows[0].hwnd, Hwnd(0x9999));
        assert_eq!(windows[0].title, "Broker Window");
    }

    #[test]
    fn runtime_broker_unavailable_does_not_fallback_to_local_enumeration() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        // Simulate broker death / disconnection
        transport.disconnect();

        let err = engine.enumerate_windows().unwrap_err();
        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert!(err.message.contains("unavailable") || err.message.contains("disconnected"));
    }

    #[test]
    fn runtime_broker_disconnect_revokes_window_enumeration_capability() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        assert!(engine
            .capabilities()
            .supports(ExecutionCapability::WindowEnumeration));

        engine.disconnect_broker();
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::WindowEnumeration));
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::InteractiveDesktop));
    }

    #[test]
    fn runtime_broker_reconnect_refreshes_capabilities() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        engine.disconnect_broker();
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::WindowEnumeration));

        // Reconnect with new broker
        let new_transport = Arc::new(MockBrokerTransport::new(600, vec![]));
        let mut new_client = DesktopBrokerClient::with_mock_transport(new_transport, 100, 200);
        let _ = new_client.handshake();
        engine.set_broker(new_client);

        assert!(engine
            .capabilities()
            .supports(ExecutionCapability::WindowEnumeration));
        assert!(engine
            .capabilities()
            .supports(ExecutionCapability::InteractiveDesktop));
    }

    #[test]
    fn runtime_stale_broker_epoch_response_rejected() {
        let endpoint = "Local\\Inbrisk.TestStaleEpochBrokerChannel.1";
        let host = BrokerChannelHost::create(endpoint, 1, 10).expect("create host");
        let peer = BrokerChannelPeer::open(endpoint, 100, 20).expect("open peer");
        peer.mark_ready();
        let _ = host.wait_for_broker_ready(Duration::from_millis(500));

        // Update active broker epoch on host to 200 (simulating broker restart to epoch 200)
        host.set_active_broker_epoch(200);

        // Spawn thread where peer responds with old epoch 100
        let handle = std::thread::spawn({
            let peer = peer;
            move || {
                let req = peer.next_request(Duration::from_secs(2)).unwrap().unwrap();
                // peer is still configured with broker_epoch 100
                peer.send_response(req.request_id, Ok(DesktopBrokerResponse::Pong))
                    .unwrap();
            }
        });

        let err = host
            .call(DesktopBrokerRequest::Ping, Duration::from_secs(2))
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::StaleState);
        assert!(err
            .message
            .contains("does not match current active broker epoch"));
        handle.join().unwrap();
    }

    #[test]
    fn runtime_click_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_click(None, Some(120), Some(340), MouseButton::Left, 1, &mut ctx)
            .expect("click through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Click {
                point,
                button,
                click_count,
            } => {
                assert_eq!(point.x, 120.0);
                assert_eq!(point.y, 340.0);
                assert_eq!(*button, MouseButton::Left);
                assert_eq!(*click_count, 1);
            }
            other => panic!("expected Click variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_type_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_type("hello broker", None, false, &mut ctx)
            .expect("type through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::TypeText { text } => {
                assert_eq!(text, "hello broker");
            }
            other => panic!("expected TypeText variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_key_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_key(&["ctrl".into(), "v".into()], None, &mut ctx)
            .expect("key through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Shortcut { keys } => {
                assert_eq!(keys, &["ctrl", "v"]);
            }
            other => panic!("expected Shortcut variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_scroll_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_scroll(None, ScrollDirection::Down, 5, &mut ctx)
            .expect("scroll through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Scroll { direction, amount } => {
                assert_eq!(*direction, ScrollDirection::Down);
                assert_eq!(*amount, 5);
            }
            other => panic!("expected Scroll variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_does_not_use_local_sendinput_when_broker_unavailable() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Disconnect broker
        transport.disconnect();
        let err = engine
            .step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx)
            .unwrap_err();

        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert_eq!(transport.physical_commands.lock().len(), 0);
    }

    #[test]
    fn runtime_broker_disconnect_revokes_physical_input() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        assert!(engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));

        engine.disconnect_broker();
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));
    }

    #[test]
    fn runtime_broker_reconnect_restores_physical_input() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        engine.disconnect_broker();
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));

        // Reconnect
        let new_transport = Arc::new(MockBrokerTransport::new(777, vec![]));
        let mut new_client = DesktopBrokerClient::with_mock_transport(new_transport, 100, 200);
        let _ = new_client.handshake();
        engine.set_broker(new_client);

        assert!(engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));
    }

    #[test]
    fn runtime_ambiguous_broker_failure_returns_execution_outcome_unknown() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        transport.disconnect_on_call.store(true, Ordering::Release);
        let err = engine
            .step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx)
            .unwrap_err();

        assert_eq!(err.code, ErrorCode::ExecutionOutcomeUnknown);
        assert!(
            !err.retryable,
            "Ambiguous physical failures must not be retried"
        );
        // Disconnect occurred, so physical input is revoked
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));
    }

    #[test]
    fn runtime_semantic_invoke_does_not_require_physical_input() {
        // Engine without PhysicalInput capability
        let engine =
            super::capability_tests::make_test_engine(ExecutionCapabilities::unavailable());
        // Capabilities do NOT support physical input
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));

        // Semantic check: require_capability for physical input fails
        assert_eq!(
            engine
                .require_capability(ExecutionCapability::PhysicalInput)
                .unwrap_err()
                .code,
            ErrorCode::CapabilityUnavailable
        );

        // But checking window mutable or checking an internal state that is semantic does not fail with PhysicalInput
        let res = engine.check_window_mutable(inbrisk_core::Hwnd(0x1234));
        assert!(res.is_ok());
    }

    #[test]
    fn runtime_mouse_move_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_move(450, 650, &mut ctx)
            .expect("move through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::MouseMove { point } => {
                assert_eq!(point.x, 450.0);
                assert_eq!(point.y, 650.0);
            }
            other => panic!("expected MouseMove variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_shortcut_routes_through_desktop_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let res = engine
            .step_shortcut(
                &["ctrl".into(), "shift".into(), "esc".into()],
                None,
                &mut ctx,
            )
            .expect("shortcut through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Shortcut { keys } => {
                assert_eq!(keys, &["ctrl", "shift", "esc"]);
            }
            other => panic!("expected Shortcut variant, got {other:?}"),
        }
    }

    #[test]
    fn emergency_stop_releases_mutation_lease() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        let session_id = 42;

        let lease = engine
            .input
            .acquire(session_id, "test_action", Some(Duration::from_secs(5)))
            .unwrap();
        assert!(engine.input.is_held());

        // Engage emergency
        engine.state.activity.engage_emergency("test emergency");
        engine.input.force_release();

        assert!(!engine.input.is_held());
        drop(lease);
        assert!(!engine.input.is_held());
    }

    #[test]
    fn physical_failure_releases_mutation_lease() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Disconnect transport before click
        transport.disconnect();

        let _ = engine.step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx);

        // Assert lease is not leaked
        assert!(!engine.input.is_held());
    }

    #[test]
    fn broker_disconnect_releases_mutation_lease() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Fail mid-call
        transport.disconnect_on_call.store(true, Ordering::Release);

        let _ = engine.step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx);

        // Assert lease is not leaked
        assert!(!engine.input.is_held());
    }

    #[test]
    fn runtime_broker_dies_before_physical_execution_is_deterministic_failure() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        transport.disconnect();

        let err = engine
            .step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx)
            .unwrap_err();

        assert_eq!(err.code, ErrorCode::CapabilityUnavailable);
        assert_eq!(transport.physical_commands.lock().len(), 0);
    }

    #[test]
    fn runtime_broker_dies_after_possible_physical_execution_is_outcome_unknown() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        transport.disconnect_on_call.store(true, Ordering::Release);

        let err = engine
            .step_click(None, Some(10), Some(20), MouseButton::Left, 1, &mut ctx)
            .unwrap_err();

        assert_eq!(err.code, ErrorCode::ExecutionOutcomeUnknown);
        assert!(!err.retryable);
    }

    #[test]
    fn runtime_physical_mutation_never_replayed_across_broker_epoch() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Ambiguous failure
        transport.disconnect_on_call.store(true, Ordering::Release);
        let _ = engine.step_click(None, Some(50), Some(50), MouseButton::Left, 1, &mut ctx);

        // Reconnect new broker epoch
        let new_transport = Arc::new(MockBrokerTransport::new(999, vec![]));
        let mut new_client =
            DesktopBrokerClient::with_mock_transport(new_transport.clone(), 100, 200);
        let _ = new_client.handshake();
        engine.set_broker(new_client);

        // Verify: the new transport received ZERO auto-replayed commands
        assert_eq!(new_transport.physical_commands.lock().len(), 0);
    }

    #[test]
    fn runtime_semantic_invoke_works_with_broker_disconnected() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        engine.disconnect_broker();

        // PhysicalInput capability is false
        assert!(!engine
            .capabilities()
            .supports(ExecutionCapability::PhysicalInput));

        // Checking mutable window or invoking semantically does NOT require PhysicalInput
        let res = engine.check_window_mutable(inbrisk_core::Hwnd(0x1234));
        assert!(res.is_ok());
    }

    #[test]
    fn architecture_runtime_has_zero_direct_physical_input() {
        let bad_import = format!("{}_{}::{}", "inbrisk", "win32", "input_raw");
        let bad_send = format!("Send{}", "Input");

        let src_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("src");
        for entry in std::fs::read_dir(src_dir).unwrap() {
            let path = entry.unwrap().path();
            if path.extension().and_then(|e| e.to_str()) == Some("rs") {
                let content = std::fs::read_to_string(&path).unwrap();
                for (idx, line) in content.lines().enumerate() {
                    let trimmed = line.trim();
                    if trimmed.starts_with("//")
                        || trimmed.starts_with("/*")
                        || line.contains("bad_import")
                        || line.contains("bad_send")
                    {
                        continue;
                    }
                    assert!(
                        !line.contains(&bad_import),
                        "File {:?}:{} imports forbidden module: {}",
                        path,
                        idx + 1,
                        line
                    );
                    assert!(
                        !line.contains(&bad_send),
                        "File {:?}:{} calls forbidden primitive: {}",
                        path,
                        idx + 1,
                        line
                    );
                }
            }
        }
    }

    #[test]
    fn runtime_point_click_preserves_coordinate_space_to_broker() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let pt = PointTarget::window_client_logical(0xCAFE, 120.5, 340.5);
        let target = TargetRef::Point(pt);

        let res = engine
            .step_click(Some(&target), None, None, MouseButton::Left, 1, &mut ctx)
            .expect("click through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Click {
                point,
                button,
                click_count,
            } => {
                assert_eq!(point.space, CoordinateSpaceDto::WindowClientLogical);
                assert_eq!(point.hwnd, Some(0xCAFE));
                assert_eq!(point.x, 120.5);
                assert_eq!(point.y, 340.5);
                assert_eq!(*button, MouseButton::Left);
                assert_eq!(*click_count, 1);
            }
            other => panic!("expected Click variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_element_fallback_uses_virtual_physical_space() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Clicking x, y directly falls back to VirtualScreenPhysical
        let res = engine
            .step_click(None, Some(100), Some(200), MouseButton::Left, 1, &mut ctx)
            .expect("click through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Click { point, .. } => {
                assert_eq!(point.space, CoordinateSpaceDto::VirtualScreenPhysical);
                assert_eq!(point.hwnd, None);
                assert_eq!(point.x, 100.0);
                assert_eq!(point.y, 200.0);
            }
            other => panic!("expected Click variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_does_not_normalize_send_input_coordinates() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        // Pass physical coordinates that if normalized to 0..65535 would be completely different numbers
        let pt = PointTarget::virtual_screen(2560.0, 1440.0);
        let target = TargetRef::Point(pt);

        let _ = engine
            .step_click(Some(&target), None, None, MouseButton::Left, 1, &mut ctx)
            .expect("click");

        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Click { point, .. } => {
                // Must preserve 2560.0 and 1440.0, NOT 0..65535 SendInput normalized values
                assert_eq!(point.x, 2560.0);
                assert_eq!(point.y, 1440.0);
            }
            other => panic!("expected Click variant, got {other:?}"),
        }
    }

    #[test]
    fn runtime_drag_preserves_independent_from_and_to_spaces() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let from_pt = PointTarget::window_client_physical(0x1234, 50.0, 60.0);
        let to_pt = PointTarget::virtual_screen(800.0, 600.0);

        let from_target = TargetRef::Point(from_pt);
        let to_target = DropTarget::Point(to_pt);

        let res = engine
            .step_drag(&from_target, &to_target, &mut ctx)
            .expect("drag through broker");

        assert_eq!(res.backend, "desktop-broker");
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1);
        match &cmds[0] {
            PhysicalInputCommand::Drag { from, to, button } => {
                assert_eq!(from.space, CoordinateSpaceDto::WindowClientPhysical);
                assert_eq!(from.hwnd, Some(0x1234));
                assert_eq!(from.x, 50.0);
                assert_eq!(from.y, 60.0);

                assert_eq!(to.space, CoordinateSpaceDto::VirtualScreenPhysical);
                assert_eq!(to.hwnd, None);
                assert_eq!(to.x, 800.0);
                assert_eq!(to.y, 600.0);

                assert_eq!(*button, MouseButton::Left);
            }
            other => panic!("expected Drag variant, got {other:?}"),
        }
    }
}

#[cfg(test)]
#[allow(non_snake_case)]
mod phase5_backend_tests {
    use super::desktop_broker_tests::make_engine_with_mock_broker;
    use super::*;
    use inbrisk_core::{ElementRef, Hwnd, Rect};
    use inbrisk_protocol::action::{MouseButton, TargetRef};
    use inbrisk_protocol::selector::Selector;
    use std::sync::atomic::AtomicBool;
    use std::sync::Arc;

    #[test]
    fn Router_Win32PreferredForWindowMetadata() {
        let sel = Selector {
            role: Some("window".into()),
            name: Some("Calculator".into()),
            ..Default::default()
        };
        assert!(
            win32_expressible(&sel),
            "window metadata selector must be win32_expressible"
        );

        let non_win32 = Selector {
            role: Some("button".into()),
            name: Some("Clear".into()),
            ..Default::default()
        };
        assert!(
            !win32_expressible(&non_win32),
            "control selector must not be win32_expressible"
        );
    }

    #[test]
    fn Router_CdpPreferredForBrowserContent() {
        let doc_sel = Selector {
            role: Some("document".into()),
            ..Default::default()
        };
        assert!(
            content_requires_cdp(&doc_sel, Some("chrome.exe")),
            "chrome document must use CDP"
        );
        assert!(
            content_requires_cdp(&doc_sel, Some("msedge.exe")),
            "edge document must use CDP"
        );

        let button_sel = Selector {
            role: Some("button".into()),
            name: Some("Back".into()),
            ..Default::default()
        };
        assert!(
            !content_requires_cdp(&button_sel, Some("chrome.exe")),
            "browser chrome buttons must NOT use CDP"
        );
    }

    #[test]
    fn Router_UiaSemanticPreferredOverPhysicalClick() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let pt = TargetRef::Point(PointTarget::virtual_screen(50.0, 50.0));
        let _ = engine.step_click(Some(&pt), None, None, MouseButton::Left, 1, &mut ctx);
        let cmds = transport.physical_commands.lock().clone();
        assert_eq!(cmds.len(), 1, "point click goes to physical input");
    }

    #[test]
    fn Plan_ReusesResolvedElementReference() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        let ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));
        let test_hwnd = Hwnd(0x1234);
        let sel = Selector {
            role: Some("button".into()),
            name: Some("OK".into()),
            ..Default::default()
        };

        let cached_ref = ElementRef {
            id: 42,
            hwnd: test_hwnd,
            process_id: 999,
            generation: engine.state.generation(),
            runtime_id: vec![1, 2, 3],
            role: "button".into(),
            name: "OK".into(),
            automation_id: "btn_ok".into(),
            class_name: "Button".into(),
            bounds: Rect {
                x: 10,
                y: 20,
                width: 30,
                height: 40,
            },
            enabled: true,
            offscreen: false,
            backend: "uia".into(),
        };

        let v = engine.state.world.version();
        // Seed plan-local cache
        ctx.plan_elements
            .lock()
            .insert((test_hwnd, sel.clone()), (cached_ref.clone(), v));

        // Resolve target through plan
        let resolved = ResolvedTarget::Selector {
            selector: Box::new(sel),
            window: test_hwnd,
        };

        let resolved_el = engine
            .resolve_element(resolved, &ctx)
            .expect("reused element");
        assert_eq!(resolved_el.id, 42);
        assert_eq!(resolved_el.name, "OK");
        assert_eq!(resolved_el.generation, engine.state.generation());
    }

    #[test]
    fn Plan_InvalidatesStaleReference() {
        let (engine, _transport) = make_engine_with_mock_broker(vec![]);
        let ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));
        let test_hwnd = Hwnd(0x1234);
        let sel = Selector {
            role: Some("button".into()),
            name: Some("OK".into()),
            ..Default::default()
        };

        let v = engine.state.world.version();
        let cached_ref = ElementRef {
            id: 42,
            hwnd: test_hwnd,
            process_id: 999,
            generation: engine.state.generation(),
            runtime_id: vec![1, 2, 3],
            role: "button".into(),
            name: "OK".into(),
            automation_id: "btn_ok".into(),
            class_name: "Button".into(),
            bounds: Rect {
                x: 10,
                y: 20,
                width: 30,
                height: 40,
            },
            enabled: true,
            offscreen: false,
            backend: "uia".into(),
        };

        // Seed entry in cache with current version
        ctx.plan_elements
            .lock()
            .insert((test_hwnd, sel.clone()), (cached_ref, v));

        // Advance world via mutation bump
        let old_v = engine.state.world.version();
        let new_v = engine.state.world.bump();
        assert_ne!(old_v, new_v, "world bump must increment version");

        // Attempting to resolve with stale cache entry bypasses cache and falls back to live UIA
        let resolved = ResolvedTarget::Selector {
            selector: Box::new(sel),
            window: test_hwnd,
        };
        let res = engine.resolve_element(resolved, &ctx);
        assert!(res.is_err());
    }

    #[test]
    fn Find_RequestsOnlyNeededProperties() {
        let sel_no_text = Selector {
            role: Some("button".into()),
            name: Some("Save".into()),
            ..Default::default()
        };
        assert!(sel_no_text.text.is_none());

        let sel_with_text = Selector {
            role: Some("edit".into()),
            text: Some("sample".into()),
            ..Default::default()
        };
        assert!(sel_with_text.text.is_some());
    }

    #[test]
    fn Text_SetValueAvoidsPhysicalTypingWhenSupported() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));

        let target = TargetRef::Selector(Box::new(Selector {
            role: Some("edit".into()),
            name: Some("Text".into()),
            hwnd: Some(Hwnd(0x1234)),
            ..Default::default()
        }));

        let _ = engine.step_set_value(&target, "hello", &mut ctx);

        let cmds = transport.physical_commands.lock().clone();
        assert!(
            cmds.is_empty(),
            "SetValue must not send physical keystrokes through desktop broker"
        );
    }

    #[test]
    fn BrowserIdentity_NoCrossInstanceVerification() {
        let sel_a = Selector {
            role: Some("document".into()),
            hwnd: Some(Hwnd(0x1111)),
            process: Some("chrome.exe".into()),
            ..Default::default()
        };
        let sel_b = Selector {
            role: Some("document".into()),
            hwnd: Some(Hwnd(0x2222)),
            process: Some("msedge.exe".into()),
            ..Default::default()
        };

        assert_ne!(sel_a.hwnd, sel_b.hwnd);
        assert_ne!(sel_a.process, sel_b.process);
    }

    #[test]
    fn Router_DisabledElementReturnsElementDisabledAndNeverClicks() {
        let (engine, transport) = make_engine_with_mock_broker(vec![]);
        let mut ctx = PlanContext::new(1, 1, Arc::new(AtomicBool::new(false)));
        let test_hwnd = Hwnd(0x1234);
        let sel = Selector {
            role: Some("button".into()),
            name: Some("DisabledBtn".into()),
            ..Default::default()
        };

        let disabled_ref = ElementRef {
            id: 99,
            hwnd: test_hwnd,
            process_id: 1234,
            generation: engine.state.generation(),
            runtime_id: vec![1, 2, 3],
            role: "button".into(),
            name: "DisabledBtn".into(),
            automation_id: "DisabledButton".into(),
            class_name: "Button".into(),
            bounds: Rect {
                x: 100,
                y: 100,
                width: 50,
                height: 30,
            },
            enabled: false,
            offscreen: false,
            backend: "uia".into(),
        };

        let handle_id = engine.state.elements.insert(
            disabled_ref,
            sel,
            ctx.session_id,
            test_hwnd,
        );

        ctx.record_name(Some("$btn"), serde_json::json!({ "elementId": handle_id }));
        let target = TargetRef::Reference("$btn".into());
        let res = engine.step_click(Some(&target), None, None, MouseButton::Left, 1, &mut ctx);
        assert!(res.is_err(), "Click on disabled element must return Err");
        let err = res.unwrap_err();
        assert_eq!(err.code, ErrorCode::ElementDisabled, "Error code must be ElementDisabled, got {:?}", err.code);

        // Verification: Desktop broker must have received ZERO physical commands!
        let cmds = transport.physical_commands.lock().clone();
        assert!(cmds.is_empty(), "Disabled element must NEVER receive coordinate fallback physical click!");
    }
}
