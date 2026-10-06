//! End-to-end IPC tests: a real region, a real client, a real wake handshake.
//!
//! These tests are the proof that the fast path works: no mocks, the actual
//! `CreateFileMappingW` + `WaitOnAddress` path that ships.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use inbrisk_core::{now_ms, RuntimeIdentity};
use inbrisk_ipc::{region_name, IncomingRequest, IpcClient, IpcServer};
use inbrisk_protocol::caps::Capabilities;
use inbrisk_protocol::opcode::Op;
use inbrisk_protocol::request::{Hello, Ping, Request};
use inbrisk_protocol::response::{Pong, Reply, ResponsePayload, Welcome};

/// Only one test may own the per-user region at a time.
static REGION_LOCK: Mutex<()> = Mutex::new(());

/// Minimal runtime stand-in: answers HELLO, PING and STATUS.
struct EchoRuntime {
    server: Arc<IpcServer>,
    stop: Arc<AtomicBool>,
    thread: Option<std::thread::JoinHandle<()>>,
    served: Arc<AtomicU64>,
}

impl EchoRuntime {
    fn start(capabilities: Capabilities) -> Self {
        let server = Arc::new(IpcServer::create(capabilities, "test").expect("region"));
        let stop = Arc::new(AtomicBool::new(false));
        let served = Arc::new(AtomicU64::new(0));
        let s = Arc::clone(&server);
        let st = Arc::clone(&stop);
        let sv = Arc::clone(&served);
        let thread = std::thread::Builder::new()
            .name("test-runtime".into())
            .spawn(move || {
                while !st.load(Ordering::Acquire) {
                    if let Some(incoming) = s.wait(Duration::from_millis(50)) {
                        let reply = answer(&s, incoming);
                        let session = reply.session_id.unwrap_or(0);
                        let _ = s.respond(session, &reply);
                        sv.fetch_add(1, Ordering::Relaxed);
                    }
                }
            })
            .expect("spawn");
        Self {
            server,
            stop,
            thread: Some(thread),
            served,
        }
    }

    fn name(&self) -> &str {
        self.server.name()
    }
}

impl Drop for EchoRuntime {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(t) = self.thread.take() {
            let _ = t.join();
        }
    }
}

fn answer(server: &IpcServer, incoming: IncomingRequest) -> Reply {
    let op = incoming.request.op();
    let state_version = server.header().state_version;
    let generation = server.header().generation;
    match incoming.request {
        Request::Hello(hello) => {
            let client_id = (hello.nonce & 0xFFFF_FFFF) | 1;
            let _ = server.activate(incoming.session_id, client_id);
            Reply::ok(
                incoming.request_id,
                op,
                ResponsePayload::Welcome(Welcome {
                    session_id: incoming.session_id,
                    client_id,
                    runtime_epoch: server.epoch(),
                    protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
                    runtime: RuntimeIdentity {
                        name: "test-runtime".into(),
                        version: "0.0.0".into(),
                        protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
                        pid: std::process::id(),
                        epoch: server.epoch(),
                    },
                    capabilities: Capabilities::empty().with(Capabilities::WIN32),
                    region_name: server.name().to_string(),
                    heartbeat_ms: 1000,
                    state_version,
                    generation,
                }),
                state_version,
                generation,
                0,
            )
            .with_session(incoming.session_id)
        }
        Request::Ping(ping) => Reply::ok(
            incoming.request_id,
            op,
            ResponsePayload::Pong(Pong {
                seq: ping.seq,
                client_send_ns: ping.client_send_ns,
                runtime_recv_ns: now_ms() * 1_000_000,
                runtime_send_ns: now_ms() * 1_000_000,
                session_id: incoming.session_id,
            }),
            state_version,
            generation,
            0,
        )
        .with_session(incoming.session_id),
        _ => Reply::err(
            incoming.request_id,
            op,
            inbrisk_core::InbriskError::not_implemented("the echo runtime only answers hello/ping"),
            state_version,
            generation,
            0,
        )
        .with_session(incoming.session_id),
    }
}

fn connect(name: &str) -> IpcClient {
    IpcClient::connect(name, "test", true, Duration::from_secs(5)).expect("handshake")
}

#[test]
fn handshake_ping_and_latency_histogram() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let runtime = EchoRuntime::start(Capabilities::empty().with(Capabilities::WIN32));
    assert_eq!(
        region_name().unwrap(),
        runtime.name(),
        "the client and the runtime must agree on the object name"
    );

    let client = connect("latency-test");
    assert_eq!(client.session_id(), 1);
    assert_eq!(client.region_name(), runtime.name());
    assert!(client.runtime_epoch() > 0);

    // Warm up, then measure. The loop below is the real fast path: one ring
    // publish, one WaitOnAddress, one response.
    const ITERATIONS: usize = 2_000;
    let mut samples = Vec::with_capacity(ITERATIONS);
    for seq in 0..ITERATIONS {
        let started = Instant::now();
        let reply = client
            .call(
                &Request::Ping(Ping {
                    seq: seq as u64,
                    client_send_ns: 0,
                }),
                Duration::from_secs(5),
            )
            .expect("ping must be answered");
        let micros = started.elapsed().as_micros() as u64;
        assert!(reply.ok);
        assert_eq!(reply.op, Op::Ping);
        if seq >= 100 {
            samples.push(micros);
        }
    }
    samples.sort_unstable();
    let pct = |p: f64| samples[((samples.len() - 1) as f64 * p).round() as usize];
    let (p50, p95, p99) = (pct(0.50), pct(0.95), pct(0.99));
    println!(
        "IPC RTT over {} round trips: p50 {p50}us  p95 {p95}us  p99 {p99}us  max {}us  (served {})",
        samples.len(),
        samples.last().copied().unwrap_or(0),
        runtime.served.load(Ordering::Relaxed)
    );

    // Deliberately loose bounds: this asserts the transport is *fast*, not that
    // the machine is idle. Sub-millisecond p50 is the design target.
    assert!(
        p50 < 2_000,
        "p50 round trip was {p50}us, expected well under 2ms"
    );
    assert!(
        p99 < 20_000,
        "p99 round trip was {p99}us, expected well under 20ms"
    );

    // The runtime must have been woken by the wake address, not by its timeout.
    let stats = runtime.server.stats();
    assert!(
        stats.wake_signals > 0,
        "expected WaitOnAddress wakeups, saw {}",
        stats.wake_signals
    );
    assert!(stats.requests_seen >= ITERATIONS as u64);

    client.disconnect();
}

#[test]
fn two_clients_get_independent_sessions() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let runtime = EchoRuntime::start(Capabilities::empty().with(Capabilities::WIN32));

    let a = connect("client-a");
    let b = connect("client-b");
    assert_ne!(a.session_id(), b.session_id());
    assert_eq!(a.region_name(), b.region_name());

    // Interleaved traffic must not cross wires: each client sees its own id.
    for seq in 0..25u64 {
        let ra = a
            .call(
                &Request::Ping(Ping {
                    seq,
                    client_send_ns: 0,
                }),
                Duration::from_secs(5),
            )
            .unwrap();
        let rb = b
            .call(
                &Request::Ping(Ping {
                    seq: seq + 1000,
                    client_send_ns: 0,
                }),
                Duration::from_secs(5),
            )
            .unwrap();
        assert_eq!(ra.request_id, seq + 2);
        assert_eq!(rb.request_id, seq + 2);
    }

    let sessions = runtime.server.sessions();
    assert_eq!(sessions.len(), 2, "two live sessions expected");
    let names: Vec<String> = sessions.iter().map(|s| s.name.clone()).collect();
    assert!(names.contains(&"client-a".to_string()));
    assert!(names.contains(&"client-b".to_string()));

    a.disconnect();
    b.disconnect();
}

#[test]
fn disconnect_frees_the_session_slot() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let runtime = EchoRuntime::start(Capabilities::empty());
    for _ in 0..16 {
        let client = connect("cycler");
        assert_eq!(client.session_id(), 1, "the freed slot must be reused");
        client.disconnect();
    }
    let sessions = runtime.server.sessions();
    assert!(
        sessions.is_empty(),
        "disconnected sessions must not linger: {sessions:?}"
    );
}

#[test]
fn connecting_without_a_runtime_fails_cleanly() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    // Nothing owns the region in this test.
    let err = IpcClient::connect("probe", "test", false, Duration::from_millis(500)).unwrap_err();
    assert_eq!(err.code, inbrisk_core::ErrorCode::RuntimeUnavailable);
    assert_eq!(err.retryable, true);
}

#[test]
fn protocol_mismatch_is_rejected_before_any_work() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let _runtime = EchoRuntime::start(Capabilities::empty());
    // Corrupt the advertised protocol version and confirm the client refuses to
    // proceed rather than talking a dialect the runtime does not speak.
    let name = region_name().unwrap();
    {
        let region = inbrisk_ipc::MappedRegion::open(&name).unwrap();
        unsafe {
            let header = region.region().header_ptr();
            (*header).protocol_version = inbrisk_protocol::PROTOCOL_VERSION + 1;
        }
    }
    let err =
        IpcClient::connect("mismatch", "test", false, Duration::from_millis(500)).unwrap_err();
    assert_eq!(err.code, inbrisk_core::ErrorCode::ProtocolMismatch);

    // Restore so a later test can still use the region.
    {
        let region = inbrisk_ipc::MappedRegion::open(&name).unwrap();
        unsafe {
            let header = region.region().header_ptr();
            (*header).protocol_version = inbrisk_protocol::PROTOCOL_VERSION;
        }
    }
}

#[test]
fn hello_handshake_carries_identity() {
    let _guard = REGION_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let runtime = EchoRuntime::start(Capabilities::empty().with(Capabilities::EVENTS));
    let client = connect("identity-test");
    let hello = Request::Hello(Hello {
        protocol_version: inbrisk_protocol::PROTOCOL_VERSION,
        client_name: "identity-test".into(),
        client_version: "test".into(),
        client_pid: std::process::id(),
        nonce: 12345,
        capabilities: Capabilities::empty(),
        wants_events: true,
        expected_epoch: Some(client.runtime_epoch()),
    });
    // A second HELLO on the same session must still be answered.
    let reply = client.call(&hello, Duration::from_secs(5)).unwrap();
    assert!(reply.ok);
    assert_eq!(reply.session_id, Some(client.session_id()));
    assert_eq!(reply.op, Op::Hello);
    assert!(matches!(reply.result, Some(ResponsePayload::Welcome(_))));
    assert!(runtime.served.load(Ordering::Relaxed) >= 2);
    client.disconnect();
}
