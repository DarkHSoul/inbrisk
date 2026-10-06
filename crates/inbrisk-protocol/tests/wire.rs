//! Wire-format tests.
//!
//! These lock down the *external* protocol shape: the JSON an agent host, the
//! CLI or the MCP proxy sends. If one of these tests breaks, the contract broke.

use inbrisk_core::{ErrorCode, Expect, Hwnd, InbriskError};
use inbrisk_protocol::action::{OnError, RunMode, Step, TargetRef, WaitSpec, WindowWaitState};
use inbrisk_protocol::opcode::Op;
use inbrisk_protocol::request::{Hello, Ping, Request};
use inbrisk_protocol::response::{
    ActionOutcome, Reply, ResponsePayload, StatusReport, StepOutcome, StepStatus,
};
use inbrisk_protocol::selector::Selector;
use serde_json::json;

/// The exact example from the architecture document must keep working.
#[test]
fn documented_run_example_parses() {
    let raw = r#"{
        "op": "run",
        "steps": [
            { "action": "launch", "app": "notepad" },
            { "action": "set_value", "target": { "role": "document" }, "value": "Hello" }
        ]
    }"#;
    let request: Request = serde_json::from_str(raw).expect("documented example must parse");
    assert_eq!(request.op(), Op::Run);
    match request {
        Request::Run(run) => {
            assert_eq!(run.plan.steps.len(), 2);
            assert!(matches!(
                &run.plan.steps[0],
                Step::Launch { app, .. } if app == "notepad"
            ));
            match &run.plan.steps[1] {
                Step::SetValue { target, value, .. } => {
                    assert_eq!(value, "Hello");
                    match target {
                        TargetRef::Selector(sel) => {
                            assert_eq!(sel.role.as_deref(), Some("document"))
                        }
                        other => panic!("expected an inline selector, got {other:?}"),
                    }
                }
                other => panic!("expected set_value, got {other:?}"),
            }
        }
        other => panic!("expected a run request, got {other:?}"),
    }
}

#[test]
fn plan_defaults_are_serde_defaults() {
    let raw = r#"{"op":"run","steps":[{"action":"click","target":{"name":"OK"}}]}"#;
    let request: Request = serde_json::from_str(raw).unwrap();
    let Request::Run(run) = request else {
        panic!("expected run")
    };
    assert_eq!(run.mode, RunMode::Execute);
    assert_eq!(run.on_error, OnError::Abort);
    assert!(run.expect.is_none());
    let Step::Click { button, clicks, .. } = &run.plan.steps[0] else {
        panic!("expected click")
    };
    assert_eq!(*clicks, 1, "clicks defaults to a single click");
    assert_eq!(format!("{button:?}"), "Left");
}

#[test]
fn target_reference_and_selector_are_distinguishable() {
    let ref_step: Step = serde_json::from_value(json!({
        "action": "invoke",
        "target": "$save_button"
    }))
    .unwrap();
    match ref_step {
        Step::Invoke { target, .. } => {
            assert_eq!(target.as_reference(), Some("$save_button"));
        }
        other => panic!("expected invoke, got {other:?}"),
    }

    let sel_step: Step = serde_json::from_value(json!({
        "action": "invoke",
        "target": { "role": "button", "name": "Save" }
    }))
    .unwrap();
    match sel_step {
        Step::Invoke { target, .. } => {
            assert!(target.as_reference().is_none());
            assert_eq!(
                target,
                TargetRef::Selector(Box::new(Selector::role_name("button", "Save")))
            );
        }
        other => panic!("expected invoke, got {other:?}"),
    }
}

#[test]
fn wait_specs_round_trip() {
    let cases = vec![
        json!({ "until": "window", "selector": { "name": "Save As" } }),
        json!({ "until": "element", "selector": { "role": "edit" }, "state": "enabled" }),
        json!({ "until": "value", "target": "$0", "equals": "done" }),
        json!({ "until": "process_exit", "name": "notepad.exe", "timeout_ms": 5000 }),
        json!({ "until": "idle", "ms": 250 }),
    ];
    for case in cases {
        let spec: WaitSpec = serde_json::from_value(case.clone()).expect("wait spec must parse");
        let back = serde_json::to_value(&spec).unwrap();
        let again: WaitSpec = serde_json::from_value(back).expect("wait spec must re-parse");
        assert_eq!(spec, again, "round trip changed the spec: {case}");
    }

    let spec: WaitSpec = serde_json::from_value(json!({
        "until": "window",
        "selector": { "name": "Save As" },
        "state": "foreground"
    }))
    .unwrap();
    match spec {
        WaitSpec::Window { state, .. } => assert_eq!(state, WindowWaitState::Foreground),
        other => panic!("expected a window wait, got {other:?}"),
    }
}

#[test]
fn full_action_vocabulary_parses() {
    let steps = json!([
        { "action": "launch", "app": "notepad", "args": ["a.txt"], "intent": "ephemeral" },
        { "action": "focus", "target": "$0" },
        { "action": "find", "selector": { "role": "document" }, "store": "doc", "all": false },
        { "action": "invoke", "target": "$doc" },
        { "action": "set_value", "target": "$doc", "value": "hi" },
        { "action": "click", "target": { "name": "OK" }, "button": "right", "clicks": 2 },
        { "action": "type", "text": "hello", "clear_first": true },
        { "action": "key", "keys": ["ctrl", "shift", "s"] },
        { "action": "scroll", "direction": "down", "amount": 5 },
        { "action": "select", "target": "$list", "option": "Second" },
        { "action": "select", "target": "$list", "option": 1 },
        { "action": "toggle", "target": "$check", "state": true },
        { "action": "drag", "from": "$a", "to": { "x": 10, "y": 20 } },
        { "action": "wait", "until": "idle", "ms": 10 },
        { "action": "sleep", "ms": 5 },
        { "action": "close", "target": "$0", "force": false },
        { "action": "human", "message": "check the file" }
    ]);
    let plan: inbrisk_protocol::action::Plan =
        serde_json::from_value(json!({ "name": "everything", "steps": steps }))
            .expect("every documented action must parse");
    assert_eq!(plan.steps.len(), 17);
    assert_eq!(plan.steps[0].action_name(), "launch");
    assert_eq!(plan.steps[12].action_name(), "drag");
    assert_eq!(plan.steps[16].action_name(), "human");
}

#[test]
fn expect_guard_shape_is_stable() {
    // The documented camelCase spelling must be accepted...
    let request: Request = serde_json::from_value(json!({
        "op": "run",
        "expect": { "stateVersion": 812 },
        "steps": [{ "action": "invoke", "target": "$x" }]
    }))
    .unwrap();
    let expect = request.expect().expect("expect must survive parsing");
    assert_eq!(expect.state_version, Some(812));
    assert_eq!(request.state_version_hint(), Some(812));

    // ...and the native snake_case spelling is the one we emit.
    let encoded = serde_json::to_value(Expect::version(812)).unwrap();
    assert_eq!(encoded, json!({ "state_version": 812 }));
    let back: Expect = serde_json::from_value(json!({ "state_version": 5 })).unwrap();
    assert_eq!(back.state_version, Some(5));
}

#[test]
fn step_guard_carries_state_version() {
    let step: Step = serde_json::from_value(json!({
        "action": "invoke",
        "target": "$save",
        "expect": { "stateVersion": 42 }
    }))
    .unwrap();
    match step {
        Step::Invoke { expect, .. } => {
            assert_eq!(expect.and_then(|e| e.state_version), Some(42));
        }
        other => panic!("expected invoke, got {other:?}"),
    }
    assert!(step.is_mutating());
}

#[test]
fn request_opcodes_round_trip() {
    for op in Op::ALL {
        assert_eq!(Op::from_code(op.code()), Some(op));
        assert_eq!(
            op.is_mutating(),
            matches!(op, Op::Act | Op::Run | Op::Shutdown)
        );
    }
}

#[test]
fn replies_are_self_describing() {
    let reply = Reply::ok(
        7,
        Op::Act,
        ResponsePayload::Acted(ActionOutcome {
            action: "invoke".into(),
            backend: "uia".into(),
            element: None,
            verified: true,
            detail: None,
            state_version: 9,
            generation: 2,
            elapsed_us: 120,
        }),
        9,
        2,
        120,
    )
    .with_session(1);
    let value = serde_json::to_value(&reply).unwrap();
    assert_eq!(value["ok"], json!(true));
    assert_eq!(value["op"], json!("act"));
    assert_eq!(value["state_version"], json!(9));
    assert!(value.get("error").is_none(), "ok replies omit the error");
    let back: Reply = serde_json::from_value(value).unwrap();
    assert!(back.ok);

    let failure = Reply::err(
        8,
        Op::Run,
        InbriskError::new(ErrorCode::StaleState, "world moved"),
        12,
        3,
        90,
    );
    let value = serde_json::to_value(&failure).unwrap();
    assert_eq!(value["error"]["code"], json!("stale_state"));
    assert_eq!(value["error"]["retryable"], json!(true));
}

#[test]
fn hello_and_ping_round_trip() {
    let hello = Request::Hello(Hello {
        protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
        client_name: "unit-test".into(),
        client_version: "0.0.0".into(),
        client_pid: 4242,
        nonce: 0xDEAD_BEEF,
        capabilities: inbrisk_protocol::caps::Capabilities::empty()
            .with(inbrisk_protocol::caps::Capabilities::WIN32),
        wants_events: true,
        expected_epoch: Some(1),
    });
    let value = serde_json::to_value(&hello).unwrap();
    assert_eq!(value["op"], json!("hello"));
    assert_eq!(value["protocol_version"], json!(1));
    let back: Request = serde_json::from_value(value).unwrap();
    assert_eq!(back.op(), Op::Hello);

    let ping = Request::Ping(Ping {
        seq: 3,
        client_send_ns: 99,
    });
    assert_eq!(serde_json::to_value(&ping).unwrap()["op"], json!("ping"));
}

#[test]
fn status_report_serialises_optional_sections_away() {
    let report = StatusReport {
        identity: inbrisk_core::RuntimeIdentity {
            name: "inbrisk-runtime".into(),
            version: "0.1.0".into(),
            protocol_version: 1,
            pid: 10,
            epoch: 20,
        },
        uptime_ms: 5,
        state_version: 1,
        generation: 1,
        activity: Default::default(),
        capabilities: inbrisk_protocol::caps::Capabilities::empty(),
        sessions: Vec::new(),
        counters: Default::default(),
        ipc: None,
        ownership: Vec::new(),
        windows: Vec::new(),
        log_tail: Vec::new(),
        backends: vec!["win32".into()],
        emergency: false,
        paused: false,
        desktop_probe: None,
        desktop_broker: None,
        virtual_desktop: None,
    };
    let value = serde_json::to_value(&report).unwrap();
    assert!(value.get("ipc").is_none());
    assert!(value.get("ownership").is_none());
    assert!(value.get("desktop_probe").is_none());
    assert!(value.get("desktop_broker").is_none());
    assert!(value.get("virtual_desktop").is_none());
    assert_eq!(value["backends"][0], json!("win32"));
}

#[test]
fn step_outcomes_are_stable() {
    let outcome = StepOutcome {
        index: 0,
        action: "click".into(),
        status: StepStatus::Ok,
        backend: "input".into(),
        verified: false,
        elapsed_us: 10,
        element: None,
        error: None,
        output: Some(json!({ "clicked": [1, 2] })),
    };
    let value = serde_json::to_value(&outcome).unwrap();
    assert_eq!(value["status"], json!("ok"));
    assert_eq!(value["verified"], json!(false));

    // hwnd survives a JSON round trip as a plain number.
    let selector = Selector::default().with_hwnd(Hwnd(0x1234));
    let value = serde_json::to_value(&selector).unwrap();
    assert_eq!(value["hwnd"], json!(0x1234));
}
