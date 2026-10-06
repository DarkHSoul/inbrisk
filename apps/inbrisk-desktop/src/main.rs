//! Inbrisk Interactive Desktop Broker (`inbrisk-desktop.exe`).
//!
//! Dedicated worker process attached to the interactive user desktop (`WinSta0\Default`).
//! Communicates with `inbrisk-runtime` over an internal shared-memory broker channel.

use std::sync::Arc;
use std::time::Duration;

use inbrisk_core::now_ms;
use inbrisk_desktop::{build_welcome, enumerate_windows, PhysicalInputExecutor};
use inbrisk_ipc::BrokerChannelPeer;
use inbrisk_protocol::desktop_broker::{
    BrokerErrorPayload, DesktopBrokerRequest, DesktopBrokerResponse,
    DESKTOP_BROKER_PROTOCOL_VERSION,
};
use inbrisk_win32::desktop::probe_desktop_context;

fn main() {
    // 0. Initialize process DPI awareness explicitly to PerMonitorV2
    let _ = inbrisk_win32::geometry::set_per_monitor_v2_dpi_awareness();

    let args: Vec<String> = std::env::args().collect();
    let broker_pid = std::process::id();
    let broker_epoch = now_ms();

    // 1. Initial probe
    let probe = match probe_desktop_context() {
        Ok(p) => p,
        Err(e) => {
            eprintln!("[inbrisk-desktop] Desktop probe failed: {e}");
            std::process::exit(1);
        }
    };

    // Diagnostic flags for direct CLI / testing inspection
    if args.iter().any(|a| a == "--probe") {
        let welcome = build_welcome(&probe, broker_pid, broker_epoch);
        println!("{}", serde_json::to_string_pretty(&welcome).unwrap());
        return;
    }

    if args.iter().any(|a| a == "--windows") {
        match enumerate_windows() {
            Ok(windows) => {
                println!("{}", serde_json::to_string_pretty(&windows).unwrap());
            }
            Err(e) => {
                eprintln!("[inbrisk-desktop] Windows enumeration failed: {e}");
                std::process::exit(1);
            }
        }
        return;
    }

    // 2. Identify broker channel endpoint
    let channel_arg = args
        .windows(2)
        .find(|w| w[0] == "--channel")
        .map(|w| w[1].clone());

    let endpoint = match channel_arg {
        Some(ep) => ep,
        None => {
            eprintln!("Usage: inbrisk-desktop.exe --channel <endpoint> [--runtime-pid <pid>] [--runtime-epoch <epoch>]");
            std::process::exit(1);
        }
    };

    // 3. Assert interactive desktop requirements before serving
    if !probe.input_desktop_available && !probe.fresh_thread_can_bind_input_desktop {
        eprintln!(
            "[inbrisk-desktop] FATAL: Cannot bind to interactive desktop (Session {}, WinSta {:?}, Desktop {:?})",
            probe.process_session_id, probe.current_window_station, probe.current_desktop
        );
        std::process::exit(2);
    }

    // 4. Attach to the broker channel
    let peer = match BrokerChannelPeer::open(&endpoint, broker_epoch, broker_pid) {
        Ok(p) => p,
        Err(e) => {
            eprintln!("[inbrisk-desktop] Failed to open channel '{endpoint}': {e}");
            std::process::exit(3);
        }
    };

    peer.mark_ready();

    let executor = Arc::new(PhysicalInputExecutor::default());

    // 5. Server loop
    loop {
        match peer.next_request(Duration::from_millis(500)) {
            Ok(Some(env)) => {
                let req_id = env.request_id;
                match env.request {
                    DesktopBrokerRequest::Hello(hello) => {
                        if hello.protocol_version != DESKTOP_BROKER_PROTOCOL_VERSION {
                            let _ = peer.send_response(
                                req_id,
                                Err(BrokerErrorPayload {
                                    code: "ProtocolMismatch".into(),
                                    message: format!(
                                        "protocol version {} != expected {}",
                                        hello.protocol_version, DESKTOP_BROKER_PROTOCOL_VERSION
                                    ),
                                }),
                            );
                        } else {
                            let welcome = build_welcome(&probe, broker_pid, broker_epoch);
                            let _ = peer
                                .send_response(req_id, Ok(DesktopBrokerResponse::Welcome(welcome)));
                        }
                    }
                    DesktopBrokerRequest::Ping => {
                        let _ = peer.send_response(req_id, Ok(DesktopBrokerResponse::Pong));
                    }
                    DesktopBrokerRequest::Status => {
                        let welcome = build_welcome(&probe, broker_pid, broker_epoch);
                        let _ =
                            peer.send_response(req_id, Ok(DesktopBrokerResponse::Status(welcome)));
                    }
                    DesktopBrokerRequest::EnumerateWindows => match enumerate_windows() {
                        Ok(windows) => {
                            let _ = peer
                                .send_response(req_id, Ok(DesktopBrokerResponse::Windows(windows)));
                        }
                        Err(e) => {
                            let _ = peer.send_response(
                                req_id,
                                Err(BrokerErrorPayload {
                                    code: e.code.as_str().into(),
                                    message: e.message,
                                }),
                            );
                        }
                    },
                    DesktopBrokerRequest::PhysicalInput(req) => match executor.execute(&req) {
                        Ok(res) => {
                            let _ = peer.send_response(
                                req_id,
                                Ok(DesktopBrokerResponse::PhysicalInput(res)),
                            );
                        }
                        Err(e) => {
                            let _ = peer.send_response(
                                req_id,
                                Err(BrokerErrorPayload {
                                    code: e.code.as_str().into(),
                                    message: e.message,
                                }),
                            );
                        }
                    },
                    DesktopBrokerRequest::EmergencyStop => {
                        executor.engage_emergency();
                        let _ = peer.send_response(req_id, Ok(DesktopBrokerResponse::Ack));
                    }
                    DesktopBrokerRequest::Shutdown => {
                        executor.cleanup_stuck_modifiers();
                        let _ = peer.send_response(req_id, Ok(DesktopBrokerResponse::Ack));
                        break;
                    }
                }
            }
            Ok(None) => {
                // Heartbeat / idle check
            }
            Err(e) => {
                eprintln!("[inbrisk-desktop] Error reading request: {e}");
                break;
            }
        }
    }

    executor.cleanup_stuck_modifiers();
}
