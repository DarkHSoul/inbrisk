//! Desktop Broker client for `inbrisk-runtime`.
//!
//! Spawns, manages, and routes window enumeration requests to the dedicated
//! `inbrisk-desktop.exe` interactive broker process.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use inbrisk_core::{log_info, ErrorCode, ExecutionCapability, InbriskError, Result};
use inbrisk_ipc::{broker_endpoint_name, BrokerChannelHost};
use inbrisk_protocol::action::{MouseButton, PointTarget, ScrollDirection};
use inbrisk_protocol::desktop_broker::{
    BrokerWindowInfo, DesktopBrokerHello, DesktopBrokerReport, DesktopBrokerRequest,
    DesktopBrokerResponse, DesktopBrokerWelcome, PhysicalInputCommand, PhysicalInputGuard,
    PhysicalInputRequest, PhysicalInputResult, DESKTOP_BROKER_PROTOCOL_VERSION,
};
use parking_lot::RwLock;

/// Transport abstraction allowing live IPC or in-memory testing mocks.
pub trait BrokerTransport: Send + Sync {
    fn call(
        &self,
        request: DesktopBrokerRequest,
        timeout: Duration,
    ) -> Result<DesktopBrokerResponse>;
    fn is_alive(&self) -> bool;
    fn disconnect(&self);
}

/// Real IPC implementation using `BrokerChannelHost`.
pub struct ChannelTransport {
    host: BrokerChannelHost,
    child_pid: Option<u32>,
    connected: AtomicBool,
}

impl ChannelTransport {
    pub fn new(host: BrokerChannelHost, child_pid: Option<u32>) -> Self {
        Self {
            host,
            child_pid,
            connected: AtomicBool::new(true),
        }
    }
}

impl BrokerTransport for ChannelTransport {
    fn call(
        &self,
        request: DesktopBrokerRequest,
        timeout: Duration,
    ) -> Result<DesktopBrokerResponse> {
        if !self.connected.load(Ordering::Acquire) {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::WindowEnumeration,
                "desktop broker is disconnected",
            ));
        }

        match self.host.call(request, timeout) {
            Ok(resp) => Ok(resp),
            Err(e) => {
                if !self.is_alive() {
                    self.connected.store(false, Ordering::Release);
                    return Err(InbriskError::capability_unavailable(
                        ExecutionCapability::WindowEnumeration,
                        format!("desktop broker process exited: {e}"),
                    ));
                }
                Err(e)
            }
        }
    }

    fn is_alive(&self) -> bool {
        if !self.connected.load(Ordering::Acquire) {
            return false;
        }
        if let Some(pid) = self.child_pid {
            inbrisk_win32::process::is_alive(pid)
        } else {
            true
        }
    }

    fn disconnect(&self) {
        self.connected.store(false, Ordering::Release);
    }
}

/// Client managing the lifecycle of the desktop broker connection.
#[derive(Clone)]
pub struct DesktopBrokerClient {
    transport: Arc<dyn BrokerTransport>,
    welcome: Arc<RwLock<Option<DesktopBrokerWelcome>>>,
    runtime_epoch: u64,
    runtime_pid: u32,
}

fn resolve_broker_binary() -> Result<PathBuf> {
    if let Ok(exe) = std::env::current_exe() {
        if let Some(parent) = exe.parent() {
            let sibling = parent.join("inbrisk-desktop.exe");
            if sibling.is_file() {
                return Ok(sibling);
            }
        }
    }

    for candidate in [
        PathBuf::from("target/debug/inbrisk-desktop.exe"),
        PathBuf::from("target/release/inbrisk-desktop.exe"),
    ] {
        if candidate.is_file() {
            return Ok(candidate);
        }
    }

    Ok(PathBuf::from("inbrisk-desktop.exe"))
}

impl DesktopBrokerClient {
    /// Create a client using a custom/mock transport for unit tests.
    pub fn with_mock_transport(
        transport: Arc<dyn BrokerTransport>,
        runtime_epoch: u64,
        runtime_pid: u32,
    ) -> Self {
        Self {
            transport,
            welcome: Arc::new(RwLock::new(None)),
            runtime_epoch,
            runtime_pid,
        }
    }

    /// Spawn `inbrisk-desktop.exe` in the interactive user session and complete handshake.
    pub fn spawn_and_connect(runtime_epoch: u64, runtime_pid: u32) -> Result<Self> {
        // Assert: Phase 2A only supports same interactive user session (Session > 0)
        let probe = inbrisk_win32::desktop::probe_desktop_context()?;
        if probe.process_session_id == 0 {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::InteractiveDesktop,
                "Runtime is running in Session 0; cross-session broker spawning is not supported in Phase 2A",
            ));
        }

        let endpoint = broker_endpoint_name(runtime_epoch)?;
        let host = BrokerChannelHost::create(&endpoint, runtime_epoch, runtime_pid)?;

        let broker_exe = resolve_broker_binary()?;
        let args = vec![
            "--channel".to_string(),
            endpoint.clone(),
            "--runtime-pid".to_string(),
            runtime_pid.to_string(),
            "--runtime-epoch".to_string(),
            runtime_epoch.to_string(),
        ];

        log_info!(
            "desktop_broker",
            "spawning broker: {} (endpoint: {})",
            broker_exe.display(),
            endpoint
        );

        let launch_res = inbrisk_win32::launch(&broker_exe.to_string_lossy(), &args, None)?;
        let child_pid = launch_res.pid;

        // Wait for broker to mark ready
        let (broker_pid, _broker_epoch) = host
            .wait_for_broker_ready(Duration::from_secs(5))
            .map_err(|e| {
                InbriskError::capability_unavailable(
                    ExecutionCapability::WindowEnumeration,
                    format!("broker failed to signal ready: {e}"),
                )
            })?;

        log_info!(
            "desktop_broker",
            "broker ready (pid: {broker_pid}, child_pid: {child_pid:?})"
        );

        let transport = Arc::new(ChannelTransport::new(host, child_pid));
        let mut client = Self {
            transport,
            welcome: Arc::new(RwLock::new(None)),
            runtime_epoch,
            runtime_pid,
        };

        client.handshake()?;
        Ok(client)
    }

    /// Perform authenticated, correlated handshake with broker.
    pub fn handshake(&mut self) -> Result<DesktopBrokerWelcome> {
        let nonce = [42u8; 16]; // Random / constant nonce for handshake correlation
        let hello = DesktopBrokerHello {
            protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
            runtime_pid: self.runtime_pid,
            runtime_epoch: self.runtime_epoch,
            nonce,
        };

        let resp = self
            .transport
            .call(DesktopBrokerRequest::Hello(hello), Duration::from_secs(3))?;
        match resp {
            DesktopBrokerResponse::Welcome(welcome) => {
                if welcome.protocol_version != DESKTOP_BROKER_PROTOCOL_VERSION {
                    return Err(InbriskError::new(
                        ErrorCode::ProtocolMismatch,
                        format!(
                            "broker speaks protocol {}, expected {}",
                            welcome.protocol_version, DESKTOP_BROKER_PROTOCOL_VERSION
                        ),
                    ));
                }
                *self.welcome.write() = Some(welcome.clone());
                Ok(welcome)
            }
            other => Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                format!("expected Welcome from broker, got {other:?}"),
            )),
        }
    }

    /// Query broker status.
    pub fn status(&self) -> Result<DesktopBrokerWelcome> {
        let resp = self
            .transport
            .call(DesktopBrokerRequest::Status, Duration::from_secs(2))?;
        match resp {
            DesktopBrokerResponse::Status(welcome) | DesktopBrokerResponse::Welcome(welcome) => {
                *self.welcome.write() = Some(welcome.clone());
                Ok(welcome)
            }
            other => Err(InbriskError::internal(format!(
                "unexpected broker status response: {other:?}"
            ))),
        }
    }

    /// Enumerate top-level windows via broker.
    pub fn enumerate_windows(&self) -> Result<Vec<BrokerWindowInfo>> {
        if !self.is_connected() {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::WindowEnumeration,
                "desktop broker unavailable",
            ));
        }

        let resp = self.transport.call(
            DesktopBrokerRequest::EnumerateWindows,
            Duration::from_secs(5),
        )?;
        match resp {
            DesktopBrokerResponse::Windows(windows) => Ok(windows),
            other => Err(InbriskError::internal(format!(
                "unexpected response to EnumerateWindows: {other:?}"
            ))),
        }
    }

    /// Send ping to broker.
    pub fn ping(&self) -> Result<()> {
        let resp = self
            .transport
            .call(DesktopBrokerRequest::Ping, Duration::from_secs(1))?;
        match resp {
            DesktopBrokerResponse::Pong => Ok(()),
            other => Err(InbriskError::internal(format!(
                "unexpected ping response: {other:?}"
            ))),
        }
    }

    /// Request clean shutdown of broker.
    pub fn shutdown(&self) -> Result<()> {
        let _ = self
            .transport
            .call(DesktopBrokerRequest::Shutdown, Duration::from_secs(1));
        self.disconnect();
        Ok(())
    }

    pub fn is_connected(&self) -> bool {
        self.transport.is_alive()
    }

    pub fn disconnect(&self) {
        self.transport.disconnect();
        *self.welcome.write() = None;
    }

    pub fn welcome_info(&self) -> Option<DesktopBrokerWelcome> {
        self.welcome.read().clone()
    }

    /// Dispatch physical input command across the broker boundary.
    pub fn execute_physical(
        &self,
        request: PhysicalInputRequest,
        timeout: Duration,
    ) -> Result<PhysicalInputResult> {
        if !self.is_connected() {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::PhysicalInput,
                "desktop broker unavailable",
            ));
        }

        let resp = match self
            .transport
            .call(DesktopBrokerRequest::PhysicalInput(request), timeout)
        {
            Ok(r) => r,
            Err(e) => {
                if !self.is_connected() {
                    // Broker disconnected mid-execution: ambiguous side effect! Never auto-retry.
                    return Err(InbriskError::execution_outcome_unknown(format!(
                        "desktop broker disconnected during physical execution; outcome unknown: {e}"
                    )));
                }
                return Err(e);
            }
        };

        match resp {
            DesktopBrokerResponse::PhysicalInput(result) => Ok(result),
            other => Err(InbriskError::internal(format!(
                "unexpected broker physical response: {other:?}"
            ))),
        }
    }

    pub fn click(
        &self,
        operation_id: u64,
        point: PointTarget,
        button: MouseButton,
        click_count: u32,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::Click {
                    point,
                    button,
                    click_count,
                },
                guard,
            },
            Duration::from_secs(5),
        )
    }

    pub fn type_text(
        &self,
        operation_id: u64,
        text: String,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::TypeText { text },
                guard,
            },
            Duration::from_secs(10),
        )
    }

    /// Request emergency stop on broker: cancel queued requests and release all held modifiers/buttons.
    pub fn emergency_stop(&self) -> Result<()> {
        let _ = self
            .transport
            .call(DesktopBrokerRequest::EmergencyStop, Duration::from_secs(1));
        Ok(())
    }

    pub fn move_to(
        &self,
        operation_id: u64,
        point: PointTarget,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::MouseMove { point },
                guard,
            },
            Duration::from_secs(5),
        )
    }

    pub fn shortcut(
        &self,
        operation_id: u64,
        keys: Vec<String>,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::Shortcut { keys },
                guard,
            },
            Duration::from_secs(5),
        )
    }

    pub fn key(
        &self,
        operation_id: u64,
        key: String,
        down: bool,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::Key { key, down },
                guard,
            },
            Duration::from_secs(5),
        )
    }

    pub fn key_chord(
        &self,
        operation_id: u64,
        keys: Vec<String>,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.shortcut(operation_id, keys, guard)
    }

    pub fn scroll(
        &self,
        operation_id: u64,
        direction: ScrollDirection,
        amount: i32,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::Scroll { direction, amount },
                guard,
            },
            Duration::from_secs(5),
        )
    }

    pub fn drag(
        &self,
        operation_id: u64,
        from: PointTarget,
        to: PointTarget,
        button: MouseButton,
        guard: PhysicalInputGuard,
    ) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::Drag { from, to, button },
                guard,
            },
            Duration::from_secs(10),
        )
    }

    pub fn reset_modifiers(&self, operation_id: u64) -> Result<PhysicalInputResult> {
        self.execute_physical(
            PhysicalInputRequest {
                operation_id,
                command: PhysicalInputCommand::ResetModifiers,
                guard: PhysicalInputGuard::default(),
            },
            Duration::from_secs(2),
        )
    }

    pub fn to_status_report(&self) -> DesktopBrokerReport {
        let connected = self.is_connected();
        let welcome = self.welcome.read().clone();

        DesktopBrokerReport {
            connected,
            pid: welcome.as_ref().map(|w| w.broker_pid),
            broker_epoch: welcome.as_ref().map(|w| w.broker_epoch),
            protocol_version: welcome.as_ref().map(|w| w.protocol_version),
            session_id: welcome.as_ref().map(|w| w.process_session_id),
            desktop: welcome.as_ref().and_then(|w| w.desktop.clone()),
            window_station: welcome.as_ref().and_then(|w| w.window_station.clone()),
            interactive_desktop: welcome
                .as_ref()
                .map(|w| w.interactive_desktop)
                .unwrap_or(false),
            window_enumeration_ready: welcome
                .as_ref()
                .map(|w| w.window_enumeration_ready)
                .unwrap_or(false),
            physical_input_ready: welcome
                .as_ref()
                .map(|w| w.physical_input_ready)
                .unwrap_or(false),
            physical_input_routed: true,
            physical_queue_depth: 0,
            dpi_awareness: welcome.as_ref().and_then(|w| w.dpi_awareness.clone()),
            window_enumeration_source: if connected {
                "desktop-broker".into()
            } else {
                "unavailable".into()
            },
            physical_input_source: if connected {
                "desktop-broker".into()
            } else {
                "unavailable".into()
            },
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;

    struct MockTransport {
        calls: AtomicUsize,
        alive: AtomicBool,
        disconnect_on_call: bool,
    }

    impl MockTransport {
        fn new(alive: bool, disconnect_on_call: bool) -> Self {
            Self {
                calls: AtomicUsize::new(0),
                alive: AtomicBool::new(alive),
                disconnect_on_call,
            }
        }
    }

    impl BrokerTransport for MockTransport {
        fn call(
            &self,
            request: DesktopBrokerRequest,
            _timeout: Duration,
        ) -> Result<DesktopBrokerResponse> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            if self.disconnect_on_call {
                self.alive.store(false, Ordering::Release);
                return Err(InbriskError::internal("channel dropped mid-call"));
            }
            if !self.alive.load(Ordering::Acquire) {
                return Err(InbriskError::capability_unavailable(
                    ExecutionCapability::PhysicalInput,
                    "broker disconnected",
                ));
            }
            match request {
                DesktopBrokerRequest::PhysicalInput(req) => {
                    Ok(DesktopBrokerResponse::PhysicalInput(PhysicalInputResult {
                        operation_id: req.operation_id,
                        executed: true,
                        events_sent: 1,
                        details: None,
                    }))
                }
                DesktopBrokerRequest::EnumerateWindows => {
                    Ok(DesktopBrokerResponse::Windows(vec![]))
                }
                _ => Ok(DesktopBrokerResponse::Pong),
            }
        }

        fn is_alive(&self) -> bool {
            self.alive.load(Ordering::Acquire)
        }

        fn disconnect(&self) {
            self.alive.store(false, Ordering::Release);
        }
    }

    #[test]
    fn runtime_click_routes_through_desktop_broker() {
        let mock = Arc::new(MockTransport::new(true, false));
        let client = DesktopBrokerClient::with_mock_transport(mock.clone(), 1, 1);

        let res = client.click(
            42,
            PointTarget::virtual_screen(100.0, 200.0),
            MouseButton::Left,
            1,
            PhysicalInputGuard::default(),
        );
        assert!(res.is_ok());
        let outcome = res.unwrap();
        assert_eq!(outcome.operation_id, 42);
        assert_eq!(mock.calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn runtime_broker_disconnect_revokes_physical_input() {
        let mock = Arc::new(MockTransport::new(false, false));
        let client = DesktopBrokerClient::with_mock_transport(mock.clone(), 1, 1);

        let res = client.click(
            1,
            PointTarget::virtual_screen(50.0, 50.0),
            MouseButton::Left,
            1,
            PhysicalInputGuard::default(),
        );
        assert!(res.is_err());
        assert_eq!(res.unwrap_err().code, ErrorCode::CapabilityUnavailable);
        assert_eq!(mock.calls.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn runtime_ambiguous_broker_failure_returns_execution_outcome_unknown() {
        let mock = Arc::new(MockTransport::new(true, true));
        let client = DesktopBrokerClient::with_mock_transport(mock.clone(), 1, 1);

        let res = client.click(
            99,
            PointTarget::virtual_screen(50.0, 50.0),
            MouseButton::Left,
            1,
            PhysicalInputGuard::default(),
        );
        assert!(res.is_err());
        let err = res.unwrap_err();
        assert_eq!(err.code, ErrorCode::ExecutionOutcomeUnknown);
        assert!(
            !err.retryable,
            "Physical failures must NEVER be automatically retried"
        );
    }
}
