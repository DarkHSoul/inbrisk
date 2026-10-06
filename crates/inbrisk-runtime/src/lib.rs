//! Inbrisk runtime: the single persistent native process.
//!
//! Composition (nothing below is optional in the final architecture):
//!
//! ```text
//! Runtime
//! ├─ RuntimeState      world version, ownership, element cache, counters
//! ├─ Engine            execution router + plan executor + wait service
//! ├─ IpcServer         shared-memory fast path (WaitOnAddress)
//! └─ Dispatcher        protocol in, protocol out
//! ```
//!
//! The loop never busy-waits: it sleeps on the region's `request_epoch` word and
//! is woken by a client in microseconds. Long operations are handed to a bounded
//! worker set so a `run` can never block a `status`.

pub mod desktop_broker;
pub mod dispatch;
pub mod engine;
pub mod state;
pub mod terminal;

pub use desktop_broker::{BrokerTransport, DesktopBrokerClient};

use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use inbrisk_core::{
    log_debug, log_error, log_info, log_warn, ActivityManager, InbriskError, Result,
    RuntimeIdentity,
};
use inbrisk_input::GlobalInputArbiter;
use inbrisk_ipc::IpcServer;
use inbrisk_policy::PolicyEngine;
use inbrisk_protocol::caps::Capabilities;
use inbrisk_protocol::event::Event;
use inbrisk_protocol::response::Reply;
use inbrisk_protocol::PROTOCOL_VERSION;
use inbrisk_uia::UiaService;

pub use dispatch::Dispatcher;
pub use engine::{
    CancelRegistry, Engine, FindMatches, PlanContext, ResolvedTarget, WaitService, WorldFingerprint,
};
pub use state::{AppProfile, Backend, CachedHandle, ElementCache, RuntimeState, PROFILES};

pub const RUNTIME_NAME: &str = "inbrisk-runtime";
pub const RUNTIME_VERSION: &str = env!("CARGO_PKG_VERSION");

/// How the runtime is configured. Defaults are chosen so that a plain
/// `inbrisk.exe` is already the production configuration.
#[derive(Debug, Clone)]
pub struct RuntimeConfig {
    pub readers: usize,
    pub operation_timeout: Duration,
    pub max_concurrent_operations: usize,
    pub event_bridge: bool,
    pub label: String,
    /// Maximum simultaneously open persistent terminal sessions.
    pub max_terminal_sessions: usize,
}

impl Default for RuntimeConfig {
    fn default() -> Self {
        Self {
            readers: 3,
            operation_timeout: Duration::from_secs(30),
            max_concurrent_operations: 8,
            event_bridge: true,
            label: "inbrisk".into(),
            max_terminal_sessions: 8,
        }
    }
}

/// The runtime process.
pub struct Runtime {
    pub config: RuntimeConfig,
    pub state: Arc<RuntimeState>,
    pub engine: Arc<Engine>,
    pub ipc: Arc<IpcServer>,
    pub dispatcher: Arc<Dispatcher>,
    pub identity: RuntimeIdentity,
    capabilities: Capabilities,
    shutting_down: Arc<AtomicBool>,
    in_flight: Arc<AtomicUsize>,
    handles: std::sync::Mutex<Vec<std::thread::JoinHandle<()>>>,
}

impl std::fmt::Debug for Runtime {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Runtime")
            .field("region", &self.ipc.name())
            .field("epoch", &self.ipc.epoch())
            .finish()
    }
}

impl Runtime {
    /// Build everything and publish the region.
    pub fn start(config: RuntimeConfig) -> Result<Arc<Self>> {
        let capabilities = Capabilities::empty()
            .with(Capabilities::WIN32)
            .with(Capabilities::UIA)
            .with(Capabilities::PHYSICAL_INPUT)
            .with(Capabilities::EVENTS)
            .with(Capabilities::CANCEL)
            .with(Capabilities::PLANS)
            .with(Capabilities::DRY_RUN)
            .with(Capabilities::CDP)
            .with(Capabilities::TERMINAL);

        let activity = Arc::new(ActivityManager::new());
        let state = Arc::new(RuntimeState::new(Arc::clone(&activity)));
        let policy = Arc::new(PolicyEngine::windows_default());
        let uia = Arc::new(UiaService::new(config.readers, Duration::from_secs(8))?);
        let input = Arc::new(GlobalInputArbiter::new(Duration::from_secs(30)));
        let waits = Arc::new(WaitService::start()?);
        let cancels = Arc::new(CancelRegistry::new());

        let engine = Arc::new(Engine::new(
            Arc::clone(&state),
            uia,
            input,
            Arc::clone(&policy),
            waits,
            cancels,
        ));

        let ipc = Arc::new(IpcServer::create(capabilities, RUNTIME_VERSION)?);
        let identity = RuntimeIdentity {
            name: RUNTIME_NAME.to_string(),
            version: RUNTIME_VERSION.to_string(),
            protocol_version: PROTOCOL_VERSION,
            pid: std::process::id(),
            epoch: ipc.epoch(),
        };

        let shutting_down = Arc::new(AtomicBool::new(false));
        let dispatcher = Arc::new(Dispatcher {
            engine: Arc::clone(&engine),
            ipc: Arc::clone(&ipc),
            identity: identity.clone(),
            capabilities,
            shutting_down: Arc::clone(&shutting_down),
            read_timeout: config.operation_timeout.min(Duration::from_secs(8)),
            terminals: Arc::new(terminal::TerminalManager::new(
                config.max_terminal_sessions,
                {
                    let activity = Arc::clone(&activity);
                    Box::new(move || {
                        if activity.is_emergency() {
                            return Err(InbriskError::new(
                                inbrisk_core::ErrorCode::Emergency,
                                "emergency stop is active; terminal open/write refused",
                            ));
                        }
                        if activity.is_paused() {
                            return Err(InbriskError::new(
                                inbrisk_core::ErrorCode::Paused,
                                "Inbrisk is paused; terminal open/write refused",
                            ));
                        }
                        Ok(())
                    })
                },
            )),
        });

        // Learn the current desktop so protected surfaces are stamped before
        // any agent can ask about them.
        state.ensure_protected(&policy);

        // In interactive user sessions (Session > 0), spawn and attach desktop broker
        match DesktopBrokerClient::spawn_and_connect(ipc.epoch(), std::process::id()) {
            Ok(broker) => {
                log_info!("runtime", "interactive desktop broker connected");
                engine.set_broker(broker);
            }
            Err(e) => {
                log_warn!(
                    "runtime",
                    "desktop broker not active ({e}); running in standalone mode"
                );
            }
        }

        log_info!(
            "runtime",
            "{RUNTIME_NAME} {RUNTIME_VERSION} listening on '{}' ({} KiB, pid {})",
            ipc.name(),
            ipc.region_bytes() / 1024,
            std::process::id()
        );

        Ok(Arc::new(Self {
            config,
            state,
            engine,
            ipc,
            dispatcher,
            identity,
            capabilities,
            shutting_down,
            in_flight: Arc::new(AtomicUsize::new(0)),
            handles: std::sync::Mutex::new(Vec::new()),
        }))
    }

    pub fn capabilities(&self) -> Capabilities {
        self.capabilities
    }

    pub fn activity(&self) -> &Arc<ActivityManager> {
        &self.state.activity
    }

    pub fn region_name(&self) -> &str {
        self.ipc.name()
    }

    pub fn epoch(&self) -> u64 {
        self.ipc.epoch()
    }

    pub fn request_shutdown(&self) {
        self.shutting_down.store(true, Ordering::Release);
    }

    /// Emergency stop: block every mutation, drop the physical input lease and
    /// cancel every running plan. Only a human can undo this.
    pub fn emergency_stop(&self, reason: &str) -> u32 {
        self.state.activity.engage_emergency(reason.to_string());
        self.engine.input.force_release();
        if let Some(broker) = self.engine.broker.read().as_ref() {
            let _ = broker.emergency_stop();
        }
        let cancelled = self.engine.cancels.cancel_all();
        let _ = self
            .ipc
            .publish_event(&inbrisk_protocol::event::Event::Emergency {
                engaged: true,
                reason: reason.to_string(),
            });
        log_info!("runtime", "emergency stop engaged: {reason}");
        cancelled
    }

    pub fn clear_emergency(&self) {
        self.state.activity.clear_emergency();
        let _ = self
            .ipc
            .publish_event(&inbrisk_protocol::event::Event::Emergency {
                engaged: false,
                reason: "cleared by the human".into(),
            });
    }

    pub fn pause(&self, reason: &str) {
        self.state.activity.pause(reason.to_string());
        let _ = self
            .ipc
            .publish_event(&inbrisk_protocol::event::Event::Paused {
                reason: reason.to_string(),
            });
    }

    pub fn resume(&self) {
        self.state.activity.resume();
        let _ = self
            .ipc
            .publish_event(&inbrisk_protocol::event::Event::Resumed);
    }

    pub fn in_flight(&self) -> usize {
        self.in_flight.load(Ordering::Acquire)
    }

    pub fn is_shutting_down(&self) -> bool {
        self.shutting_down.load(Ordering::Acquire)
    }

    /// Publish activity transitions to event subscribers (coalesced).
    fn spawn_event_bridge(self: &Arc<Self>) {
        if !self.config.event_bridge {
            return;
        }
        let mut rx = self.state.activity.subscribe();
        let ipc = Arc::clone(&self.ipc);
        let stop = Arc::clone(&self.shutting_down);
        let handle = std::thread::Builder::new()
            .name("inbrisk-events".into())
            .spawn(move || {
                let mut last = inbrisk_core::ActivityState::Idle;
                loop {
                    if stop.load(Ordering::Acquire) {
                        break;
                    }
                    match rx.try_recv() {
                        Ok(snapshot) => {
                            if snapshot.state != last {
                                last = snapshot.state;
                                let _ = ipc.publish_event(&Event::Activity(snapshot));
                            }
                        }
                        Err(tokio::sync::broadcast::error::TryRecvError::Lagged(_)) => continue,
                        Err(tokio::sync::broadcast::error::TryRecvError::Closed) => break,
                        Err(tokio::sync::broadcast::error::TryRecvError::Empty) => {
                            // Idle: the broadcast channel has no blocking recv in
                            // a sync thread, so we poll at a human-imperceptible
                            // rate instead of spinning.
                            std::thread::sleep(Duration::from_millis(80));
                        }
                    }
                }
            })
            .ok();
        if let Some(h) = handle {
            self.handles.lock().unwrap().push(h);
        }
    }

    /// Watch the desktop and publish window lifecycle events.
    fn spawn_window_watcher(self: &Arc<Self>) {
        let ipc = Arc::clone(&self.ipc);
        let state = Arc::clone(&self.state);
        let stop = Arc::clone(&self.shutting_down);
        let handle = std::thread::Builder::new()
            .name("inbrisk-windows".into())
            .spawn(move || {
                let mut known: std::collections::HashMap<isize, String> = Default::default();
                let mut tick: u64 = 0;
                while !stop.load(Ordering::Acquire) {
                    std::thread::sleep(Duration::from_millis(1000));
                    tick += 1;
                    let raws = inbrisk_win32::window::enumerate_top_level();
                    let mut seen = std::collections::HashSet::new();
                    for raw in &raws {
                        seen.insert(raw.hwnd.0);
                        match known.get(&raw.hwnd.0) {
                            None => {
                                let window = state.decorate(raw);
                                let _ = ipc.publish_event(&Event::WindowOpened(window));
                                known.insert(raw.hwnd.0, raw.title.clone());
                            }
                            Some(title) if title != &raw.title => {
                                known.insert(raw.hwnd.0, raw.title.clone());
                            }
                            _ => {}
                        }
                    }
                    for (hwnd, _) in known.clone() {
                        if !seen.contains(&hwnd) {
                            known.remove(&hwnd);
                            state.forget_window(inbrisk_core::Hwnd(hwnd));
                            let _ = ipc.publish_event(&Event::WindowClosed {
                                hwnd: inbrisk_core::Hwnd(hwnd),
                                process_id: 0,
                            });
                        }
                    }
                    if tick % 5 == 0 {
                        state.prune();
                        ipc.reap();
                        ipc.set_state(state.state_version(), state.generation());
                    }
                }
            })
            .ok();
        if let Some(h) = handle {
            self.handles.lock().unwrap().push(h);
        }
    }

    /// Run a long operation on a bounded worker thread.
    fn spawn_operation(self: &Arc<Self>, incoming: inbrisk_ipc::IncomingRequest) {
        let dispatcher = Arc::clone(&self.dispatcher);
        let ipc = Arc::clone(&self.ipc);
        let in_flight = Arc::clone(&self.in_flight);
        let limit = self.config.max_concurrent_operations;
        if in_flight.load(Ordering::Acquire) >= limit {
            let reply = Reply::err(
                incoming.request_id,
                incoming.request.op(),
                InbriskError::new(
                    inbrisk_core::ErrorCode::BudgetExhausted,
                    format!("runtime is already running {limit} operations"),
                )
                .with_hint("cancel something, or retry shortly"),
                self.state.state_version(),
                self.state.generation(),
                0,
            )
            .with_session(incoming.session_id);
            let _ = ipc.respond(incoming.session_id, &reply);
            return;
        }

        let session = incoming.session_id;
        in_flight.fetch_add(1, Ordering::AcqRel);
        let counter = Arc::clone(&in_flight);
        let handle = std::thread::Builder::new()
            .name("inbrisk-op".into())
            .spawn(move || {
                let reply = dispatcher.handle(incoming);
                if let Err(e) = ipc.respond(session, &reply) {
                    log_debug!(
                        "runtime",
                        "could not deliver reply to session {session}: {e}"
                    );
                }
                counter.fetch_sub(1, Ordering::AcqRel);
            });
        match handle {
            Ok(h) => self.handles.lock().unwrap().push(h),
            Err(e) => {
                in_flight.fetch_sub(1, Ordering::AcqRel);
                log_error!("runtime", "cannot spawn an operation thread: {e}");
            }
        }
    }

    /// The main loop. Returns when shutdown is requested.
    pub fn run(self: &Arc<Self>) -> Result<()> {
        self.spawn_event_bridge();
        self.spawn_window_watcher();

        let maintenance = Duration::from_millis(250);
        let started = Instant::now();
        while !self.is_shutting_down() {
            match self.ipc.wait(maintenance) {
                Some(incoming) => {
                    if dispatch::is_long_running(incoming.request.op()) {
                        self.spawn_operation(incoming);
                    } else {
                        let reply = self.dispatcher.handle(incoming);
                        if let Err(e) = self.ipc.respond(reply.session_id.unwrap_or(0), &reply) {
                            log_debug!("runtime", "reply failed: {e}");
                        }
                    }
                }
                None => {}
            }
        }

        log_info!(
            "runtime",
            "stopping after {}s uptime",
            started.elapsed().as_secs()
        );
        // Persistent shells die with the runtime — deterministically.
        self.dispatcher.terminals.shutdown_all();
        self.ipc.shutdown("runtime stopping");
        self.state.activity.clear_emergency();
        Ok(())
    }

    /// Join the worker threads (best effort, short wait).
    pub fn join_workers(&self) {
        let handles = std::mem::take(&mut *self.handles.lock().unwrap());
        for h in handles {
            let _ = h.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn runtime_core_components_construct() {
        let activity = Arc::new(ActivityManager::new());
        let _state = Arc::new(RuntimeState::new(Arc::clone(&activity)));
        let _policy = Arc::new(PolicyEngine::windows_default());
        // UIA apartment creation exercises the dynamically loaded COM path.
        let _ = inbrisk_uia::UiaApartment::open();
    }
}
