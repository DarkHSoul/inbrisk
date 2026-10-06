//! Minimal, dependency-free logger.
//!
//! `tracing-subscriber` is not available in this offline build, so the runtime
//! ships a tiny logger with a bounded in-memory ring (for the UI "Logs" page
//! and for `inbrisk status --json`) plus optional file output.

use std::fs::OpenOptions;
use std::io::Write;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicU8, Ordering};
use std::sync::OnceLock;

use parking_lot::Mutex;

#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u8)]
pub enum Level {
    Error = 0,
    Warn = 1,
    Info = 2,
    Debug = 3,
    Trace = 4,
}

impl Level {
    pub const fn as_str(self) -> &'static str {
        match self {
            Level::Error => "ERROR",
            Level::Warn => "WARN",
            Level::Info => "INFO",
            Level::Debug => "DEBUG",
            Level::Trace => "TRACE",
        }
    }

    pub fn parse(s: &str) -> Option<Level> {
        match s.to_ascii_lowercase().as_str() {
            "error" => Some(Level::Error),
            "warn" | "warning" => Some(Level::Warn),
            "info" => Some(Level::Info),
            "debug" => Some(Level::Debug),
            "trace" => Some(Level::Trace),
            _ => None,
        }
    }
}

struct Sink {
    level: AtomicU8,
    ring: Mutex<Vec<String>>,
    ring_cap: usize,
    file: Mutex<Option<PathBuf>>,
    mirror_stderr: AtomicBool,
}

static SINK: OnceLock<Sink> = OnceLock::new();

fn sink() -> &'static Sink {
    SINK.get_or_init(|| Sink {
        level: AtomicU8::new(Level::Info as u8),
        ring: Mutex::new(Vec::with_capacity(512)),
        ring_cap: 2000,
        file: Mutex::new(None),
        mirror_stderr: AtomicBool::new(true),
    })
}

pub fn set_level(level: Level) {
    sink().level.store(level as u8, Ordering::Release);
}

pub fn level() -> Level {
    match sink().level.load(Ordering::Acquire) {
        0 => Level::Error,
        1 => Level::Warn,
        2 => Level::Info,
        3 => Level::Debug,
        _ => Level::Trace,
    }
}

pub fn set_file(path: Option<PathBuf>) {
    *sink().file.lock() = path;
}

pub fn set_mirror_stderr(on: bool) {
    sink().mirror_stderr.store(on, Ordering::Release);
}

pub fn enabled(level: Level) -> bool {
    level <= self::level()
}

pub fn log(level: Level, target: &str, message: &str) {
    if !enabled(level) {
        return;
    }
    let s = sink();
    let line = format!(
        "{} [{}] {}: {}",
        timestamp(),
        level.as_str(),
        target,
        message
    );

    {
        let mut ring = s.ring.lock();
        if ring.len() >= s.ring_cap {
            let drain = ring.len() - s.ring_cap + 1;
            ring.drain(0..drain);
        }
        ring.push(line.clone());
    }

    if let Some(path) = s.file.lock().clone() {
        if let Ok(mut f) = OpenOptions::new().create(true).append(true).open(&path) {
            let _ = writeln!(f, "{line}");
        }
    }

    if s.mirror_stderr.load(Ordering::Acquire) {
        let _ = writeln!(std::io::stderr(), "{line}");
    }
}

/// Most recent `n` log lines (newest last) — used by the UI and diagnostics.
pub fn recent(n: usize) -> Vec<String> {
    let ring = sink().ring.lock();
    let start = ring.len().saturating_sub(n);
    ring[start..].to_vec()
}

/// RFC3339-ish local timestamp without pulling in `chrono`.
pub fn timestamp() -> String {
    let now = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default();
    let secs = now.as_secs();
    let millis = now.subsec_millis();
    let days = secs / 86_400;
    let tod = secs % 86_400;
    let (y, m, d) = civil_from_days(days as i64);
    format!(
        "{:04}-{:02}-{:02}T{:02}:{:02}:{:02}.{:03}Z",
        y,
        m,
        d,
        tod / 3600,
        (tod % 3600) / 60,
        tod % 60,
        millis
    )
}

/// Howard Hinnant's civil-from-days algorithm.
fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = if z >= 0 { z } else { z - 146_096 } / 146_097;
    let doe = (z - era * 146_097) as u64;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe as i64 + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = if mp < 10 { mp + 3 } else { mp - 9 } as u32;
    (if m <= 2 { y + 1 } else { y }, m, d)
}

#[macro_export]
macro_rules! log_error {
    ($target:expr, $($arg:tt)*) => {
        $crate::logging::log($crate::logging::Level::Error, $target, &format!($($arg)*))
    };
}

#[macro_export]
macro_rules! log_warn {
    ($target:expr, $($arg:tt)*) => {
        $crate::logging::log($crate::logging::Level::Warn, $target, &format!($($arg)*))
    };
}

#[macro_export]
macro_rules! log_info {
    ($target:expr, $($arg:tt)*) => {
        $crate::logging::log($crate::logging::Level::Info, $target, &format!($($arg)*))
    };
}

#[macro_export]
macro_rules! log_debug {
    ($target:expr, $($arg:tt)*) => {
        $crate::logging::log($crate::logging::Level::Debug, $target, &format!($($arg)*))
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn timestamp_is_well_formed() {
        let ts = timestamp();
        assert_eq!(ts.len(), 24, "unexpected timestamp {ts}");
        assert!(ts.ends_with('Z'));
    }

    #[test]
    fn ring_keeps_recent_lines() {
        set_level(Level::Info);
        log(Level::Info, "test", "hello-ring");
        assert!(recent(10).iter().any(|l| l.contains("hello-ring")));
    }
}
