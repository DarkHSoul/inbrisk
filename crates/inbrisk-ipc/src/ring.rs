//! SPSC ring producer/consumer.
//!
//! Every ring in the region is single-producer/single-consumer, which is what
//! lets the whole protocol run on plain acquire/release atomics with no locks
//! and no cross-process mutexes.

use inbrisk_core::{ErrorCode, InbriskError, Result};

use crate::layout::{RegionView, Slot, SLOT_READY};
use crate::wake::{wait_on_u64, WaitOutcome};

/// Producer half. Keeps its bump-allocator state locally; nothing here is
/// shared, so there is no contention even in the same process.
#[derive(Debug)]
pub struct Producer {
    pub entries_offset: u64,
    pub capacity: u64,
    pub arena_base: u64,
    pub arena_size: u64,
    pub payload_max: u32,
    cursor: u64,
    /// `(offset, len)` of whatever payload currently occupies each slot index.
    spans: Vec<(u64, u64)>,
}

impl Producer {
    pub fn new(
        entries_offset: u64,
        capacity: u64,
        arena_base: u64,
        arena_size: u64,
        payload_max: u32,
    ) -> Self {
        Self {
            entries_offset,
            capacity,
            arena_base,
            arena_size,
            payload_max,
            cursor: 0,
            spans: vec![(0, 0); capacity as usize],
        }
    }

    /// Publish one message. Blocks (on `WaitOnAddress`) while the ring is full.
    #[allow(clippy::too_many_arguments)]
    pub fn publish(
        &mut self,
        view: &RegionView,
        write: &std::sync::atomic::AtomicU64,
        read: &std::sync::atomic::AtomicU64,
        payload: &[u8],
        session_id: u32,
        opcode: u16,
        request_id: u64,
        flags: u16,
        timeout_ms: Option<u32>,
    ) -> Result<u64> {
        use std::sync::atomic::Ordering;

        if payload.len() > self.payload_max as usize {
            return Err(InbriskError::new(
                ErrorCode::InvalidPlan,
                format!(
                    "payload is {} bytes, the ring accepts {}",
                    payload.len(),
                    self.payload_max
                ),
            )
            .with_hint("trim the plan or split it into several runs"));
        }
        if payload.len() as u64 > self.arena_size {
            // Never let a payload run past its arena into the ring entries.
            return Err(InbriskError::new(
                ErrorCode::InvalidPlan,
                format!(
                    "payload is {} bytes but the arena only holds {}",
                    payload.len(),
                    self.arena_size
                ),
            ));
        }

        // 1. Capacity: wait for the consumer to advance `read`.
        let mut waited = 0u32;
        loop {
            let w = write.load(Ordering::Acquire);
            let r = read.load(Ordering::Acquire);
            if w - r < self.capacity {
                break;
            }
            let expected = r;
            let outcome =
                unsafe { wait_on_u64(read as *const _ as *const u64, expected, Some(50)) };
            if outcome == WaitOutcome::Failed {
                return Err(InbriskError::new(
                    ErrorCode::Internal,
                    "WaitOnAddress failed while waiting for ring space",
                ));
            }
            waited += 50;
            if let Some(limit) = timeout_ms {
                if waited >= limit {
                    return Err(InbriskError::new(
                        ErrorCode::Timeout,
                        "ring stayed full: the consumer is not draining",
                    ));
                }
            }
        }

        let w = write.load(Ordering::Acquire);
        let r = read.load(Ordering::Acquire);
        let ticket = w + 1;

        // 2. Arena allocation with an explicit overlap check against live
        //    payloads, so wrapping can never clobber a message in flight.
        let len = payload.len() as u64;
        let mut offset = self.cursor;
        let mut attempts = 0;
        loop {
            attempts += 1;
            if attempts > 3 {
                return Err(InbriskError::new(
                    ErrorCode::Timeout,
                    "payload arena is full; the consumer is not keeping up",
                )
                .with_hint("retry the call"));
            }
            if offset + len > self.arena_size {
                offset = 0;
            }
            if self.overlaps_live(offset, len, r, w) {
                if offset == 0 {
                    continue;
                }
                offset = 0;
                continue;
            }
            break;
        }
        self.cursor = if offset + len >= self.arena_size {
            0
        } else {
            offset + len
        };

        let abs = self.arena_base + offset;
        view.write_payload(abs, payload);

        // 3. Fill the slot and publish.
        let slot_index = ((ticket - 1) % self.capacity) as usize;
        self.spans[slot_index] = (offset, len);
        unsafe {
            let slot = view.at::<Slot>(
                self.entries_offset + slot_index as u64 * std::mem::size_of::<Slot>() as u64,
            );
            (*slot).seq = ticket;
            (*slot).request_id = request_id;
            (*slot).payload_offset = abs;
            (*slot).payload_len = payload.len() as u32;
            (*slot).session_id = session_id;
            (*slot).opcode = opcode;
            (*slot).flags = flags;
            (*slot).state = SLOT_READY;
        }
        write.store(ticket, Ordering::Release);
        Ok(ticket)
    }

    fn overlaps_live(&self, offset: u64, len: u64, read: u64, write: u64) -> bool {
        if len == 0 {
            return false;
        }
        for ticket in (read + 1)..=write {
            let idx = ((ticket - 1) % self.capacity) as usize;
            let (o, l) = self.spans[idx];
            if l == 0 {
                continue;
            }
            if offset < o + l && o < offset + len {
                return true;
            }
        }
        false
    }
}

/// Consumer half.
#[derive(Debug, Clone, Copy)]
pub struct Consumer {
    pub entries_offset: u64,
    pub capacity: u64,
}

impl Consumer {
    pub fn new(entries_offset: u64, capacity: u64) -> Self {
        Self {
            entries_offset,
            capacity,
        }
    }

    /// Read the next ready message, if any.
    pub fn next(
        &self,
        view: &RegionView,
        write: &std::sync::atomic::AtomicU64,
        read: &std::sync::atomic::AtomicU64,
    ) -> Option<(Slot, Vec<u8>)> {
        use std::sync::atomic::Ordering;
        let w = write.load(Ordering::Acquire);
        let r = read.load(Ordering::Acquire);
        if r >= w {
            return None;
        }
        let slot_index = (r % self.capacity) as usize;
        let slot = unsafe {
            let p = view.at::<Slot>(
                self.entries_offset + slot_index as u64 * std::mem::size_of::<Slot>() as u64,
            );
            *p
        };
        if slot.seq != r + 1 || slot.state != SLOT_READY {
            // The producer reserved this slot but has not published it yet.
            return None;
        }
        let bytes = view.read_payload(slot.payload_offset, slot.payload_len as usize);
        read.store(r + 1, Ordering::Release);
        Some((slot, bytes))
    }

    /// True when the ring has nothing new for the consumer.
    pub fn is_empty(
        &self,
        write: &std::sync::atomic::AtomicU64,
        read: &std::sync::atomic::AtomicU64,
    ) -> bool {
        use std::sync::atomic::Ordering;
        read.load(Ordering::Acquire) >= write.load(Ordering::Acquire)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::{RegionView, HEADER_SIZE};
    use std::sync::atomic::AtomicU64;

    fn test_view(size: usize) -> (Vec<u8>, usize) {
        let buf = vec![0u8; size];
        (buf, size)
    }

    #[test]
    fn publish_then_consume_round_trips() {
        let (mut buf, size) = test_view(4096 + 64 * 1024);
        // Entries right after the header, arena after the entries.
        let entries = HEADER_SIZE as u64;
        let arena = entries + 16 * 48;
        let view = unsafe { RegionView::new(buf.as_mut_ptr(), size) };
        let write = AtomicU64::new(0);
        let read = AtomicU64::new(0);
        let mut producer = Producer::new(entries, 16, arena, 64 * 1024, 4096);
        let consumer = Consumer::new(entries, 16);

        let t = producer
            .publish(&view, &write, &read, b"hello", 1, 6, 42, 0, None)
            .unwrap();
        assert_eq!(t, 1);
        assert!(!consumer.is_empty(&write, &read));
        let (slot, payload) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(payload, b"hello");
        assert_eq!(slot.request_id, 42);
        assert_eq!(slot.session_id, 1);
        assert!(consumer.next(&view, &write, &read).is_none());
    }

    #[test]
    fn oversized_payloads_are_rejected_instead_of_truncated() {
        let (mut buf, size) = test_view(4096 + 64 * 1024);
        let view = unsafe { RegionView::new(buf.as_mut_ptr(), size) };
        let write = AtomicU64::new(0);
        let read = AtomicU64::new(0);
        let mut producer = Producer::new(
            HEADER_SIZE as u64,
            4,
            HEADER_SIZE as u64 + 4 * 48,
            64 * 1024,
            16,
        );
        let err = producer
            .publish(&view, &write, &read, &[0u8; 64], 1, 1, 1, 0, None)
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::InvalidPlan);
    }

    #[test]
    fn wrapping_never_clobbers_a_live_payload() {
        // Arena holds exactly four 24-byte payloads, so the fifth write has to
        // wrap onto space that earlier messages vacated. The overlap check is
        // what makes that safe.
        let (mut buf, size) = test_view(4096 + 16 * 48 + 96);
        let entries = HEADER_SIZE as u64;
        let arena = entries + 16 * 48;
        let view = unsafe { RegionView::new(buf.as_mut_ptr(), size) };
        let write = AtomicU64::new(0);
        let read = AtomicU64::new(0);
        let mut producer = Producer::new(entries, 16, arena, 96, 24);
        let consumer = Consumer::new(entries, 16);

        for i in 0..3u64 {
            let payload = vec![b'a' + i as u8; 24];
            producer
                .publish(&view, &write, &read, &payload, 1, 6, i, 0, None)
                .unwrap();
        }
        let (_s, first) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(first, vec![b'a'; 24]);

        // Wrap point: 72 + 24 == 96, so this lands at the end of the arena.
        producer
            .publish(&view, &write, &read, &vec![b'z'; 24], 1, 6, 9, 0, None)
            .unwrap();

        // Consume one more, then force an actual wrap back to offset 0.
        let (_s, second) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(second, vec![b'b'; 24]);
        producer
            .publish(&view, &write, &read, &vec![b'y'; 24], 1, 6, 10, 0, None)
            .unwrap();

        let (_s, third) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(third, vec![b'c'; 24], "live payload must survive the wrap");
        let (_s, fourth) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(fourth, vec![b'z'; 24]);
        let (_s, fifth) = consumer.next(&view, &write, &read).unwrap();
        assert_eq!(fifth, vec![b'y'; 24]);
    }

    #[test]
    fn a_payload_larger_than_the_arena_is_refused() {
        let (mut buf, size) = test_view(4096 + 16 * 48 + 64);
        let view = unsafe { RegionView::new(buf.as_mut_ptr(), size) };
        let write = AtomicU64::new(0);
        let read = AtomicU64::new(0);
        let mut producer =
            Producer::new(HEADER_SIZE as u64, 4, HEADER_SIZE as u64 + 4 * 48, 32, 64);
        let err = producer
            .publish(&view, &write, &read, &[0u8; 33], 1, 1, 1, 0, None)
            .unwrap_err();
        assert_eq!(
            err.code,
            ErrorCode::InvalidPlan,
            "a payload bigger than the arena must never be written"
        );
    }
}
