//! Native status UI: tray icon + status window.
//!
//! Design rules (from the architecture contract):
//! * the tray is always available;
//! * the window is a *view* — it owns no state that matters, so closing it can
//!   never affect automation;
//! * every control maps to exactly one explicit user intent (pause, resume,
//!   emergency stop, exit). The agent has no way to click these.
//!
//! The floating activity pill, the perimeter wall and the yellow target border
//! live in `overlay` and read the same `UiState` this window renders.

pub mod overlay;
pub mod tray;
pub mod window;

use std::sync::atomic::{AtomicBool, AtomicIsize, Ordering};
use std::sync::Arc;

use inbrisk_core::{ActivityState, InbriskError, Result};
use parking_lot::Mutex;

/// Something only a human may do.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum UiCommand {
    Pause,
    Resume,
    EmergencyStop,
    ClearEmergency,
    ToggleWindow,
    Exit,
    ShutdownRuntime,
}

/// What the window shows. Cheap to clone; updated from the runtime thread.
#[derive(Debug, Clone, Default)]
pub struct UiState {
    pub headline: String,
    pub activity: Option<ActivityState>,
    pub body: String,
    pub tray_tip: String,
    pub emergency: bool,
    pub paused: bool,
    pub clients: u32,
    /// Live count from `ActivityManager`. Drives the pill, not a second flag.
    pub active_operations: u32,
    /// Window the yellow frame is bound to. Zero hides the frame.
    pub target_hwnd: isize,
    /// Current activity label, for example the plan name.
    pub label: String,
}

impl UiState {
    pub fn tip(&self) -> String {
        if self.tray_tip.is_empty() {
            "Inbrisk".to_string()
        } else {
            self.tray_tip.clone()
        }
    }
}

/// Shared context between the message loop and the runtime.
pub(crate) struct UiContext {
    pub state: Mutex<UiState>,
    pub on_command: Box<dyn Fn(UiCommand) + Send + Sync>,
    pub hwnd: AtomicIsize,
    pub shutting_down: AtomicBool,
}

static CONTEXT: once_cell::sync::OnceCell<Arc<UiContext>> = once_cell::sync::OnceCell::new();

pub(crate) fn context() -> Option<Arc<UiContext>> {
    CONTEXT.get().cloned()
}

/// Handle used by the runtime to drive the UI.
#[derive(Clone)]
pub struct UiHandle {
    context: Arc<UiContext>,
}

impl std::fmt::Debug for UiHandle {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UiHandle").finish()
    }
}

impl UiHandle {
    /// Install the UI context. The message loop has not started yet.
    pub fn prepare<F>(initial: UiState, on_command: F) -> Result<Self>
    where
        F: Fn(UiCommand) + Send + Sync + 'static,
    {
        let context = Arc::new(UiContext {
            state: Mutex::new(initial),
            on_command: Box::new(on_command),
            hwnd: AtomicIsize::new(0),
            shutting_down: AtomicBool::new(false),
        });
        CONTEXT
            .set(Arc::clone(&context))
            .map_err(|_| InbriskError::internal("the UI is already running in this process"))?;

        Ok(Self { context })
    }

    /// Pump the tray and the status window on this thread until exit.
    ///
    /// Call it from the process main thread. The runtime wait loop belongs on
    /// a worker; this thread has to stay in `GetMessage` or the tray menu sticks.
    pub fn run(&self, show: bool) {
        window::run_message_loop(show);
    }

    pub fn update<F: FnOnce(&mut UiState)>(&self, f: F) {
        {
            let mut state = self.context.state.lock();
            f(&mut state);
        }
        window::request_refresh();
    }

    pub fn set_state(&self, state: UiState) {
        *self.context.state.lock() = state;
        window::request_refresh();
    }

    pub fn state(&self) -> UiState {
        self.context.state.lock().clone()
    }

    /// Show the window (used by the tray "Open" item and by `inbrisk ui`).
    pub fn show(&self) {
        window::show_window(true);
    }

    pub fn hide(&self) {
        window::show_window(false);
    }

    pub fn hwnd(&self) -> isize {
        self.context.hwnd.load(Ordering::Acquire)
    }

    /// Ask the message loop to destroy the window and return.
    pub fn stop(&self) {
        request_exit();
    }
}

/// Exit the process UI. Closing the window with the title-bar button only
/// hides it; this is the path that actually ends the message loop.
pub fn request_exit() {
    if let Some(ctx) = context() {
        ctx.shutting_down.store(true, Ordering::Release);
    }
    window::request_quit();
}
