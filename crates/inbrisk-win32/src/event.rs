//! Out-of-context WinEvent monitoring.
//!
//! This is the event source behind every `wait` step: the runtime learns about
//! window creation, destruction, focus change and name change the moment it
//! happens, instead of polling `EnumWindows` in a loop.

use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use std::sync::OnceLock;
use std::time::Duration;

use crossbeam_channel::{unbounded, Receiver, RecvTimeoutError, Sender, TryRecvError};
use inbrisk_core::{ErrorCode, Hwnd, InbriskError, Result};
use parking_lot::Mutex;
use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::{
    DispatchMessageW, GetMessageW, PostThreadMessageW, TranslateMessage, MSG, WM_QUIT,
};

use crate::from_hwnd;

/// Event object ids we care about.
pub const EVENT_SYSTEM_FOREGROUND: u32 = 0x0003;
pub const EVENT_SYSTEM_MINIMIZESTART: u32 = 0x0016;
pub const EVENT_SYSTEM_MINIMIZEEND: u32 = 0x0017;
pub const EVENT_OBJECT_CREATE: u32 = 0x8000;
pub const EVENT_OBJECT_DESTROY: u32 = 0x8001;
pub const EVENT_OBJECT_SHOW: u32 = 0x8002;
pub const EVENT_OBJECT_HIDE: u32 = 0x8003;
pub const EVENT_OBJECT_FOCUS: u32 = 0x8005;
pub const EVENT_OBJECT_STATECHANGE: u32 = 0x800A;
pub const EVENT_OBJECT_LOCATIONCHANGE: u32 = 0x800B;
pub const EVENT_OBJECT_NAMECHANGE: u32 = 0x800C;
pub const EVENT_OBJECT_VALUECHANGE: u32 = 0x800E;

const WINEVENT_OUTOFCONTEXT: u32 = 0x0000;
const WINEVENT_SKIPOWNPROCESS: u32 = 0x0002;

/// Normalized event kind so the wait service can match without magic numbers.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WinEventKind {
    ObjectCreate,
    ObjectDestroy,
    ObjectShow,
    ObjectHide,
    FocusChange,
    Foreground,
    NameChange,
    LocationChange,
    StateChange,
    ValueChange,
    MinimizeStart,
    MinimizeEnd,
    Other(u32),
}

impl WinEventKind {
    pub const fn from_raw(raw: u32) -> WinEventKind {
        match raw {
            EVENT_OBJECT_CREATE => WinEventKind::ObjectCreate,
            EVENT_OBJECT_DESTROY => WinEventKind::ObjectDestroy,
            EVENT_OBJECT_SHOW => WinEventKind::ObjectShow,
            EVENT_OBJECT_HIDE => WinEventKind::ObjectHide,
            EVENT_OBJECT_FOCUS => WinEventKind::FocusChange,
            EVENT_SYSTEM_FOREGROUND => WinEventKind::Foreground,
            EVENT_OBJECT_NAMECHANGE => WinEventKind::NameChange,
            EVENT_OBJECT_LOCATIONCHANGE => WinEventKind::LocationChange,
            EVENT_OBJECT_STATECHANGE => WinEventKind::StateChange,
            EVENT_OBJECT_VALUECHANGE => WinEventKind::ValueChange,
            EVENT_SYSTEM_MINIMIZESTART => WinEventKind::MinimizeStart,
            EVENT_SYSTEM_MINIMIZEEND => WinEventKind::MinimizeEnd,
            other => WinEventKind::Other(other),
        }
    }
}

#[derive(Debug, Clone, Copy)]
pub struct WinEvent {
    pub kind: WinEventKind,
    pub raw: u32,
    pub hwnd: Hwnd,
    /// `OBJID_WINDOW` (0) for top-level window events.
    pub object_id: i32,
    pub child_id: i32,
    pub thread_id: u32,
    pub time_ms: u32,
}

static SENDER: OnceLock<Mutex<Option<Sender<WinEvent>>>> = OnceLock::new();

fn sender_slot() -> &'static Mutex<Option<Sender<WinEvent>>> {
    SENDER.get_or_init(|| Mutex::new(None))
}

unsafe extern "system" fn win_event_proc(
    _hook: HWINEVENTHOOK,
    event: u32,
    hwnd: HWND,
    id_object: i32,
    id_child: i32,
    id_thread: u32,
    time: u32,
) {
    let ev = WinEvent {
        kind: WinEventKind::from_raw(event),
        raw: event,
        hwnd: from_hwnd(hwnd),
        object_id: id_object,
        child_id: id_child,
        thread_id: id_thread,
        time_ms: time,
    };
    if let Some(tx) = sender_slot().lock().as_ref() {
        // A full channel must never block the hook thread.
        let _ = tx.send(ev);
    }
}

/// A running WinEvent subscription.
#[derive(Debug)]
pub struct WinEventMonitor {
    rx: Receiver<WinEvent>,
    thread_id: u32,
    stop: std::sync::Arc<AtomicBool>,
    received: std::sync::Arc<AtomicU32>,
}

impl WinEventMonitor {
    /// Install the hooks on a dedicated thread with its own message loop.
    pub fn start() -> Result<Self> {
        let (tx, rx) = unbounded();
        *sender_slot().lock() = Some(tx);

        let thread_id = std::sync::Arc::new(AtomicU32::new(0));
        let ready = std::sync::Arc::new((Mutex::new(false), parking_lot::Condvar::new()));
        let stop = std::sync::Arc::new(AtomicBool::new(false));
        let received = std::sync::Arc::new(AtomicU32::new(0));

        {
            let thread_id = std::sync::Arc::clone(&thread_id);
            let ready = std::sync::Arc::clone(&ready);
            let stop = std::sync::Arc::clone(&stop);
            std::thread::Builder::new()
                .name("inbrisk-winevent".into())
                .spawn(move || unsafe {
                    let hooks: Vec<HWINEVENTHOOK> = vec![
                        SetWinEventHook(
                            EVENT_OBJECT_CREATE,
                            EVENT_OBJECT_HIDE,
                            None,
                            Some(win_event_proc),
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                        ),
                        SetWinEventHook(
                            EVENT_OBJECT_NAMECHANGE,
                            EVENT_OBJECT_NAMECHANGE,
                            None,
                            Some(win_event_proc),
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                        ),
                        SetWinEventHook(
                            EVENT_OBJECT_VALUECHANGE,
                            EVENT_OBJECT_VALUECHANGE,
                            None,
                            Some(win_event_proc),
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                        ),
                        SetWinEventHook(
                            EVENT_SYSTEM_FOREGROUND,
                            EVENT_SYSTEM_FOREGROUND,
                            None,
                            Some(win_event_proc),
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                        ),
                        SetWinEventHook(
                            EVENT_SYSTEM_MINIMIZESTART,
                            EVENT_SYSTEM_MINIMIZEEND,
                            None,
                            Some(win_event_proc),
                            0,
                            0,
                            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                        ),
                    ];

                    thread_id.store(
                        windows::Win32::System::Threading::GetCurrentThreadId(),
                        Ordering::Release,
                    );
                    {
                        let (lock, cv) = &*ready;
                        *lock.lock() = true;
                        cv.notify_all();
                    }

                    let mut msg = MSG::default();
                    while !stop.load(Ordering::Acquire) {
                        let r = GetMessageW(&mut msg, None, 0, 0);
                        if !r.as_bool() || msg.message == WM_QUIT {
                            break;
                        }
                        let _ = TranslateMessage(&msg);
                        let _ = DispatchMessageW(&msg);
                    }

                    for hook in hooks {
                        if !hook.is_invalid() {
                            let _ = UnhookWinEvent(hook);
                        }
                    }
                })
                .map_err(|e| {
                    InbriskError::internal(format!("cannot spawn winevent thread: {e}"))
                })?;
        }

        {
            let (lock, cv) = &*ready;
            let mut started = lock.lock();
            let deadline = std::time::Instant::now() + Duration::from_secs(5);
            while !*started {
                if std::time::Instant::now() > deadline {
                    return Err(InbriskError::new(
                        ErrorCode::Internal,
                        "winevent monitor did not start in time",
                    ));
                }
                let _ = cv.wait_for(&mut started, Duration::from_millis(200));
            }
        }

        Ok(Self {
            rx,
            thread_id: thread_id.load(Ordering::Acquire),
            stop,
            received,
        })
    }

    /// Non-blocking poll.
    pub fn try_recv(&self) -> Option<WinEvent> {
        match self.rx.try_recv() {
            Ok(e) => {
                self.received.fetch_add(1, Ordering::Relaxed);
                Some(e)
            }
            Err(TryRecvError::Empty) | Err(TryRecvError::Disconnected) => None,
        }
    }

    /// Block up to `timeout` for the next event.
    pub fn recv_timeout(&self, timeout: Duration) -> Option<WinEvent> {
        match self.rx.recv_timeout(timeout) {
            Ok(e) => {
                self.received.fetch_add(1, Ordering::Relaxed);
                Some(e)
            }
            Err(RecvTimeoutError::Timeout) | Err(RecvTimeoutError::Disconnected) => None,
        }
    }

    /// A cloneable stream handle, so the wait service can own its own receiver
    /// while the monitor keeps the hook thread alive.
    pub fn receiver(&self) -> Receiver<WinEvent> {
        self.rx.clone()
    }

    pub fn events_received(&self) -> u32 {
        self.received.load(Ordering::Relaxed)
    }

    pub fn thread_id(&self) -> u32 {
        self.thread_id
    }
}

impl Drop for WinEventMonitor {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        unsafe {
            let _ = PostThreadMessageW(self.thread_id, WM_QUIT, WPARAM(0), LPARAM(0));
        }
    }
}

/// Keeps the hook thread alive; dropping it posts `WM_QUIT`.

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_map_from_raw_values() {
        assert_eq!(
            WinEventKind::from_raw(EVENT_OBJECT_CREATE),
            WinEventKind::ObjectCreate
        );
        assert_eq!(
            WinEventKind::from_raw(EVENT_SYSTEM_FOREGROUND),
            WinEventKind::Foreground
        );
        assert_eq!(WinEventKind::from_raw(0x1234), WinEventKind::Other(0x1234));
    }
}
