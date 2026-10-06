//! Runtime state: world version, ownership, element cache, profiles, counters.

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::Instant;

use inbrisk_core::{
    ActivityManager, ElementRef, Hwnd, InbriskError, LifecycleIntent, Ownership, OwnershipRegistry,
    OwnershipStamp, Result, SessionId, StateVersion, WindowRef, WorldState,
};
use inbrisk_protocol::selector::Selector;
use parking_lot::RwLock;

/// A resolved element handle plus the selector that produced it.
///
/// The selector is what makes safe re-resolution possible: handle -> selector
/// -> fresh element, with the runtime id used to reject a bait-and-switch.
#[derive(Debug, Clone)]
pub struct CachedHandle {
    pub element: ElementRef,
    pub selector: Selector,
    /// Top-level window the element lives in (UIA returns 0 for non-window
    /// elements, so the window is tracked explicitly).
    pub window_hwnd: Hwnd,
    pub created_ms: u64,
    pub session_id: SessionId,
}

/// Element handle table.
#[derive(Debug, Default)]
pub struct ElementCache {
    next_id: AtomicU64,
    entries: RwLock<HashMap<u64, CachedHandle>>,
    hits: AtomicU64,
    misses: AtomicU64,
}

impl ElementCache {
    pub fn new() -> Self {
        Self {
            next_id: AtomicU64::new(1),
            entries: RwLock::new(HashMap::new()),
            hits: AtomicU64::new(0),
            misses: AtomicU64::new(0),
        }
    }

    pub fn insert(
        &self,
        element: ElementRef,
        selector: Selector,
        session_id: SessionId,
        window_hwnd: Hwnd,
    ) -> u64 {
        let id = self.next_id.fetch_add(1, Ordering::AcqRel);
        let mut entry = element;
        entry.id = id;
        let handle = CachedHandle {
            element: entry,
            selector,
            window_hwnd,
            created_ms: inbrisk_core::now_ms(),
            session_id,
        };
        self.entries.write().insert(id, handle);
        id
    }

    pub fn get(&self, id: u64) -> Result<CachedHandle> {
        match self.entries.read().get(&id) {
            Some(h) => {
                self.hits.fetch_add(1, Ordering::Relaxed);
                Ok(h.clone())
            }
            None => {
                self.misses.fetch_add(1, Ordering::Relaxed);
                Err(InbriskError::new(
                    inbrisk_core::ErrorCode::StaleState,
                    format!("element handle {id} is not in the cache any more"),
                )
                .with_hint("find/observe the element again"))
            }
        }
    }

    pub fn invalidate_all(&self) {
        self.entries.write().clear();
    }

    pub fn len(&self) -> usize {
        self.entries.read().len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    pub fn hits(&self) -> u64 {
        self.hits.load(Ordering::Relaxed)
    }

    pub fn misses(&self) -> u64 {
        self.misses.load(Ordering::Relaxed)
    }
}

/// Where a backend sits in the execution order. Cheapest first.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Backend {
    Win32,
    Uia,
    Input,
    Cdp,
}

impl Backend {
    pub const fn as_str(self) -> &'static str {
        match self {
            Backend::Win32 => "win32",
            Backend::Uia => "uia",
            Backend::Input => "input",
            Backend::Cdp => "cdp",
        }
    }
}

/// Per-application knowledge (Phase 13 seed, already wired into the router).
#[derive(Debug, Clone, Copy)]
pub struct AppProfile {
    pub name: &'static str,
    pub process: &'static str,
    /// Backend for the application chrome (title bar, menus, dialogs).
    pub shell_backend: Backend,
    /// Backend for the document/content area.
    pub content_backend: Backend,
    pub aliases: &'static [&'static str],
    /// Window titles that usually mean "still loading".
    pub busy_hints: &'static [&'static str],
}

pub const PROFILES: &[AppProfile] = &[
    AppProfile {
        name: "notepad",
        process: "notepad.exe",
        shell_backend: Backend::Win32,
        content_backend: Backend::Uia,
        aliases: &["notepad", "not defteri"],
        busy_hints: &[],
    },
    AppProfile {
        name: "explorer",
        process: "explorer.exe",
        shell_backend: Backend::Win32,
        content_backend: Backend::Uia,
        aliases: &["explorer", "files", "dosya gezgini"],
        busy_hints: &["working on it", "hesaplanıyor"],
    },
    AppProfile {
        name: "chrome",
        process: "chrome.exe",
        shell_backend: Backend::Uia,
        // Content must go through CDP, never UIA.
        content_backend: Backend::Cdp,
        aliases: &["chrome", "google chrome"],
        busy_hints: &["loading"],
    },
    AppProfile {
        name: "edge",
        process: "msedge.exe",
        shell_backend: Backend::Uia,
        content_backend: Backend::Cdp,
        aliases: &["edge", "msedge"],
        busy_hints: &["loading"],
    },
    AppProfile {
        name: "calculator",
        process: "calculator.exe",
        shell_backend: Backend::Win32,
        content_backend: Backend::Uia,
        aliases: &["calc", "calculator", "hesap makinesi"],
        busy_hints: &[],
    },
    AppProfile {
        name: "vscode",
        process: "code.exe",
        shell_backend: Backend::Win32,
        content_backend: Backend::Uia,
        aliases: &["code", "vscode"],
        busy_hints: &[],
    },
    AppProfile {
        name: "blender",
        process: "blender.exe",
        shell_backend: Backend::Win32,
        content_backend: Backend::Uia,
        aliases: &["blender"],
        busy_hints: &[],
    },
];

pub fn profile_for_process(process: &str) -> Option<&'static AppProfile> {
    let p = process.to_ascii_lowercase();
    PROFILES.iter().find(|prof| prof.process == p)
}

pub fn profile_for_app(app: &str) -> Option<&'static AppProfile> {
    let a = app.to_ascii_lowercase();
    let a = a.trim_end_matches(".exe");
    PROFILES
        .iter()
        .find(|p| p.name == a || p.aliases.iter().any(|alias| *alias == a))
}

/// Counters surfaced through `status`.
#[derive(Debug, Default)]
pub struct Counters {
    pub requests: AtomicU64,
    pub failures: AtomicU64,
    pub plans: AtomicU64,
    pub steps: AtomicU64,
    pub actions: AtomicU64,
    pub uia_calls: AtomicU64,
    pub win32_calls: AtomicU64,
    pub stale: AtomicU64,
    pub denials: AtomicU64,
    pub launched: AtomicU64,
    pub closed: AtomicU64,
    pub native_requests: AtomicU64,
    pub mcp_requests: AtomicU64,
    pub shared_memory_requests: AtomicU64,
    pub native_run_plans: AtomicU64,
}

impl Counters {
    pub fn snapshot(
        &self,
        cache_hits: u64,
        cache_misses: u64,
    ) -> inbrisk_protocol::response::Counters {
        use std::sync::atomic::Ordering::Relaxed;
        inbrisk_protocol::response::Counters {
            requests_total: self.requests.load(Relaxed),
            requests_failed: self.failures.load(Relaxed),
            plans_total: self.plans.load(Relaxed),
            steps_total: self.steps.load(Relaxed),
            actions_total: self.actions.load(Relaxed),
            uia_calls: self.uia_calls.load(Relaxed),
            win32_calls: self.win32_calls.load(Relaxed),
            cache_hits,
            cache_misses,
            stale_rejections: self.stale.load(Relaxed),
            denials: self.denials.load(Relaxed),
            native_requests_total: self.native_requests.load(Relaxed),
            mcp_requests_total: self.mcp_requests.load(Relaxed),
            shared_memory_requests_total: self.shared_memory_requests.load(Relaxed),
            native_run_plans_total: self.native_run_plans.load(Relaxed),
        }
    }
}

/// Everything the runtime knows.
#[derive(Debug)]
pub struct RuntimeState {
    pub world: WorldState,
    pub ownership: RwLock<OwnershipRegistry>,
    pub activity: Arc<ActivityManager>,
    pub elements: ElementCache,
    pub counters: Counters,
    pub started: Instant,
    /// Windows this runtime launched, by process id.
    pub launched: RwLock<HashMap<u32, LaunchRecord>>,
}

#[derive(Debug, Clone)]
pub struct LaunchRecord {
    pub pid: u32,
    pub app: String,
    pub session_id: SessionId,
    pub intent: LifecycleIntent,
    pub launched_at_ms: u64,
    pub hwnds: Vec<Hwnd>,
}

impl RuntimeState {
    pub fn new(activity: Arc<ActivityManager>) -> Self {
        Self {
            world: WorldState::new(),
            ownership: RwLock::new(OwnershipRegistry::new()),
            activity,
            elements: ElementCache::new(),
            counters: Counters::default(),
            started: Instant::now(),
            launched: RwLock::new(HashMap::new()),
        }
    }

    pub fn state_version(&self) -> StateVersion {
        self.world.version()
    }

    pub fn generation(&self) -> u64 {
        self.world.generation()
    }

    pub fn uptime_ms(&self) -> u64 {
        self.started.elapsed().as_millis() as u64
    }

    /// Apply ownership + profile information to a raw window.
    pub fn decorate(&self, raw: &inbrisk_win32::RawWindow) -> WindowRef {
        let registry = self.ownership.read();
        let ownership = registry.ownership_of(raw.hwnd);
        let intent = registry.intent_of(raw.hwnd);
        WindowRef {
            hwnd: raw.hwnd,
            process_id: raw.process_id,
            process_name: raw.process_name.clone(),
            title: raw.title.clone(),
            class_name: raw.class_name.clone(),
            bounds: raw.bounds,
            visible: raw.visible,
            minimized: raw.minimized,
            maximized: raw.maximized,
            foreground: raw.foreground,
            tool_window: raw.tool_window,
            ownership,
            intent,
            state_version: self.world.version(),
        }
    }

    pub fn stamp_ownership(&self, stamp: OwnershipStamp) {
        self.ownership.write().stamp(stamp);
    }

    pub fn ownership_of(&self, hwnd: Hwnd) -> Ownership {
        self.ownership.read().ownership_of(hwnd)
    }

    pub fn intent_of(&self, hwnd: Hwnd) -> LifecycleIntent {
        self.ownership.read().intent_of(hwnd)
    }

    pub fn stamp_for(&self, hwnd: Hwnd) -> Option<OwnershipStamp> {
        self.ownership.read().get(hwnd).cloned()
    }

    pub fn mark_user_takeover(&self, hwnd: Hwnd) {
        self.ownership.write().mark_user_takeover(hwnd);
    }

    pub fn mark_explicit_user_close(&self, hwnd: Hwnd) {
        self.ownership.write().mark_explicit_user_close(hwnd);
    }

    pub fn forget_window(&self, hwnd: Hwnd) {
        self.ownership.write().forget(hwnd);
    }

    /// Registered protected windows (including ones we only learned about).
    pub fn ensure_protected(&self, policy: &inbrisk_policy::PolicyEngine) -> usize {
        let raws = inbrisk_win32::window::enumerate_top_level();
        let mut registry = self.ownership.write();
        let mut added = 0;
        for raw in &raws {
            if registry.get(raw.hwnd).is_some() {
                continue;
            }
            let stamp = policy.classify(
                &WindowRef {
                    hwnd: raw.hwnd,
                    process_id: raw.process_id,
                    process_name: raw.process_name.clone(),
                    title: raw.title.clone(),
                    class_name: raw.class_name.clone(),
                    bounds: raw.bounds,
                    visible: raw.visible,
                    minimized: raw.minimized,
                    maximized: raw.maximized,
                    foreground: raw.foreground,
                    tool_window: raw.tool_window,
                    ownership: Ownership::Unknown,
                    intent: LifecycleIntent::Unknown,
                    state_version: self.world.version(),
                },
                false,
            );
            registry.stamp(stamp);
            added += 1;
        }
        added
    }

    /// Drop ownership records for windows that no longer exist.
    pub fn prune(&self) {
        let mut registry = self.ownership.write();
        registry.retain_live(&|hwnd| inbrisk_win32::window::is_alive(hwnd));
    }
}
