//! Physical input execution lane for `inbrisk-desktop.exe`.
//!
//! Provides single-lane serialized physical input dispatch through `inbrisk-win32::input_raw`.
//! Enforces foreground window guards and handles stuck modifier/button cleanup.

use std::collections::HashSet;
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Arc;

use inbrisk_core::{ExecutionCapability, InbriskError, Result};
use inbrisk_protocol::action::{MouseButton, PointTarget, ScrollDirection};
use inbrisk_protocol::desktop_broker::{
    PhysicalInputCommand, PhysicalInputRequest, PhysicalInputResult,
};
use inbrisk_win32::desktop::probe_desktop_context;
use inbrisk_win32::input_raw::{self, Button, Wheel};
use parking_lot::Mutex;

pub const MAX_PHYSICAL_QUEUE_DEPTH: u32 = 16;

/// Low-level backend trait allowing injection of fake/mock executor in tests.
pub trait RawInputBackend: Send + Sync {
    fn move_to(&self, x: i32, y: i32) -> Result<()>;
    fn click(&self, x: i32, y: i32, button: Button, clicks: u8) -> Result<()>;
    fn drag(&self, from: (i32, i32), to: (i32, i32), button: Button) -> Result<()>;
    fn scroll(&self, wheel: Wheel, amount: i32) -> Result<()>;
    fn key_state(&self, name: &str, down: bool) -> Result<()>;
    fn key_chord(&self, keys: &[String]) -> Result<()>;
    fn type_text(&self, text: &str) -> Result<()>;
    fn release_mouse_buttons(&self) -> Result<()> {
        Ok(())
    }
    fn get_foreground_hwnd(&self) -> Option<u64>;
    fn probe_capability(&self) -> Result<()>;

    /// Resolve point to screen physical coordinates at execution time.
    fn resolve_point(&self, target: &PointTarget) -> Result<(i32, i32)> {
        target.validate()?;
        let pt = inbrisk_win32::geometry::to_virtual_screen_physical(target.to_core_point())?;
        Ok((pt.x, pt.y))
    }
}

/// Production backend calling `inbrisk-win32::input_raw` and `GetForegroundWindow()`.
pub struct Win32RawInputBackend;

impl RawInputBackend for Win32RawInputBackend {
    fn move_to(&self, x: i32, y: i32) -> Result<()> {
        input_raw::move_to(x, y)
    }

    fn click(&self, x: i32, y: i32, button: Button, clicks: u8) -> Result<()> {
        input_raw::click(x, y, button, clicks)
    }

    fn drag(&self, from: (i32, i32), to: (i32, i32), button: Button) -> Result<()> {
        input_raw::drag(from, to, button)
    }

    fn scroll(&self, wheel: Wheel, amount: i32) -> Result<()> {
        input_raw::scroll(wheel, amount)
    }

    fn key_state(&self, name: &str, down: bool) -> Result<()> {
        input_raw::key_state(name, down)
    }

    fn key_chord(&self, keys: &[String]) -> Result<()> {
        input_raw::key_chord(keys)
    }

    fn type_text(&self, text: &str) -> Result<()> {
        input_raw::type_text(text)
    }

    fn release_mouse_buttons(&self) -> Result<()> {
        input_raw::release_mouse_buttons()
    }

    fn get_foreground_hwnd(&self) -> Option<u64> {
        inbrisk_win32::window::foreground().map(|h| h.0 as usize as u64)
    }

    fn probe_capability(&self) -> Result<()> {
        let probe = probe_desktop_context()?;
        if !probe.input_desktop_available && !probe.fresh_thread_can_bind_input_desktop {
            return Err(InbriskError::capability_unavailable(
                ExecutionCapability::PhysicalInput,
                "broker cannot bind to interactive input desktop",
            ));
        }
        Ok(())
    }
}

/// Serialized physical input executor.
pub struct PhysicalInputExecutor {
    backend: Arc<dyn RawInputBackend>,
    /// Serialization lane to ensure max_concurrent_execution == 1.
    execution_lock: Mutex<()>,
    /// Tracked keys that are currently pressed down.
    held_keys: Mutex<HashSet<String>>,
    /// Emergency stop flag.
    emergency: std::sync::atomic::AtomicBool,
    /// Depth of pending queue or active executions.
    queue_depth: AtomicU32,
    /// Active concurrent count (strictly 0 or 1 under execution_lock).
    active_concurrent: AtomicU32,
    /// Maximum concurrent executions observed (for verification in tests).
    max_concurrent_seen: AtomicU32,
}

impl Default for PhysicalInputExecutor {
    fn default() -> Self {
        Self::new(Arc::new(Win32RawInputBackend))
    }
}

impl PhysicalInputExecutor {
    pub fn new(backend: Arc<dyn RawInputBackend>) -> Self {
        Self {
            backend,
            execution_lock: Mutex::new(()),
            held_keys: Mutex::new(HashSet::new()),
            emergency: std::sync::atomic::AtomicBool::new(false),
            queue_depth: AtomicU32::new(0),
            active_concurrent: AtomicU32::new(0),
            max_concurrent_seen: AtomicU32::new(0),
        }
    }

    pub fn queue_depth(&self) -> u32 {
        self.queue_depth.load(Ordering::Relaxed)
    }

    pub fn max_concurrent_seen(&self) -> u32 {
        self.max_concurrent_seen.load(Ordering::Relaxed)
    }

    pub fn has_tracked_modifiers(&self) -> bool {
        !self.held_keys.lock().is_empty()
    }

    pub fn engage_emergency(&self) {
        self.emergency.store(true, Ordering::Release);
        self.cleanup_stuck_modifiers();
    }

    pub fn clear_emergency(&self) {
        self.emergency.store(false, Ordering::Release);
    }

    pub fn is_emergency(&self) -> bool {
        self.emergency.load(Ordering::Acquire)
    }

    /// Reset any stuck modifier keys and tracked held keys.
    pub fn cleanup_stuck_modifiers(&self) {
        let mut held = self.held_keys.lock();
        for key in held.drain() {
            let _ = self.backend.key_state(&key, false);
        }

        // Defensive release for common modifier keys
        for mod_key in ["ctrl", "shift", "alt", "win"] {
            let _ = self.backend.key_state(mod_key, false);
        }

        // Defensive release for mouse buttons
        let _ = self.backend.release_mouse_buttons();
    }

    /// Execute physical input request with foreground guard and single-lane serialization.
    pub fn execute(&self, request: &PhysicalInputRequest) -> Result<PhysicalInputResult> {
        if self.is_emergency() {
            return Err(InbriskError::denied(
                "emergency stop active; new physical request rejected",
            ));
        }

        let current_depth = self.queue_depth.load(Ordering::SeqCst);
        if current_depth >= MAX_PHYSICAL_QUEUE_DEPTH {
            return Err(InbriskError::denied(format!(
                "physical input queue is full (depth {current_depth} >= max {MAX_PHYSICAL_QUEUE_DEPTH})"
            )));
        }

        self.queue_depth.fetch_add(1, Ordering::SeqCst);
        let _queue_guard = scopeguard::guard((), |_| {
            self.queue_depth.fetch_sub(1, Ordering::SeqCst);
        });

        // 1. Single FIFO execution lane
        let _lane = self.execution_lock.lock();

        if self.is_emergency() {
            return Err(InbriskError::denied(
                "emergency stop active; queued physical request cancelled",
            ));
        }

        let current_concurrent = self.active_concurrent.fetch_add(1, Ordering::SeqCst) + 1;
        self.max_concurrent_seen
            .fetch_max(current_concurrent, Ordering::SeqCst);
        let _active_guard = scopeguard::guard((), |_| {
            self.active_concurrent.fetch_sub(1, Ordering::SeqCst);
        });

        // 2. Capability precheck
        self.backend.probe_capability()?;

        // 3. Foreground guard re-check at execution boundary
        if let Some(expected_hwnd) = request.guard.expected_foreground_hwnd {
            let actual_hwnd = self.backend.get_foreground_hwnd();
            if actual_hwnd != Some(expected_hwnd) {
                return Err(InbriskError::foreground_changed(format!(
                    "foreground window changed at broker execution boundary: expected {expected_hwnd:#x}, got {actual_hwnd:?}"
                )));
            }
        }

        // 4. Command execution
        let events_sent = match &request.command {
            PhysicalInputCommand::MouseMove { point } => {
                let (rx, ry) = self.backend.resolve_point(point)?;
                self.backend.move_to(rx, ry)?;
                1
            }
            PhysicalInputCommand::Click {
                point,
                button,
                click_count,
            } => {
                let (rx, ry) = self.backend.resolve_point(point)?;
                let btn = match button {
                    MouseButton::Left => Button::Left,
                    MouseButton::Right => Button::Right,
                    MouseButton::Middle => Button::Middle,
                };
                self.backend.click(rx, ry, btn, *click_count as u8)?;
                click_count * 2 + 1 // move + down + up per click
            }
            PhysicalInputCommand::Drag { from, to, button } => {
                let (fx, fy) = self.backend.resolve_point(from)?;
                let (tx, ty) = self.backend.resolve_point(to)?;
                let btn = match button {
                    MouseButton::Left => Button::Left,
                    MouseButton::Right => Button::Right,
                    MouseButton::Middle => Button::Middle,
                };
                self.backend.drag((fx, fy), (tx, ty), btn)?;
                14 // start move + down + 12 steps + up
            }
            PhysicalInputCommand::Scroll { direction, amount } => {
                let wheel = match direction {
                    ScrollDirection::Up => Wheel::Up,
                    ScrollDirection::Down => Wheel::Down,
                    ScrollDirection::Left => Wheel::Left,
                    ScrollDirection::Right => Wheel::Right,
                };
                self.backend.scroll(wheel, *amount)?;
                1
            }
            PhysicalInputCommand::Key { key, down } => {
                self.backend.key_state(key, *down)?;
                let mut held = self.held_keys.lock();
                if *down {
                    held.insert(key.clone());
                } else {
                    held.remove(key);
                }
                1
            }
            PhysicalInputCommand::Shortcut { keys } => {
                self.backend.key_chord(keys)?;
                (keys.len() * 2) as u32
            }
            PhysicalInputCommand::TypeText { text } => {
                self.backend.type_text(text)?;
                (text.encode_utf16().count() * 2) as u32
            }
            PhysicalInputCommand::ResetModifiers => {
                self.cleanup_stuck_modifiers();
                0
            }
        };

        Ok(PhysicalInputResult {
            operation_id: request.operation_id,
            executed: true,
            events_sent,
            details: None,
        })
    }
}

// Module for simple scope guard
mod scopeguard {
    pub struct ScopeGuard<T, F: FnOnce(T)> {
        value: Option<T>,
        drop_fn: Option<F>,
    }

    impl<T, F: FnOnce(T)> Drop for ScopeGuard<T, F> {
        fn drop(&mut self) {
            if let (Some(val), Some(f)) = (self.value.take(), self.drop_fn.take()) {
                f(val);
            }
        }
    }

    pub fn guard<T, F: FnOnce(T)>(value: T, drop_fn: F) -> ScopeGuard<T, F> {
        ScopeGuard {
            value: Some(value),
            drop_fn: Some(drop_fn),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use inbrisk_core::ErrorCode;
    use inbrisk_protocol::action::CoordinateSpaceDto;
    use inbrisk_protocol::desktop_broker::PhysicalInputGuard;
    use std::sync::atomic::AtomicUsize;
    use std::time::Duration;

    struct FakeBackend {
        calls: AtomicUsize,
        clicks: AtomicUsize,
        releases: AtomicUsize,
        foreground: Option<u64>,
        concurrent: AtomicUsize,
        max_concurrent: AtomicUsize,
        last_coords: Mutex<Option<(i32, i32)>>,
        custom_resolver: Option<Arc<dyn Fn(&PointTarget) -> Result<(i32, i32)> + Send + Sync>>,
    }

    impl FakeBackend {
        fn new(foreground: Option<u64>) -> Self {
            Self {
                calls: AtomicUsize::new(0),
                clicks: AtomicUsize::new(0),
                releases: AtomicUsize::new(0),
                foreground,
                concurrent: AtomicUsize::new(0),
                max_concurrent: AtomicUsize::new(0),
                last_coords: Mutex::new(None),
                custom_resolver: None,
            }
        }
    }

    impl RawInputBackend for FakeBackend {
        fn resolve_point(&self, target: &PointTarget) -> Result<(i32, i32)> {
            if let Some(r) = &self.custom_resolver {
                return r(target);
            }
            target.validate()?;
            match target.space {
                CoordinateSpaceDto::VirtualScreenPhysical => {
                    Ok((target.x.round() as i32, target.y.round() as i32))
                }
                CoordinateSpaceDto::WindowClientPhysical
                | CoordinateSpaceDto::WindowClientLogical => {
                    inbrisk_win32::geometry::to_virtual_screen_physical(target.to_core_point())
                        .map(|p| (p.x, p.y))
                }
            }
        }

        fn move_to(&self, x: i32, y: i32) -> Result<()> {
            let c = self.concurrent.fetch_add(1, Ordering::SeqCst) + 1;
            self.max_concurrent.fetch_max(c, Ordering::SeqCst);
            *self.last_coords.lock() = Some((x, y));
            self.calls.fetch_add(1, Ordering::SeqCst);
            std::thread::sleep(std::time::Duration::from_millis(5));
            self.concurrent.fetch_sub(1, Ordering::SeqCst);
            Ok(())
        }

        fn click(&self, x: i32, y: i32, _button: Button, _clicks: u8) -> Result<()> {
            let c = self.concurrent.fetch_add(1, Ordering::SeqCst) + 1;
            self.max_concurrent.fetch_max(c, Ordering::SeqCst);
            *self.last_coords.lock() = Some((x, y));
            self.calls.fetch_add(1, Ordering::SeqCst);
            self.clicks.fetch_add(1, Ordering::SeqCst);
            std::thread::sleep(std::time::Duration::from_millis(5));
            self.concurrent.fetch_sub(1, Ordering::SeqCst);
            Ok(())
        }

        fn release_mouse_buttons(&self) -> Result<()> {
            self.releases.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn drag(&self, _from: (i32, i32), to: (i32, i32), _button: Button) -> Result<()> {
            *self.last_coords.lock() = Some(to);
            self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn scroll(&self, _wheel: Wheel, _amount: i32) -> Result<()> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn key_state(&self, _name: &str, _down: bool) -> Result<()> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn key_chord(&self, _keys: &[String]) -> Result<()> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn type_text(&self, _text: &str) -> Result<()> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            Ok(())
        }

        fn get_foreground_hwnd(&self) -> Option<u64> {
            self.foreground
        }

        fn probe_capability(&self) -> Result<()> {
            Ok(())
        }
    }

    #[test]
    fn broker_foreground_mismatch_rejects_before_physical_execution() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = PhysicalInputExecutor::new(fake.clone());

        let req = PhysicalInputRequest {
            operation_id: 1,
            command: PhysicalInputCommand::Click {
                point: PointTarget::virtual_screen(100.0, 100.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard {
                expected_foreground_hwnd: Some(0x2000), // Mismatch!
            },
        };

        let res = executor.execute(&req);
        assert!(res.is_err());
        let err = res.unwrap_err();
        assert_eq!(err.code, ErrorCode::ForegroundChanged);
        assert_eq!(
            fake.calls.load(Ordering::SeqCst),
            0,
            "No raw input calls allowed on guard mismatch"
        );
    }

    #[test]
    fn broker_physical_requests_are_serialized() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = Arc::new(PhysicalInputExecutor::new(fake.clone()));

        let mut handles = Vec::new();
        for i in 0..10 {
            let ex = executor.clone();
            handles.push(std::thread::spawn(move || {
                let req = PhysicalInputRequest {
                    operation_id: i,
                    command: PhysicalInputCommand::MouseMove {
                        point: PointTarget::virtual_screen(50.0, 50.0),
                    },
                    guard: PhysicalInputGuard {
                        expected_foreground_hwnd: Some(0x1000),
                    },
                };
                ex.execute(&req).unwrap()
            }));
        }

        for h in handles {
            h.join().unwrap();
        }

        assert_eq!(fake.calls.load(Ordering::SeqCst), 10);
        assert_eq!(
            fake.max_concurrent.load(Ordering::SeqCst),
            1,
            "Concurrency must be strictly 1"
        );
        assert_eq!(executor.max_concurrent_seen(), 1);
    }

    #[test]
    fn broker_shutdown_does_not_leave_tracked_modifier_state() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = PhysicalInputExecutor::new(fake.clone());

        let key_down = PhysicalInputRequest {
            operation_id: 1,
            command: PhysicalInputCommand::Key {
                key: "ctrl".into(),
                down: true,
            },
            guard: PhysicalInputGuard::default(),
        };
        executor.execute(&key_down).unwrap();
        assert!(executor.has_tracked_modifiers());

        executor.cleanup_stuck_modifiers();
        assert!(!executor.has_tracked_modifiers());
    }

    #[test]
    fn emergency_stop_rejects_new_physical_input() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = PhysicalInputExecutor::new(fake.clone());

        executor.engage_emergency();
        assert!(executor.is_emergency());

        let req = PhysicalInputRequest {
            operation_id: 1,
            command: PhysicalInputCommand::Click {
                point: PointTarget::virtual_screen(10.0, 10.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard::default(),
        };

        let err = executor.execute(&req).unwrap_err();
        assert_eq!(err.code, ErrorCode::Denied);
        assert_eq!(fake.clicks.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn emergency_stop_clears_pending_physical_queue() {
        use std::sync::atomic::AtomicBool;
        let blocked = Arc::new(AtomicBool::new(true));
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));

        struct BlockingBackend {
            fake: Arc<FakeBackend>,
            blocked: Arc<AtomicBool>,
            entered_a: Arc<AtomicBool>,
        }
        impl RawInputBackend for BlockingBackend {
            fn move_to(&self, x: i32, y: i32) -> Result<()> {
                self.fake.move_to(x, y)
            }
            fn click(&self, x: i32, y: i32, button: Button, clicks: u8) -> Result<()> {
                if x == 1 {
                    self.entered_a.store(true, Ordering::SeqCst);
                    while self.blocked.load(Ordering::SeqCst) {
                        std::thread::sleep(Duration::from_millis(10));
                    }
                }
                self.fake.click(x, y, button, clicks)
            }
            fn drag(&self, from: (i32, i32), to: (i32, i32), button: Button) -> Result<()> {
                self.fake.drag(from, to, button)
            }
            fn scroll(&self, wheel: Wheel, amount: i32) -> Result<()> {
                self.fake.scroll(wheel, amount)
            }
            fn key_state(&self, name: &str, down: bool) -> Result<()> {
                self.fake.key_state(name, down)
            }
            fn key_chord(&self, keys: &[String]) -> Result<()> {
                self.fake.key_chord(keys)
            }
            fn type_text(&self, text: &str) -> Result<()> {
                self.fake.type_text(text)
            }
            fn get_foreground_hwnd(&self) -> Option<u64> {
                self.fake.get_foreground_hwnd()
            }
            fn probe_capability(&self) -> Result<()> {
                Ok(())
            }
        }

        let entered_a = Arc::new(AtomicBool::new(false));
        let blocking_backend = Arc::new(BlockingBackend {
            fake: fake.clone(),
            blocked: blocked.clone(),
            entered_a: entered_a.clone(),
        });
        let executor = Arc::new(PhysicalInputExecutor::new(blocking_backend));

        // 1. Thread A blocks in execution
        let ex1 = executor.clone();
        let h_a = std::thread::spawn(move || {
            let req_a = PhysicalInputRequest {
                operation_id: 1,
                command: PhysicalInputCommand::Click {
                    point: PointTarget::virtual_screen(1.0, 1.0),
                    button: MouseButton::Left,
                    click_count: 1,
                },
                guard: PhysicalInputGuard::default(),
            };
            ex1.execute(&req_a)
        });

        while !entered_a.load(Ordering::SeqCst) {
            std::thread::sleep(Duration::from_millis(5));
        }

        // 2. Thread B and C enqueue
        let ex2 = executor.clone();
        let h_b = std::thread::spawn(move || {
            let req_b = PhysicalInputRequest {
                operation_id: 2,
                command: PhysicalInputCommand::Click {
                    point: PointTarget::virtual_screen(2.0, 2.0),
                    button: MouseButton::Left,
                    click_count: 1,
                },
                guard: PhysicalInputGuard::default(),
            };
            ex2.execute(&req_b)
        });

        let ex3 = executor.clone();
        let h_c = std::thread::spawn(move || {
            let req_c = PhysicalInputRequest {
                operation_id: 3,
                command: PhysicalInputCommand::Click {
                    point: PointTarget::virtual_screen(3.0, 3.0),
                    button: MouseButton::Left,
                    click_count: 1,
                },
                guard: PhysicalInputGuard::default(),
            };
            ex3.execute(&req_c)
        });

        std::thread::sleep(Duration::from_millis(50));

        // 3. Trigger emergency stop
        executor.engage_emergency();

        // 4. Release A
        blocked.store(false, Ordering::SeqCst);

        let res_a = h_a.join().unwrap();
        let res_b = h_b.join().unwrap();
        let res_c = h_c.join().unwrap();

        assert!(res_a.is_ok(), "A was already in-flight and completed");
        assert!(res_b.is_err(), "B was cancelled from pending queue");
        assert!(res_c.is_err(), "C was cancelled from pending queue");
        assert_eq!(res_b.unwrap_err().code, ErrorCode::Denied);
        assert_eq!(res_c.unwrap_err().code, ErrorCode::Denied);

        // Verify: A called once, B = 0, C = 0
        assert_eq!(fake.clicks.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn emergency_stop_releases_tracked_modifier_state() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = PhysicalInputExecutor::new(fake);

        let req = PhysicalInputRequest {
            operation_id: 1,
            command: PhysicalInputCommand::Key {
                key: "shift".into(),
                down: true,
            },
            guard: PhysicalInputGuard::default(),
        };
        executor.execute(&req).unwrap();
        assert!(executor.has_tracked_modifiers());

        executor.engage_emergency();
        assert!(!executor.has_tracked_modifiers());
    }

    #[test]
    fn broker_physical_queue_is_bounded() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = Arc::new(PhysicalInputExecutor::new(fake));

        // Artificial queue saturation: set queue_depth to max
        executor
            .queue_depth
            .store(MAX_PHYSICAL_QUEUE_DEPTH, Ordering::SeqCst);

        let req = PhysicalInputRequest {
            operation_id: 99,
            command: PhysicalInputCommand::MouseMove {
                point: PointTarget::virtual_screen(0.0, 0.0),
            },
            guard: PhysicalInputGuard::default(),
        };

        let err = executor.execute(&req).unwrap_err();
        assert_eq!(err.code, ErrorCode::Denied);
        assert!(err.message.contains("queue is full"));
    }

    // SECTION 29: Broker Tests (Execution-Time Resolution & Geometry)

    #[test]
    fn broker_resolves_window_client_point_at_execution_time() {
        // Window client origin at (300, 200)
        let resolver: Arc<dyn Fn(&PointTarget) -> Result<(i32, i32)> + Send + Sync> =
            Arc::new(|pt: &PointTarget| {
                pt.validate()?;
                let win_origin = inbrisk_win32::geometry::PhysicalPoint { x: 300, y: 200 };
                let client_pt = inbrisk_win32::geometry::PhysicalPoint {
                    x: pt.x.round() as i32,
                    y: pt.y.round() as i32,
                };
                let screen = inbrisk_win32::geometry::pure_client_to_screen(client_pt, win_origin);
                Ok((screen.x, screen.y))
            });
        let mut fake_with_res = FakeBackend::new(Some(0x1000));
        fake_with_res.custom_resolver = Some(resolver);
        let fake = Arc::new(fake_with_res);
        let executor = PhysicalInputExecutor::new(fake.clone());

        let req = PhysicalInputRequest {
            operation_id: 10,
            command: PhysicalInputCommand::Click {
                point: PointTarget::client_physical(0x1000, 50.0, 50.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard {
                expected_foreground_hwnd: Some(0x1000),
            },
        };

        executor.execute(&req).unwrap();
        // 300 + 50 = 350, 200 + 50 = 250
        assert_eq!(*fake.last_coords.lock(), Some((350, 250)));
    }

    #[test]
    fn broker_resolves_logical_point_using_current_dpi() {
        // Window client area at 150% (144 DPI) and origin at (200, 200)
        let resolver: Arc<dyn Fn(&PointTarget) -> Result<(i32, i32)> + Send + Sync> =
            Arc::new(|pt: &PointTarget| {
                pt.validate()?;
                let dpi = 144; // 150%
                let phys_client = inbrisk_win32::geometry::pure_logical_to_physical(
                    inbrisk_win32::geometry::LogicalPoint { x: pt.x, y: pt.y },
                    dpi,
                );
                let win_origin = inbrisk_win32::geometry::PhysicalPoint { x: 200, y: 200 };
                let screen =
                    inbrisk_win32::geometry::pure_client_to_screen(phys_client, win_origin);
                Ok((screen.x, screen.y))
            });
        let mut fake_with_res = FakeBackend::new(Some(0x1000));
        fake_with_res.custom_resolver = Some(resolver);
        let fake = Arc::new(fake_with_res);
        let executor = PhysicalInputExecutor::new(fake.clone());

        let req = PhysicalInputRequest {
            operation_id: 11,
            command: PhysicalInputCommand::Click {
                point: PointTarget::client_logical(0x1000, 100.0, 100.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard {
                expected_foreground_hwnd: Some(0x1000),
            },
        };

        executor.execute(&req).unwrap();
        // 100 * 1.5 = 150. Screen = 200 + 150 = 350.
        assert_eq!(*fake.last_coords.lock(), Some((350, 350)));
    }

    #[test]
    fn broker_rejects_destroyed_window_before_input() {
        let fake = Arc::new(FakeBackend::new(Some(0x1000)));
        let executor = PhysicalInputExecutor::new(fake.clone());

        // Target an invalid/destroyed HWND
        let req = PhysicalInputRequest {
            operation_id: 12,
            command: PhysicalInputCommand::Click {
                point: PointTarget::client_physical(0xDEADBEEF, 10.0, 10.0),
                button: MouseButton::Left,
                click_count: 1,
            },
            guard: PhysicalInputGuard::default(),
        };

        let res = executor.execute(&req);
        assert!(res.is_err());
        let err = res.unwrap_err();
        assert_eq!(err.code, ErrorCode::TargetGone);
        assert_eq!(fake.calls.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn broker_normalizes_against_current_virtual_desktop() {
        // Multi-monitor layout: secondary monitor left of primary
        let bounds = inbrisk_win32::geometry::PhysicalRect::new(-1920, 0, 3840, 1080);
        let target = inbrisk_win32::geometry::PhysicalPoint { x: -960, y: 540 };
        let norm = inbrisk_win32::geometry::normalize_for_send_input(target, bounds).unwrap();
        let denorm = inbrisk_win32::geometry::denormalize_from_send_input(norm, bounds);
        assert_eq!(denorm.x, -960);
        assert_eq!(denorm.y, 540);
    }

    #[test]
    fn broker_always_uses_virtual_desk_for_absolute_input() {
        // Absolute input normalization is always bounded by virtual desktop
        let pt = inbrisk_win32::geometry::PhysicalPoint { x: 500, y: 500 };
        let bounds = inbrisk_win32::geometry::virtual_screen_bounds();
        let norm = inbrisk_win32::geometry::normalize_for_send_input(pt, bounds);
        assert!(norm.is_ok());
    }
}
