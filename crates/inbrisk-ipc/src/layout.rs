//! Shared-memory layout.
//!
//! ```text
//! Region
//! ├─ Header (4 KiB page)
//! │  ├─ identity: magic, protocolVersion, runtimePid, runtimeEpoch, nonce
//! │  ├─ capabilities, stateVersion, generation
//! │  ├─ requestEpoch        <- global wake word for the runtime
//! │  ├─ eventWriteSeq       <- wake word for event readers
//! │  └─ per-session headers (session table)
//! ├─ Session shards (one per session, 64-byte aligned)
//! │  ├─ request ring  (SPSC: client -> runtime)
//! │  ├─ response ring (SPSC: runtime -> client)
//! │  └─ payload arenas
//! └─ Event ring (single producer, many readers, inline payloads)
//! ```
//!
//! Design note (deviation from the original sketch, on purpose):
//! the sketch had one Request Ring and one Response Ring. Those are MPSC /
//! SPMC structures: a slow publisher in a single shared ring can stall every
//! other client (out-of-order publication), and a slow client can block the
//! runtime's response writes. The rings are therefore *sharded per session*
//! (each one SPSC), which keeps the wake logic to a single `WaitOnAddress`
//! per direction and removes head-of-line blocking between clients entirely.
//! The wake primitive — `WaitOnAddress`/`WakeByAddressSingle` — is exactly as
//! specified, and idle CPU stays at ~0%.

use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};

use inbrisk_core::Hwnd;

pub const MAGIC: u64 = 0x494E_4252_4953_4B31; // "INBRISK1"
pub const HEADER_SIZE: usize = 4096;
pub const MAX_SESSIONS: usize = 8;
pub const REQ_CAPACITY: usize = 16;
pub const RESP_CAPACITY: usize = 8;
pub const EVENT_CAPACITY: usize = 512;
pub const REQ_PAYLOAD_MAX: u32 = 64 * 1024;
pub const RESP_PAYLOAD_MAX: u32 = 256 * 1024;
pub const EVENT_INLINE_MAX: usize = 960;

/// Cache-line-ish entry. 48 bytes keeps a 16-entry ring inside one 768-byte run.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct Slot {
    /// 1-based ticket. 0 means "never used".
    pub seq: u64,
    pub request_id: u64,
    /// Absolute offset inside the region.
    pub payload_offset: u64,
    pub payload_len: u32,
    pub session_id: u32,
    pub opcode: u16,
    pub flags: u16,
    pub state: u32,
    pub _reserved: u32,
}

pub const SLOT_FREE: u32 = 0;
pub const SLOT_READY: u32 = 1;
pub const SLOT_CONSUMED: u32 = 2;

/// Inline event slot: header + payload in one 1 KiB line, so event delivery
/// needs no arena and can never overwrite a payload another reader is using.
#[repr(C)]
#[derive(Clone, Copy)]
pub struct EventSlot {
    pub seq: u64,
    pub len: u32,
    pub kind_flags: u32,
    pub at_ms: u64,
    pub inline: [u8; EVENT_INLINE_MAX],
}

impl Default for EventSlot {
    fn default() -> Self {
        Self {
            seq: 0,
            len: 0,
            kind_flags: 0,
            at_ms: 0,
            inline: [0u8; EVENT_INLINE_MAX],
        }
    }
}

pub const SESSION_FREE: u32 = 0;
pub const SESSION_HANDSHAKE: u32 = 1;
pub const SESSION_ACTIVE: u32 = 2;
pub const SESSION_CLOSING: u32 = 3;
/// A client won the CAS on this slot but has not finished filling it in.
pub const SESSION_CLAIMING: u32 = 4;

#[repr(C)]
pub struct SessionHeader {
    pub state: AtomicU32,
    pub generation: u32,
    pub client_id: u64,
    pub client_pid: u32,
    pub client_nonce_lo: u32,
    pub connected_at_ms: u64,
    pub last_seen_ms: u64,
    /// Client-owned publish counter (`WaitOnAddress` target for the runtime is
    /// the global request epoch, this one only bounds the ring).
    pub req_write: AtomicU64,
    /// Runtime-owned consume cursor; also the wake word for a blocked client.
    pub req_read: AtomicU64,
    /// Runtime-owned publish counter; the wake word for the waiting client.
    pub resp_write: AtomicU64,
    /// Client-owned consume cursor.
    pub resp_read: AtomicU64,
    pub requests: u64,
    pub responses: u64,
    pub dropped: u64,
    /// Runtime epoch this session belongs to.
    pub epoch: u64,
    pub name: [u16; 32],
    pub version: [u16; 16],
    pub _pad: [u8; 16],
}

impl Default for SessionHeader {
    fn default() -> Self {
        Self {
            state: AtomicU32::new(SESSION_FREE),
            generation: 0,
            client_id: 0,
            client_pid: 0,
            client_nonce_lo: 0,
            connected_at_ms: 0,
            last_seen_ms: 0,
            req_write: AtomicU64::new(0),
            req_read: AtomicU64::new(0),
            resp_write: AtomicU64::new(0),
            resp_read: AtomicU64::new(0),
            requests: 0,
            responses: 0,
            dropped: 0,
            epoch: 0,
            name: [0u16; 32],
            version: [0u16; 16],
            _pad: [0u8; 16],
        }
    }
}

impl SessionHeader {
    pub fn set_name(&mut self, s: &str) {
        let v: Vec<u16> = s.encode_utf16().take(31).collect();
        self.name = [0u16; 32];
        self.name[..v.len()].copy_from_slice(&v);
    }

    pub fn name_string(&self) -> String {
        let end = self.name.iter().position(|c| *c == 0).unwrap_or(32);
        String::from_utf16_lossy(&self.name[..end])
    }

    pub fn set_version(&mut self, s: &str) {
        let v: Vec<u16> = s.encode_utf16().take(15).collect();
        self.version = [0u16; 16];
        self.version[..v.len()].copy_from_slice(&v);
    }

    pub fn version_string(&self) -> String {
        let end = self.version.iter().position(|c| *c == 0).unwrap_or(16);
        String::from_utf16_lossy(&self.version[..end])
    }
}

#[repr(C)]
pub struct Header {
    pub magic: u64,
    pub protocol_version: u16,
    pub codec: u16,
    pub header_size: u32,
    pub flags: u32,

    pub runtime_pid: u32,
    pub runtime_session_id: u32,
    pub runtime_epoch: u64,
    pub runtime_nonce: u64,
    pub capabilities: u64,
    pub created_at_ms: u64,

    pub state_version: u64,
    pub generation: u64,

    /// Bumped by *any* client after publishing a request; the runtime sleeps on
    /// this exact address.
    pub request_epoch: AtomicU64,
    /// Bumped by the runtime when it publishes an event.
    pub event_write_seq: AtomicU64,
    pub event_seq_dropped: AtomicU64,

    pub req_capacity: u32,
    pub resp_capacity: u32,
    pub event_capacity: u32,
    pub req_payload_max: u32,
    pub resp_payload_max: u32,
    pub event_inline_max: u32,
    pub session_capacity: u32,
    pub client_count: AtomicU32,

    pub data_offset: u64,
    pub shard_size: u64,
    pub shard_stride: u64,
    pub event_entries_offset: u64,
    pub event_entries_size: u64,

    pub total_requests: AtomicU64,
    pub total_responses: AtomicU64,
    pub total_events: AtomicU64,
    pub total_wakes: AtomicU64,

    pub sessions: [SessionHeader; MAX_SESSIONS],
}

impl Default for Header {
    fn default() -> Self {
        Self {
            magic: 0,
            protocol_version: 0,
            codec: 0,
            header_size: HEADER_SIZE as u32,
            flags: 0,
            runtime_pid: 0,
            runtime_session_id: 0,
            runtime_epoch: 0,
            runtime_nonce: 0,
            capabilities: 0,
            created_at_ms: 0,
            state_version: 1,
            generation: 1,
            request_epoch: AtomicU64::new(0),
            event_write_seq: AtomicU64::new(0),
            event_seq_dropped: AtomicU64::new(0),
            req_capacity: REQ_CAPACITY as u32,
            resp_capacity: RESP_CAPACITY as u32,
            event_capacity: EVENT_CAPACITY as u32,
            req_payload_max: REQ_PAYLOAD_MAX,
            resp_payload_max: RESP_PAYLOAD_MAX,
            event_inline_max: EVENT_INLINE_MAX as u32,
            session_capacity: MAX_SESSIONS as u32,
            client_count: AtomicU32::new(0),
            data_offset: HEADER_SIZE as u64,
            shard_size: 0,
            shard_stride: 0,
            event_entries_offset: 0,
            event_entries_size: 0,
            total_requests: AtomicU64::new(0),
            total_responses: AtomicU64::new(0),
            total_events: AtomicU64::new(0),
            total_wakes: AtomicU64::new(0),
            sessions: std::array::from_fn(|_| SessionHeader::default()),
        }
    }
}

/// Computed byte offsets for one session shard.
#[derive(Debug, Clone, Copy, Default)]
pub struct ShardLayout {
    pub base: u64,
    pub req_entries: u64,
    pub req_arena: u64,
    pub req_arena_size: u64,
    pub resp_entries: u64,
    pub resp_arena: u64,
    pub resp_arena_size: u64,
    pub size: u64,
}

pub fn align_up(v: u64, a: u64) -> u64 {
    (v + a - 1) / a * a
}

/// Total region size and per-session shard stride.
pub fn layout() -> (u64, u64) {
    let req_entries = (REQ_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let req_arena = REQ_CAPACITY as u64 * REQ_PAYLOAD_MAX as u64;
    let resp_entries = (RESP_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let resp_arena = RESP_CAPACITY as u64 * RESP_PAYLOAD_MAX as u64;
    let raw = req_entries + req_arena + resp_entries + resp_arena;
    let stride = align_up(raw, 4096);
    let event_bytes = (EVENT_CAPACITY * std::mem::size_of::<EventSlot>()) as u64;
    let total = align_up(HEADER_SIZE as u64, 4096) + stride * MAX_SESSIONS as u64 + event_bytes;
    (total, stride)
}

/// Byte offset of one session shard.
pub fn shard_layout(shard_stride: u64, index: usize) -> ShardLayout {
    let req_entries = (REQ_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let req_arena = REQ_CAPACITY as u64 * REQ_PAYLOAD_MAX as u64;
    let resp_entries = (RESP_CAPACITY * std::mem::size_of::<Slot>()) as u64;
    let resp_arena = RESP_CAPACITY as u64 * RESP_PAYLOAD_MAX as u64;
    let base = HEADER_SIZE as u64 + shard_stride * index as u64;
    let req_arena_off = base + req_entries;
    let resp_entries_off = req_arena_off + req_arena;
    let resp_arena_off = resp_entries_off + resp_entries;
    ShardLayout {
        base,
        req_entries: base,
        req_arena: req_arena_off,
        req_arena_size: req_arena,
        resp_entries: resp_entries_off,
        resp_arena: resp_arena_off,
        resp_arena_size: resp_arena,
        size: req_entries + req_arena + resp_entries + resp_arena,
    }
}

/// Raw view over a mapped region.
#[derive(Debug)]
pub struct RegionView {
    base: *mut u8,
    size: usize,
}

unsafe impl Send for RegionView {}
unsafe impl Sync for RegionView {}

impl RegionView {
    /// # Safety
    /// `base` must point at a mapped region of at least `size` bytes that stays
    /// mapped for the lifetime of the view.
    pub unsafe fn new(base: *mut u8, size: usize) -> Self {
        Self { base, size }
    }

    #[inline]
    pub fn base(&self) -> *mut u8 {
        self.base
    }

    #[inline]
    pub fn size(&self) -> usize {
        self.size
    }

    #[inline]
    pub fn header(&self) -> &Header {
        unsafe { &*(self.base as *const Header) }
    }

    /// Zero the whole region. Only the creating runtime may call this.
    pub fn reset(&self) {
        unsafe {
            std::ptr::write_bytes(self.base, 0, self.size);
        }
    }

    #[inline]
    pub fn header_ptr(&self) -> *mut Header {
        self.base as *mut Header
    }

    #[inline]
    pub fn at<T>(&self, offset: u64) -> *mut T {
        unsafe { self.base.add(offset as usize) as *mut T }
    }

    /// A slice of slots, bounded by the region.
    pub fn slots(&self, offset: u64, capacity: usize) -> &[Slot] {
        debug_assert!(offset as usize + capacity * std::mem::size_of::<Slot>() <= self.size);
        unsafe { std::slice::from_raw_parts(self.at::<Slot>(offset), capacity) }
    }

    pub fn event_slots(&self, offset: u64, capacity: usize) -> &[EventSlot] {
        debug_assert!(offset as usize + capacity * std::mem::size_of::<EventSlot>() <= self.size);
        unsafe { std::slice::from_raw_parts(self.at::<EventSlot>(offset), capacity) }
    }

    /// Write bytes into the arena, returning the absolute offset.
    pub fn write_payload(&self, offset: u64, bytes: &[u8]) {
        debug_assert!(offset as usize + bytes.len() <= self.size);
        unsafe {
            std::ptr::copy_nonoverlapping(
                bytes.as_ptr(),
                self.base.add(offset as usize),
                bytes.len(),
            );
        }
    }

    pub fn read_payload(&self, offset: u64, len: usize) -> Vec<u8> {
        if offset as usize + len > self.size {
            return Vec::new();
        }
        unsafe { std::slice::from_raw_parts(self.base.add(offset as usize), len).to_vec() }
    }
}

/// Read a shared atomic through a raw pointer.
///
/// # Safety
/// `p` must point at a properly aligned `u64` inside the mapped region.
#[inline]
pub unsafe fn shared_atomic(p: *const u64) -> &'static AtomicU64 {
    &*(p as *const AtomicU64)
}

#[inline]
pub fn hwnd_bits(h: Hwnd) -> i64 {
    h.0 as i64
}

#[inline]
pub fn hwnd_from_bits(v: i64) -> Hwnd {
    Hwnd(v as isize)
}

/// Load helper used by both sides for acquire/release discipline.
#[inline]
pub fn load(atomic: &AtomicU64) -> u64 {
    atomic.load(Ordering::Acquire)
}

#[inline]
pub fn store(atomic: &AtomicU64, value: u64) {
    atomic.store(value, Ordering::Release);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn header_fits_in_one_page() {
        assert!(
            std::mem::size_of::<Header>() <= HEADER_SIZE,
            "header is {} bytes, page is {HEADER_SIZE}",
            std::mem::size_of::<Header>()
        );
    }

    #[test]
    fn slots_are_compact() {
        assert_eq!(std::mem::size_of::<Slot>(), 48);
    }

    #[test]
    fn shards_do_not_overlap() {
        let (total, stride) = layout();
        let a = shard_layout(stride, 0);
        let b = shard_layout(stride, 1);
        assert!(a.base + a.size <= b.base);
        let event_off = align_up(HEADER_SIZE as u64, 4096) + stride * MAX_SESSIONS as u64;
        assert!(b.base + b.size <= event_off);
        assert!(event_off + (EVENT_CAPACITY * std::mem::size_of::<EventSlot>()) as u64 <= total);
    }
}
