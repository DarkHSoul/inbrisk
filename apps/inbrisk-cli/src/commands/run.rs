//! `run`: submit a whole plan in one round trip.
//!
//! The plan is loaded and validated before connecting, so a malformed payload
//! fails fast without touching the runtime. Human output is one line per step
//! plus a summary and the `$name` output table.

use std::time::Duration;

use inbrisk_protocol::action::RunMode;
use inbrisk_protocol::response::{RunOutcome, StepStatus};

use crate::cli::RunArgs;
use crate::error::{CliError, CliResult, EXIT_ERROR, EXIT_OK};
use crate::output;

pub fn execute(args: &RunArgs) -> CliResult<i32> {
    let plan = args.source.load()?;
    let source = args.source.describe();

    let runtime = super::connect(false)?;
    let outcome = if args.dry_run {
        runtime.dry_run(plan).map_err(CliError::from)?
    } else {
        match args.timeout_ms {
            Some(ms) => runtime
                .run_with(plan, Duration::from_millis(ms), None)
                .map_err(CliError::from)?,
            None => runtime.run(plan).map_err(CliError::from)?,
        }
    };

    if args.json {
        let mut val = serde_json::to_value(&outcome).map_err(crate::output::encode_error)?;
        if let Some(map) = val.as_object_mut() {
            map.insert("success".into(), serde_json::Value::Bool(outcome.succeeded));
            map.insert("operationId".into(), serde_json::json!(outcome.plan_id));
            map.insert(
                "stateVersion".into(),
                serde_json::json!(outcome.state_version),
            );
            map.insert("generation".into(), serde_json::json!(outcome.generation));
            map.insert(
                "stepsExecuted".into(),
                serde_json::json!(outcome.steps.len()),
            );
            map.insert(
                "transport".into(),
                serde_json::Value::String("native-shm".into()),
            );
            map.insert(
                "runtime".into(),
                serde_json::json!({
                    "name": runtime.welcome().runtime.name,
                    "pid": runtime.welcome().runtime.pid,
                    "version": runtime.welcome().runtime.version,
                    "epoch": runtime.welcome().runtime.epoch,
                }),
            );
            if !outcome.succeeded {
                if let Some(err) = failing_error(&outcome) {
                    map.insert(
                        "error".into(),
                        serde_json::json!({
                            "code": err.code.as_str(),
                            "message": err.message,
                            "hint": err.hint,
                            "retryable": err.retryable
                        }),
                    );
                }
            }
        }
        crate::output::print_json(&val)?;
    } else {
        print_human(&outcome, &source)?;
    }
    Ok(if outcome.succeeded {
        EXIT_OK
    } else {
        EXIT_ERROR
    })
}

fn print_human(outcome: &RunOutcome, source: &str) -> CliResult<()> {
    let total = outcome.steps.len();
    let named = match &outcome.name {
        Some(name) => format!(" \"{name}\""),
        None => String::new(),
    };
    output::print_line(&format!(
        "plan{named}  {total} step(s)  plan_id={}  mode={}  source={source}",
        outcome.plan_id,
        mode_name(outcome.mode)
    ))?;

    for step in &outcome.steps {
        let mut line = format!(
            "[{}/{}] {:<10} {:<6} verified={:<3} {:>9}",
            step.index + 1,
            total,
            step.action,
            super::backend_name(&step.backend),
            output::yes_no(step.verified),
            output::fmt_us(step.elapsed_us)
        );
        if step.status != StepStatus::Ok {
            line.push_str(&format!("  status={}", status_name(step.status)));
        }
        output::print_line(&line)?;

        if let Some(element) = &step.element {
            output::print_line(&format!(
                "        element: {}",
                super::format_element(element)
            ))?;
        }
        if let Some(value) = &step.output {
            output::print_line(&format!("        output:  {}", super::compact_json(value)))?;
        }
    }

    output::print_line(&format!("succeeded: {}", output::yes_no(outcome.succeeded)))?;
    output::print_line(&format!(
        "failed_step: {}",
        match outcome.failed_step {
            Some(index) => index.to_string(),
            None => "none".to_string(),
        }
    ))?;
    output::print_line(&format!("elapsed: {}", output::fmt_us(outcome.elapsed_us)))?;

    if !outcome.outputs.is_empty() {
        output::print_line("outputs:")?;
        for (key, value) in &outcome.outputs {
            output::print_line(&format!("  {key} = {}", super::compact_json(value)))?;
        }
    }
    if !outcome.notes.is_empty() {
        output::print_line("notes:")?;
        for note in &outcome.notes {
            output::print_line(&format!("  - {note}"))?;
        }
    }

    if !outcome.succeeded {
        if let Some(error) = failing_error(outcome) {
            output::eprint_line(&format!("error: {error}"))?;
            if let Some(hint) = &error.hint {
                output::eprint_line(&format!("hint: {hint}"))?;
            }
        }
    }
    Ok(())
}

/// The error of the step the runtime blamed, falling back to the first failure.
fn failing_error(outcome: &RunOutcome) -> Option<&inbrisk_core::InbriskError> {
    if let Some(index) = outcome.failed_step {
        if let Some(step) = outcome.steps.iter().find(|step| step.index == index) {
            if step.error.is_some() {
                return step.error.as_ref();
            }
        }
    }
    outcome.steps.iter().find_map(|step| step.error.as_ref())
}

fn mode_name(mode: RunMode) -> &'static str {
    match mode {
        RunMode::Execute => "execute",
        RunMode::DryRun => "dry_run",
    }
}

fn status_name(status: StepStatus) -> &'static str {
    match status {
        StepStatus::Ok => "ok",
        StepStatus::Failed => "failed",
        StepStatus::Skipped => "skipped",
        StepStatus::Cancelled => "cancelled",
    }
}
