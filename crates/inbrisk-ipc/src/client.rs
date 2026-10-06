//! Client side of the shared-memory channel.
//!
//! One `call` == one ring entry == one `WaitOnAddress`. There is no polling
//! and no helper process: the CLI, the SDK and the MCP proxy all speak this.

use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

use inbrisk_core::{now_ms, ErrorCode, InbriskError, Result, SessionId};
use inbrisk_protocol::caps::Capabilities;
use inbrisk_protocol::event::EventEnvelope;
use inbrisk_protocol::opcode::Op;
use inbrisk_protocol::request::{Hello, Request};
use inbrisk_protocol::response::{Reply, ResponsePayload};
use inbrisk_protocol::PROTOCOL_VERSION;

use crate::layout::{
    self, align_up, EventSlot, SessionHeader, HEADER_SIZE, MAGIC, MAX_SESSIONS, SESSION_CLAIMING,
    SESSION_FREE, SESSION_HANDSHAKE,
};
use crate::region::MappedRegion;
use crate::ring::{Consumer, Producer};
use crate::security::region_name;
use crate::wake::{wake_one, IpcEvent, WaitOutcome};

/// Everything a client needs to talk to the runtime in-process.
#[derive(Debug)]
pub struct IpcClient {
    region: MappedRegion,
    index: usize,
    session_id: SessionId,
    client_id: u64,
    epoch: u64,
    next_request_id: AtomicU64,
    request_producer: std::sync::Mutex<Producer>,
    response_consumer: Consumer,
    call_lock: std::sync::Mutex<()>,
    event_cursor: std::sync::Mutex<u64>,
    region_name: String,
    client_name: String,
    client_version: String,
    nonce: u64,
    req_event: IpcEvent,
    resp_event: IpcEvent,
    event_signal: IpcEvent,
}

unsafe impl Send for IpcClient {}
unsafe impl Sync for IpcClient {}

impl IpcClient {
    /// Open the region and complete the `HELLO`/`WELCOME` handshake.
    pub fn connect(
        client_name: &str,
        client_version: &str,
        wants_events: bool,
        timeout: Duration,
    ) -> Result<Self> {
        let name = region_name()?;
        let region = MappedRegion::open(&name)?;
        let view = region.region();
        let header = view.header();

        if header.magic != MAGIC {
            return Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                "shared region has no Inbrisk magic value",
            )
            .with_hint("an unrelated object occupies the region name"));
        }
        if header.protocol_version != PROTOCOL_VERSION {
            return Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                format!(
                    "runtime speaks protocol {}, this client speaks {PROTOCOL_VERSION}",
                    header.protocol_version
                ),
            )
            .with_hint("update the client and the runtime together"));
        }
        if !crate::server::process_alive(header.runtime_pid) {
            return Err(InbriskError::new(
                ErrorCode::RuntimeUnavailable,
                format!(
                    "region belongs to pid {} which is no longer running",
                    header.runtime_pid
                ),
            ));
        }

        let stride = header.shard_stride;
        let capacity = header.session_capacity.min(MAX_SESSIONS as u32) as usize;

        // Claim a session slot: FREE -> CLAIMING is the only atomic step.
        let mut claimed = None;
        for i in 0..capacity {
            let sh = unsafe { &mut (*view.header_ptr()).sessions[i] };
            if sh
                .state
                .compare_exchange(
                    SESSION_FREE,
                    SESSION_CLAIMING,
                    Ordering::AcqRel,
                    Ordering::Acquire,
                )
                .is_ok()
            {
                claimed = Some(i);
                break;
            }
        }
        let index = claimed.ok_or_else(|| {
            InbriskError::new(
                ErrorCode::RuntimeUnavailable,
                format!("all {capacity} runtime sessions are in use"),
            )
            .with_hint("close idle MCP hosts or restart inbrisk.exe")
        })?;

        let nonce = make_nonce();
        let pid = std::process::id();
        {
            let sh = unsafe { &mut (*view.header_ptr()).sessions[index] };
            sh.generation = sh.generation.wrapping_add(1);
            sh.client_id = 0;
            sh.client_pid = pid;
            sh.client_nonce_lo = nonce as u32;
            sh.connected_at_ms = now_ms();
            sh.last_seen_ms = now_ms();
            sh.req_write.store(0, Ordering::Release);
            sh.req_read.store(0, Ordering::Release);
            sh.resp_write.store(0, Ordering::Release);
            sh.resp_read.store(0, Ordering::Release);
            sh.requests = 0;
            sh.responses = 0;
            sh.dropped = 0;
            sh.epoch = header.runtime_epoch;
            sh.set_name(client_name);
            sh.set_version(client_version);
            // Publish only once every field is in place.
            sh.state.store(SESSION_HANDSHAKE, Ordering::Release);
        }

        let shard = layout::shard_layout(stride, index);
        let producer = Producer::new(
            shard.req_entries,
            header.req_capacity as u64,
            shard.req_arena,
            shard.req_arena_size,
            header.req_payload_max,
        );
        let consumer = Consumer::new(shard.resp_entries, header.resp_capacity as u64);
        let runtime_epoch = header.runtime_epoch;
        let initial_event_cursor = header.event_write_seq.load(Ordering::Acquire);

        let req_event = IpcEvent::create_or_open(&format!("{name}.req"), false)?;
        let resp_event = IpcEvent::create_or_open(&format!("{name}.resp.{}", index + 1), false)?;
        let event_signal = IpcEvent::create_or_open(&format!("{name}.events"), false)?;

        let mut client = Self {
            region,
            index,
            session_id: (index + 1) as SessionId,
            client_id: 0,
            epoch: runtime_epoch,
            next_request_id: AtomicU64::new(0),
            request_producer: std::sync::Mutex::new(producer),
            response_consumer: consumer,
            call_lock: std::sync::Mutex::new(()),
            event_cursor: std::sync::Mutex::new(initial_event_cursor),
            region_name: name,
            client_name: client_name.to_string(),
            client_version: client_version.to_string(),
            nonce,
            req_event,
            resp_event,
            event_signal,
        };

        let hello = Request::Hello(Hello {
            protocol_version: PROTOCOL_VERSION,
            client_name: client_name.to_string(),
            client_version: client_version.to_string(),
            client_pid: pid,
            nonce,
            capabilities: Capabilities::empty(),
            wants_events,
            expected_epoch: Some(client.epoch),
        });

        let reply = client.call(&hello, timeout)?;
        if !reply.ok {
            let err = reply
                .error
                .unwrap_or_else(|| InbriskError::internal("handshake rejected"));
            client.release();
            return Err(err);
        }
        match reply.result {
            Some(ResponsePayload::Welcome(w)) => {
                if w.protocol_version != PROTOCOL_VERSION {
                    client.release();
                    return Err(InbriskError::new(
                        ErrorCode::ProtocolMismatch,
                        format!("runtime welcomed protocol {}", w.protocol_version),
                    ));
                }
                if w.session_id != client.session_id {
                    client.release();
                    return Err(InbriskError::new(
                        ErrorCode::ProtocolMismatch,
                        format!(
                            "runtime assigned session {} but the shard is {}",
                            w.session_id, client.session_id
                        ),
                    ));
                }
                client.client_id = w.client_id;
                client.epoch = w.runtime_epoch;
            }
            _ => {
                client.release();
                return Err(InbriskError::new(
                    ErrorCode::ProtocolMismatch,
                    "runtime answered HELLO with an unexpected payload",
                ));
            }
        }

        Ok(client)
    }

    fn release(&self) {
        let view = self.region.region();
        let sh = unsafe { &mut (*view.header_ptr()).sessions[self.index] };
        sh.state.store(SESSION_FREE, Ordering::Release);
    }

    pub fn session_id(&self) -> SessionId {
        self.session_id
    }

    pub fn client_id(&self) -> u64 {
        self.client_id
    }

    pub fn epoch(&self) -> u64 {
        self.epoch
    }

    pub fn nonce(&self) -> u64 {
        self.nonce
    }

    pub fn region_name(&self) -> &str {
        &self.region_name
    }

    pub fn client_name(&self) -> &str {
        &self.client_name
    }

    pub fn client_version(&self) -> &str {
        &self.client_version
    }

    pub fn region_bytes(&self) -> usize {
        self.region.size()
    }

    fn session_header(&self) -> &SessionHeader {
        &self.region.region().header().sessions[self.index]
    }

    pub fn runtime_epoch(&self) -> u64 {
        self.region.region().header().runtime_epoch
    }

    pub fn runtime_pid(&self) -> u32 {
        self.region.region().header().runtime_pid
    }

    pub fn state_version(&self) -> u64 {
        self.region.region().header().state_version
    }

    pub fn generation(&self) -> u64 {
        self.region.region().header().generation
    }

    /// Send one request and wait for its reply.
    pub fn call(&self, request: &Request, timeout: Duration) -> Result<Reply> {
        let _guard = self
            .call_lock
            .lock()
            .map_err(|_| InbriskError::internal("client lock poisoned"))?;
        let view = self.region.region();
        let request_id = self.next_request_id.fetch_add(1, Ordering::AcqRel) + 1;
        let op = request.op();
        let bytes = serde_json::to_vec(request)
            .map_err(|e| InbriskError::internal(format!("cannot encode request: {e}")))?;

        let _ticket = {
            let mut producer = self
                .request_producer
                .lock()
                .map_err(|_| InbriskError::internal("request ring poisoned"))?;
            let sh = self.session_header();
            producer.publish(
                view,
                &sh.req_write,
                &sh.req_read,
                &bytes,
                self.session_id,
                op.code(),
                request_id,
                0,
                Some(timeout.as_millis().min(u32::MAX as u128) as u32),
            )?
        };

        // Wake the runtime: a plain atomic store does not wake a waiter.
        let header = view.header();
        header.request_epoch.fetch_add(1, Ordering::AcqRel);
        unsafe { wake_one(&header.request_epoch as *const _ as *const u64) };
        self.req_event.set();

        let deadline = Instant::now() + timeout;
        let sh = self.session_header();
        loop {
            let r = sh.resp_read.load(Ordering::Acquire);
            let w = sh.resp_write.load(Ordering::Acquire);
            if r < w {
                if let Some((slot, payload)) =
                    self.response_consumer
                        .next(view, &sh.resp_write, &sh.resp_read)
                {
                    let reply: Reply = serde_json::from_slice(&payload).map_err(|e| {
                        InbriskError::internal(format!("runtime sent an undecodable reply: {e}"))
                    })?;
                    unsafe { wake_one(&sh.resp_read as *const _ as *const u64) };
                    if reply.request_id == request_id {
                        return Ok(reply);
                    }
                    // A reply for another request id can only happen after a
                    // protocol bug; keep draining rather than blocking forever.
                    let _ = slot;
                    continue;
                }
            }
            let now = Instant::now();
            if now >= deadline {
                return Err(InbriskError::new(
                    ErrorCode::Timeout,
                    format!("'{}' did not answer within {:?}", op.as_str(), timeout),
                )
                .with_hint("check inbrisk status; the operation may still be running"));
            }
            let remaining = deadline - now;
            match self
                .resp_event
                .wait(Some(remaining.as_millis().min(1000) as u32))
            {
                WaitOutcome::Woken => {}
                WaitOutcome::TimedOut => {}
                WaitOutcome::Failed => {
                    return Err(InbriskError::new(
                        ErrorCode::RuntimeUnavailable,
                        "waiting on the response ring failed",
                    ));
                }
            }
        }
    }

    /// Convenience: send and unwrap the payload, turning `ok == false` into an error.
    pub fn call_ok(&self, request: &Request, timeout: Duration) -> Result<ResponsePayload> {
        let reply = self.call(request, timeout)?;
        if reply.ok {
            reply
                .result
                .ok_or_else(|| InbriskError::internal("runtime returned ok without a payload"))
        } else {
            Err(reply.error.unwrap_or_else(|| {
                InbriskError::internal("runtime returned an error with no code")
            }))
        }
    }

    /// Non-blocking event read.
    pub fn poll_event(&self) -> Option<EventEnvelope> {
        let mut cursor = self.event_cursor.lock().ok()?;
        read_event(&self.region, &mut cursor)
    }

    /// Block for the next event.
    pub fn wait_event(&self, timeout: Duration) -> Option<EventEnvelope> {
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(ev) = self.poll_event() {
                return Some(ev);
            }
            let now = Instant::now();
            if now >= deadline {
                return None;
            }
            let ms = (deadline - now).as_millis().min(1000) as u32;
            if self.event_signal.wait(Some(ms)) == WaitOutcome::Failed {
                return None;
            }
        }
    }

    /// Leave the session. Also runs on drop.
    pub fn disconnect(&self) {
        self.release();
        let header = self.region.region().header();
        header.request_epoch.fetch_add(1, Ordering::AcqRel);
        unsafe { wake_one(&header.request_epoch as *const _ as *const u64) };
        self.req_event.set();
    }
}

impl Drop for IpcClient {
    fn drop(&mut self) {
        self.release();
    }
}

fn read_event(region: &MappedRegion, cursor: &mut u64) -> Option<EventEnvelope> {
    let view = region.region();
    let header = view.header();
    let w = header.event_write_seq.load(Ordering::Acquire);
    if *cursor >= w {
        return None;
    }
    let capacity = header.event_capacity as u64;
    let base =
        align_up(HEADER_SIZE as u64, 4096) + header.shard_stride * header.session_capacity as u64;
    let next = *cursor + 1;
    let index = ((next - 1) % capacity) as usize;
    let slot = unsafe {
        &*view.at::<EventSlot>(base + index as u64 * std::mem::size_of::<EventSlot>() as u64)
    };
    if slot.seq != next {
        // The writer lapped us; resynchronise instead of spinning.
        *cursor = w.saturating_sub(capacity.saturating_sub(1));
        return None;
    }
    let bytes = &slot.inline[..slot.len as usize];
    *cursor = next;
    serde_json::from_slice::<EventEnvelope>(bytes).ok()
}

/// Which operation a reply belongs to, without decoding the payload.
pub fn reply_op(reply: &Reply) -> Op {
    reply.op
}

fn make_nonce() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_nanos() as u64)
        .unwrap_or(1);
    nanos.wrapping_mul(0x9E37_79B9_7F4A_7C15).rotate_left(23) ^ (std::process::id() as u64)
}
