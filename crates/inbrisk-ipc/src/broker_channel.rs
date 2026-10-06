//! Dedicated, high-performance shared-memory channel between Inbrisk Runtime and `inbrisk-desktop.exe`.
//!
//! This channel reuses existing `inbrisk-ipc` primitives (`MappedRegion`, `Producer`, `Consumer`,
//! `Slot`, `IpcEvent`) on an independent namespace and request queue:
//! `Local\Inbrisk.Desktop.<UserSid>.<RuntimeEpoch>`.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::time::{Duration, Instant};

use inbrisk_core::{ErrorCode, InbriskError, Result};
use inbrisk_protocol::desktop_broker::{
    BrokerErrorPayload, BrokerRequestEnvelope, BrokerResponseEnvelope, DesktopBrokerRequest,
    DesktopBrokerResponse, DESKTOP_BROKER_PROTOCOL_VERSION,
};
use parking_lot::Mutex;

use crate::layout::{align_up, Slot};
use crate::region::MappedRegion;
use crate::ring::{Consumer, Producer};
use crate::wake::{IpcEvent, WaitOutcome};

pub const BROKER_IPC_MAGIC: u64 = 0x494E_4252_4953_4B44; // "INBRISKD"
pub const BROKER_HEADER_SIZE: usize = 4096;
pub const BROKER_REQ_CAPACITY: usize = 16;
pub const BROKER_RESP_CAPACITY: usize = 16;
pub const BROKER_REQ_PAYLOAD_MAX: u32 = 64 * 1024;
pub const BROKER_RESP_PAYLOAD_MAX: u32 = 256 * 1024;

#[repr(C)]
pub struct BrokerHeader {
    pub magic: u64,
    pub protocol_version: u32,
    pub runtime_pid: u32,
    pub runtime_epoch: u64,

    pub broker_pid: AtomicU32,
    pub broker_epoch: AtomicU64,
    pub broker_ready: AtomicU32,
    pub shutdown_requested: AtomicU32,

    pub req_write: AtomicU64,
    pub req_read: AtomicU64,

    pub resp_write: AtomicU64,
    pub resp_read: AtomicU64,
}

impl Default for BrokerHeader {
    fn default() -> Self {
        Self {
            magic: BROKER_IPC_MAGIC,
            protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
            runtime_pid: 0,
            runtime_epoch: 0,
            broker_pid: AtomicU32::new(0),
            broker_epoch: AtomicU64::new(0),
            broker_ready: AtomicU32::new(0),
            shutdown_requested: AtomicU32::new(0),
            req_write: AtomicU64::new(0),
            req_read: AtomicU64::new(0),
            resp_write: AtomicU64::new(0),
            resp_read: AtomicU64::new(0),
        }
    }
}

pub struct BrokerChannelLayout {
    pub total_size: usize,
    pub req_entries_offset: u64,
    pub req_arena_offset: u64,
    pub req_arena_size: u64,
    pub resp_entries_offset: u64,
    pub resp_arena_offset: u64,
    pub resp_arena_size: u64,
}

pub fn broker_layout() -> BrokerChannelLayout {
    let req_entries = (BROKER_REQ_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let req_arena = (BROKER_REQ_CAPACITY as u64) * (BROKER_REQ_PAYLOAD_MAX as u64);
    let resp_entries = (BROKER_RESP_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let resp_arena = (BROKER_RESP_CAPACITY as u64) * (BROKER_RESP_PAYLOAD_MAX as u64);

    let base = BROKER_HEADER_SIZE as u64;
    let req_entries_offset = base;
    let req_arena_offset = req_entries_offset + req_entries;
    let resp_entries_offset = align_up(req_arena_offset + req_arena, 64);
    let resp_arena_offset = resp_entries_offset + resp_entries;
    let total_size = align_up(resp_arena_offset + resp_arena, 4096) as usize;

    BrokerChannelLayout {
        total_size,
        req_entries_offset,
        req_arena_offset,
        req_arena_size: req_arena,
        resp_entries_offset,
        resp_arena_offset,
        resp_arena_size: resp_arena,
    }
}

/// Runtime-side host for the broker channel.
pub struct BrokerChannelHost {
    region: MappedRegion,
    endpoint: String,
    req_producer: Mutex<Producer>,
    resp_consumer: Consumer,
    req_event: IpcEvent,
    resp_event: IpcEvent,
    next_request_id: AtomicU64,
    runtime_epoch: u64,
    runtime_pid: u32,
    active_broker_epoch: AtomicU64,
}

unsafe impl Send for BrokerChannelHost {}
unsafe impl Sync for BrokerChannelHost {}

impl BrokerChannelHost {
    pub fn create(endpoint: &str, runtime_epoch: u64, runtime_pid: u32) -> Result<Self> {
        let layout = broker_layout();
        let region = MappedRegion::create(endpoint, layout.total_size)?;
        let view = region.region();
        view.reset();

        unsafe {
            let h = &mut *(view.base() as *mut BrokerHeader);
            *h = BrokerHeader::default();
            h.runtime_pid = runtime_pid;
            h.runtime_epoch = runtime_epoch;
        }

        let req_event = IpcEvent::create_or_open(&format!("{endpoint}.req"), false)?;
        let resp_event = IpcEvent::create_or_open(&format!("{endpoint}.resp"), false)?;

        let req_producer = Mutex::new(Producer::new(
            layout.req_entries_offset,
            BROKER_REQ_CAPACITY as u64,
            layout.req_arena_offset,
            layout.req_arena_size,
            BROKER_REQ_PAYLOAD_MAX,
        ));
        let resp_consumer = Consumer::new(layout.resp_entries_offset, BROKER_RESP_CAPACITY as u64);

        Ok(Self {
            region,
            endpoint: endpoint.to_string(),
            req_producer,
            resp_consumer,
            req_event,
            resp_event,
            next_request_id: AtomicU64::new(1),
            runtime_epoch,
            runtime_pid,
            active_broker_epoch: AtomicU64::new(0),
        })
    }

    pub fn endpoint(&self) -> &str {
        &self.endpoint
    }

    pub fn runtime_pid(&self) -> u32 {
        self.runtime_pid
    }

    fn header(&self) -> &BrokerHeader {
        unsafe { &*(self.region.region().base() as *const BrokerHeader) }
    }

    pub fn is_broker_ready(&self) -> bool {
        self.header().broker_ready.load(Ordering::Acquire) == 1
    }

    pub fn broker_pid(&self) -> u32 {
        self.header().broker_pid.load(Ordering::Acquire)
    }

    pub fn broker_epoch(&self) -> u64 {
        self.header().broker_epoch.load(Ordering::Acquire)
    }

    pub fn set_active_broker_epoch(&self, epoch: u64) {
        self.active_broker_epoch.store(epoch, Ordering::Release);
    }

    pub fn wait_for_broker_ready(&self, timeout: Duration) -> Result<(u32, u64)> {
        let deadline = Instant::now() + timeout;
        while Instant::now() < deadline {
            if self.is_broker_ready() {
                let pid = self.broker_pid();
                let epoch = self.broker_epoch();
                if pid != 0 && epoch != 0 {
                    self.set_active_broker_epoch(epoch);
                    return Ok((pid, epoch));
                }
            }
            std::thread::sleep(Duration::from_millis(15));
        }
        Err(InbriskError::new(
            ErrorCode::Timeout,
            "broker did not report ready within timeout",
        ))
    }

    pub fn call(
        &self,
        request: DesktopBrokerRequest,
        timeout: Duration,
    ) -> Result<DesktopBrokerResponse> {
        let request_id = self.next_request_id.fetch_add(1, Ordering::AcqRel);
        let expected_broker_epoch = match self.active_broker_epoch.load(Ordering::Acquire) {
            0 => None,
            ep => Some(ep),
        };

        let env = BrokerRequestEnvelope {
            request_id,
            runtime_epoch: self.runtime_epoch,
            expected_broker_epoch,
            request,
        };

        let bytes = serde_json::to_vec(&env).map_err(|e| {
            InbriskError::internal(format!("failed to serialize broker request: {e}"))
        })?;

        if bytes.len() > BROKER_REQ_PAYLOAD_MAX as usize {
            return Err(InbriskError::internal(
                "broker request payload exceeds limit",
            ));
        }

        let view = self.region.region();
        let header = self.header();

        // 1. Publish to request ring
        {
            let mut producer = self.req_producer.lock();
            producer.publish(
                view,
                &header.req_write,
                &header.req_read,
                &bytes,
                1,
                1,
                request_id,
                0,
                Some(timeout.as_millis().min(u32::MAX as u128) as u32),
            )?;
        }
        self.req_event.set();

        // 2. Wait for response
        let started = Instant::now();
        while started.elapsed() < timeout {
            if let Some((_slot, payload)) =
                self.resp_consumer
                    .next(view, &header.resp_write, &header.resp_read)
            {
                let resp_env: BrokerResponseEnvelope =
                    serde_json::from_slice(&payload).map_err(|e| {
                        InbriskError::internal(format!("invalid broker response envelope: {e}"))
                    })?;

                if resp_env.request_id != request_id {
                    continue; // Skip out-of-order or stale responses
                }

                if resp_env.runtime_epoch != self.runtime_epoch {
                    return Err(InbriskError::new(
                        ErrorCode::ProtocolMismatch,
                        "broker answered request intended for a different runtime epoch",
                    ));
                }

                // Stale broker epoch validation: if we have an active broker epoch and response is from an older epoch
                let current_broker_epoch = self.active_broker_epoch.load(Ordering::Acquire);
                if current_broker_epoch != 0 && resp_env.broker_epoch != current_broker_epoch {
                    return Err(InbriskError::new(
                        ErrorCode::StaleState,
                        format!(
                            "broker response epoch {} does not match current active broker epoch {current_broker_epoch}",
                            resp_env.broker_epoch
                        ),
                    ));
                }

                return match resp_env.response {
                    Ok(resp) => Ok(resp),
                    Err(err) => Err(InbriskError::new(
                        ErrorCode::Internal,
                        format!("broker returned error [{}]: {}", err.code, err.message),
                    )),
                };
            }

            let remaining_ms = timeout
                .saturating_sub(started.elapsed())
                .as_millis()
                .min(50) as u32;
            let _ = self.resp_event.wait(Some(remaining_ms.max(1)));
        }

        Err(InbriskError::new(
            ErrorCode::Timeout,
            "broker call timed out",
        ))
    }
}

/// Broker-side peer for the broker channel.
pub struct BrokerChannelPeer {
    region: MappedRegion,
    endpoint: String,
    req_consumer: Consumer,
    resp_producer: Mutex<Producer>,
    req_event: IpcEvent,
    resp_event: IpcEvent,
    runtime_epoch: u64,
    runtime_pid: u32,
    broker_epoch: u64,
    broker_pid: u32,
}

unsafe impl Send for BrokerChannelPeer {}
unsafe impl Sync for BrokerChannelPeer {}

impl BrokerChannelPeer {
    pub fn open(endpoint: &str, broker_epoch: u64, broker_pid: u32) -> Result<Self> {
        let region = MappedRegion::open(endpoint)?;
        let layout = broker_layout();
        let view = region.region();

        let header = unsafe { &mut *(view.base() as *mut BrokerHeader) };
        if header.magic != BROKER_IPC_MAGIC {
            return Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                "shared memory region does not match Broker magic",
            ));
        }

        if header.protocol_version != DESKTOP_BROKER_PROTOCOL_VERSION {
            return Err(InbriskError::new(
                ErrorCode::ProtocolMismatch,
                format!(
                    "broker speaks protocol {}, runtime speaks {}",
                    DESKTOP_BROKER_PROTOCOL_VERSION, header.protocol_version
                ),
            ));
        }

        let runtime_epoch = header.runtime_epoch;
        let runtime_pid = header.runtime_pid;

        header.broker_pid.store(broker_pid, Ordering::Release);
        header.broker_epoch.store(broker_epoch, Ordering::Release);

        let req_event = IpcEvent::create_or_open(&format!("{endpoint}.req"), false)?;
        let resp_event = IpcEvent::create_or_open(&format!("{endpoint}.resp"), false)?;

        let req_consumer = Consumer::new(layout.req_entries_offset, BROKER_REQ_CAPACITY as u64);
        let resp_producer = Mutex::new(Producer::new(
            layout.resp_entries_offset,
            BROKER_RESP_CAPACITY as u64,
            layout.resp_arena_offset,
            layout.resp_arena_size,
            BROKER_RESP_PAYLOAD_MAX,
        ));

        Ok(Self {
            region,
            endpoint: endpoint.to_string(),
            req_consumer,
            resp_producer,
            req_event,
            resp_event,
            runtime_epoch,
            runtime_pid,
            broker_epoch,
            broker_pid,
        })
    }

    fn header(&self) -> &BrokerHeader {
        unsafe { &*(self.region.region().base() as *const BrokerHeader) }
    }

    pub fn endpoint(&self) -> &str {
        &self.endpoint
    }

    pub fn runtime_pid(&self) -> u32 {
        self.runtime_pid
    }

    pub fn broker_pid(&self) -> u32 {
        self.broker_pid
    }

    pub fn mark_ready(&self) {
        self.header().broker_ready.store(1, Ordering::Release);
        self.resp_event.set();
    }

    pub fn next_request(&self, timeout: Duration) -> Result<Option<BrokerRequestEnvelope>> {
        let view = self.region.region();
        let header = self.header();

        if let Some((_slot, payload)) =
            self.req_consumer
                .next(view, &header.req_write, &header.req_read)
        {
            let env: BrokerRequestEnvelope = serde_json::from_slice(&payload).map_err(|e| {
                InbriskError::internal(format!("invalid broker request envelope: {e}"))
            })?;
            return Ok(Some(env));
        }

        if timeout.is_zero() {
            return Ok(None);
        }

        let wait_ms = timeout.as_millis().min(u32::MAX as u128) as u32;
        match self.req_event.wait(Some(wait_ms)) {
            WaitOutcome::Woken => {
                if let Some((_slot, payload)) =
                    self.req_consumer
                        .next(view, &header.req_write, &header.req_read)
                {
                    let env: BrokerRequestEnvelope =
                        serde_json::from_slice(&payload).map_err(|e| {
                            InbriskError::internal(format!("invalid broker request envelope: {e}"))
                        })?;
                    Ok(Some(env))
                } else {
                    Ok(None)
                }
            }
            _ => Ok(None),
        }
    }

    pub fn send_response(
        &self,
        request_id: u64,
        response: std::result::Result<DesktopBrokerResponse, BrokerErrorPayload>,
    ) -> Result<()> {
        let env = BrokerResponseEnvelope {
            request_id,
            runtime_epoch: self.runtime_epoch,
            broker_epoch: self.broker_epoch,
            response,
        };

        let bytes = serde_json::to_vec(&env)
            .map_err(|e| InbriskError::internal(format!("cannot encode broker response: {e}")))?;

        let view = self.region.region();
        let header = self.header();

        {
            let mut producer = self.resp_producer.lock();
            producer.publish(
                view,
                &header.resp_write,
                &header.resp_read,
                &bytes,
                1,
                1,
                request_id,
                0,
                Some(1000),
            )?;
        }
        self.resp_event.set();
        Ok(())
    }
}

/// Helper to generate a unique per-user desktop broker endpoint name.
pub fn broker_endpoint_name(runtime_epoch: u64) -> Result<String> {
    let sid = crate::security::current_user_sid_string().unwrap_or_else(|| "LocalUser".into());
    Ok(format!("Local\\Inbrisk.Desktop.{sid}.{runtime_epoch}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn broker_channel_layout_bounds() {
        let layout = broker_layout();
        assert!(layout.total_size > 0);
        assert!(layout.resp_arena_offset + layout.resp_arena_size <= layout.total_size as u64);
    }

    #[test]
    fn broker_channel_round_trip_in_memory() {
        let epoch = 12345;
        let endpoint = format!("Local\\Inbrisk.TestBrokerChannel.{epoch}");

        let host = BrokerChannelHost::create(&endpoint, epoch, 100).expect("host create");
        let peer = BrokerChannelPeer::open(&endpoint, 999, 200).expect("peer open");

        peer.mark_ready();
        let (pid, b_epoch) = host
            .wait_for_broker_ready(Duration::from_millis(500))
            .expect("ready");
        assert_eq!(pid, 200);
        assert_eq!(b_epoch, 999);

        // Host dispatches Hello in separate thread
        let handle = std::thread::spawn({
            let peer = peer;
            move || {
                let req = peer
                    .next_request(Duration::from_secs(2))
                    .expect("next_req")
                    .expect("some req");
                assert_eq!(req.request_id, 1);
                match req.request {
                    DesktopBrokerRequest::Ping => {
                        peer.send_response(req.request_id, Ok(DesktopBrokerResponse::Pong))
                            .expect("send pong");
                    }
                    _ => panic!("unexpected request"),
                }
            }
        });

        let resp = host
            .call(DesktopBrokerRequest::Ping, Duration::from_secs(2))
            .expect("call ping");
        assert_eq!(resp, DesktopBrokerResponse::Pong);
        handle.join().unwrap();
    }
}
