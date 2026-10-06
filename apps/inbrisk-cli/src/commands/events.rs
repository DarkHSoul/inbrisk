//! `events`: subscribe, then stream the runtime's event ring until the deadline.
//!
//! With `--json` this is JSONL (one envelope per line), which is what an agent
//! pipeline wants; otherwise each event is rendered as `[seq] kind  payload`.

use std::time::{Duration, Instant};

use inbrisk_protocol::event::EventEnvelope;
use serde_json::Value;

use crate::cli::EventsArgs;
use crate::error::{CliError, CliResult, EXIT_OK};
use crate::output;

pub fn execute(args: &EventsArgs) -> CliResult<i32> {
    // The event ring is only attached when the handshake asks for it.
    let runtime = super::connect(true)?;
    let subscribed = runtime.subscribe(Vec::new()).map_err(CliError::from)?;

    if !args.json {
        let kinds = if subscribed.kinds.is_empty() {
            "all".to_string()
        } else {
            subscribed.kinds.join(",")
        };
        output::print_line(&format!(
            "subscribed id={} kinds={kinds}",
            subscribed.subscription_id
        ))?;
    }

    let deadline = Instant::now() + Duration::from_secs_f64(args.seconds);
    let mut seen = 0usize;
    loop {
        let now = Instant::now();
        if now >= deadline {
            break;
        }
        match runtime.wait_event(deadline - now) {
            Some(envelope) => {
                seen += 1;
                if args.json {
                    crate::emit_json_line!(envelope)?;
                } else {
                    output::print_line(&format_event(&envelope))?;
                }
            }
            // `wait_event` also returns early when the ring is unusable; a short
            // park keeps that path from spinning the CPU until the deadline.
            None => std::thread::sleep(Duration::from_millis(20)),
        }
    }

    if !args.json {
        output::print_line(&format!("{seen} event(s) in {:.1}s", args.seconds))?;
    }
    Ok(EXIT_OK)
}

fn format_event(envelope: &EventEnvelope) -> String {
    let kind = envelope.event.kind();
    match serde_json::to_value(&envelope.event) {
        Ok(Value::Object(mut fields)) => {
            // The tag is already rendered as the kind column.
            fields.remove("event");
            format!(
                "[{}] {:<20} {}  at={}ms",
                envelope.seq,
                kind,
                Value::Object(fields),
                envelope.at_ms
            )
        }
        _ => format!("[{}] {}  at={}ms", envelope.seq, kind, envelope.at_ms),
    }
}
