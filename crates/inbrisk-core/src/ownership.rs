use serde::{Deserialize, Serialize};
use std::collections::HashMap;

use crate::ids::{Generation, Hwnd};

/// Who a window/process belongs to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum Ownership {
    /// Inbrisk created it on behalf of an agent.
    Agent,
    /// The human created it, or the human touched it after we did.
    User,
    /// System/protected surface: never mutated, never closed.
    Protected,
    #[default]
    Unknown,
}

impl Ownership {
    pub const fn as_str(self) -> &'static str {
        match self {
            Ownership::Agent => "AgentOwned",
            Ownership::User => "UserOwned",
            Ownership::Protected => "Protected",
            Ownership::Unknown => "Unknown",
        }
    }
}

/// Why a window exists. Drives cleanup policy — the single place where
/// "agent opened it" is *not* automatically "close it".
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize, Default)]
#[serde(rename_all = "snake_case")]
pub enum LifecycleIntent {
    /// Scratch window, worthless the moment the task ends.
    Ephemeral,
    /// May be reused by later tasks (browser, editor with a workspace).
    Reusable,
    /// The point of the task: the user asked for this artifact to exist.
    TaskArtifact,
    /// Produced something the human will keep using.
    UserUseful,
    #[default]
    Unknown,
}

impl LifecycleIntent {
    pub const fn as_str(self) -> &'static str {
        match self {
            LifecycleIntent::Ephemeral => "Ephemeral",
            LifecycleIntent::Reusable => "Reusable",
            LifecycleIntent::TaskArtifact => "TaskArtifact",
            LifecycleIntent::UserUseful => "UserUseful",
            LifecycleIntent::Unknown => "Unknown",
        }
    }
}

/// Rust port of the C# safety rule, expressed as a total function so no
/// caller can re-derive it incorrectly.
///
/// Cleanup happens **only** when the runtime owns the window *and* the window
/// was declared ephemeral. Everything else is kept.
pub const fn eligible_for_cleanup(ownership: Ownership, intent: LifecycleIntent) -> bool {
    matches!(ownership, Ownership::Agent) && matches!(intent, LifecycleIntent::Ephemeral)
}

/// Whether a window may be mutated (focused, clicked, typed into, closed).
pub const fn mutable(ownership: Ownership) -> bool {
    !matches!(ownership, Ownership::Protected)
}

/// The provenance record kept for a live window.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OwnershipStamp {
    pub hwnd: Hwnd,
    pub ownership: Ownership,
    /// Which session/agent caused this window to appear.
    pub opened_by_session: Option<u32>,
    pub opened_by_pid: Option<u32>,
    pub intent: LifecycleIntent,
    /// Human explicitly asked for this window to close.
    pub explicit_user_close_requested: bool,
    /// Cache generation at the time the stamp was created.
    pub generation: Generation,
    pub reason: String,
}

impl OwnershipStamp {
    pub fn new(hwnd: Hwnd, ownership: Ownership, intent: LifecycleIntent) -> Self {
        Self {
            hwnd,
            ownership,
            opened_by_session: None,
            opened_by_pid: None,
            intent,
            explicit_user_close_requested: false,
            generation: 0,
            reason: String::new(),
        }
    }
}

/// Process-wide registry of ownership decisions.
#[derive(Debug, Default)]
pub struct OwnershipRegistry {
    stamps: HashMap<Hwnd, OwnershipStamp>,
}

impl OwnershipRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn stamp(&mut self, stamp: OwnershipStamp) {
        self.stamps.insert(stamp.hwnd, stamp);
    }

    pub fn get(&self, hwnd: Hwnd) -> Option<&OwnershipStamp> {
        self.stamps.get(&hwnd)
    }

    pub fn ownership_of(&self, hwnd: Hwnd) -> Ownership {
        self.stamps
            .get(&hwnd)
            .map(|s| s.ownership)
            .unwrap_or(Ownership::Unknown)
    }

    pub fn intent_of(&self, hwnd: Hwnd) -> LifecycleIntent {
        self.stamps
            .get(&hwnd)
            .map(|s| s.intent)
            .unwrap_or(LifecycleIntent::Unknown)
    }

    /// A human touched the window after the agent opened it: it is theirs now.
    pub fn mark_user_takeover(&mut self, hwnd: Hwnd) {
        if let Some(s) = self.stamps.get_mut(&hwnd) {
            if s.ownership == Ownership::Agent {
                s.ownership = Ownership::User;
                s.reason = "human interacted with an agent-opened window".into();
            }
        }
    }

    pub fn mark_explicit_user_close(&mut self, hwnd: Hwnd) {
        if let Some(s) = self.stamps.get_mut(&hwnd) {
            s.explicit_user_close_requested = true;
        } else {
            let mut s = OwnershipStamp::new(hwnd, Ownership::User, LifecycleIntent::UserUseful);
            s.explicit_user_close_requested = true;
            s.reason = "human asked to close this window".into();
            self.stamps.insert(hwnd, s);
        }
    }

    pub fn mark_protected(&mut self, hwnd: Hwnd, reason: impl Into<String>) {
        let mut s = OwnershipStamp::new(hwnd, Ownership::Protected, LifecycleIntent::UserUseful);
        s.reason = reason.into();
        self.stamps.insert(hwnd, s);
    }

    pub fn forget(&mut self, hwnd: Hwnd) {
        self.stamps.remove(&hwnd);
    }

    pub fn retain_live(&mut self, live: &dyn Fn(Hwnd) -> bool) {
        self.stamps.retain(|hwnd, _| live(*hwnd));
    }

    pub fn iter(&self) -> impl Iterator<Item = (&Hwnd, &OwnershipStamp)> {
        self.stamps.iter()
    }

    pub fn len(&self) -> usize {
        self.stamps.len()
    }

    pub fn is_empty(&self) -> bool {
        self.stamps.is_empty()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cleanup_requires_agent_owned_and_ephemeral() {
        assert!(eligible_for_cleanup(
            Ownership::Agent,
            LifecycleIntent::Ephemeral
        ));
        // agent opened it, but it is a reusable/valuable surface -> KEEP
        assert!(!eligible_for_cleanup(
            Ownership::Agent,
            LifecycleIntent::Reusable
        ));
        assert!(!eligible_for_cleanup(
            Ownership::Agent,
            LifecycleIntent::TaskArtifact
        ));
        assert!(!eligible_for_cleanup(
            Ownership::Agent,
            LifecycleIntent::UserUseful
        ));
        assert!(!eligible_for_cleanup(
            Ownership::Agent,
            LifecycleIntent::Unknown
        ));
        // not ours -> KEEP
        assert!(!eligible_for_cleanup(
            Ownership::User,
            LifecycleIntent::Ephemeral
        ));
        assert!(!eligible_for_cleanup(
            Ownership::Unknown,
            LifecycleIntent::Ephemeral
        ));
        assert!(!eligible_for_cleanup(
            Ownership::Protected,
            LifecycleIntent::Ephemeral
        ));
    }

    #[test]
    fn user_takeover_releases_agent_ownership() {
        let mut reg = OwnershipRegistry::new();
        let hwnd = Hwnd(42);
        reg.stamp(OwnershipStamp::new(
            hwnd,
            Ownership::Agent,
            LifecycleIntent::Ephemeral,
        ));
        assert!(eligible_for_cleanup(
            reg.ownership_of(hwnd),
            reg.intent_of(hwnd)
        ));
        reg.mark_user_takeover(hwnd);
        assert_eq!(reg.ownership_of(hwnd), Ownership::User);
        assert!(!eligible_for_cleanup(
            reg.ownership_of(hwnd),
            reg.intent_of(hwnd)
        ));
    }
}
