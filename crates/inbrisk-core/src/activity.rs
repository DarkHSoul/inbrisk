//! Single source of truth for "what is Inbrisk doing right now".
//!
//! The tray, the floating pill, the perimeter wall and the target border all
//! subscribe to this. Nothing may keep its own parallel activity flag.

use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant};

use parking_lot::Mutex;
use serde::{Deserialize, Serialize};
use tokio::sync::broadcast;

use crate::world::now_ms;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ActivityState {
    Idle,
    Active,
    Waiting,
    Paused,
    FailureSettle,
    Emergency,
}

impl ActivityState {
    pub const fn is_visible(self) -> bool {
        !matches!(self, ActivityState::Idle)
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            ActivityState::Idle => "Idle",
            ActivityState::Active => "Active",
            ActivityState::Waiting => "Waiting",
            ActivityState::Paused => "Paused",
            ActivityState::FailureSettle => "FailureSettle",
            ActivityState::Emergency => "Emergency",
        }
    }
}

/// Immutable snapshot published on every transition.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ActivitySnapshot {
    pub state: ActivityState,
    pub active_operations: u32,
    pub waiting_operations: u32,
    /// Human readable description of the current work, e.g. `Save As`.
    pub label: String,
    pub reason: String,
    /// Which window the activity is bound to (drives the yellow border).
    pub target_hwnd: isize,
    pub changed_at_ms: u64,
    /// Bumped on every transition; UI uses it to drop out-of-order frames.
    pub generation: u64,
}

impl Default for ActivitySnapshot {
    fn default() -> Self {
        Self {
            state: ActivityState::Idle,
            active_operations: 0,
            waiting_operations: 0,
            label: String::new(),
            reason: String::new(),
            target_hwnd: 0,
            changed_at_ms: now_ms(),
            generation: 0,
        }
    }
}

#[derive(Debug)]
struct Inner {
    active: AtomicU32,
    waiting: AtomicU32,
    emergency: AtomicBool,
    paused: AtomicBool,
    /// Unix-ms until which the "failure settle" animation should stay up.
    failure_until_ms: AtomicU64,
    label: Mutex<String>,
    target_hwnd: Mutex<isize>,
    generation: AtomicU64,
    last: Mutex<ActivitySnapshot>,
}

/// Tracks live activity and broadcasts transitions.
#[derive(Debug)]
pub struct ActivityManager {
    inner: Arc<Inner>,
    tx: broadcast::Sender<ActivitySnapshot>,
}

impl Default for ActivityManager {
    fn default() -> Self {
        Self::new()
    }
}

impl ActivityManager {
    pub fn new() -> Self {
        let (tx, _rx) = broadcast::channel(256);
        Self {
            inner: Arc::new(Inner {
                active: AtomicU32::new(0),
                waiting: AtomicU32::new(0),
                emergency: AtomicBool::new(false),
                paused: AtomicBool::new(false),
                failure_until_ms: AtomicU64::new(0),
                label: Mutex::new(String::new()),
                target_hwnd: Mutex::new(0),
                generation: AtomicU64::new(0),
                last: Mutex::new(ActivitySnapshot::default()),
            }),
            tx,
        }
    }

    pub fn subscribe(&self) -> broadcast::Receiver<ActivitySnapshot> {
        self.tx.subscribe()
    }

    pub fn snapshot(&self) -> ActivitySnapshot {
        let current_state = self.state();
        let mut last = self.inner.last.lock();
        if last.state != current_state {
            last.state = current_state;
            if current_state == ActivityState::Idle {
                last.target_hwnd = 0;
            }
            last.changed_at_ms = now_ms();
            last.generation = self.inner.generation.fetch_add(1, Ordering::AcqRel) + 1;
        }
        last.clone()
    }

    pub fn state(&self) -> ActivityState {
        if self.inner.emergency.load(Ordering::Acquire) {
            return ActivityState::Emergency;
        }
        if self.inner.paused.load(Ordering::Acquire) {
            return ActivityState::Paused;
        }
        if self.inner.active.load(Ordering::Acquire) > 0 {
            return ActivityState::Active;
        }
        if self.inner.waiting.load(Ordering::Acquire) > 0 {
            return ActivityState::Waiting;
        }
        if now_ms() < self.inner.failure_until_ms.load(Ordering::Acquire) {
            return ActivityState::FailureSettle;
        }
        ActivityState::Idle
    }

    pub fn active_operations(&self) -> u32 {
        self.inner.active.load(Ordering::Acquire)
    }

    pub fn is_emergency(&self) -> bool {
        self.inner.emergency.load(Ordering::Acquire)
    }

    pub fn is_paused(&self) -> bool {
        self.inner.paused.load(Ordering::Acquire)
    }

    pub fn set_label(&self, label: impl Into<String>) {
        *self.inner.label.lock() = label.into();
        self.publish();
    }

    pub fn set_target(&self, hwnd: isize) {
        *self.inner.target_hwnd.lock() = hwnd;
        self.publish();
    }

    /// Engage emergency stop. Idempotent, and blocks every mutation.
    pub fn engage_emergency(&self, reason: impl Into<String>) {
        self.inner.emergency.store(true, Ordering::Release);
        *self.inner.label.lock() = reason.into();
        self.publish();
    }

    pub fn clear_emergency(&self) {
        self.inner.emergency.store(false, Ordering::Release);
        self.publish();
    }

    pub fn pause(&self, reason: impl Into<String>) {
        self.inner.paused.store(true, Ordering::Release);
        *self.inner.label.lock() = reason.into();
        self.publish();
    }

    pub fn resume(&self) {
        self.inner.paused.store(false, Ordering::Release);
        self.publish();
    }

    /// Show the amber settle indicator for a short while after a failure.
    pub fn settle_failure(&self, reason: impl Into<String>, hold: Duration) {
        self.inner
            .failure_until_ms
            .store(now_ms() + hold.as_millis() as u64, Ordering::Release);
        *self.inner.label.lock() = reason.into();
        self.publish();
    }

    /// Begin an operation. The returned token releases it on drop — which is
    /// why cancellation and panics can never leak a stuck "Active" pill.
    pub fn begin(self: &Arc<Self>, label: impl Into<String>) -> ActivityToken {
        let label = label.into();
        self.inner.active.fetch_add(1, Ordering::AcqRel);
        *self.inner.label.lock() = label.clone();
        self.publish();
        ActivityToken {
            manager: Arc::clone(self),
            kind: TokenKind::Active,
        }
    }

    /// Begin a *waiting* phase (event-driven wait, no busy loop).
    pub fn begin_waiting(self: &Arc<Self>, label: impl Into<String>) -> ActivityToken {
        let label = label.into();
        self.inner.waiting.fetch_add(1, Ordering::AcqRel);
        *self.inner.label.lock() = label.clone();
        self.publish();
        ActivityToken {
            manager: Arc::clone(self),
            kind: TokenKind::Waiting,
        }
    }

    pub fn publish(&self) {
        // Read the label once: parking_lot mutexes are not reentrant, and a
        // second `lock()` inside the same statement would self-deadlock.
        let label = self.inner.label.lock().clone();
        let snapshot = ActivitySnapshot {
            state: self.state(),
            active_operations: self.inner.active.load(Ordering::Acquire),
            waiting_operations: self.inner.waiting.load(Ordering::Acquire),
            label: label.clone(),
            reason: label,
            target_hwnd: *self.inner.target_hwnd.lock(),
            changed_at_ms: now_ms(),
            generation: self.inner.generation.fetch_add(1, Ordering::AcqRel) + 1,
        };
        *self.inner.last.lock() = snapshot.clone();
        // A send error only means "nobody is listening" — never fatal.
        let _ = self.tx.send(snapshot);
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TokenKind {
    Active,
    Waiting,
}

/// RAII activity marker.
#[derive(Debug)]
pub struct ActivityToken {
    manager: Arc<ActivityManager>,
    kind: TokenKind,
}

impl ActivityToken {
    pub fn manager(&self) -> &Arc<ActivityManager> {
        &self.manager
    }
}

impl Drop for ActivityToken {
    fn drop(&mut self) {
        match self.kind {
            TokenKind::Active => {
                let _ = self.manager.inner.active.fetch_update(
                    Ordering::AcqRel,
                    Ordering::Acquire,
                    |v| Some(v.saturating_sub(1)),
                );
            }
            TokenKind::Waiting => {
                let _ = self.manager.inner.waiting.fetch_update(
                    Ordering::AcqRel,
                    Ordering::Acquire,
                    |v| Some(v.saturating_sub(1)),
                );
            }
        }
        self.manager.publish();
    }
}

/// Tracks one window's activity for the yellow target border.
#[derive(Debug)]
pub struct WindowActivityToken {
    manager: Arc<ActivityManager>,
    hwnd: isize,
}

impl WindowActivityToken {
    pub fn new(manager: Arc<ActivityManager>, hwnd: isize) -> Self {
        manager.set_target(hwnd);
        Self { manager, hwnd }
    }

    pub fn hwnd(&self) -> isize {
        self.hwnd
    }
}

impl Drop for WindowActivityToken {
    fn drop(&mut self) {
        // Only clear the target if nobody else took it over meanwhile.
        let mut target = self.manager.inner.target_hwnd.lock();
        if *target == self.hwnd {
            *target = 0;
        }
        drop(target);
        self.manager.publish();
    }
}

/// Convenience for measuring how long a phase took.
pub fn stopwatch() -> Instant {
    Instant::now()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn token_drop_returns_to_idle() {
        let mgr = Arc::new(ActivityManager::new());
        assert_eq!(mgr.state(), ActivityState::Idle);
        {
            let _t = mgr.begin("notepad");
            assert_eq!(mgr.state(), ActivityState::Active);
        }
        assert_eq!(mgr.state(), ActivityState::Idle);
    }

    #[test]
    fn emergency_wins_over_active() {
        let mgr = Arc::new(ActivityManager::new());
        let _t = mgr.begin("work");
        mgr.engage_emergency("panic");
        assert_eq!(mgr.state(), ActivityState::Emergency);
        mgr.clear_emergency();
        assert_eq!(mgr.state(), ActivityState::Active);
    }

    #[test]
    fn window_token_clears_target() {
        let mgr = Arc::new(ActivityManager::new());
        {
            let _w = WindowActivityToken::new(Arc::clone(&mgr), 0x1234);
            assert_eq!(mgr.snapshot().target_hwnd, 0x1234);
        }
        assert_eq!(mgr.snapshot().target_hwnd, 0);
    }
}
