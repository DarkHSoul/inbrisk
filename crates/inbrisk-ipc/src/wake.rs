use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::System::Threading::{CreateEventW, ResetEvent, SetEvent, WaitForSingleObject};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WaitOutcome {
    /// The value at the address changed (or a wake was signalled).
    Woken,
    TimedOut,
    Failed,
}

/// A native Windows event object for zero-overhead, cross-process IPC wakes.
#[derive(Debug)]
pub struct IpcEvent {
    handle: HANDLE,
}

unsafe impl Send for IpcEvent {}
unsafe impl Sync for IpcEvent {}

impl IpcEvent {
    pub fn create_or_open(name: &str, manual_reset: bool) -> inbrisk_core::Result<Self> {
        let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
        // Same user-only DACL and Low label as the mapped region. A sandboxed
        // MCP process cannot open an event that inherited a higher integrity.
        // Attributes are ignored when the event already exists.
        let attrs = crate::security::UserOnlyAttributes::new();
        if let Some(a) = attrs.as_ref() {
            a.touch();
        }
        let handle = unsafe {
            CreateEventW(
                attrs.as_ref().map(|a| a.as_ptr()),
                manual_reset,
                false,
                PCWSTR(wide.as_ptr()),
            )
        }
        .map_err(|e| {
            inbrisk_core::InbriskError::internal(format!("CreateEventW('{name}') failed: {e}"))
        })?;

        Ok(Self { handle })
    }

    pub fn set(&self) {
        unsafe {
            let _ = SetEvent(self.handle);
        }
    }

    pub fn reset(&self) {
        unsafe {
            let _ = ResetEvent(self.handle);
        }
    }

    pub fn wait(&self, timeout_ms: Option<u32>) -> WaitOutcome {
        let ms = timeout_ms.unwrap_or(u32::MAX);
        let status = unsafe { WaitForSingleObject(self.handle, ms) };
        if status == WAIT_OBJECT_0 {
            WaitOutcome::Woken
        } else if status == WAIT_TIMEOUT {
            WaitOutcome::TimedOut
        } else {
            WaitOutcome::Failed
        }
    }
}

impl Drop for IpcEvent {
    fn drop(&mut self) {
        if !self.handle.is_invalid() {
            unsafe {
                let _ = CloseHandle(self.handle);
            }
        }
    }
}

pub unsafe fn wait_on_u64(
    _address: *const u64,
    _expected: u64,
    timeout_ms: Option<u32>,
) -> WaitOutcome {
    if let Some(ms) = timeout_ms {
        std::thread::sleep(std::time::Duration::from_millis(ms as u64));
    }
    WaitOutcome::TimedOut
}

pub unsafe fn wake_one(_address: *const u64) {}
pub unsafe fn wake_all(_address: *const u64) {}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ipc_event_signaling() {
        let name = "Local\\Inbrisk.TestEvent";
        let ev = IpcEvent::create_or_open(name, false).expect("event created");
        assert_eq!(ev.wait(Some(0)), WaitOutcome::TimedOut);
        ev.set();
        assert_eq!(ev.wait(Some(100)), WaitOutcome::Woken);
    }
}
