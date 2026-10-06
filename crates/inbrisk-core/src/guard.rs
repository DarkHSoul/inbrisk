use serde::{Deserialize, Serialize};

use crate::ids::StateVersion;

/// Optional state precondition carried by any mutating request.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub struct Expect {
    /// Required world version. `None` = no precondition (caller accepts risk).
    ///
    /// Accepts both `state_version` and the documented `stateVersion` spelling
    /// so hand-written plans from the architecture notes keep working.
    #[serde(
        default,
        alias = "stateVersion",
        skip_serializing_if = "Option::is_none"
    )]
    pub state_version: Option<StateVersion>,
    /// Required element-cache generation.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub generation: Option<u64>,
}

impl Expect {
    pub const fn version(v: StateVersion) -> Self {
        Self {
            state_version: Some(v),
            generation: None,
        }
    }

    pub fn merge_generation(mut self, g: u64) -> Self {
        self.generation = Some(g);
        self
    }
}

/// What a guard check decided, kept for audit/telemetry.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GuardOutcome {
    pub ok: bool,
    pub expected_version: Option<StateVersion>,
    pub observed_version: StateVersion,
    pub side_effects: u32,
}

/// Applies `Expect` against the live world.
#[derive(Debug)]
pub struct StateGuard;

impl StateGuard {
    /// Returns `Ok(GuardOutcome)` when the precondition holds, otherwise the
    /// caller must return `StaleState` with **zero** side effects.
    pub fn check(
        expect: &Expect,
        world: &crate::world::WorldState,
    ) -> Result<GuardOutcome, crate::error::InbriskError> {
        let observed = world.version();
        if let Some(expected) = expect.state_version {
            if expected != observed {
                return Err(crate::error::InbriskError::new(
                    crate::error::ErrorCode::StaleState,
                    format!("expected stateVersion {expected}, world is at {observed}"),
                )
                .with_hint("re-observe and retry; nothing was executed"));
            }
        }
        if let Some(gen) = expect.generation {
            let current = world.generation();
            if gen != current {
                return Err(crate::error::InbriskError::new(
                    crate::error::ErrorCode::StaleState,
                    format!("expected element generation {gen}, current is {current}"),
                )
                .with_hint("refresh the element handle with find/observe"));
            }
        }
        Ok(GuardOutcome {
            ok: true,
            expected_version: expect.state_version,
            observed_version: observed,
            side_effects: 0,
        })
    }
}
