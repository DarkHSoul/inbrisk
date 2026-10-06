use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use crate::error::{ErrorCode, InbriskError, Result};

/// Monotonic world-state version.
///
/// Every observation that reveals a relevant change bumps it. Mutations may
/// carry `expect.stateVersion`; a mismatch aborts *before* any side effect.
#[derive(Debug)]
pub struct WorldState {
    version: AtomicU64,
    /// Generation counter used to invalidate cached element handles.
    generation: AtomicU64,
}

impl Default for WorldState {
    fn default() -> Self {
        Self::new()
    }
}

impl WorldState {
    pub fn new() -> Self {
        Self {
            version: AtomicU64::new(1),
            generation: AtomicU64::new(1),
        }
    }

    #[inline]
    pub fn version(&self) -> u64 {
        self.version.load(Ordering::Acquire)
    }

    #[inline]
    pub fn generation(&self) -> u64 {
        self.generation.load(Ordering::Acquire)
    }

    /// Record a relevant change; returns the new version.
    #[inline]
    pub fn bump(&self) -> u64 {
        self.version.fetch_add(1, Ordering::AcqRel) + 1
    }

    /// Invalidate every cached element handle (window closed, UIA tree
    /// rebuilt, navigation happened, ...).
    #[inline]
    pub fn invalidate(&self) -> u64 {
        let g = self.generation.fetch_add(1, Ordering::AcqRel) + 1;
        self.bump();
        g
    }

    /// Guard helper: fail with `StaleState` when the caller's expectation is
    /// no longer satisfied. This runs at the mutation boundary.
    pub fn check(&self, expected: u64) -> Result<()> {
        let current = self.version();
        if expected != current {
            return Err(InbriskError::new(
                ErrorCode::StaleState,
                format!("expected stateVersion {expected}, world is at {current}"),
            )
            .with_hint("re-observe, then resubmit with the new stateVersion"));
        }
        Ok(())
    }
}

/// Milliseconds since the Unix epoch — the protocol's only clock.
pub fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stale_state_is_detected() {
        let w = WorldState::new();
        let v = w.version();
        assert!(w.check(v).is_ok());
        w.bump();
        let err = w.check(v).unwrap_err();
        assert_eq!(err.code, ErrorCode::StaleState);
    }

    #[test]
    fn invalidate_bumps_version_and_generation() {
        let w = WorldState::new();
        let v = w.version();
        let g = w.generation();
        let g2 = w.invalidate();
        assert_eq!(g2, g + 1);
        assert!(w.version() > v);
    }
}
