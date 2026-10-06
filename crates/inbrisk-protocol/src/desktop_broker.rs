//! Internal runtime <-> desktop-broker protocol.
//!
//! This protocol connects the Inbrisk runtime daemon to the interactive
//! `inbrisk-desktop.exe` broker process attached to `WinSta0\Default`.
//! These definitions are internal and never exposed as MCP tools.

use serde::{Deserialize, Serialize};

use crate::action::{MouseButton, PointTarget, ScrollDirection};

/// Protocol version implemented by the broker channel.
pub const DESKTOP_BROKER_PROTOCOL_VERSION: u32 = 1;

/// Target guard for physical input verification prior to dispatch.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq, Default)]
pub struct PhysicalInputGuard {
    pub expected_foreground_hwnd: Option<u64>,
}

/// Commands for physical input execution by the broker.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub enum PhysicalInputCommand {
    MouseMove {
        point: PointTarget,
    },
    Click {
        point: PointTarget,
        button: MouseButton,
        click_count: u32,
    },
    Drag {
        from: PointTarget,
        to: PointTarget,
        button: MouseButton,
    },
    Scroll {
        direction: ScrollDirection,
        amount: i32,
    },
    Key {
        key: String,
        down: bool,
    },
    Shortcut {
        keys: Vec<String>,
    },
    TypeText {
        text: String,
    },
    ResetModifiers,
}

/// Physical input request carrying operation ID, command payload, and foreground guard.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PhysicalInputRequest {
    pub operation_id: u64,
    pub command: PhysicalInputCommand,
    pub guard: PhysicalInputGuard,
}

/// Structured outcome of a physical input execution.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct PhysicalInputResult {
    pub operation_id: u64,
    pub executed: bool,
    pub events_sent: u32,
    pub details: Option<String>,
}

/// Top-level command dispatched from the runtime to the broker.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub enum DesktopBrokerRequest {
    Hello(DesktopBrokerHello),
    Ping,
    Status,
    EnumerateWindows,
    PhysicalInput(PhysicalInputRequest),
    EmergencyStop,
    Shutdown,
}

/// Initial handshake request from the runtime.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DesktopBrokerHello {
    pub protocol_version: u32,
    pub runtime_pid: u32,
    pub runtime_epoch: u64,
    pub nonce: [u8; 16],
}

/// Handshake acknowledgment and context response from the broker.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DesktopBrokerWelcome {
    pub protocol_version: u32,
    pub broker_pid: u32,
    pub broker_epoch: u64,
    pub process_session_id: u32,

    pub window_station: Option<String>,
    pub desktop: Option<String>,

    pub interactive_desktop: bool,
    pub window_enumeration_ready: bool,
    pub physical_input_ready: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dpi_awareness: Option<String>,
}

/// Compact description of a top-level window returned across the broker boundary.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct BrokerWindowInfo {
    pub hwnd: u64,
    pub pid: u32,
    pub title: String,
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
    pub visible: bool,
    pub minimized: bool,
    #[serde(default)]
    pub process_name: String,
    #[serde(default)]
    pub class_name: String,
}

/// Structured error payload across the broker wire.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct BrokerErrorPayload {
    pub code: String,
    pub message: String,
}

/// Responses emitted by the desktop broker to runtime requests.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub enum DesktopBrokerResponse {
    Welcome(DesktopBrokerWelcome),
    Pong,
    Status(DesktopBrokerWelcome),
    Windows(Vec<BrokerWindowInfo>),
    PhysicalInput(PhysicalInputResult),
    Ack,
    Error(BrokerErrorPayload),
}

/// Correlation envelope for requests sent across the shared-memory broker channel.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct BrokerRequestEnvelope {
    pub request_id: u64,
    pub runtime_epoch: u64,
    pub expected_broker_epoch: Option<u64>,
    pub request: DesktopBrokerRequest,
}

/// Correlation envelope for responses returned from the broker channel.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct BrokerResponseEnvelope {
    pub request_id: u64,
    pub runtime_epoch: u64,
    pub broker_epoch: u64,
    pub response: Result<DesktopBrokerResponse, BrokerErrorPayload>,
}

/// Telemetry report embedded in `StatusReport`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct DesktopBrokerReport {
    pub connected: bool,
    pub pid: Option<u32>,
    pub broker_epoch: Option<u64>,
    pub protocol_version: Option<u32>,
    pub session_id: Option<u32>,
    pub desktop: Option<String>,
    pub window_station: Option<String>,
    pub interactive_desktop: bool,
    pub window_enumeration_ready: bool,
    pub physical_input_ready: bool,
    pub physical_input_routed: bool,
    pub physical_queue_depth: u32,
    pub window_enumeration_source: String,
    pub physical_input_source: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dpi_awareness: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::action::CoordinateSpaceDto;

    #[test]
    fn desktop_broker_protocol_version_round_trip() {
        let hello = DesktopBrokerHello {
            protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
            runtime_pid: 1234,
            runtime_epoch: 42,
            nonce: [7u8; 16],
        };
        let req = DesktopBrokerRequest::Hello(hello);
        let encoded = serde_json::to_string(&req).expect("serialize");
        let decoded: DesktopBrokerRequest = serde_json::from_str(&encoded).expect("deserialize");
        assert_eq!(req, decoded);

        if let DesktopBrokerRequest::Hello(h) = decoded {
            assert_eq!(h.protocol_version, DESKTOP_BROKER_PROTOCOL_VERSION);
            assert_eq!(h.runtime_pid, 1234);
            assert_eq!(h.runtime_epoch, 42);
            assert_eq!(h.nonce, [7u8; 16]);
        } else {
            panic!("expected Hello variant");
        }
    }

    #[test]
    fn desktop_broker_wrong_protocol_version_rejected() {
        let wrong_version = DESKTOP_BROKER_PROTOCOL_VERSION + 99;
        let welcome = DesktopBrokerWelcome {
            protocol_version: wrong_version,
            broker_pid: 5678,
            broker_epoch: 100,
            process_session_id: 1,
            window_station: Some("WinSta0".into()),
            desktop: Some("Default".into()),
            interactive_desktop: true,
            window_enumeration_ready: true,
            physical_input_ready: true,
            dpi_awareness: Some("PerMonitorV2".into()),
        };

        let is_valid = welcome.protocol_version == DESKTOP_BROKER_PROTOCOL_VERSION;
        assert!(!is_valid, "wrong protocol version must be rejected");
    }

    #[test]
    fn desktop_broker_epoch_is_part_of_handshake() {
        let hello = DesktopBrokerHello {
            protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
            runtime_pid: 1000,
            runtime_epoch: 9999,
            nonce: [1u8; 16],
        };
        let welcome = DesktopBrokerWelcome {
            protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
            broker_pid: 2000,
            broker_epoch: 8888,
            process_session_id: 1,
            window_station: Some("WinSta0".into()),
            desktop: Some("Default".into()),
            interactive_desktop: true,
            window_enumeration_ready: true,
            physical_input_ready: true,
            dpi_awareness: Some("PerMonitorV2".into()),
        };

        let env = BrokerRequestEnvelope {
            request_id: 1,
            runtime_epoch: hello.runtime_epoch,
            expected_broker_epoch: Some(welcome.broker_epoch),
            request: DesktopBrokerRequest::EnumerateWindows,
        };

        assert_eq!(env.runtime_epoch, 9999);
        assert_eq!(env.expected_broker_epoch, Some(8888));
    }

    #[test]
    fn broker_physical_input_request_round_trip() {
        let req = DesktopBrokerRequest::PhysicalInput(PhysicalInputRequest {
            operation_id: 101,
            command: PhysicalInputCommand::Click {
                point: PointTarget::virtual_screen(500.0, 300.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard {
                expected_foreground_hwnd: Some(0x12345),
            },
        });

        let encoded = serde_json::to_string(&req).expect("serialize physical request");
        let decoded: DesktopBrokerRequest =
            serde_json::from_str(&encoded).expect("deserialize physical request");
        assert_eq!(req, decoded);
    }

    #[test]
    fn broker_physical_input_request_carries_operation_id() {
        let req = PhysicalInputRequest {
            operation_id: 4242,
            command: PhysicalInputCommand::MouseMove {
                point: PointTarget::virtual_screen(100.0, 200.0),
            },
            guard: PhysicalInputGuard::default(),
        };
        assert_eq!(req.operation_id, 4242);
    }

    #[test]
    fn broker_click_carries_explicit_coordinate_space() {
        let req = PhysicalInputCommand::Click {
            point: PointTarget {
                x: 150.0,
                y: 250.0,
                space: CoordinateSpaceDto::WindowClientPhysical,
                hwnd: Some(0xCAFE),
            },
            button: MouseButton::Right,
            click_count: 2,
        };
        let encoded = serde_json::to_string(&req).expect("serialize");
        let decoded: PhysicalInputCommand = serde_json::from_str(&encoded).expect("deserialize");
        assert_eq!(req, decoded);
        if let PhysicalInputCommand::Click { point, .. } = decoded {
            assert_eq!(point.space, CoordinateSpaceDto::WindowClientPhysical);
            assert_eq!(point.hwnd, Some(0xCAFE));
            assert_eq!(point.x, 150.0);
            assert_eq!(point.y, 250.0);
        } else {
            panic!("expected Click");
        }
    }

    #[test]
    fn broker_move_carries_explicit_coordinate_space() {
        let req = PhysicalInputCommand::MouseMove {
            point: PointTarget {
                x: -500.0,
                y: 100.0,
                space: CoordinateSpaceDto::VirtualScreenPhysical,
                hwnd: None,
            },
        };
        let encoded = serde_json::to_string(&req).expect("serialize");
        let decoded: PhysicalInputCommand = serde_json::from_str(&encoded).expect("deserialize");
        assert_eq!(req, decoded);
        if let PhysicalInputCommand::MouseMove { point } = decoded {
            assert_eq!(point.space, CoordinateSpaceDto::VirtualScreenPhysical);
            assert_eq!(point.hwnd, None);
            assert_eq!(point.x, -500.0);
            assert_eq!(point.y, 100.0);
        } else {
            panic!("expected MouseMove");
        }
    }

    #[test]
    fn broker_drag_carries_explicit_coordinate_spaces() {
        let req = PhysicalInputCommand::Drag {
            from: PointTarget::client_logical(0x1000, 10.0, 20.0),
            to: PointTarget::client_physical(0x1000, 100.0, 200.0),
            button: MouseButton::Left,
        };
        let encoded = serde_json::to_string(&req).expect("serialize");
        let decoded: PhysicalInputCommand = serde_json::from_str(&encoded).expect("deserialize");
        assert_eq!(req, decoded);
        if let PhysicalInputCommand::Drag { from, to, .. } = decoded {
            assert_eq!(from.space, CoordinateSpaceDto::WindowClientLogical);
            assert_eq!(from.hwnd, Some(0x1000));
            assert_eq!(to.space, CoordinateSpaceDto::WindowClientPhysical);
            assert_eq!(to.hwnd, Some(0x1000));
        } else {
            panic!("expected Drag");
        }
    }

    #[test]
    fn broker_window_relative_point_requires_hwnd() {
        let p_phys = PointTarget {
            x: 10.0,
            y: 20.0,
            space: CoordinateSpaceDto::WindowClientPhysical,
            hwnd: None,
        };
        assert!(p_phys.validate().is_err());

        let p_log = PointTarget {
            x: 10.0,
            y: 20.0,
            space: CoordinateSpaceDto::WindowClientLogical,
            hwnd: None,
        };
        assert!(p_log.validate().is_err());

        let p_ok = PointTarget {
            x: 10.0,
            y: 20.0,
            space: CoordinateSpaceDto::WindowClientPhysical,
            hwnd: Some(0x123),
        };
        assert!(p_ok.validate().is_ok());
    }

    #[test]
    fn broker_virtual_screen_point_does_not_require_hwnd() {
        let vs = PointTarget::virtual_screen(1920.0, 1080.0);
        assert_eq!(vs.space, CoordinateSpaceDto::VirtualScreenPhysical);
        assert_eq!(vs.hwnd, None);
        assert!(vs.validate().is_ok());

        let vs_with_hwnd = PointTarget {
            x: 100.0,
            y: 100.0,
            space: CoordinateSpaceDto::VirtualScreenPhysical,
            hwnd: Some(0x123),
        };
        assert!(vs_with_hwnd.validate().is_err());
    }

    #[test]
    fn broker_physical_input_request_carries_foreground_guard() {
        let req = PhysicalInputRequest {
            operation_id: 1,
            command: PhysicalInputCommand::TypeText {
                text: "hello".into(),
            },
            guard: PhysicalInputGuard {
                expected_foreground_hwnd: Some(0xABCD),
            },
        };
        assert_eq!(req.guard.expected_foreground_hwnd, Some(0xABCD));
    }

    #[test]
    fn broker_physical_input_response_carries_broker_epoch() {
        let env = BrokerResponseEnvelope {
            request_id: 5,
            runtime_epoch: 12345,
            broker_epoch: 67890,
            response: Ok(DesktopBrokerResponse::PhysicalInput(PhysicalInputResult {
                operation_id: 42,
                executed: true,
                events_sent: 2,
                details: None,
            })),
        };

        assert_eq!(env.broker_epoch, 67890);
        assert_eq!(env.runtime_epoch, 12345);
        if let Ok(DesktopBrokerResponse::PhysicalInput(res)) = env.response {
            assert_eq!(res.operation_id, 42);
            assert!(res.executed);
            assert_eq!(res.events_sent, 2);
        } else {
            panic!("expected PhysicalInput variant");
        }
    }
}
