//! Runtime side of the shared-memory channel.

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use inbrisk_core::{log_debug, log_info, now_ms, ClientId, InbriskError, Result, SessionId};
use inbrisk_protocol::caps::Capabilities;
use inbrisk_protocol::event::{Event, EventEnvelope};
use inbrisk_protocol::response::{ClientInfo, IpcStats, Reply};
use inbrisk_protocol::{Request, PROTOCOL_VERSION};

use crate::layout::{
    self, align_up, EventSlot, Header, RegionView, SessionHeader, HEADER_SIZE, MAGIC, MAX_SESSIONS,
    SESSION_ACTIVE, SESSION_CLOSING, SESSION_FREE, SESSION_HANDSHAKE,
};
use crate::region::MappedRegion;
use crate::ring::{Consumer, Producer};
use crate::security::region_name;
use crate::wake::{wake_all, wake_one, IpcEvent, WaitOutcome};

pub use crate::layout::SessionHeader as SessionHeaderView;

/// One request lifted out of a session ring.
#[derive(Debug)]
pub struct IncomingRequest {
    pub session_id: SessionId,
    pub client_id: ClientId,
    pub request_id: u64,
    pub request: Request,
}

/// A connected client as seen by the runtime.
#[derive(Debug, Clone)]
pub struct ServerSession {
    pub session_id: SessionId,
    pub client_id: ClientId,
    pub client_pid: u32,
    pub name: String,
    pub version: String,
    pub connected_at_ms: u64,
    pub requests: u64,
}

#[derive(Debug)]
struct SessionState {
    consumer: Consumer,
    producer: std::sync::Mutex<Producer>,
}

/// The runtime's handle on the shared region.
pub struct IpcServer {
    region: MappedRegion,
    name: String,
    epoch: u64,
    nonce: u64,
    stride: u64,
    event_offset: u64,
    event_capacity: u64,
    event_cursor: std::sync::Mutex<u64>,
    sessions: Vec<SessionState>,
    started: Instant,
    rtt_samples: std::sync::Mutex<Vec<u64>>,
    req_event: IpcEvent,
    resp_events: Vec<IpcEvent>,
    event_signal: IpcEvent,
}

unsafe impl Send for IpcServer {}
unsafe impl Sync for IpcServer {}

impl IpcServer {
    /// Object name for the current user.
    pub fn object_name() -> Result<String> {
        region_name()
    }

    /// Create the region and publish the runtime identity into it.
    pub fn create(capabilities: Capabilities, runtime_version: &str) -> Result<Self> {
        let name = region_name()?;
        let (total, stride) = layout::layout();
        let region = MappedRegion::create(&name, total as usize)?;
        let view = region.region();
        view.reset();

        let epoch = make_epoch();
        let nonce = make_nonce();
        let event_offset = align_up(HEADER_SIZE as u64, 4096) + stride * MAX_SESSIONS as u64;
        let event_capacity = layout::EVENT_CAPACITY as u64;
        let event_size = event_capacity * std::mem::size_of::<EventSlot>() as u64;

        unsafe {
            let h = &mut *view.header_ptr();
            // The region was just zeroed, so *every* layout field has to be
            // published here: a client derives all of its offsets from them.
            h.protocol_version = PROTOCOL_VERSION;
            h.codec = 1;
            h.header_size = HEADER_SIZE as u32;
            h.runtime_pid = std::process::id();
            h.runtime_epoch = epoch;
            h.runtime_nonce = nonce;
            h.capabilities = capabilities.0;
            h.created_at_ms = now_ms();
            h.shard_stride = stride;
            h.shard_size = stride;
            h.event_entries_offset = event_offset;
            h.event_entries_size = event_size;
            h.state_version = 1;
            h.generation = 1;
            h.req_capacity = layout::REQ_CAPACITY as u32;
            h.resp_capacity = layout::RESP_CAPACITY as u32;
            h.event_capacity = event_capacity as u32;
            h.req_payload_max = layout::REQ_PAYLOAD_MAX;
            h.resp_payload_max = layout::RESP_PAYLOAD_MAX;
            h.event_inline_max = layout::EVENT_INLINE_MAX as u32;
            h.session_capacity = MAX_SESSIONS as u32;
            // Magic is written last: a client that maps the region can never
            // observe a half-initialised header.
            h.magic = MAGIC;
        }

        let mut sessions = Vec::with_capacity(MAX_SESSIONS);
        for i in 0..MAX_SESSIONS {
            let shard = layout::shard_layout(stride, i);
            sessions.push(SessionState {
                consumer: Consumer::new(shard.req_entries, layout::REQ_CAPACITY as u64),
                producer: std::sync::Mutex::new(Producer::new(
                    shard.resp_entries,
                    layout::RESP_CAPACITY as u64,
                    shard.resp_arena,
                    shard.resp_arena_size,
                    layout::RESP_PAYLOAD_MAX,
                )),
            });
        }

        let req_event = IpcEvent::create_or_open(&format!("{name}.req"), false)?;
        let mut resp_events = Vec::with_capacity(MAX_SESSIONS);
        for i in 0..MAX_SESSIONS {
            resp_events.push(IpcEvent::create_or_open(
                &format!("{name}.resp.{}", i + 1),
                false,
            )?);
        }
        let event_signal = IpcEvent::create_or_open(&format!("{name}.events"), false)?;

        log_info!(
            "ipc",
            "region '{name}' created ({} KiB, version {runtime_version}, epoch {epoch})",
            total / 1024
        );

        Ok(Self {
            region,
            name,
            epoch,
            nonce,
            stride,
            event_offset,
            event_capacity,
            event_cursor: std::sync::Mutex::new(0),
            sessions,
            started: Instant::now(),
            rtt_samples: std::sync::Mutex::new(Vec::with_capacity(1024)),
            req_event,
            resp_events,
            event_signal,
        })
    }

    pub fn name(&self) -> &str {
        &self.name
    }

    pub fn epoch(&self) -> u64 {
        self.epoch
    }

    pub fn nonce(&self) -> u64 {
        self.nonce
    }

    pub fn view(&self) -> &RegionView {
        self.region.region()
    }

    pub fn header(&self) -> &Header {
        self.region.region().header()
    }

    pub fn session_header(&self, index: usize) -> &SessionHeader {
        &self.region.region().header().sessions[index]
    }

    /// Where one session's rings and arenas live inside the region.
    pub fn session_shard(&self, index: usize) -> layout::ShardLayout {
        layout::shard_layout(self.stride, index)
    }

    /// Mutable access to a session header.
    ///
    /// Only the runtime mutates these fields, and every cross-process read is
    /// ordered by the `state` atomic, so this is a single-writer structure.
    fn session_mut(&self, index: usize) -> &mut SessionHeader {
        unsafe { &mut (*self.region.region().header_ptr()).sessions[index] }
    }

    pub fn uptime(&self) -> Duration {
        self.started.elapsed()
    }

    pub fn region_bytes(&self) -> usize {
        self.region.size()
    }

    /// Publish the current world version so guards can be checked cheaply.
    pub fn set_state(&self, state_version: u64, generation: u64) {
        unsafe {
            let h = &mut *self.region.region().header_ptr();
            h.state_version = state_version;
            h.generation = generation;
        }
    }

    /// Look for the next request. Non-blocking.
    pub fn poll(&self) -> Option<IncomingRequest> {
        let view = self.region.region();
        for (index, state) in self.sessions.iter().enumerate() {
            let sh = &view.header().sessions[index];
            let status = sh.state.load(Ordering::Acquire);
            if status != SESSION_ACTIVE && status != SESSION_HANDSHAKE {
                continue;
            }
            if let Some((slot, bytes)) = state.consumer.next(view, &sh.req_write, &sh.req_read) {
                // Let a client blocked on ring capacity continue.
                unsafe { wake_one(&sh.req_read as *const _ as *const u64) };
                let request: Request = match serde_json::from_slice(&bytes) {
                    Ok(r) => r,
                    Err(e) => {
                        log_debug!("ipc", "undecodable request from session {}: {e}", index + 1);
                        continue;
                    }
                };
                self.header().total_requests.fetch_add(1, Ordering::Relaxed);
                return Some(IncomingRequest {
                    session_id: (index + 1) as SessionId,
                    client_id: sh.client_id,
                    request_id: slot.request_id,
                    request,
                });
            }
        }
        None
    }

    /// Block until a request arrives or the timeout elapses. Zero CPU while idle.
    pub fn wait(&self, timeout: Duration) -> Option<IncomingRequest> {
        if let Some(req) = self.poll() {
            return Some(req);
        }
        let deadline = Instant::now() + timeout;
        loop {
            if let Some(req) = self.poll() {
                return Some(req);
            }
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return self.poll();
            }
            let ms = remaining.as_millis().min(1000) as u32;
            match self.req_event.wait(Some(ms)) {
                WaitOutcome::Woken => {
                    self.header().total_wakes.fetch_add(1, Ordering::Relaxed);
                    if let Some(req) = self.poll() {
                        return Some(req);
                    }
                }
                WaitOutcome::TimedOut => {
                    if Instant::now() >= deadline {
                        return self.poll();
                    }
                }
                WaitOutcome::Failed => return self.poll(),
            }
        }
    }

    /// Send a reply to a session.
    pub fn respond(&self, session_id: SessionId, reply: &Reply) -> Result<()> {
        let index = session_id
            .checked_sub(1)
            .map(|i| i as usize)
            .filter(|i| *i < MAX_SESSIONS)
            .ok_or_else(|| InbriskError::internal(format!("bad session id {session_id}")))?;
        let view = self.region.region();
        let sh = &view.header().sessions[index];
        if sh.state.load(Ordering::Acquire) == SESSION_FREE {
            return Ok(());
        }
        let bytes = serde_json::to_vec(reply)
            .map_err(|e| InbriskError::internal(format!("cannot encode reply: {e}")))?;
        let mut producer = self.sessions[index]
            .producer
            .lock()
            .map_err(|_| InbriskError::internal("response ring poisoned"))?;
        let _ticket = producer.publish(
            view,
            &sh.resp_write,
            &sh.resp_read,
            &bytes,
            session_id,
            reply.op.code(),
            reply.request_id,
            0,
            Some(30_000),
        )?;
        unsafe { wake_one(&sh.resp_write as *const _ as *const u64) };
        if index < self.resp_events.len() {
            self.resp_events[index].set();
        }
        self.header()
            .total_responses
            .fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    /// Publish an event to every subscriber (inline payloads, no arena).
    pub fn publish_event(&self, event: &Event) -> Result<()> {
        let envelope = EventEnvelope {
            seq: 0,
            at_ms: now_ms(),
            subscription_id: None,
            event: event.clone(),
        };
        let bytes = serde_json::to_vec(&envelope)
            .map_err(|e| InbriskError::internal(format!("cannot encode event: {e}")))?;
        let inline_max = layout::EVENT_INLINE_MAX;
        if bytes.len() > inline_max {
            self.header()
                .event_seq_dropped
                .fetch_add(1, Ordering::Relaxed);
            log_debug!(
                "ipc",
                "event '{}' is {} bytes (> {inline_max}); dropped",
                event.kind(),
                bytes.len()
            );
            return Ok(());
        }

        let view = self.region.region();
        let ticket = self.header().event_write_seq.load(Ordering::Acquire) + 1;
        let index = ((ticket - 1) % self.event_capacity) as usize;
        let slot = unsafe {
            &mut *view.at::<EventSlot>(
                self.event_offset + index as u64 * std::mem::size_of::<EventSlot>() as u64,
            )
        };
        slot.seq = ticket;
        slot.len = bytes.len() as u32;
        slot.kind_flags = 0;
        slot.at_ms = envelope.at_ms;
        slot.inline[..bytes.len()].copy_from_slice(&bytes);

        self.header()
            .event_write_seq
            .store(ticket, Ordering::Release);
        *self
            .event_cursor
            .lock()
            .map_err(|_| InbriskError::internal("event ring poisoned"))? = ticket;
        unsafe { wake_all(&self.header().event_write_seq as *const _ as *const u64) };
        self.event_signal.set();
        self.header().total_events.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    /// Promote a handshaken session to `ACTIVE`.
    pub fn activate(&self, session_id: SessionId, client_id: ClientId) -> Result<()> {
        let index = session_index(session_id)?;
        let sh = self.session_mut(index);
        sh.client_id = client_id;
        sh.epoch = self.epoch;
        sh.state.store(SESSION_ACTIVE, Ordering::Release);
        self.header().client_count.fetch_add(1, Ordering::Relaxed);
        Ok(())
    }

    /// Sessions the runtime currently knows about.
    pub fn sessions(&self) -> Vec<ServerSession> {
        let view = self.region.region();
        let mut out = Vec::new();
        for index in 0..MAX_SESSIONS {
            let sh = &view.header().sessions[index];
            let state = sh.state.load(Ordering::Acquire);
            if state == SESSION_FREE || state == SESSION_CLOSING {
                continue;
            }
            out.push(ServerSession {
                session_id: (index + 1) as SessionId,
                client_id: sh.client_id,
                client_pid: sh.client_pid,
                name: sh.name_string(),
                version: sh.version_string(),
                connected_at_ms: sh.connected_at_ms,
                requests: sh.requests,
            });
        }
        out
    }

    /// The client name registered for a session, if any.
    pub fn session_name(&self, session_id: SessionId) -> Option<String> {
        let index = session_id.checked_sub(1).map(|i| i as usize)?;
        if index < MAX_SESSIONS {
            let sh = self.session_header(index);
            let s = sh.name_string();
            if s.is_empty() {
                None
            } else {
                Some(s)
            }
        } else {
            None
        }
    }

    pub fn client_infos(&self) -> Vec<ClientInfo> {
        self.sessions()
            .into_iter()
            .map(|s| ClientInfo {
                session_id: s.session_id,
                client_id: s.client_id,
                name: s.name,
                version: s.version,
                pid: s.client_pid,
                connected_at_ms: s.connected_at_ms,
                requests: s.requests,
            })
            .collect()
    }

    /// Drop sessions whose client process died without disconnecting.
    pub fn reap(&self) -> u32 {
        let mut reaped = 0;
        for index in 0..MAX_SESSIONS {
            let pid = self.session_header(index).client_pid;
            let state = self.session_header(index).state.load(Ordering::Acquire);
            if state == SESSION_FREE || pid == 0 {
                continue;
            }
            if !process_alive(pid) {
                log_debug!("ipc", "reaping dead session {} (pid {pid})", index + 1);
                let sh = self.session_mut(index);
                sh.state.store(SESSION_FREE, Ordering::Release);
                sh.client_id = 0;
                sh.client_pid = 0;
                sh.req_write.store(0, Ordering::Release);
                sh.req_read.store(0, Ordering::Release);
                sh.resp_write.store(0, Ordering::Release);
                sh.resp_read.store(0, Ordering::Release);
                reaped += 1;
            }
        }
        reaped
    }

    /// Mark every session closing and tell clients the runtime is going away.
    pub fn shutdown(&self, reason: &str) {
        let _ = self.publish_event(&Event::RuntimeShutdown {
            reason: reason.to_string(),
            epoch: self.epoch,
        });
        let view = self.region.region();
        for index in 0..MAX_SESSIONS {
            let sh = &view.header().sessions[index];
            if sh.state.load(Ordering::Acquire) != SESSION_FREE {
                sh.state.store(SESSION_CLOSING, Ordering::Release);
            }
        }
    }

    /// Record a measured round trip for the status histogram.
    pub fn record_rtt(&self, micros: u64) {
        if let Ok(mut v) = self.rtt_samples.lock() {
            if v.len() < 4096 {
                v.push(micros);
            }
        }
    }

    pub fn stats(&self) -> IpcStats {
        let h = self.header();
        let mut samples = self
            .rtt_samples
            .lock()
            .map(|v| v.clone())
            .unwrap_or_default();
        samples.sort_unstable();
        let pct = |p: f64| -> u64 {
            if samples.is_empty() {
                return 0;
            }
            let idx = ((samples.len() as f64 - 1.0) * p).round() as usize;
            samples[idx.min(samples.len() - 1)]
        };
        IpcStats {
            transport: "shared_memory+waitonaddress".into(),
            region_name: self.name.clone(),
            request_capacity: h.req_capacity,
            response_capacity: h.resp_capacity,
            event_capacity: h.event_capacity,
            arena_bytes: self.region.size() as u64,
            requests_seen: h.total_requests.load(Ordering::Relaxed),
            responses_sent: h.total_responses.load(Ordering::Relaxed),
            events_published: h.total_events.load(Ordering::Relaxed),
            events_dropped: h.event_seq_dropped.load(Ordering::Relaxed),
            wake_signals: h.total_wakes.load(Ordering::Relaxed),
            rtt_p50_us: pct(0.50),
            rtt_p95_us: pct(0.95),
            rtt_p99_us: pct(0.99),
            rtt_max_us: samples.last().copied().unwrap_or(0),
            runtime_pid: h.runtime_pid,
        }
    }

    /// Note that a session was seen (keeps the reaper honest).
    pub fn touch(&self, session_id: SessionId) {
        if let Ok(index) = session_index(session_id) {
            self.session_mut(index).last_seen_ms = now_ms();
        }
    }
}

pub fn session_index(session_id: SessionId) -> Result<usize> {
    match session_id.checked_sub(1) {
        Some(i) if (i as usize) < MAX_SESSIONS => Ok(i as usize),
        _ => Err(InbriskError::internal(format!(
            "session id {session_id} is out of range"
        ))),
    }
}

/// Byte offset of a session header inside the region.
pub fn header_session_offset(_stride: u64, index: usize) -> u64 {
    let base = std::mem::offset_of!(Header, sessions) as u64;
    let entry = std::mem::size_of::<SessionHeader>() as u64;
    base + entry * index as u64
}

fn make_epoch() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_micros() as u64)
        .unwrap_or(1);
    now ^ ((std::process::id() as u64) << 32)
}

fn make_nonce() -> u64 {
    use std::time::{SystemTime, UNIX_EPOCH};
    let a = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.subsec_nanos() as u64)
        .unwrap_or(0);
    a.wrapping_mul(0x9E37_79B9_7F4A_7C15) ^ (std::process::id() as u64).rotate_left(17)
}

pub fn process_alive(pid: u32) -> bool {
    use windows::Win32::Foundation::{CloseHandle, WAIT_TIMEOUT};
    use windows::Win32::System::Threading::{
        OpenProcess, WaitForSingleObject, PROCESS_SYNCHRONIZE,
    };
    if pid == 0 {
        return false;
    }
    unsafe {
        match OpenProcess(PROCESS_SYNCHRONIZE, false, pid) {
            Ok(h) => {
                let r = WaitForSingleObject(h, 0);
                let _ = CloseHandle(h);
                r == WAIT_TIMEOUT
            }
            Err(_) => false,
        }
    }
}
