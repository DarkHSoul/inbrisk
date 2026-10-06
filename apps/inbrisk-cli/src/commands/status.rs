//! `status`: runtime identity, capabilities, sessions and optional detail.

use inbrisk_core::{now_ms, ErrorCode, InbriskError};
use inbrisk_protocol::request::{Request, StatusRequest};
use inbrisk_protocol::response::{ResponsePayload, StatusReport};
use inbrisk_sdk::{Runtime, DEFAULT_CALL_TIMEOUT};

use crate::cli::StatusArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &StatusArgs) -> CliResult<i32> {
    let runtime = super::connect(false)?;
    let report = fetch(&runtime, args)?;

    if args.json {
        crate::emit_json!(report)?;
    } else {
        print_human(&report, args, &runtime)?;
    }
    Ok(EXIT_OK)
}

/// `Runtime::status()` deliberately asks for the compact report, so the optional
/// sections are requested through the SDK's public IPC accessor instead of
/// duplicating the call path.
fn fetch(runtime: &Runtime, args: &StatusArgs) -> CliResult<StatusReport> {
    if !args.windows && !args.logs {
        return runtime.status().map_err(CliError::from);
    }

    let request = Request::Status(StatusRequest {
        include_windows: args.windows,
        include_ownership: false,
        include_logs: args.logs,
        log_lines: if args.logs { args.log_lines } else { 0 },
        include_ipc_stats: true,
    });
    match runtime
        .client()
        .call_ok(&request, DEFAULT_CALL_TIMEOUT)
        .map_err(CliError::from)?
    {
        ResponsePayload::Status(report) => Ok(*report),
        other => Err(CliError::from(InbriskError::new(
            ErrorCode::ProtocolMismatch,
            format!("status answered with a '{}' payload", other.kind()),
        ))),
    }
}

fn print_human(report: &StatusReport, args: &StatusArgs, runtime: &Runtime) -> CliResult<()> {
    output::print_line("inbrisk runtime")?;
    output::print_line(&format!("  endpoint      : {}", runtime.endpoint()))?;
    output::print_line(&format!(
        "  identity      : {} {}  protocol {}",
        report.identity.name,
        super::version_or_unknown(&report.identity.version),
        report.identity.protocol_version
    ))?;
    output::print_line(&format!(
        "  process       : pid {}  epoch {}",
        report.identity.pid, report.identity.epoch
    ))?;
    output::print_line(&format!(
        "  uptime        : {} ({} ms)",
        output::fmt_uptime(report.uptime_ms),
        report.uptime_ms
    ))?;
    output::print_line(&format!(
        "  state         : version={}  generation={}",
        report.state_version, report.generation
    ))?;
    output::print_line(&format!(
        "  activity      : {}{}",
        report.activity.state.as_str(),
        activity_label(&report.activity.label)
    ))?;
    output::print_line(&format!(
        "  flags         : paused={}  emergency={}",
        output::yes_no(report.paused),
        output::yes_no(report.emergency)
    ))?;
    output::print_line(&format!("  capabilities  : {}", report.capabilities))?;
    output::print_line(&format!(
        "  backends      : {}",
        super::list_or_dash(&report.backends)
    ))?;
    if let Some(ref probe) = report.desktop_probe {
        output::print_line(&format!(
            "  desktop probe : interactiveDesktop={} physicalInput={} windowEnumeration={} sessionId={} desktop={} windowStation={}",
            output::yes_no(probe.interactive_desktop),
            output::yes_no(probe.physical_input),
            output::yes_no(probe.window_enumeration),
            probe.session_id,
            probe.desktop,
            probe.window_station,
        ))?;
    }
    if let Some(ref broker) = report.desktop_broker {
        output::print_line(&format!(
            "  desktop broker: {}",
            if broker.connected {
                "connected"
            } else {
                "disconnected"
            }
        ))?;
        if broker.connected {
            output::print_line(&format!(
                "    PID         : {}",
                broker
                    .pid
                    .map(|p| p.to_string())
                    .unwrap_or_else(|| "unknown".into())
            ))?;
            output::print_line(&format!(
                "    Session     : {}",
                broker
                    .session_id
                    .map(|s| s.to_string())
                    .unwrap_or_else(|| "unknown".into())
            ))?;
            output::print_line(&format!(
                "    Desktop     : {}",
                broker.desktop.as_deref().unwrap_or("unknown")
            ))?;
            output::print_line(&format!(
                "    Window sta  : {}",
                broker.window_station.as_deref().unwrap_or("unknown")
            ))?;
            output::print_line(&format!(
                "    Window enum : {}",
                if broker.window_enumeration_ready {
                    "ready"
                } else {
                    "unavailable"
                }
            ))?;
            output::print_line(&format!(
                "    Physical inp: {} (routed={}, queue={})",
                if broker.physical_input_ready {
                    "ready"
                } else {
                    "unavailable"
                },
                output::yes_no(broker.physical_input_routed),
                broker.physical_queue_depth
            ))?;
            output::print_line(&format!(
                "    Input source: {}",
                broker.physical_input_source
            ))?;
        }
    } else {
        output::print_line("  desktop broker: not running / standalone")?;
    }
    output::print_line(&format!("  sessions      : {}", report.sessions.len()))?;
    for session in &report.sessions {
        output::print_line(&format!(
            "    - session {}  {} {}  pid {}{}",
            session.session_id,
            session.name,
            super::version_or_unknown(&session.version),
            session.pid,
            session_age(session.connected_at_ms)
        ))?;
    }
    output::print_line(&format!(
        "  counters      : requests={} (native={}, mcp={}, shm={}) failed={} plans={} steps={} actions={} uia={} win32={} cache={}/{} stale={} denials={}",
        report.counters.requests_total,
        report.counters.native_requests_total,
        report.counters.mcp_requests_total,
        report.counters.shared_memory_requests_total,
        report.counters.requests_failed,
        report.counters.plans_total,
        report.counters.steps_total,
        report.counters.actions_total,
        report.counters.uia_calls,
        report.counters.win32_calls,
        report.counters.cache_hits,
        report.counters.cache_misses,
        report.counters.stale_rejections,
        report.counters.denials
    ))?;

    if args.windows {
        output::print_line(&format!("  windows       : {}", report.windows.len()))?;
        super::windows::print_window_list(&report.windows, "    ")?;
    }
    if args.logs {
        output::print_line(&format!(
            "  log tail      : {} line(s)",
            report.log_tail.len()
        ))?;
        for line in &report.log_tail {
            output::print_line(&format!("    {line}"))?;
        }
    }
    if args.ipc {
        match &report.ipc {
            Some(ipc) => {
                output::print_line("  ipc")?;
                output::print_line(&format!("    transport   : {}", ipc.transport))?;
                output::print_line(&format!("    region      : {}", ipc.region_name))?;
                output::print_line(&format!(
                    "    capacities  : request {}  response {}  event {}",
                    ipc.request_capacity, ipc.response_capacity, ipc.event_capacity
                ))?;
                output::print_line(&format!("    arena       : {} bytes", ipc.arena_bytes))?;
                output::print_line(&format!(
                    "    traffic     : seen={} sent={} published={} dropped={} wake_signals={}",
                    ipc.requests_seen,
                    ipc.responses_sent,
                    ipc.events_published,
                    ipc.events_dropped,
                    ipc.wake_signals
                ))?;
                output::print_line(&format!(
                    "    rtt         : p50={} p95={} p99={} max={}",
                    output::fmt_us(ipc.rtt_p50_us),
                    output::fmt_us(ipc.rtt_p95_us),
                    output::fmt_us(ipc.rtt_p99_us),
                    output::fmt_us(ipc.rtt_max_us)
                ))?;
                output::print_line(&format!("    runtime pid : {}", ipc.runtime_pid))?;
            }
            None => output::print_line("  ipc           : not reported")?,
        }
    }
    Ok(())
}

fn activity_label(label: &str) -> String {
    if label.is_empty() {
        String::new()
    } else {
        format!("  label=\"{label}\"")
    }
}

fn session_age(connected_at_ms: u64) -> String {
    let now = now_ms();
    if connected_at_ms == 0 || connected_at_ms > now {
        return String::new();
    }
    format!(
        "  connected {} ago",
        output::fmt_uptime(now - connected_at_ms)
    )
}
