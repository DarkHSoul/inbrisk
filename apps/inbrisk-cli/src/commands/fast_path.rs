//! `fast-path`: self-test and verification of the direct native fast path.
//!
//! Validates that the CLI connects directly to the runtime via native shared memory,
//! checks protocol version and runtime epoch, verifies the desktop broker connection,
//! and proves that MCP is completely unnecessary for fast-path execution.

use inbrisk_protocol::PROTOCOL_VERSION;
use inbrisk_sdk::Runtime;

use crate::cli::FastPathArgs;
use crate::error::{CliResult, EXIT_ERROR, EXIT_OK, EXIT_UNAVAILABLE};
use crate::output;

pub fn execute(args: &FastPathArgs) -> CliResult<i32> {
    if !Runtime::is_available() {
        if args.json {
            let res = serde_json::json!({
                "success": false,
                "transport": "native-shm",
                "runtimePid": null,
                "runtimeEpoch": null,
                "brokerConnected": false,
                "mcpRequired": false,
                "error": "Runtime unavailable (no shared memory endpoint reachable)"
            });
            output::print_json(&res)?;
        } else {
            output::eprint_line(
                "error: Inbrisk runtime is not reachable via native shared memory.",
            )?;
            output::eprint_line(
                "hint: Ensure Inbrisk runtime is running (inbrisk.exe). MCP is not required.",
            )?;
        }
        return Ok(EXIT_UNAVAILABLE);
    }

    let runtime = match super::connect(false) {
        Ok(rt) => rt,
        Err(err) => {
            if args.json {
                let res = serde_json::json!({
                    "success": false,
                    "transport": "native-shm",
                    "runtimePid": null,
                    "runtimeEpoch": null,
                    "brokerConnected": false,
                    "mcpRequired": false,
                    "error": format!("Handshake failed: {err}")
                });
                output::print_json(&res)?;
            } else {
                output::eprint_line(&format!(
                    "error: Failed to connect via native shared memory: {err}"
                ))?;
            }
            return Ok(EXIT_ERROR);
        }
    };

    let welcome = runtime.welcome();
    let runtime_pid = welcome.runtime.pid;
    let runtime_epoch = welcome.runtime_epoch;
    let protocol_version = welcome.protocol_version;

    let proto_ok = protocol_version == PROTOCOL_VERSION;
    let epoch_ok = runtime_epoch > 0;
    let ping_micros = runtime.ping().ok();
    let ping_ok = ping_micros.is_some();

    let status = runtime.status().ok();
    let broker_connected = status
        .as_ref()
        .and_then(|s| s.desktop_broker.as_ref())
        .map(|b| b.connected)
        .unwrap_or(false);

    let all_ok = proto_ok && epoch_ok && ping_ok;

    if args.json {
        let res = serde_json::json!({
            "success": all_ok,
            "transport": "native-shm",
            "runtimePid": runtime_pid,
            "runtimeEpoch": runtime_epoch,
            "protocolVersion": protocol_version,
            "pingUs": ping_micros,
            "brokerConnected": broker_connected,
            "mcpRequired": false,
            "checks": {
                "runtimeReachable": true,
                "nativeSharedMemoryConnected": true,
                "protocolVersionCompatible": proto_ok,
                "runtimeEpochValid": epoch_ok,
                "nativeTransportUsable": ping_ok,
                "desktopBrokerConnected": broker_connected,
                "mcpNotRequired": true
            }
        });
        output::print_json(&res)?;
    } else {
        output::print_line("=== Inbrisk Native Fast-Path Verification ===")?;
        output::print_line(&format!("Transport:            native-shm"))?;
        output::print_line(&format!("Runtime PID:          {runtime_pid}"))?;
        output::print_line(&format!("Runtime Epoch:        {runtime_epoch}"))?;
        output::print_line(&format!(
            "Protocol Version:     {protocol_version} (compatible={proto_ok})"
        ))?;
        output::print_line(&format!(
            "Shared-Memory Ping:   {}",
            match ping_micros {
                Some(us) => format!("{us} us (OK)"),
                None => "FAILED".to_string(),
            }
        ))?;
        output::print_line(&format!(
            "Desktop Broker:       {}",
            if broker_connected {
                "Connected (OK)"
            } else {
                "Not Connected (Warning)"
            }
        ))?;
        output::print_line(&format!(
            "MCP Required:         false (MCP bypassed completely)"
        ))?;
        output::print_line(&format!(
            "Overall Status:       {}",
            if all_ok { "PASSED" } else { "FAILED" }
        ))?;
    }

    Ok(if all_ok { EXIT_OK } else { EXIT_ERROR })
}
