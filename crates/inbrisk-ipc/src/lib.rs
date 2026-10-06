//! Inbrisk fast IPC: one shared-memory region + `WaitOnAddress` wakeups.
//!
//! * Transport: `Local\Inbrisk.Runtime.<UserSid>` file mapping, name- and
//!   DACL-scoped to the current user.
//! * Structure: 4 KiB header + per-session SPSC request/response shards +
//!   a single-producer event ring with inline payloads.
//! * Wakeup: `WaitOnAddress`/`WakeByAddressSingle` — idle CPU is zero, request
//!   pickup is a few microseconds.
//! * Security: per-user name, user-only DACL, runtime epoch + nonce, session
//!   CAS claim, protocol version check.
//!
//! The same crate backs the runtime (`IpcServer`), the CLI/SDK (`IpcClient`)
//! and the MCP proxy — they are all the same binary protocol.

pub mod broker_channel;
pub mod client;
pub mod layout;
pub mod region;
pub mod ring;
pub mod security;
pub mod server;
pub mod wake;

pub use broker_channel::{broker_endpoint_name, BrokerChannelHost, BrokerChannelPeer};
pub use client::IpcClient;
pub use layout::{
    Header, RegionView, SessionHeader, Slot, EVENT_CAPACITY, HEADER_SIZE, MAGIC, MAX_SESSIONS,
    REQ_CAPACITY, RESP_CAPACITY,
};
pub use region::MappedRegion;
pub use ring::{Consumer, Producer};
pub use security::region_name;
pub use server::{IncomingRequest, IpcServer, ServerSession};
pub use wake::{wait_on_u64, wake_all, wake_one, IpcEvent, WaitOutcome};

/// Total bytes the region reserves. Virtual memory only: pages are committed
/// as they are touched.
pub fn region_size() -> u64 {
    layout::layout().0
}
