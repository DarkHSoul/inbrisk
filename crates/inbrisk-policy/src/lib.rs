//! Safety policy: what Inbrisk may touch, and under which conditions.
//!
//! The two rules that matter most, both ported from the C# implementation:
//!
//! 1. **Protected surfaces are never mutated or closed.** IDE/agent hosts,
//!    shells, the desktop shell itself and the OS are hard-listed.
//! 2. **"The agent opened it" is not a reason to close it.** Cleanup is allowed
//!    only for windows that are `Agent`-owned *and* declared `Ephemeral`. The
//!    human asking for a close always wins; `force` is a request that this
//!    engine decides on, never a client-side switch.

use inbrisk_core::{
    eligible_for_cleanup, mutable, ErrorCode, Hwnd, InbriskError, LifecycleIntent, Ownership,
    OwnershipStamp, Result, WindowRef,
};
use serde::{Deserialize, Serialize};

/// What policy says about a close request.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "decision", rename_all = "snake_case")]
pub enum CloseDecision {
    /// Post `WM_CLOSE` and let the app save/ask.
    PostClose { reason: String },
    /// Terminate the owning process (only after `PostClose` was ignored, and
    /// only for agent-owned ephemeral windows).
    Terminate { reason: String },
    /// Do not close.
    Keep { reason: String },
    /// Refuse outright (protected surface, or force requested without cause).
    Deny { reason: String },
}

impl CloseDecision {
    pub fn is_allowed(&self) -> bool {
        matches!(
            self,
            CloseDecision::PostClose { .. } | CloseDecision::Terminate { .. }
        )
    }

    pub fn reason(&self) -> &str {
        match self {
            CloseDecision::PostClose { reason }
            | CloseDecision::Terminate { reason }
            | CloseDecision::Keep { reason }
            | CloseDecision::Deny { reason } => reason,
        }
    }
}

/// A protected process/class rule.
#[derive(Debug, Clone)]
pub struct ProtectedRule {
    pub process: String,
    pub class: Option<String>,
    pub reason: &'static str,
}

/// The safety engine.
#[derive(Debug, Clone)]
pub struct PolicyEngine {
    protected: Vec<ProtectedRule>,
    /// Processes whose windows we may launch/normalize but never close.
    never_close: Vec<String>,
}

impl Default for PolicyEngine {
    fn default() -> Self {
        Self::windows_default()
    }
}

impl PolicyEngine {
    /// Hard-listed surfaces for this machine.
    pub fn windows_default() -> Self {
        let mk = |p: &str, reason: &'static str| ProtectedRule {
            process: p.to_ascii_lowercase(),
            class: None,
            reason,
        };
        let mut protected = vec![
            mk("antigravity.exe", "AI IDE / assistant host"),
            mk("hermes.exe", "agent runtime host"),
            mk("code.exe", "VS Code window"),
            mk("code - insiders.exe", "VS Code window"),
            mk("devenv.exe", "Visual Studio window"),
            mk("inbrisk.exe", "Inbrisk's own window"),
            mk("inbrisk-cli.exe", "Inbrisk's own tooling"),
            mk("inbrisk-mcp.exe", "Inbrisk's own tooling"),
            mk("explorer.exe", "the desktop shell"),
            mk("winlogon.exe", "system logon process"),
            mk("csrss.exe", "system critical process"),
            mk("lsass.exe", "system security process"),
            mk("services.exe", "service control manager"),
            mk("smss.exe", "session manager"),
            mk("dwm.exe", "desktop window manager"),
            mk("svchost.exe", "system service host"),
            mk("system", "kernel"),
            mk("registry", "kernel"),
            mk("memcompression", "kernel"),
            mk("searchhost.exe", "shell search host"),
            mk("shellexperiencehost.exe", "shell experience host"),
            mk("startmenuexperiencehost.exe", "start menu"),
            mk("textinputhost.exe", "input host"),
            mk("securityhealthsystray.exe", "security center"),
        ];
        // Window-class rules catch UWP/system surfaces regardless of process.
        protected.extend([
            ProtectedRule {
                process: String::new(),
                class: Some("shell_traywnd".into()),
                reason: "taskbar",
            },
            ProtectedRule {
                process: String::new(),
                class: Some("progman".into()),
                reason: "desktop",
            },
            ProtectedRule {
                process: String::new(),
                class: Some("workerw".into()),
                reason: "desktop",
            },
            ProtectedRule {
                process: String::new(),
                class: Some("windows.ui.core.corewindow".into()),
                reason: "system UI surface",
            },
            ProtectedRule {
                process: String::new(),
                class: Some("taskmanagerwindow".into()),
                reason: "task manager",
            },
            ProtectedRule {
                process: String::new(),
                class: Some("foregroundstaging".into()),
                reason: "input staging window",
            },
        ]);

        let never_close = vec![
            "explorer.exe".into(),
            "chrome.exe".into(),
            "msedge.exe".into(),
            "firefox.exe".into(),
            "teams.exe".into(),
            "outlook.exe".into(),
            "slack.exe".into(),
            "discord.exe".into(),
            "winword.exe".into(),
            "excel.exe".into(),
            "powerpnt.exe".into(),
        ];

        Self {
            protected,
            never_close,
        }
    }

    pub fn is_protected_process(&self, process_name: &str) -> Option<&'static str> {
        let p = process_name.to_ascii_lowercase();
        self.protected
            .iter()
            .find(|r| !r.process.is_empty() && r.process == p)
            .map(|r| r.reason)
    }

    pub fn is_protected_class(&self, class_name: &str) -> Option<&'static str> {
        let c = class_name.to_ascii_lowercase();
        self.protected
            .iter()
            .find(|r| r.class.as_deref() == Some(c.as_str()))
            .map(|r| r.reason)
    }

    /// Classify a window that just appeared.
    pub fn classify(&self, window: &WindowRef, opened_by_agent: bool) -> OwnershipStamp {
        if let Some(reason) = self
            .is_protected_process(&window.process_name)
            .or_else(|| self.is_protected_class(&window.class_name))
        {
            let mut stamp = OwnershipStamp::new(
                window.hwnd,
                Ownership::Protected,
                LifecycleIntent::UserUseful,
            );
            stamp.reason = reason.to_string();
            return stamp;
        }
        let ownership = if opened_by_agent {
            Ownership::Agent
        } else {
            Ownership::Unknown
        };
        let mut stamp = OwnershipStamp::new(window.hwnd, ownership, LifecycleIntent::Unknown);
        stamp.reason = if opened_by_agent {
            "launched by an agent session".into()
        } else {
            "already present when observed".into()
        };
        stamp
    }

    /// May this window be focused / clicked / typed into at all?
    pub fn check_mutable(&self, ownership: Ownership, hwnd: Hwnd, name: &str) -> Result<()> {
        if !mutable(ownership) {
            return Err(InbriskError::new(
                ErrorCode::Protected,
                format!("{name} ({hwnd}) is a protected surface"),
            )
            .with_hint("protected windows are read-only; pick another target"));
        }
        Ok(())
    }

    /// Decide a close request.
    pub fn decide_close(
        &self,
        process_name: &str,
        ownership: Ownership,
        intent: LifecycleIntent,
        explicit_user_close_requested: bool,
        force: bool,
    ) -> CloseDecision {
        if ownership == Ownership::Protected {
            return CloseDecision::Deny {
                reason: format!("{process_name} is a protected surface"),
            };
        }
        if explicit_user_close_requested {
            return CloseDecision::PostClose {
                reason: "the human asked for this window to close".into(),
            };
        }
        if self
            .never_close
            .iter()
            .any(|p| p.eq_ignore_ascii_case(process_name))
        {
            if force {
                return CloseDecision::Deny {
                    reason: format!("{process_name} is protected from forced closure"),
                };
            }
            return CloseDecision::Keep {
                reason: format!(
                    "{process_name} windows are never closed automatically; ask the human"
                ),
            };
        }
        if force {
            // `force` is only honoured where cleanup would already be legal.
            if eligible_for_cleanup(ownership, intent) {
                return CloseDecision::Terminate {
                    reason: "agent-owned ephemeral window did not answer WM_CLOSE".into(),
                };
            }
            return CloseDecision::Deny {
                reason: format!(
                    "force is not available for a {} window (intent {})",
                    ownership.as_str(),
                    intent.as_str()
                ),
            };
        }
        if eligible_for_cleanup(ownership, intent) {
            return CloseDecision::PostClose {
                reason: "agent-owned ephemeral window, task finished".into(),
            };
        }
        CloseDecision::Keep {
            reason: format!(
                "ownership {} / intent {} is not eligible for automatic cleanup",
                ownership.as_str(),
                intent.as_str()
            ),
        }
    }

    /// Whether a plan may end by cleaning up this window.
    pub fn cleanup_allowed(
        &self,
        ownership: Ownership,
        intent: LifecycleIntent,
        explicit_user_close_requested: bool,
    ) -> bool {
        !explicit_user_close_requested && eligible_for_cleanup(ownership, intent)
    }

    pub fn protected_count(&self) -> usize {
        self.protected.len()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn engine() -> PolicyEngine {
        PolicyEngine::windows_default()
    }

    #[test]
    fn agent_hosts_are_protected() {
        let e = engine();
        assert!(e.is_protected_process("Antigravity.exe").is_some());
        assert!(e.is_protected_process("Code.exe").is_some());
        assert!(e.is_protected_process("node.exe").is_none());
        assert!(e.is_protected_process("notepad.exe").is_none());
        assert!(e.is_protected_class("Shell_TrayWnd").is_some());
    }

    #[test]
    fn agent_owned_ephemeral_is_the_only_auto_close_case() {
        let e = engine();
        assert!(matches!(
            e.decide_close(
                "notepad.exe",
                Ownership::Agent,
                LifecycleIntent::Ephemeral,
                false,
                false
            ),
            CloseDecision::PostClose { .. }
        ));
        // same window, but the user touched it -> hands off
        assert!(!e
            .decide_close(
                "notepad.exe",
                Ownership::User,
                LifecycleIntent::Ephemeral,
                false,
                false
            )
            .is_allowed());
        // agent opened it, but it is a keeper
        assert!(!e
            .decide_close(
                "notepad.exe",
                Ownership::Agent,
                LifecycleIntent::TaskArtifact,
                false,
                false
            )
            .is_allowed());
        // chrome is never auto-closed
        assert!(!e
            .decide_close(
                "chrome.exe",
                Ownership::Agent,
                LifecycleIntent::Ephemeral,
                false,
                false
            )
            .is_allowed());
        // user said close -> allowed even though chrome is in never_close
        assert!(e
            .decide_close(
                "chrome.exe",
                Ownership::User,
                LifecycleIntent::Reusable,
                true,
                false
            )
            .is_allowed());
    }

    #[test]
    fn force_is_never_a_client_switch() {
        let e = engine();
        assert!(matches!(
            e.decide_close(
                "notepad.exe",
                Ownership::Agent,
                LifecycleIntent::Ephemeral,
                false,
                true
            ),
            CloseDecision::Terminate { .. }
        ));
        assert!(matches!(
            e.decide_close(
                "notepad.exe",
                Ownership::Unknown,
                LifecycleIntent::Unknown,
                false,
                true
            ),
            CloseDecision::Deny { .. }
        ));
        assert!(matches!(
            e.decide_close(
                "explorer.exe",
                Ownership::Agent,
                LifecycleIntent::Ephemeral,
                false,
                true
            ),
            CloseDecision::Deny { .. }
        ));
    }

    #[test]
    fn protected_windows_are_not_mutable() {
        let e = engine();
        assert!(e
            .check_mutable(Ownership::Protected, Hwnd(1), "taskbar")
            .is_err());
        assert!(e
            .check_mutable(Ownership::Agent, Hwnd(1), "notepad")
            .is_ok());
    }
}
