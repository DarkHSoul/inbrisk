//! Tray icon and its context menu.
//!
//! The tray is the one control surface that is always present, which is why the
//! emergency stop lives here and only here.

use inbrisk_core::{log_warn, Result};
use windows::core::PCWSTR;
use windows::Win32::Foundation::{HWND, POINT};
use windows::Win32::UI::Shell::{
    NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NIM_MODIFY, NOTIFYICONDATAW,
};
use windows::Win32::UI::WindowsAndMessaging::{
    AppendMenuW, CreatePopupMenu, DestroyMenu, GetCursorPos, LoadIconW, PostMessageW,
    SetForegroundWindow, TrackPopupMenu, IDI_APPLICATION, MF_SEPARATOR, MF_STRING, TPM_RETURNCMD,
    TPM_RIGHTBUTTON, WM_NULL,
};

fn dynamic_shell_notify_icon_w(message: u32, data: &NOTIFYICONDATAW) -> bool {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryA};
        if let Ok(shell32) = LoadLibraryA(s!("shell32.dll")) {
            if let Some(proc) = GetProcAddress(shell32, s!("Shell_NotifyIconW")) {
                type ShellNotifyIconWFn =
                    unsafe extern "system" fn(u32, *const NOTIFYICONDATAW) -> windows::core::BOOL;
                let func: ShellNotifyIconWFn = std::mem::transmute(proc);
                return func(message, data as *const _).as_bool();
            }
        }
        false
    }
}

use crate::UiCommand;

/// Callback message the shell sends to our window for tray events.
pub const TRAY_CALLBACK: u32 = windows::Win32::UI::WindowsAndMessaging::WM_APP + 1;

const ID_OPEN: usize = 2001;
const ID_PAUSE: usize = 2002;
const ID_RESUME: usize = 2003;
const ID_EMERGENCY: usize = 2004;
const ID_CLEAR_EMERGENCY: usize = 2005;
const ID_EXIT: usize = 2006;

fn wide(s: &str, len: usize) -> Vec<u16> {
    let mut buf = vec![0u16; len];
    let encoded: Vec<u16> = s.encode_utf16().take(len.saturating_sub(1)).collect();
    buf[..encoded.len()].copy_from_slice(&encoded);
    buf
}

fn icon_data(hwnd: HWND, tip: &str) -> NOTIFYICONDATAW {
    let icon = unsafe { LoadIconW(None, IDI_APPLICATION).unwrap_or_default() };
    let mut data = NOTIFYICONDATAW {
        cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: 1,
        uFlags: NIF_ICON | NIF_MESSAGE | NIF_TIP,
        uCallbackMessage: TRAY_CALLBACK,
        hIcon: icon,
        ..Default::default()
    };
    let tip_wide = wide(tip, 128);
    data.szTip.copy_from_slice(&tip_wide);
    data
}

/// Install the tray icon.
pub fn add(hwnd: HWND, tip: &str) -> Result<()> {
    let data = icon_data(hwnd, tip);
    let ok = dynamic_shell_notify_icon_w(NIM_ADD.0, &data);
    if !ok {
        // A missing tray (locked-down session, no shell) must never kill the
        // runtime; automation has to keep working headless.
        log_warn!(
            "ui.tray",
            "the shell refused the tray icon; running without a tray"
        );
    }
    Ok(())
}

pub fn set_tip(hwnd: HWND, tip: &str) {
    let data = icon_data(hwnd, tip);
    let _ = dynamic_shell_notify_icon_w(NIM_MODIFY.0, &data);
}

pub fn remove(hwnd: HWND) {
    let data = NOTIFYICONDATAW {
        cbSize: std::mem::size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: hwnd,
        uID: 1,
        ..Default::default()
    };
    let _ = dynamic_shell_notify_icon_w(NIM_DELETE.0, &data);
}

/// Show the context menu and translate the choice into a `UiCommand`.
pub fn show_menu(hwnd: HWND) -> Option<UiCommand> {
    unsafe {
        let menu = CreatePopupMenu().ok()?;
        let entries: [(&str, usize); 6] = [
            ("Open Inbrisk", ID_OPEN),
            ("Pause", ID_PAUSE),
            ("Resume", ID_RESUME),
            ("Emergency Stop", ID_EMERGENCY),
            ("Clear Emergency", ID_CLEAR_EMERGENCY),
            ("Exit", ID_EXIT),
        ];
        let _ = AppendMenuW(
            menu,
            MF_STRING,
            entries[0].1,
            PCWSTR(wide(entries[0].0, 32).as_ptr()),
        );
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        for (label, id) in entries.iter().skip(1).take(4) {
            let l = wide(label, 32);
            let _ = AppendMenuW(menu, MF_STRING, *id, PCWSTR(l.as_ptr()));
        }
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let exit = wide(entries[5].0, 32);
        let _ = AppendMenuW(menu, MF_STRING, entries[5].1, PCWSTR(exit.as_ptr()));

        let mut point = POINT::default();
        let _ = GetCursorPos(&mut point);
        // Required so the menu closes when the user clicks elsewhere.
        let _ = SetForegroundWindow(hwnd);
        let choice = TrackPopupMenu(
            menu,
            TPM_RIGHTBUTTON | TPM_RETURNCMD,
            point.x,
            point.y,
            None,
            hwnd,
            None,
        );
        let _ = PostMessageW(Some(hwnd), WM_NULL, Default::default(), Default::default());
        let _ = DestroyMenu(menu);

        match choice.0 as usize {
            ID_OPEN => Some(UiCommand::ToggleWindow),
            ID_PAUSE => Some(UiCommand::Pause),
            ID_RESUME => Some(UiCommand::Resume),
            ID_EMERGENCY => Some(UiCommand::EmergencyStop),
            ID_CLEAR_EMERGENCY => Some(UiCommand::ClearEmergency),
            ID_EXIT => Some(UiCommand::Exit),
            _ => None,
        }
    }
}
