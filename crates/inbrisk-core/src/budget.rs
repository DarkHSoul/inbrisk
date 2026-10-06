use std::time::{Duration, Instant};

use crate::error::{ErrorCode, InbriskError, Result};

/// Bounds for one operation so nothing can run away.
#[derive(Debug, Clone, Copy)]
pub struct OperationBudget {
    pub deadline: Option<Instant>,
    pub max_steps: u32,
    pub steps_used: u32,
    pub max_wait: Option<Duration>,
}

impl Default for OperationBudget {
    fn default() -> Self {
        Self {
            deadline: None,
            max_steps: 256,
            steps_used: 0,
            max_wait: None,
        }
    }
}

impl OperationBudget {
    pub fn with_timeout(timeout: Duration) -> Self {
        Self {
            deadline: Some(Instant::now() + timeout),
            ..Default::default()
        }
    }

    pub fn with_steps(max_steps: u32) -> Self {
        Self {
            max_steps,
            ..Default::default()
        }
    }

    pub fn expired(&self) -> bool {
        self.deadline.map(|d| Instant::now() >= d).unwrap_or(false)
    }

    pub fn tick(&mut self) -> Result<()> {
        self.steps_used += 1;
        if self.steps_used > self.max_steps {
            return Err(InbriskError::new(
                ErrorCode::BudgetExhausted,
                format!("plan exceeded {} steps", self.max_steps),
            ));
        }
        if self.expired() {
            return Err(InbriskError::new(
                ErrorCode::Timeout,
                "operation deadline exceeded",
            ));
        }
        Ok(())
    }

    pub fn remaining(&self) -> Option<Duration> {
        self.deadline
            .map(|d| d.saturating_duration_since(Instant::now()))
    }
}
