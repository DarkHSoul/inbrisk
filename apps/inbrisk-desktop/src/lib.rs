//! Inbrisk Interactive Desktop Broker library.

pub mod input_executor;

pub use input_executor::{PhysicalInputExecutor, RawInputBackend, Win32RawInputBackend};

use inbrisk_core::{InbriskError, Result};
use inbrisk_protocol::desktop_broker::{
    BrokerWindowInfo, DesktopBrokerWelcome, DESKTOP_BROKER_PROTOCOL_VERSION,
};
use inbrisk_win32::desktop::{probe_desktop_context, DesktopContextProbe};

/// Enumerate visible top-level windows through canonical desktop-bound Win32 primitives.
pub fn enumerate_windows() -> Result<Vec<BrokerWindowInfo>> {
    let probe = probe_desktop_context()?;
    if !probe.input_desktop_available && !probe.fresh_thread_can_bind_input_desktop {
        return Err(InbriskError::capability_unavailable(
            inbrisk_core::ExecutionCapability::WindowEnumeration,
            "broker cannot bind to input desktop for window enumeration",
        ));
    }

    let raws = inbrisk_win32::window::enumerate_top_level();
    let infos = raws
        .into_iter()
        .map(|w| BrokerWindowInfo {
            hwnd: w.hwnd.0 as usize as u64,
            pid: w.process_id,
            title: w.title,
            left: w.bounds.x,
            top: w.bounds.y,
            right: w.bounds.x + w.bounds.width as i32,
            bottom: w.bounds.y + w.bounds.height as i32,
            visible: w.visible,
            minimized: w.minimized,
            process_name: w.process_name,
            class_name: w.class_name,
        })
        .collect();

    Ok(infos)
}

pub fn build_welcome(
    probe: &DesktopContextProbe,
    broker_pid: u32,
    broker_epoch: u64,
) -> DesktopBrokerWelcome {
    let interactive = probe.input_desktop_available && probe.fresh_thread_can_bind_input_desktop;
    DesktopBrokerWelcome {
        protocol_version: DESKTOP_BROKER_PROTOCOL_VERSION,
        broker_pid,
        broker_epoch,
        process_session_id: probe.process_session_id,
        window_station: probe.current_window_station.clone(),
        desktop: probe.current_desktop.clone(),
        interactive_desktop: interactive,
        window_enumeration_ready: interactive,
        physical_input_ready: interactive,
        dpi_awareness: Some(inbrisk_win32::geometry::get_process_dpi_awareness()),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn broker_status_returns_structured_desktop_context() {
        let probe = probe_desktop_context().expect("probe context");
        let welcome = build_welcome(&probe, 1234, 5678);

        assert_eq!(welcome.broker_pid, 1234);
        assert_eq!(welcome.broker_epoch, 5678);
        assert_eq!(welcome.protocol_version, DESKTOP_BROKER_PROTOCOL_VERSION);
        assert_eq!(welcome.process_session_id, probe.process_session_id);
        assert_eq!(welcome.window_station, probe.current_window_station);
        assert_eq!(welcome.desktop, probe.current_desktop);
    }

    #[test]
    fn broker_enumerate_windows_returns_structured_result_or_capability_error() {
        let res = enumerate_windows();
        match res {
            Ok(windows) => {
                for w in windows {
                    assert!(w.hwnd > 0);
                    assert!(w.right >= w.left);
                    assert!(w.bottom >= w.top);
                }
            }
            Err(err) => {
                assert_eq!(err.code, inbrisk_core::ErrorCode::CapabilityUnavailable);
            }
        }
    }
}
