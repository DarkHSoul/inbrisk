use serde::{Deserialize, Serialize};

/// The nine first-class operations of the runtime.
///
/// The numeric codes are the fast path for the IPC ring; the string names are
/// the stable external surface. Adding an op means adding a code at the end —
/// never renumbering.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Op {
    /// Session handshake (`HELLO` / `WELCOME`).
    Hello,
    /// Look at the world: windows, foreground, activity.
    Observe,
    /// Resolve a selector to element handle(s).
    Find,
    /// Read properties of a resolved element.
    Read,
    /// Single action against a resolved target.
    Act,
    /// Execute a whole plan locally, in one round trip.
    Run,
    /// Event-driven wait.
    Wait,
    /// Cancel an in-flight operation.
    Cancel,
    /// Runtime status, capabilities, counters.
    Status,
    /// Start streaming events.
    Subscribe,
    /// Latency probe.
    Ping,
    /// Ask the runtime to shut down.
    Shutdown,
    /// Persistent terminal session operations (open/write/read/status/close).
    Terminal,
}

impl Op {
    pub const ALL: [Op; 13] = [
        Op::Terminal,
        Op::Hello,
        Op::Observe,
        Op::Find,
        Op::Read,
        Op::Act,
        Op::Run,
        Op::Wait,
        Op::Cancel,
        Op::Status,
        Op::Subscribe,
        Op::Ping,
        Op::Shutdown,
    ];

    pub const fn code(self) -> u16 {
        match self {
            Op::Hello => 1,
            Op::Observe => 2,
            Op::Find => 3,
            Op::Read => 4,
            Op::Act => 5,
            Op::Run => 6,
            Op::Wait => 7,
            Op::Cancel => 8,
            Op::Status => 9,
            Op::Subscribe => 10,
            Op::Ping => 11,
            Op::Shutdown => 12,
            Op::Terminal => 13,
        }
    }

    pub const fn from_code(code: u16) -> Option<Op> {
        match code {
            1 => Some(Op::Hello),
            2 => Some(Op::Observe),
            3 => Some(Op::Find),
            4 => Some(Op::Read),
            5 => Some(Op::Act),
            6 => Some(Op::Run),
            7 => Some(Op::Wait),
            8 => Some(Op::Cancel),
            9 => Some(Op::Status),
            10 => Some(Op::Subscribe),
            11 => Some(Op::Ping),
            12 => Some(Op::Shutdown),
            13 => Some(Op::Terminal),
            _ => None,
        }
    }

    pub const fn is_mutating(self) -> bool {
        matches!(self, Op::Act | Op::Run | Op::Shutdown)
    }

    pub const fn as_str(self) -> &'static str {
        match self {
            Op::Hello => "hello",
            Op::Observe => "observe",
            Op::Find => "find",
            Op::Read => "read",
            Op::Act => "act",
            Op::Run => "run",
            Op::Wait => "wait",
            Op::Cancel => "cancel",
            Op::Status => "status",
            Op::Subscribe => "subscribe",
            Op::Ping => "ping",
            Op::Shutdown => "shutdown",
            Op::Terminal => "terminal",
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn codes_round_trip() {
        for op in Op::ALL {
            assert_eq!(Op::from_code(op.code()), Some(op));
        }
    }
}
