//! Raw input synthesis via `SendInput`.
//!
//! This is the *last* backend in the execution router: semantic patterns
//! (`InvokePattern`, `ValuePattern`) never touch this code. Everything here is
//! serialized by `inbrisk-input::GlobalInputArbiter` before it is called.

use std::thread::sleep;
use std::time::Duration;

use inbrisk_core::{ErrorCode, InbriskError, Result};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_0, INPUT_KEYBOARD, INPUT_MOUSE, KEYBDINPUT, KEYBD_EVENT_FLAGS, KEYEVENTF_KEYUP,
    KEYEVENTF_UNICODE, MAPVK_VK_TO_VSC, MapVirtualKeyW, MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_HWHEEL,
    MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP,
    MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, MOUSEEVENTF_VIRTUALDESK,
    MOUSEEVENTF_WHEEL, MOUSEINPUT, MOUSE_EVENT_FLAGS, VIRTUAL_KEY, VK_BACK, VK_CONTROL, VK_DELETE,
    VK_DOWN, VK_END, VK_ESCAPE, VK_HOME, VK_LEFT, VK_LWIN, VK_MENU, VK_NEXT, VK_PRIOR, VK_RETURN,
    VK_RIGHT, VK_SHIFT, VK_SPACE, VK_TAB, VK_UP,
};
use windows::Win32::UI::WindowsAndMessaging::SetCursorPos;

/// Which physical button a click uses.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum Button {
    #[default]
    Left,
    Right,
    Middle,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Wheel {
    Up,
    Down,
    Left,
    Right,
}

fn send(inputs: &[INPUT]) -> Result<()> {
    if inputs.is_empty() {
        return Ok(());
    }
    let sent = unsafe {
        windows::Win32::UI::Input::KeyboardAndMouse::SendInput(
            inputs,
            std::mem::size_of::<INPUT>() as i32,
        )
    };
    if sent as usize != inputs.len() {
        return Err(InbriskError::new(
            ErrorCode::Internal,
            format!(
                "SendInput delivered {sent}/{} events (a higher-integrity window may own input)",
                inputs.len()
            ),
        ));
    }
    Ok(())
}

fn keyboard(vk: VIRTUAL_KEY, scan: u16, flags: KEYBD_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: vk,
                wScan: scan,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

fn mouse(dx: i32, dy: i32, data: i32, flags: MOUSE_EVENT_FLAGS) -> INPUT {
    INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dx,
                dy,
                mouseData: data as u32,
                dwFlags: flags,
                time: 0,
                dwExtraInfo: 0,
            },
        },
    }
}

/// Virtual desktop geometry: `(x, y, width, height)`.
pub fn virtual_screen() -> (i32, i32, i32, i32) {
    let b = crate::geometry::virtual_screen_bounds();
    (b.left, b.top, b.width, b.height)
}

fn absolute(x: i32, y: i32) -> (i32, i32) {
    let bounds = crate::geometry::virtual_screen_bounds();
    let norm =
        crate::geometry::normalize_for_send_input(crate::geometry::PhysicalPoint { x, y }, bounds)
            .unwrap_or_default();
    (norm.x as i32, norm.y as i32)
}

/// Move mouse to physical virtual screen coordinates.
pub fn move_to_virtual_physical(point: crate::geometry::PhysicalPoint) -> Result<()> {
    let bounds = crate::geometry::virtual_screen_bounds();
    let norm = crate::geometry::normalize_for_send_input(point, bounds)?;
    send(&[mouse(
        norm.x as i32,
        norm.y as i32,
        0,
        MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    )])
}

pub fn move_to(x: i32, y: i32) -> Result<()> {
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::SetCursorPos(x, y);
    }
    move_to_virtual_physical(crate::geometry::PhysicalPoint { x, y })
}

pub fn click(x: i32, y: i32, button: Button, clicks: u8) -> Result<()> {
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::SetCursorPos(x, y);
    }
    let (nx, ny) = absolute(x, y);
    let (down, up) = match button {
        Button::Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        Button::Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        Button::Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
    };
    send(&[mouse(
        nx,
        ny,
        0,
        MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    )])?;
    sleep(Duration::from_millis(15));
    for i in 0..clicks.max(1) {
        send(&[mouse(
            nx,
            ny,
            0,
            down | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        )])?;
        sleep(Duration::from_millis(35));
        send(&[mouse(
            nx,
            ny,
            0,
            up | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        )])?;
        if i + 1 < clicks {
            sleep(Duration::from_millis(50));
        }
    }
    Ok(())
}

/// Release any physically pressed mouse buttons (Left, Right, Middle).
pub fn release_mouse_buttons() -> Result<()> {
    send(&[
        mouse(0, 0, 0, MOUSEEVENTF_LEFTUP),
        mouse(0, 0, 0, MOUSEEVENTF_RIGHTUP),
        mouse(0, 0, 0, MOUSEEVENTF_MIDDLEUP),
    ])
}

pub fn scroll(wheel: Wheel, amount: i32) -> Result<()> {
    let notches = amount.max(1);
    let delta: i32 = match wheel {
        Wheel::Up => 120 * notches,
        Wheel::Down => -120 * notches,
        Wheel::Right => 120 * notches,
        Wheel::Left => -120 * notches,
    };
    let flags = match wheel {
        Wheel::Up | Wheel::Down => MOUSEEVENTF_WHEEL,
        Wheel::Left | Wheel::Right => MOUSEEVENTF_HWHEEL,
    };
    send(&[mouse(0, 0, delta, flags)])
}

/// Type literal text using unicode scancodes — layout independent.
pub fn type_text(text: &str) -> Result<()> {
    let mut chars = text.chars().peekable();
    let mut batch: Vec<INPUT> = Vec::with_capacity(text.len() * 2);
    while let Some(ch) = chars.next() {
        if ch == '\r' {
            if chars.peek() == Some(&'\n') {
                continue; // normalize CRLF to single Return
            }
            batch.push(keyboard(VK_RETURN, 0, KEYBD_EVENT_FLAGS(0)));
            batch.push(keyboard(VK_RETURN, 0, KEYEVENTF_KEYUP));
            continue;
        }
        if ch == '\n' {
            batch.push(keyboard(VK_RETURN, 0, KEYBD_EVENT_FLAGS(0)));
            batch.push(keyboard(VK_RETURN, 0, KEYEVENTF_KEYUP));
            continue;
        }
        if ch == '\t' {
            batch.push(keyboard(VK_TAB, 0, KEYBD_EVENT_FLAGS(0)));
            batch.push(keyboard(VK_TAB, 0, KEYEVENTF_KEYUP));
            continue;
        }
        let mut buf = [0u16; 2];
        for &unit in ch.encode_utf16(&mut buf).iter() {
            batch.push(keyboard(VIRTUAL_KEY(0), unit, KEYEVENTF_UNICODE));
            batch.push(keyboard(
                VIRTUAL_KEY(0),
                unit,
                KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
            ));
        }
        if batch.len() >= 200 {
            send(&batch)?;
            batch.clear();
            sleep(Duration::from_millis(1));
        }
    }
    send(&batch)
}

/// Map a human key name to a virtual key code.
pub fn virtual_key_for(name: &str) -> Option<VIRTUAL_KEY> {
    let n = name.trim().to_ascii_lowercase();
    let vk = match n.as_str() {
        "ctrl" | "control" => VK_CONTROL,
        "shift" => VK_SHIFT,
        "alt" | "menu" => VK_MENU,
        "win" | "windows" | "super" | "meta" => VK_LWIN,
        "enter" | "return" => VK_RETURN,
        "esc" | "escape" => VK_ESCAPE,
        "tab" => VK_TAB,
        "space" => VK_SPACE,
        "backspace" | "back" => VK_BACK,
        "delete" | "del" => VK_DELETE,
        "up" => VK_UP,
        "down" => VK_DOWN,
        "left" => VK_LEFT,
        "right" => VK_RIGHT,
        "home" => VK_HOME,
        "end" => VK_END,
        "pageup" | "pgup" => VK_PRIOR,
        "pagedown" | "pgdn" => VK_NEXT,
        "plus" => VIRTUAL_KEY(0xBB),  // VK_OEM_PLUS
        "minus" => VIRTUAL_KEY(0xBD), // VK_OEM_MINUS
        "comma" => VIRTUAL_KEY(0xBC),
        "period" | "dot" => VIRTUAL_KEY(0xBE),
        "slash" => VIRTUAL_KEY(0xBF),
        "semicolon" => VIRTUAL_KEY(0xBA),
        "quote" => VIRTUAL_KEY(0xDE),
        "backtick" | "grave" => VIRTUAL_KEY(0xC0),
        other => {
            if let Some(rest) = other.strip_prefix('f') {
                if let Ok(n) = rest.parse::<u16>() {
                    if (1..=24).contains(&n) {
                        return Some(VIRTUAL_KEY(0x70 + n - 1));
                    }
                }
            }
            let mut chars = other.chars();
            let c = chars.next()?;
            if chars.next().is_some() {
                return None;
            }
            if c.is_ascii_alphabetic() {
                VIRTUAL_KEY(c.to_ascii_uppercase() as u16)
            } else if c.is_ascii_digit() {
                VIRTUAL_KEY(c as u16)
            } else {
                return None;
            }
        }
    };
    Some(vk)
}

/// Press a chord such as `["ctrl", "shift", "s"]`, then release it in reverse.
pub fn key_chord(keys: &[String]) -> Result<()> {
    if keys.is_empty() {
        return Err(InbriskError::invalid_plan("key: empty chord"));
    }
    let mut vks = Vec::with_capacity(keys.len());
    for k in keys {
        let vk = virtual_key_for(k).ok_or_else(|| {
            InbriskError::invalid_plan(format!("key: unknown key name '{k}'"))
                .with_hint("use ctrl/alt/shift/win, a single character, or f1..f24")
        })?;
        vks.push(vk);
    }
    let mut down_batch: Vec<INPUT> = Vec::with_capacity(vks.len());
    for vk in &vks {
        let scan = unsafe { MapVirtualKeyW(vk.0 as u32, MAPVK_VK_TO_VSC) as u16 };
        down_batch.push(keyboard(*vk, scan, KEYBD_EVENT_FLAGS(0)));
    }
    send(&down_batch)?;

    // Hold chord down for ~40ms to ensure 30-60fps game loops poll it reliably
    sleep(Duration::from_millis(40));

    let mut up_batch: Vec<INPUT> = Vec::with_capacity(vks.len());
    for vk in vks.iter().rev() {
        let scan = unsafe { MapVirtualKeyW(vk.0 as u32, MAPVK_VK_TO_VSC) as u16 };
        up_batch.push(keyboard(*vk, scan, KEYEVENTF_KEYUP));
    }
    send(&up_batch)
}

/// Hold or release a single key (used by drag/modifier flows).
pub fn key_state(name: &str, down: bool) -> Result<()> {
    let vk = virtual_key_for(name)
        .ok_or_else(|| InbriskError::invalid_plan(format!("unknown key name '{name}'")))?;
    let scan = unsafe { MapVirtualKeyW(vk.0 as u32, MAPVK_VK_TO_VSC) as u16 };
    send(&[keyboard(
        vk,
        scan,
        if down {
            KEYBD_EVENT_FLAGS(0)
        } else {
            KEYEVENTF_KEYUP
        },
    )])
}

/// Drag from one point to another with an interpolated move.
pub fn drag(from: (i32, i32), to: (i32, i32), button: Button) -> Result<()> {
    let (fx, fy) = absolute(from.0, from.1);
    let (tx, ty) = absolute(to.0, to.1);
    let (down, up) = match button {
        Button::Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        Button::Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        Button::Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
    };

    // 1. Move to start position & sync physical cursor
    let _ = unsafe { SetCursorPos(from.0, from.1) };
    send(&[mouse(
        fx,
        fy,
        0,
        MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    )])?;
    sleep(Duration::from_millis(30));

    // 2. Press down at start position with absolute flags
    send(&[mouse(
        fx,
        fy,
        0,
        down | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    )])?;
    // Hold down for 40ms before moving so game engine registers the drag start
    sleep(Duration::from_millis(40));

    // 3. Smooth interpolation across points (20 steps @ 15ms = 300ms drag)
    const STEPS: i32 = 20;
    for i in 1..=STEPS {
        let raw_x = from.0 + (to.0 - from.0) * i / STEPS;
        let raw_y = from.1 + (to.1 - from.1) * i / STEPS;
        let nx = fx + (tx - fx) * i / STEPS;
        let ny = fy + (ty - fy) * i / STEPS;
        let _ = unsafe { SetCursorPos(raw_x, raw_y) };
        send(&[mouse(
            nx,
            ny,
            0,
            MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
        )])?;
        sleep(Duration::from_millis(15));
    }

    // 4. Hold at destination for 40ms to let game preview/rect settle
    sleep(Duration::from_millis(40));

    // 5. Release button at destination
    let _ = unsafe { SetCursorPos(to.0, to.1) };
    send(&[mouse(
        tx,
        ty,
        0,
        up | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
    )])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn key_names_map_to_virtual_keys() {
        assert_eq!(virtual_key_for("ctrl"), Some(VK_CONTROL));
        assert_eq!(virtual_key_for("A"), Some(VIRTUAL_KEY(0x41)));
        assert_eq!(virtual_key_for("f5"), Some(VIRTUAL_KEY(0x74)));
        assert_eq!(virtual_key_for("nonsense"), None);
    }

    #[test]
    fn absolute_coordinates_are_inside_the_desktop_range() {
        let (nx, ny) = absolute(0, 0);
        assert!((0..=65_535).contains(&nx));
        assert!((0..=65_535).contains(&ny));
    }

    #[test]
    fn empty_chord_is_rejected() {
        assert!(key_chord(&[]).is_err());
    }
}
