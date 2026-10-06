use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ExecutionCapability {
    InteractiveDesktop,
    PhysicalInput,
    WindowEnumeration,
}

impl std::fmt::Display for ExecutionCapability {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{self:?}")
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ExecutionCapabilities {
    pub interactive_desktop: bool,
    pub physical_input: bool,
    pub window_enumeration: bool,
}

impl ExecutionCapabilities {
    pub const fn unavailable() -> Self {
        Self {
            interactive_desktop: false,
            physical_input: false,
            window_enumeration: false,
        }
    }

    pub const fn all_available() -> Self {
        Self {
            interactive_desktop: true,
            physical_input: true,
            window_enumeration: true,
        }
    }

    pub fn supports(&self, capability: ExecutionCapability) -> bool {
        match capability {
            ExecutionCapability::InteractiveDesktop => self.interactive_desktop,
            ExecutionCapability::PhysicalInput => self.physical_input,
            ExecutionCapability::WindowEnumeration => self.window_enumeration,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn capabilities_supports_returns_expected_values() {
        let caps = ExecutionCapabilities {
            interactive_desktop: true,
            physical_input: false,
            window_enumeration: true,
        };
        assert!(caps.supports(ExecutionCapability::InteractiveDesktop));
        assert!(!caps.supports(ExecutionCapability::PhysicalInput));
        assert!(caps.supports(ExecutionCapability::WindowEnumeration));
    }

    #[test]
    fn capabilities_unavailable_defaults_false() {
        let caps = ExecutionCapabilities::unavailable();
        assert!(!caps.supports(ExecutionCapability::InteractiveDesktop));
        assert!(!caps.supports(ExecutionCapability::PhysicalInput));
        assert!(!caps.supports(ExecutionCapability::WindowEnumeration));

        let all = ExecutionCapabilities::all_available();
        assert!(all.supports(ExecutionCapability::InteractiveDesktop));
        assert!(all.supports(ExecutionCapability::PhysicalInput));
        assert!(all.supports(ExecutionCapability::WindowEnumeration));
    }
}
