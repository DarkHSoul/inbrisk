//! Physical input backend: the single global arbiter for mouse and keyboard.
//!
//! Rules encoded here:
//! * Exactly one *mutation* may hold the physical input lease at a time.
//! * Semantic reads and `Invoke`/`SetValue` calls are **not** blocked by the
//!   lease — only synthesized input is serialized.
//! * Every lease is tied to a session so a dead client cannot wedge the input
//!   queue (leases are released on session teardown).

use std::sync::Arc;
use std::time::{Duration, Instant};

use inbrisk_core::{ErrorCode, Hwnd, InbriskError, Result};
use parking_lot::{Condvar, Mutex};

#[derive(Debug, Clone)]
struct Lease {
    session_id: u32,
    holder: String,
    acquired: Instant,
}

#[derive(Debug, Default)]
struct Inner {
    current: Option<Lease>,
    /// Re-entrancy depth for the holder: a plan may nest leases, and the input
    /// is only released when the outermost one drops.
    depth: u32,
    waiters: u32,
    leases_granted: u64,
    timeouts: u64,
}

/// Global input arbiter. One per runtime process.
#[derive(Debug)]
pub struct GlobalInputArbiter {
    inner: Mutex<Inner>,
    available: Condvar,
    default_timeout: Duration,
}

impl Default for GlobalInputArbiter {
    fn default() -> Self {
        Self::new(Duration::from_secs(30))
    }
}

impl GlobalInputArbiter {
    pub fn new(default_timeout: Duration) -> Self {
        Self {
            inner: Mutex::new(Inner::default()),
            available: Condvar::new(),
            default_timeout,
        }
    }

    /// Acquire the lease, waiting up to `timeout`.
    pub fn acquire(
        self: &Arc<Self>,
        session_id: u32,
        holder: impl Into<String>,
        timeout: Option<Duration>,
    ) -> Result<InputLease> {
        let holder = holder.into();
        let timeout = timeout.unwrap_or(self.default_timeout);
        let deadline = Instant::now() + timeout;
        let mut inner = self.inner.lock();
        loop {
            match &inner.current {
                None => {
                    inner.current = Some(Lease {
                        session_id,
                        holder: holder.clone(),
                        acquired: Instant::now(),
                    });
                    inner.depth = 1;
                    inner.leases_granted += 1;
                    return Ok(InputLease {
                        arbiter: Arc::clone(self),
                        session_id,
                    });
                }
                Some(lease) if lease.session_id == session_id => {
                    // Re-entrant within the same session: the plan executor
                    // must not deadlock against itself.
                    inner.depth = inner.depth.saturating_add(1);
                    return Ok(InputLease {
                        arbiter: Arc::clone(self),
                        session_id,
                    });
                }
                Some(_) => {}
            }
            inner.waiters += 1;
            let now = Instant::now();
            if now >= deadline {
                inner.waiters -= 1;
                inner.timeouts += 1;
                let current = inner
                    .current
                    .as_ref()
                    .map(|l| l.holder.clone())
                    .unwrap_or_default();
                return Err(InbriskError::new(
                    ErrorCode::InputLeaseTimeout,
                    format!("physical input is held by '{current}'"),
                )
                .with_hint("retry after the other session releases the lease"));
            }
            self.available.wait_for(&mut inner, deadline - now);
            inner.waiters -= 1;
        }
    }

    /// Non-blocking probe: is the input free for this session?
    pub fn is_free_for(&self, session_id: u32) -> bool {
        let inner = self.inner.lock();
        match &inner.current {
            None => true,
            Some(l) => l.session_id == session_id,
        }
    }

    /// Release every lease held by a session (used on disconnect/emergency).
    pub fn release_session(&self, session_id: u32) {
        let mut inner = self.inner.lock();
        if inner.current.as_ref().map(|l| l.session_id) == Some(session_id) {
            inner.current = None;
            inner.depth = 0;
            self.available.notify_all();
        }
    }

    /// Emergency stop: drop the lease no matter who holds it.
    pub fn force_release(&self) {
        let mut inner = self.inner.lock();
        inner.current = None;
        inner.depth = 0;
        self.available.notify_all();
    }

    pub fn holder(&self) -> Option<String> {
        self.inner.lock().current.as_ref().map(|l| l.holder.clone())
    }

    pub fn is_held(&self) -> bool {
        self.inner.lock().current.is_some()
    }

    /// How long the current lease has been held (zero when the input is free).
    pub fn held_for(&self) -> Duration {
        self.inner
            .lock()
            .current
            .as_ref()
            .map(|l| l.acquired.elapsed())
            .unwrap_or_default()
    }

    pub fn stats(&self) -> (u64, u64, u32) {
        let inner = self.inner.lock();
        (inner.leases_granted, inner.timeouts, inner.waiters)
    }
}

/// RAII lease. Dropping it always releases the input.
#[derive(Debug)]
pub struct InputLease {
    arbiter: Arc<GlobalInputArbiter>,
    session_id: u32,
}

impl InputLease {
    pub fn session_id(&self) -> u32 {
        self.session_id
    }
}

impl Drop for InputLease {
    fn drop(&mut self) {
        let mut inner = self.arbiter.inner.lock();
        if inner.current.as_ref().map(|l| l.session_id) == Some(self.session_id) {
            inner.depth = inner.depth.saturating_sub(1);
            if inner.depth == 0 {
                inner.current = None;
            }
        }
        self.arbiter.available.notify_one();
    }
}

/// A physical input request, resolved by the platform layer.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InputTarget {
    Screen(i32, i32),
    Window(Hwnd),
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lease_is_exclusive_but_reentrant_per_session() {
        let arbiter = Arc::new(GlobalInputArbiter::new(Duration::from_millis(50)));
        let a = arbiter.acquire(1, "plan a", None).unwrap();
        assert!(!arbiter.is_free_for(2));
        assert!(arbiter.is_free_for(1));
        // same session may re-enter
        let a2 = arbiter.acquire(1, "plan a (nested)", None).unwrap();
        drop(a2);
        assert!(!arbiter.is_free_for(2));
        drop(a);
        assert!(arbiter.is_free_for(2));
    }

    #[test]
    fn second_session_times_out_instead_of_blocking_forever() {
        let arbiter = Arc::new(GlobalInputArbiter::new(Duration::from_millis(20)));
        let _a = arbiter.acquire(1, "holder", None).unwrap();
        let err = arbiter
            .acquire(2, "waiter", Some(Duration::from_millis(20)))
            .unwrap_err();
        assert_eq!(err.code, ErrorCode::InputLeaseTimeout);
    }

    #[test]
    fn session_teardown_releases_the_lease() {
        let arbiter = Arc::new(GlobalInputArbiter::new(Duration::from_millis(20)));
        let _a = arbiter.acquire(7, "dead client", None).unwrap();
        arbiter.release_session(7);
        assert!(arbiter.acquire(8, "other", None).is_ok());
    }
}
