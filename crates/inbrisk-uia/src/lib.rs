//! UI Automation backend.
//!
//! Two lanes, never one global COM thread:
//! * a concurrent, bounded, timeout-guarded **read pool** (MTA workers);
//! * a **serialized mutation lane** for `Invoke`/`SetValue`/`Toggle`/`Select`.
//!
//! Semantic patterns are always preferred over synthesized input; the router in
//! `inbrisk-runtime` only falls back to physical input when a pattern is
//! genuinely unavailable.

pub mod apartment;
pub mod control_type;
pub mod runtime_id;
pub mod service;

pub use apartment::{matches_selector, value_capable, ElementData, UiaApartment};
pub use control_type::{control_type_id, role_name};
pub use runtime_id::{runtime_id_of, same_element};
pub use service::{MutationLane, ReadPool, UiaService, UiaStats};
