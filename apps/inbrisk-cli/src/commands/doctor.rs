//! `doctor`: one-shot diagnosis of the CLI <-> runtime link.
//!
//! This is deliberately a real diagnostic rather than a wrapper around
//! `status`: it names the region the CLI expects, proves the handshake works,
//! measures round-trip latency, and reports every probe as a pass/fail check so
//! a skill author can tell *what* is broken instead of only *that* it is.

use inbrisk_protocol::response::{StatusReport, Welcome};
use inbrisk_protocol::PROTOCOL_VERSION;
use inbrisk_sdk::Runtime;

use crate::cli::DoctorArgs;
use crate::error::{CliResult, EXIT_ERROR, EXIT_OK, EXIT_UNAVAILABLE};
use crate::output;
use crate::region;

/// Result of a single probe.
#[derive(Debug, Clone)]
struct Check {
    name: &'static str,
    ok: bool,
    detail: String,
}

impl Check {
    fn new(name: &'static str, ok: bool, detail: impl Into<String>) -> Self {
        Self {
            name,
            ok,
            detail: detail.into(),
        }
    }
}

/// Everything `doctor` learned, so the human and JSON renderings cannot drift.
#[derive(Debug)]
struct Report {
    expected_region: String,
    sdk: String,
    endpoint: String,
    welcome: Welcome,
    samples: Vec<u64>,
    status: Option<StatusReport>,
    status_error: Option<String>,
    checks: Vec<Check>,
    ok: bool,
}

pub fn execute(args: &DoctorArgs) -> CliResult<i32> {
    let expected_region = region::expected_region_name();
    let sdk = format!(
        "inbrisk-sdk {} (in-process), protocol {PROTOCOL_VERSION}",
        super::CLIENT_VERSION
    );

    if !Runtime::is_available() {
        return report_unavailable(args, &expected_region, &sdk);
    }

    let runtime = super::connect(true)?;
    let welcome = runtime.welcome();
    let endpoint = runtime.endpoint();

    let mut checks = Vec::new();
    checks.push(Check::new(
        "region",
        welcome.region_name == expected_region,
        format!("live={} expected={expected_region}", welcome.region_name),
    ));
    checks.push(Check::new(
        "handshake",
        true,
        format!(
            "session {} / client {}",
            welcome.session_id, welcome.client_id
        ),
    ));
    checks.push(Check::new(
        "protocol",
        welcome.protocol_version == PROTOCOL_VERSION,
        format!("runtime speaks {}", welcome.protocol_version),
    ));

    // A handful of round trips separates "alive" from "wedged".
    let mut samples: Vec<u64> = Vec::new();
    let mut ping_error = None;
    for _ in 0..5 {
        match runtime.ping() {
            Ok(micros) => samples.push(micros),
            Err(error) => {
                if ping_error.is_none() {
                    ping_error = Some(error);
                }
            }
        }
    }
    let ping_ok = !samples.is_empty();
    checks.push(Check::new(
        "ping",
        ping_ok,
        match (&ping_error, ping_ok) {
            (Some(error), false) => error.to_string(),
            _ => format!("{} sample(s) round-tripped", samples.len()),
        },
    ));

    let (status, status_error) = match runtime.status() {
        Ok(report) => (Some(report), None),
        Err(error) => (None, Some(error.to_string())),
    };
    checks.push(Check::new(
        "status",
        status.is_some(),
        status_error
            .clone()
            .unwrap_or_else(|| "status report received".to_string()),
    ));
    if let Some(report) = &status {
        checks.push(Check::new(
            "health",
            !report.emergency,
            if report.emergency {
                "emergency stop is engaged"
            } else {
                "no emergency stop"
            },
        ));
    }

    let report = Report {
        ok: checks.iter().all(|check| check.ok),
        expected_region,
        sdk,
        endpoint,
        welcome,
        samples,
        status,
        status_error,
        checks,
    };

    if args.json {
        report.print_json()?;
    } else {
        report.print_human()?;
    }
    Ok(if report.ok { EXIT_OK } else { EXIT_ERROR })
}

fn report_unavailable(args: &DoctorArgs, expected_region: &str, sdk: &str) -> CliResult<i32> {
    let published = region::is_published();
    let message = region::runtime_unavailable_message();
    let handshake = "a shared region is published for this user, but the handshake failed \
                     (out of session slots, or a protocol mismatch)";
    let checks = vec![
        Check::new(
            "region",
            true,
            format!(
                "{expected_region} (published={})",
                output::yes_no(published)
            ),
        ),
        Check::new(
            "runtime",
            false,
            if published {
                handshake.to_string()
            } else {
                message.clone()
            },
        ),
    ];

    if args.json {
        let mut root = serde_json::Map::new();
        root.insert("ok".into(), false.into());
        root.insert("cli".into(), cli_identity());
        root.insert(
            "sdk".into(),
            serde_json::json!({ "identity": sdk, "protocol_version": PROTOCOL_VERSION }),
        );
        root.insert("expected_region".into(), expected_region.into());
        root.insert("region_published".into(), published.into());
        root.insert("runtime".into(), serde_json::json!({ "available": false }));
        root.insert("checks".into(), checks_json(&checks));
        root.insert("message".into(), message.into());
        crate::emit_json!(serde_json::Value::Object(root))?;
    } else {
        output::print_line("inbrisk doctor")?;
        output::print_line(&format!(
            "  cli             : {} {}",
            super::CLIENT_NAME,
            super::CLIENT_VERSION
        ))?;
        output::print_line(&format!("  sdk             : {sdk}"))?;
        output::print_line(&format!(
            "  expected region : {expected_region}  (published={})",
            output::yes_no(published)
        ))?;
        output::print_line("  runtime         : not reachable")?;
        if published {
            output::print_line(&format!("  detail          : {handshake}"))?;
        }
        output::print_line("  checks          : region=ok  runtime=fail")?;
        output::print_line(&format!("  result          : FAIL - {message}"))?;
    }
    Ok(EXIT_UNAVAILABLE)
}

impl Report {
    fn print_human(&self) -> CliResult<()> {
        output::print_line("inbrisk doctor")?;
        output::print_line(&format!(
            "  cli             : {} {}",
            super::CLIENT_NAME,
            super::CLIENT_VERSION
        ))?;
        output::print_line(&format!("  sdk             : {}", self.sdk))?;
        output::print_line(&format!("  expected region : {}", self.expected_region))?;
        output::print_line("  runtime         : reachable")?;
        output::print_line(&format!("  endpoint        : {}", self.endpoint))?;

        let identity = match &self.status {
            Some(status) => &status.identity,
            None => &self.welcome.runtime,
        };
        output::print_line(&format!(
            "  identity        : {} {}  protocol {}  pid {}  epoch {}",
            identity.name,
            super::version_or_unknown(&identity.version),
            identity.protocol_version,
            identity.pid,
            identity.epoch
        ))?;
        output::print_line(&format!(
            "  session         : id={}  client_id={}  heartbeat={}ms",
            self.welcome.session_id, self.welcome.client_id, self.welcome.heartbeat_ms
        ))?;
        output::print_line(&format!("  region (live)   : {}", self.welcome.region_name))?;
        output::print_line(&format!(
            "  state           : version={}  generation={}",
            self.welcome.state_version, self.welcome.generation
        ))?;

        if let Some(status) = &self.status {
            output::print_line(&format!(
                "  uptime          : {} ({} ms)",
                output::fmt_uptime(status.uptime_ms),
                status.uptime_ms
            ))?;
            output::print_line(&format!(
                "  activity        : {}  active={}  waiting={}",
                status.activity.state.as_str(),
                status.activity.active_operations,
                status.activity.waiting_operations
            ))?;
            output::print_line(&format!(
                "  flags           : paused={}  emergency={}",
                output::yes_no(status.paused),
                output::yes_no(status.emergency)
            ))?;
            output::print_line(&format!("  capabilities    : {}", status.capabilities))?;
            output::print_line(&format!(
                "  backends        : {}",
                super::list_or_dash(&status.backends)
            ))?;
            output::print_line(&format!("  sessions        : {}", status.sessions.len()))?;
            for session in &status.sessions {
                output::print_line(&format!(
                    "    - session {}  {} {}  pid {}  requests {}",
                    session.session_id,
                    session.name,
                    super::version_or_unknown(&session.version),
                    session.pid,
                    session.requests
                ))?;
            }
            output::print_line(&format!(
                "  counters        : requests={} failed={} plans={} steps={} denials={}",
                status.counters.requests_total,
                status.counters.requests_failed,
                status.counters.plans_total,
                status.counters.steps_total,
                status.counters.denials
            ))?;
        } else if let Some(error) = &self.status_error {
            output::print_line(&format!("  status          : {error}"))?;
        }

        output::print_line(&format!(
            "  ping            : {}",
            ping_summary(&self.samples)
        ))?;
        output::print_line(&format!(
            "  checks          : {}",
            self.checks
                .iter()
                .map(|check| format!("{}={}", check.name, if check.ok { "ok" } else { "fail" }))
                .collect::<Vec<_>>()
                .join("  ")
        ))?;
        for check in self.checks.iter().filter(|check| !check.ok) {
            output::print_line(&format!("    {}: {}", check.name, check.detail))?;
        }
        output::print_line(&format!(
            "  result          : {}",
            if self.ok { "OK" } else { "FAIL" }
        ))?;
        Ok(())
    }

    fn print_json(&self) -> CliResult<()> {
        let mut root = serde_json::Map::new();
        root.insert("ok".into(), self.ok.into());
        root.insert("cli".into(), cli_identity());
        root.insert(
            "sdk".into(),
            serde_json::json!({ "identity": self.sdk, "protocol_version": PROTOCOL_VERSION }),
        );
        root.insert(
            "expected_region".into(),
            self.expected_region.clone().into(),
        );
        root.insert(
            "runtime".into(),
            serde_json::json!({
                "available": true,
                "endpoint": self.endpoint,
                "region_name": self.welcome.region_name,
                "pid": self.welcome.runtime.pid,
                "epoch": self.welcome.runtime_epoch,
                "identity": self.welcome.runtime,
            }),
        );
        root.insert(
            "session".into(),
            serde_json::json!({
                "session_id": self.welcome.session_id,
                "client_id": self.welcome.client_id,
                "state_version": self.welcome.state_version,
                "generation": self.welcome.generation,
                "heartbeat_ms": self.welcome.heartbeat_ms,
            }),
        );
        root.insert(
            "ping_us".into(),
            serde_json::json!({
                "samples": self.samples.len(),
                "min": self.samples.iter().min().copied(),
                "mean": mean_of(&self.samples),
                "max": self.samples.iter().max().copied(),
                "values": self.samples,
            }),
        );
        root.insert(
            "status".into(),
            serde_json::to_value(&self.status).map_err(output::encode_error)?,
        );
        root.insert(
            "status_error".into(),
            serde_json::to_value(&self.status_error).map_err(output::encode_error)?,
        );
        root.insert("checks".into(), checks_json(&self.checks));
        crate::emit_json!(serde_json::Value::Object(root))
    }
}

fn checks_json(checks: &[Check]) -> serde_json::Value {
    checks
        .iter()
        .map(|check| {
            serde_json::json!({
                "name": check.name,
                "ok": check.ok,
                "detail": check.detail,
            })
        })
        .collect()
}

fn cli_identity() -> serde_json::Value {
    serde_json::json!({ "name": super::CLIENT_NAME, "version": super::CLIENT_VERSION })
}

fn ping_summary(samples: &[u64]) -> String {
    if samples.is_empty() {
        return "no round trip completed".to_string();
    }
    let min = samples.iter().min().copied().unwrap_or(0);
    let max = samples.iter().max().copied().unwrap_or(0);
    format!(
        "min={}  mean={}  max={}  ({} sample(s))",
        output::fmt_us(min),
        output::fmt_us(mean_of(samples)),
        output::fmt_us(max),
        samples.len()
    )
}

fn mean_of(samples: &[u64]) -> u64 {
    if samples.is_empty() {
        return 0;
    }
    samples.iter().sum::<u64>() / samples.len() as u64
}
