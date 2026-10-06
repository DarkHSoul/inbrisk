use serde::{Deserialize, Serialize};

use inbrisk_core::{ActivitySnapshot, ElementRef, Hwnd, PlanId, StateVersion, WindowRef};

/// Server-push notifications carried by the event ring.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "event", rename_all = "snake_case")]
pub enum Event {
    /// ActivityManager transition — drives tray/pill/perimeter.
    Activity(ActivitySnapshot),
    WindowOpened(WindowRef),
    WindowClosed {
        hwnd: Hwnd,
        process_id: u32,
    },
    WindowFocusChanged {
        hwnd: Hwnd,
        previous: Hwnd,
        title: String,
    },
    /// A relevant world change happened.
    StateChanged {
        state_version: StateVersion,
        generation: u64,
        reason: String,
    },
    /// Progress of a running plan (one event per step change, not per poll).
    PlanProgress {
        plan_id: PlanId,
        step_index: usize,
        total_steps: usize,
        action: String,
        status: String,
    },
    PlanFinished {
        plan_id: PlanId,
        succeeded: bool,
        elapsed_us: u64,
    },
    /// Element resolved by a watch/subscription.
    ElementAppeared(ElementRef),
    ElementVanished {
        element_id: u64,
        name: String,
    },
    /// Emergency stop engaged / released.
    Emergency {
        engaged: bool,
        reason: String,
    },
    Paused {
        reason: String,
    },
    Resumed,
    /// Client-facing log line (already filtered by level).
    Log {
        level: String,
        target: String,
        message: String,
    },
    /// Runtime is going away; clients must re-handshake on the next call.
    RuntimeShutdown {
        reason: String,
        epoch: u64,
    },
}

impl Event {
    pub const fn kind(&self) -> &'static str {
        match self {
            Event::Activity(_) => "activity",
            Event::WindowOpened(_) => "window_opened",
            Event::WindowClosed { .. } => "window_closed",
            Event::WindowFocusChanged { .. } => "window_focus_changed",
            Event::StateChanged { .. } => "state_changed",
            Event::PlanProgress { .. } => "plan_progress",
            Event::PlanFinished { .. } => "plan_finished",
            Event::ElementAppeared(_) => "element_appeared",
            Event::ElementVanished { .. } => "element_vanished",
            Event::Emergency { .. } => "emergency",
            Event::Paused { .. } => "paused",
            Event::Resumed => "resumed",
            Event::Log { .. } => "log",
            Event::RuntimeShutdown { .. } => "runtime_shutdown",
        }
    }

    /// High-frequency events are coalesced by the runtime before publishing.
    pub const fn is_coalescable(&self) -> bool {
        matches!(self, Event::Activity(_))
    }
}

/// Event ring entry: a sequence number plus the payload.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct EventEnvelope {
    /// Monotonic per-runtime sequence; clients detect drops with it.
    pub seq: u64,
    pub at_ms: u64,
    /// Subscription that asked for this event, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub subscription_id: Option<u64>,
    pub event: Event,
}
