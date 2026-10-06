//! Plan and step JSON normalization.
//!
//! Agent skills hand `run` three different shapes for the same plan: the bare
//! `Plan` object, the native wire form (`{"op": "run", "steps": [...]}`) and a
//! wrapper (`{"plan": {...}}`). Normalizing in one pure function keeps `run`
//! free of format trivia and makes the rules unit-testable without a runtime.

use inbrisk_core::{ErrorCode, InbriskError};
use inbrisk_protocol::action::{Plan, Step};
use serde_json::{Map, Value};

use crate::error::{CliError, CliResult};

/// Where the plan for a `run` invocation comes from.
///
/// With no source flag the CLI reads stdin, which is what makes
/// `echo '{...}' | inbrisk-cli run` the fastest useful form.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PlanSource {
    Stdin,
    File(String),
    Inline(String),
}

impl PlanSource {
    /// Short description used in the human header of `run`.
    pub fn describe(&self) -> String {
        match self {
            PlanSource::Stdin => "stdin".to_string(),
            PlanSource::File(path) => format!("file:{path}"),
            PlanSource::Inline(_) => "argument".to_string(),
        }
    }

    /// Loads, reads and normalizes the plan.
    pub fn load(&self) -> CliResult<Plan> {
        match self {
            PlanSource::Stdin => normalize_plan_json(&read_stdin()?),
            PlanSource::File(path) => {
                let raw = std::fs::read_to_string(path).map_err(|error| {
                    CliError::usage(format!("cannot read plan file '{path}': {error}"))
                })?;
                normalize_plan_json(&raw)
            }
            PlanSource::Inline(raw) => normalize_plan_json(raw),
        }
    }
}

fn read_stdin() -> CliResult<String> {
    use std::io::Read;

    let mut raw = String::new();
    std::io::stdin().read_to_string(&mut raw).map_err(|error| {
        CliError::Runtime(InbriskError::new(
            ErrorCode::Internal,
            format!("cannot read the plan from stdin: {error}"),
        ))
    })?;
    if raw.trim().is_empty() {
        return Err(CliError::usage(
            "no plan received (expected JSON with a 'steps' array)",
        ));
    }
    Ok(raw)
}

/// Accepts a bare `Plan`, the wire form and the `{"plan": {...}}` wrapper.
pub fn normalize_plan_json(raw: &str) -> CliResult<Plan> {
    let value: Value = serde_json::from_str(raw)
        .map_err(|error| CliError::usage(format!("plan is not valid JSON: {error}")))?;
    normalize_plan_value(value)
}

/// Same as [`normalize_plan_json`], for a value that is already parsed.
pub fn normalize_plan_value(value: Value) -> CliResult<Plan> {
    let outer = match value {
        Value::Object(map) => map,
        _ => {
            return Err(CliError::usage(
                "plan must be a JSON object with a 'steps' array",
            ))
        }
    };

    let (mut base, outer_name) = unwrap_plan(outer)?;
    // `op` is the wire tag: `{"op": "run", "steps": [...]}`.
    base.remove("op");
    if !base.contains_key("steps") {
        return Err(CliError::usage(
            "plan JSON has no 'steps' array (pass {\"steps\": [...]}, {\"op\":\"run\",\"steps\":[...]} or {\"plan\": {\"steps\": [...]}})",
        ));
    }
    if !base.contains_key("name") {
        if let Some(name) = outer_name {
            base.insert("name".into(), Value::String(name));
        }
    }

    serde_json::from_value::<Plan>(Value::Object(base))
        .map_err(|error| CliError::usage(format!("plan is not a valid plan: {error}")))
}

fn unwrap_plan(mut outer: Map<String, Value>) -> CliResult<(Map<String, Value>, Option<String>)> {
    let name = match outer.get("name") {
        Some(Value::String(name)) => Some(name.clone()),
        _ => None,
    };
    match outer.remove("plan") {
        None => Ok((outer, name)),
        Some(Value::Object(inner)) => Ok((inner, name)),
        Some(_) => Err(CliError::usage("'plan' must be an object")),
    }
}

/// Accepts a bare `Step` (`{"action": "click", ...}`), the wire form
/// (`{"op": "act", ...}`) and the `{"step": {...}}` wrapper.
pub fn normalize_step_json(raw: &str) -> CliResult<Step> {
    let value: Value = serde_json::from_str(raw)
        .map_err(|error| CliError::usage(format!("step is not valid JSON: {error}")))?;
    let mut map = match value {
        Value::Object(map) => map,
        _ => {
            return Err(CliError::usage(
                "step must be a JSON object with an 'action' field",
            ))
        }
    };

    map.remove("op");
    if !map.contains_key("action") {
        if let Some(Value::Object(inner)) = map.remove("step") {
            map = inner;
            map.remove("op");
        }
    }
    if !map.contains_key("action") {
        return Err(CliError::usage("step JSON has no 'action' field"));
    }

    serde_json::from_value::<Step>(Value::Object(map))
        .map_err(|error| CliError::usage(format!("step is not a valid action: {error}")))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn plan(raw: &str) -> Plan {
        normalize_plan_json(raw).expect("plan should normalize")
    }

    fn plan_error(raw: &str) -> String {
        match normalize_plan_json(raw) {
            Ok(plan) => panic!("expected a usage error, got {plan:?}"),
            Err(error) => error.to_string(),
        }
    }

    #[test]
    fn bare_plan_is_accepted() {
        let parsed = plan(r#"{"steps":[{"action":"sleep","ms":5}]}"#);
        assert_eq!(parsed.name, None);
        assert_eq!(parsed.steps.len(), 1);
        assert_eq!(parsed.steps[0].action_name(), "sleep");
    }

    #[test]
    fn wire_form_op_is_ignored() {
        let parsed = plan(r#"{"op":"run","name":"boot","steps":[{"action":"sleep","ms":1}]}"#);
        assert_eq!(parsed.name.as_deref(), Some("boot"));
        assert_eq!(parsed.steps.len(), 1);
    }

    #[test]
    fn plan_wrapper_is_unwrapped() {
        let parsed = plan(r#"{"plan":{"name":"inner","steps":[{"action":"sleep","ms":1}]}}"#);
        assert_eq!(parsed.name.as_deref(), Some("inner"));
        assert_eq!(parsed.steps.len(), 1);
    }

    #[test]
    fn wrapper_keeps_the_outer_name() {
        let parsed =
            plan(r#"{"op":"run","name":"outer","plan":{"steps":[{"action":"sleep","ms":1}]}}"#);
        assert_eq!(parsed.name.as_deref(), Some("outer"));
    }

    #[test]
    fn multiple_steps_keep_their_order() {
        let parsed = plan(
            r#"{"steps":[{"action":"launch","app":"notepad"},{"action":"key","keys":["ctrl","s"]}]}"#,
        );
        assert_eq!(parsed.steps.len(), 2);
        assert_eq!(parsed.steps[1].action_name(), "key");
    }

    #[test]
    fn malformed_plans_are_usage_errors() {
        assert!(plan_error("not json").contains("not valid JSON"));
        assert!(plan_error("[]").contains("must be a JSON object"));
        assert!(plan_error("{}").contains("no 'steps' array"));
        assert!(plan_error(r#"{"steps":{}}"#).contains("not a valid plan"));
        assert!(plan_error(r#"{"plan":42}"#).contains("'plan' must be an object"));
    }

    #[test]
    fn bare_step_is_accepted() {
        let step = normalize_step_json(r#"{"action":"click","target":"$0"}"#)
            .expect("step should normalize");
        assert_eq!(step.action_name(), "click");
    }

    #[test]
    fn step_wire_form_and_wrapper_are_unwrapped() {
        let wire = normalize_step_json(r#"{"op":"act","action":"sleep","ms":3}"#)
            .expect("wire step should normalize");
        assert!(matches!(wire, Step::Sleep { ms: 3 }));

        let wrapped = normalize_step_json(r#"{"op":"act","step":{"action":"sleep","ms":4}}"#)
            .expect("wrapped step should normalize");
        assert!(matches!(wrapped, Step::Sleep { ms: 4 }));
    }

    #[test]
    fn malformed_steps_are_usage_errors() {
        assert!(matches!(
            normalize_step_json("nope"),
            Err(CliError::Usage(_))
        ));
        assert!(normalize_step_json("{}").is_err());
        assert!(normalize_step_json(r#"{"action":"fly"}"#).is_err());
    }
}
