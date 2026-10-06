use std::path::{Path, PathBuf};

use inbrisk_core::{ErrorCode, InbriskError, Result};
use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::System::Threading::{
    CreateProcessW, CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS, PROCESS_INFORMATION, STARTUPINFOW,
};
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

fn dynamic_shell_execute_w(
    file: &[u16],
    params: &[u16],
    dir: Option<&[u16]>,
    show: windows::Win32::UI::WindowsAndMessaging::SHOW_WINDOW_CMD,
) -> isize {
    unsafe {
        use windows::core::s;
        use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryA};
        if let Ok(shell32) = LoadLibraryA(s!("shell32.dll")) {
            if let Some(proc) = GetProcAddress(shell32, s!("ShellExecuteW")) {
                type ShellExecuteWFn =
                    unsafe extern "system" fn(
                        windows::Win32::Foundation::HWND,
                        PCWSTR,
                        PCWSTR,
                        PCWSTR,
                        PCWSTR,
                        i32,
                    )
                        -> windows::Win32::Foundation::HINSTANCE;
                let func: ShellExecuteWFn = std::mem::transmute(proc);
                let res = func(
                    windows::Win32::Foundation::HWND(std::ptr::null_mut()),
                    windows::core::w!("open"),
                    PCWSTR(file.as_ptr()),
                    PCWSTR(params.as_ptr()),
                    dir.map(|d| PCWSTR(d.as_ptr())).unwrap_or(PCWSTR::null()),
                    show.0 as i32,
                );
                return res.0 as isize;
            }
        }
        0
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LaunchMethod {
    /// `CreateProcessW` — we know the pid and therefore the window ownership.
    CreateProcess,
    /// ShellExecute — pid unknown, discovered by diffing the window set.
    ShellExecute,
}

impl LaunchMethod {
    pub const fn as_str(self) -> &'static str {
        match self {
            LaunchMethod::CreateProcess => "create_process",
            LaunchMethod::ShellExecute => "shell_execute",
        }
    }
}

#[derive(Debug, Clone)]
pub struct LaunchResult {
    pub pid: Option<u32>,
    pub method: LaunchMethod,
    pub resolved: String,
    pub command_line: String,
}

fn system_roots() -> Vec<PathBuf> {
    let mut roots = Vec::new();
    for key in ["SystemRoot", "windir"] {
        if let Ok(v) = std::env::var(key) {
            roots.push(PathBuf::from(&v));
            roots.push(PathBuf::from(&v).join("System32"));
            roots.push(PathBuf::from(&v).join("SysWOW64"));
        }
    }
    if let Ok(pf) = std::env::var("ProgramFiles") {
        roots.push(PathBuf::from(pf));
    }
    if let Ok(pf) = std::env::var("ProgramFiles(x86)") {
        roots.push(PathBuf::from(pf));
    }
    if let Ok(la) = std::env::var("LOCALAPPDATA") {
        roots.push(PathBuf::from(&la).join("Programs"));
        roots.push(PathBuf::from(la).join("Microsoft\\WindowsApps"));
    }
    roots
}

/// Friendly aliases so `app: "notepad"` never depends on the shell.
fn alias(name: &str) -> Option<&'static str> {
    match name.to_ascii_lowercase().as_str() {
        "notepad" => Some("notepad.exe"),
        "calc" | "calculator" => Some("calc.exe"),
        "paint" | "mspaint" => Some("mspaint.exe"),
        "explorer" | "files" => Some("explorer.exe"),
        "cmd" | "terminal-cmd" => Some("cmd.exe"),
        "powershell" => Some("powershell.exe"),
        "pwsh" => Some("pwsh.exe"),
        "taskmgr" => Some("taskmgr.exe"),
        "control" => Some("control.exe"),
        "regedit" => Some("regedit.exe"),
        "snippingtool" => Some("SnippingTool.exe"),
        "winver" => Some("winver.exe"),
        "wordpad" => Some("write.exe"),
        _ => None,
    }
}

fn with_ext(name: &str) -> Vec<String> {
    if Path::new(name).extension().is_some() {
        vec![name.to_string()]
    } else {
        vec![
            format!("{name}.exe"),
            format!("{name}.cmd"),
            format!("{name}.bat"),
        ]
    }
}

/// Resolve an app token to a concrete path without the shell.
pub fn resolve_app(app: &str) -> Option<PathBuf> {
    let app = app.trim();
    if app.is_empty() {
        return None;
    }

    // 1. Explicit path.
    let direct = PathBuf::from(app);
    if direct.is_file() {
        return Some(direct);
    }

    // 2. Friendly alias.
    let candidates = match alias(app) {
        Some(real) => vec![real.to_string()],
        None => Vec::new(),
    };

    let names: Vec<String> = candidates
        .iter()
        .map(|s| s.to_string())
        .chain(with_ext(app))
        .collect();

    // 3. PATH.
    if let Some(path) = std::env::var_os("PATH") {
        for dir in std::env::split_paths(&path) {
            for name in &names {
                let p = dir.join(name);
                if p.is_file() {
                    return Some(p);
                }
            }
        }
    }

    // 4. Well-known roots.
    for root in system_roots() {
        for name in &names {
            let p = root.join(name);
            if p.is_file() {
                return Some(p);
            }
        }
    }

    None
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn quote(s: &str) -> String {
    if s.contains(' ') && !s.starts_with('"') {
        format!("\"{s}\"")
    } else {
        s.to_string()
    }
}

/// Build the command line. Batch files must be handed to `cmd.exe`.
fn command_line_for(exe: &Path, args: &[String]) -> (PathBuf, String) {
    let ext = exe
        .extension()
        .map(|e| e.to_string_lossy().to_ascii_lowercase())
        .unwrap_or_default();
    let is_batch = ext == "cmd" || ext == "bat";
    let (program, mut line) = if is_batch {
        let comspec = std::env::var("ComSpec").unwrap_or_else(|_| "cmd.exe".to_string());
        (
            PathBuf::from(comspec),
            format!("/c {}", quote(&exe.to_string_lossy())),
        )
    } else {
        (exe.to_path_buf(), quote(&exe.to_string_lossy()))
    };
    for a in args {
        line.push(' ');
        line.push_str(&quote(a));
    }
    (program, line)
}

/// Launch an application.
///
/// Preferred path is `CreateProcessW` because it yields the pid, which is what
/// makes window ownership (and therefore safe lifecycle cleanup) possible.
/// Shell fallback only happens for registered/UWP apps that have no real path.
pub fn launch(app: &str, args: &[String], cwd: Option<&str>) -> Result<LaunchResult> {
    if app.trim().is_empty() {
        return Err(InbriskError::invalid_plan("launch: empty app name"));
    }

    if let Some(exe) = resolve_app(app) {
        let (program, line) = command_line_for(&exe, args);
        let mut cmd: Vec<u16> = wide(&line);
        let program_w = wide(&program.to_string_lossy());
        let cwd_w = cwd.map(wide);

        let mut desktop_w: Vec<u16> = wide("WinSta0\\Default");
        let si = STARTUPINFOW {
            cb: std::mem::size_of::<STARTUPINFOW>() as u32,
            lpDesktop: PWSTR(desktop_w.as_mut_ptr()),
            ..Default::default()
        };
        let mut pi = PROCESS_INFORMATION::default();

        let result = unsafe {
            CreateProcessW(
                PCWSTR(program_w.as_ptr()),
                Some(PWSTR(cmd.as_mut_ptr())),
                None,
                None,
                false,
                // Detach from the runtime console. A GUI stub that inherits it
                // stays blocked after that console goes away.
                CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS,
                None,
                cwd_w
                    .as_ref()
                    .map(|w| PCWSTR(w.as_ptr()))
                    .unwrap_or(PCWSTR::null()),
                &si,
                &mut pi,
            )
        };

        match result {
            Ok(()) => {
                let pid = pi.dwProcessId;
                unsafe {
                    let _ = CloseHandle(pi.hThread);
                    let _ = CloseHandle(pi.hProcess);
                }
                return Ok(LaunchResult {
                    pid: Some(pid),
                    method: LaunchMethod::CreateProcess,
                    resolved: program.to_string_lossy().to_string(),
                    command_line: line,
                });
            }
            Err(e) => {
                let err_code = (e.code().0 as u32) & 0xFFFF;
                if err_code == 740 {
                    // ERROR_ELEVATION_REQUIRED (740): The executable has a requireAdministrator manifest.
                    // Retry with __COMPAT_LAYER=RunAsInvoker so Windows launches it non-elevated
                    // without triggering a modal UAC prompt that freezes the desktop.
                    std::env::set_var("__COMPAT_LAYER", "RunAsInvoker");
                    let retry_result = unsafe {
                        CreateProcessW(
                            PCWSTR(program_w.as_ptr()),
                            Some(PWSTR(cmd.as_mut_ptr())),
                            None,
                            None,
                            false,
                            CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS,
                            None,
                            cwd_w
                                .as_ref()
                                .map(|w| PCWSTR(w.as_ptr()))
                                .unwrap_or(PCWSTR::null()),
                            &si,
                            &mut pi,
                        )
                    };
                    std::env::remove_var("__COMPAT_LAYER");
                    if retry_result.is_ok() {
                        let pid = pi.dwProcessId;
                        unsafe {
                            let _ = CloseHandle(pi.hThread);
                            let _ = CloseHandle(pi.hProcess);
                        }
                        return Ok(LaunchResult {
                            pid: Some(pid),
                            method: LaunchMethod::CreateProcess,
                            resolved: program.to_string_lossy().to_string(),
                            command_line: line,
                        });
                    }
                    // If RunAsInvoker still cannot launch it, suppress falling through to ShellExecute
                    // which would pop the UAC modal on the secure desktop.
                    return Err(InbriskError::new(
                        ErrorCode::Denied,
                        format!("'{app}' requires administrator elevation (UAC prompt suppressed to prevent screen lock)"),
                    ));
                }
            }
        }
    }

    // Shell fallback: works for UWP apps, protocol handlers, Store apps.
    let file = wide(app);
    let params = wide(&args.join(" "));
    let dir = cwd.map(wide);
    let code = dynamic_shell_execute_w(&file, &params, dir.as_deref(), SW_SHOWNORMAL);
    if code <= 32 {
        return Err(InbriskError::new(
            ErrorCode::TargetNotFound,
            format!("could not launch '{app}' (ShellExecute code {code})"),
        )
        .with_hint("pass a full path, or an app name present in PATH"));
    }

    Ok(LaunchResult {
        pid: None,
        method: LaunchMethod::ShellExecute,
        resolved: app.to_string(),
        command_line: format!("{app} {}", args.join(" ")).trim().to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aliases_resolve_to_real_system_binaries() {
        // notepad.exe exists on every supported Windows install.
        let r = resolve_app("notepad");
        assert!(r.is_some(), "notepad should resolve");
        assert!(r
            .unwrap()
            .to_string_lossy()
            .to_ascii_lowercase()
            .ends_with("notepad.exe"));
    }

    #[test]
    fn empty_app_is_rejected() {
        assert!(launch("   ", &[], None).is_err());
    }

    #[test]
    fn batch_files_go_through_comspec() {
        let (prog, line) = command_line_for(Path::new("C:\\tools\\thing.cmd"), &["a b".into()]);
        assert!(prog
            .to_string_lossy()
            .to_ascii_lowercase()
            .contains("cmd.exe"));
        assert!(line.contains("thing.cmd"));
        assert!(line.contains("\"a b\""));
    }
}
