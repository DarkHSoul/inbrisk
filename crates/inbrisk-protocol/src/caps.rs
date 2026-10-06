use serde::{Deserialize, Serialize};

/// Bit set advertised by the runtime in `WELCOME`/`STATUS`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(transparent)]
pub struct Capabilities(pub u64);

impl Capabilities {
    pub const WIN32: u64 = 1 << 0;
    pub const UIA: u64 = 1 << 1;
    pub const PHYSICAL_INPUT: u64 = 1 << 2;
    pub const CDP: u64 = 1 << 3;
    pub const VISION: u64 = 1 << 4;
    pub const EVENTS: u64 = 1 << 5;
    pub const CANCEL: u64 = 1 << 6;
    pub const PLANS: u64 = 1 << 7;
    pub const RECIPES: u64 = 1 << 8;
    pub const CAPTURE: u64 = 1 << 9;
    pub const DRY_RUN: u64 = 1 << 10;
    pub const TERMINAL: u64 = 1 << 11;

    pub const fn empty() -> Self {
        Capabilities(0)
    }

    pub const fn with(self, bits: u64) -> Self {
        Capabilities(self.0 | bits)
    }

    pub const fn contains(self, bits: u64) -> bool {
        self.0 & bits == bits
    }

    pub fn names(self) -> Vec<&'static str> {
        let mut out = Vec::new();
        for (bit, name) in [
            (Self::WIN32, "win32"),
            (Self::UIA, "uia"),
            (Self::PHYSICAL_INPUT, "physical_input"),
            (Self::CDP, "cdp"),
            (Self::VISION, "vision"),
            (Self::EVENTS, "events"),
            (Self::CANCEL, "cancel"),
            (Self::PLANS, "plans"),
            (Self::RECIPES, "recipes"),
            (Self::CAPTURE, "capture"),
            (Self::DRY_RUN, "dry_run"),
            (Self::TERMINAL, "terminal"),
        ] {
            if self.contains(bit) {
                out.push(name);
            }
        }
        out
    }
}

impl std::fmt::Display for Capabilities {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.names().join("|"))
    }
}
