use std::io::{self, BufRead, Write};
use std::process;
use serde::{Deserialize, Serialize};
use serde_json::Value;

use inbrisk_core::{ErrorCode, InbriskError};
use inbrisk_protocol::action::Step;
use inbrisk_protocol::request::ObserveRequest;
use inbrisk_protocol::selector::Selector;
use inbrisk_sdk::Runtime;

#[derive(Deserialize, Debug)]
struct RequestEnvelope {
    id: Value,
    op: String,
    #[serde(flatten)]
    payload: Value,
}

#[derive(Serialize, Debug, Clone)]
struct ErrorDetail {
    code: &'static str,
    message: String,
}

#[derive(Serialize)]
struct ResponseEnvelope {
    id: Value,
    ok: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    result: Option<Value>,
    #[serde(skip_serializing_if = "Option::is_none")]
    error: Option<ErrorDetail>,
}

enum OpKind {
    ReadOnly,
    Mutating,
}

fn main() {
    // Attempt initial connect, but don't exit if runtime isn't ready yet;
    // we can connect lazily or transparently reconnect on first op.
    let mut runtime_opt: Option<Runtime> = Runtime::connect().ok();
    if runtime_opt.is_none() {
        eprintln!("bridge: initial runtime connect failed; will retry on first operation");
    }

    let stdin = io::stdin();
    let mut stdout = io::stdout();

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(l) => l,
            Err(_) => break, // EOF or broken pipe
        };

        if line.trim().is_empty() {
            continue;
        }

        let req: RequestEnvelope = match serde_json::from_str(&line) {
            Ok(r) => r,
            Err(e) => {
                let err_res = ResponseEnvelope {
                    id: Value::Null,
                    ok: false,
                    result: None,
                    error: Some(ErrorDetail {
                        code: "InvalidRequest",
                        message: format!("JSON parse error: {e}"),
                    }),
                };
                let _ = writeln!(stdout, "{}", serde_json::to_string(&err_res).unwrap());
                let _ = stdout.flush();
                continue;
            }
        };

        let is_close = req.op == "close";
        let res_result = execute_op(&mut runtime_opt, &req.op, &req.payload);

        let res = match res_result {
            Ok(val) => ResponseEnvelope {
                id: req.id.clone(),
                ok: true,
                result: Some(val),
                error: None,
            },
            Err(err) => ResponseEnvelope {
                id: req.id.clone(),
                ok: false,
                result: None,
                error: Some(err),
            },
        };

        if let Ok(json_str) = serde_json::to_string(&res) {
            if writeln!(stdout, "{}", json_str).is_err() {
                break;
            }
            if stdout.flush().is_err() {
                break;
            }
        }

        if is_close {
            break;
        }
    }
}

fn execute_op(
    runtime_opt: &mut Option<Runtime>,
    op: &str,
    payload: &Value,
) -> Result<Value, ErrorDetail> {
    if op == "hello" {
        return Ok(serde_json::json!({
            "pid": process::id(),
            "protocol": "inbrisk-pc-jsonl-v1"
        }));
    }
    if op == "close" {
        if let Some(rt) = runtime_opt.take() {
            rt.disconnect();
        }
        return Ok(serde_json::json!({ "closed": true }));
    }

    // Deterministic test fixture delay (gated strictly behind INBRISK_TEST_DELAY=1 env var)
    if std::env::var("INBRISK_TEST_DELAY").as_deref() == Ok("1") {
        if let Some(delay_ms) = payload.get("_delay_ms").and_then(|v| v.as_u64()) {
            std::thread::sleep(std::time::Duration::from_millis(delay_ms));
        }
    }

    let kind = match op {
        "status" | "windows" | "observe" | "find" | "read" => OpKind::ReadOnly,
        "act" | "cancel" => OpKind::Mutating,
        _ => {
            return Err(ErrorDetail {
                code: "UnknownOperation",
                message: format!("unknown op: '{op}'"),
            })
        }
    };

    // 1. Ensure we have an active runtime connection
    if runtime_opt.is_none() {
        match Runtime::connect() {
            Ok(mut r) => {
                r.set_default_timeout(std::time::Duration::from_millis(2500));
                *runtime_opt = Some(r);
            }
            Err(e) => {
                return Err(ErrorDetail {
                    code: "RuntimeUnavailable",
                    message: format!("failed to connect to runtime: {e}"),
                })
            }
        }
    }

    let runtime = runtime_opt.as_mut().unwrap();

    // 2. Perform dispatch
    let call_res = dispatch_call(runtime, op, payload);
    match call_res {
        Ok(v) => Ok(v),
        Err(e) => {
            let is_connection_error = e.code == "RuntimeUnavailable"
                || e.code == "SessionExpired"
                || e.code == "Timeout"
                || e.code == "IpcError"
                || e.code == "IndeterminateExecution"
                || !Runtime::is_available();

            if is_connection_error {
                // Connection was dropped or runtime died
                *runtime_opt = None; // Drop broken client
                match kind {
                    OpKind::ReadOnly => {
                        eprintln!("bridge: read-only op '{op}' connection lost. Attempting 1 transparent reconnect...");
                        match Runtime::connect() {
                            Ok(mut new_rt) => {
                                new_rt.set_default_timeout(std::time::Duration::from_millis(2500));
                                *runtime_opt = Some(new_rt);
                                dispatch_call(runtime_opt.as_mut().unwrap(), op, payload)
                            }
                            Err(reconn_err) => Err(ErrorDetail {
                                code: "RuntimeUnavailable",
                                message: format!("reconnect failed after connection loss during {op}: {reconn_err}"),
                            }),
                        }
                    }
                    OpKind::Mutating => {
                        eprintln!("bridge: mutating op '{op}' lost connection. NOT retrying to prevent duplicate execution.");
                        Err(ErrorDetail {
                            code: "IndeterminateExecution",
                            message: format!(
                                "connection lost during mutating operation '{op}'; not retried to prevent unsafe duplicate execution: {}",
                                e.message
                            ),
                        })
                    }
                }
            } else {
                Err(e)
            }
        }
    }
}

fn dispatch_call(runtime: &mut Runtime, op: &str, payload: &Value) -> Result<Value, ErrorDetail> {
    match op {
        "status" => runtime
            .status()
            .map(|r| serde_json::to_value(r).unwrap())
            .map_err(|e| map_inbrisk_err("status", e)),

        "windows" => runtime
            .windows()
            .map(|r| serde_json::to_value(r).unwrap())
            .map_err(|e| map_inbrisk_err("windows", e)),

        "observe" => {
            let req: ObserveRequest = serde_json::from_value(payload.clone()).map_err(|e| {
                ErrorDetail {
                    code: "InvalidRequest",
                    message: format!("malformed observe parameters: {e}"),
                }
            })?;
            runtime
                .observe(req)
                .map(|r| serde_json::to_value(r).unwrap())
                .map_err(|e| map_inbrisk_err("observe", e))
        }

        "find" => {
            let selector_val = match payload.get("selector") {
                Some(v) if !v.is_null() => v,
                _ => {
                    return Err(ErrorDetail {
                        code: "InvalidRequest",
                        message: "find requires 'selector' parameter".into(),
                    })
                }
            };
            let selector: Selector =
                serde_json::from_value(selector_val.clone()).map_err(|e| ErrorDetail {
                    code: "InvalidRequest",
                    message: format!("malformed selector: {e}"),
                })?;

            let all = match payload.get("all") {
                Some(v) => match v.as_bool() {
                    Some(b) => b,
                    None => {
                        return Err(ErrorDetail {
                            code: "InvalidRequest",
                            message: "'all' must be a boolean".into(),
                        })
                    }
                },
                None => false,
            };

            let limit = match payload.get("limit") {
                Some(v) => match v.as_u64() {
                    Some(n) => Some(n as usize),
                    None => {
                        return Err(ErrorDetail {
                            code: "InvalidRequest",
                            message: "'limit' must be an integer".into(),
                        })
                    }
                },
                None => None,
            };

            runtime
                .find_all(selector, all, limit)
                .map(|r| serde_json::to_value(r).unwrap())
                .map_err(|e| map_inbrisk_err("find", e))
        }

        "read" => {
            let element_id = match payload.get("element_id") {
                Some(v) => match v.as_u64() {
                    Some(id) if id > 0 => id,
                    Some(_) => {
                        return Err(ErrorDetail {
                            code: "InvalidRequest",
                            message: "'element_id' must be an integer > 0".into(),
                        })
                    }
                    None => {
                        return Err(ErrorDetail {
                            code: "InvalidRequest",
                            message: "'element_id' must be an integer".into(),
                        })
                    }
                },
                None => {
                    return Err(ErrorDetail {
                        code: "InvalidRequest",
                        message: "read requires 'element_id'".into(),
                    })
                }
            };

            runtime
                .read(element_id)
                .map(|r| serde_json::to_value(r).unwrap())
                .map_err(|e| map_inbrisk_err("read", e))
        }

        "act" => {
            if payload.get("action").is_none() {
                return Err(ErrorDetail {
                    code: "InvalidRequest",
                    message: "act requires 'action' field (e.g. 'click', 'type', 'key', 'focus')".into(),
                });
            }
            let step: Step = serde_json::from_value(payload.clone()).map_err(|e| {
                ErrorDetail {
                    code: "InvalidRequest",
                    message: format!("malformed act step: {e}"),
                }
            })?;

            runtime
                .act(step)
                .map(|r| serde_json::to_value(r).unwrap())
                .map_err(|e| map_inbrisk_err("act", e))
        }

        "cancel" => {
            let reason = match payload.get("reason") {
                Some(v) => match v.as_str() {
                    Some(s) => Some(s.to_string()),
                    None => {
                        return Err(ErrorDetail {
                            code: "InvalidRequest",
                            message: "'reason' must be a string".into(),
                        })
                    }
                },
                None => None,
            };

            runtime
                .cancel(None, reason)
                .map(|r| serde_json::to_value(r).unwrap())
                .map_err(|e| map_inbrisk_err("cancel", e))
        }

        _ => Err(ErrorDetail {
            code: "UnknownOperation",
            message: format!("unknown op: '{op}'"),
        }),
    }
}

fn map_inbrisk_err(op: &str, err: InbriskError) -> ErrorDetail {
    let msg = err.to_string();
    let code = match err.code {
        ErrorCode::RuntimeUnavailable => "RuntimeUnavailable",
        ErrorCode::SessionExpired => "SessionExpired",
        ErrorCode::ExecutionOutcomeUnknown => "IndeterminateExecution",
        ErrorCode::TargetNotFound => "TargetNotFound",
        ErrorCode::CloseMatchesFound => "CloseMatchesFound",
        ErrorCode::TargetOffscreen => "TargetOffscreen",
        ErrorCode::TargetDisabled => "TargetDisabled",
        ErrorCode::PatternUnavailable => "PatternUnavailable",
        ErrorCode::Timeout => "Timeout",
        ErrorCode::Cancelled => "Cancelled",
        ErrorCode::Denied => "Denied",
        ErrorCode::Protected => "Protected",
        ErrorCode::Emergency => "Emergency",
        ErrorCode::Paused => "Paused",
        _ => "OperationError",
    };
    ErrorDetail {
        code,
        message: format!("{op} failed: {msg}"),
    }
}
