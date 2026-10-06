use std::collections::HashMap;
use std::sync::OnceLock;

use inbrisk_core::world::now_ms;
use inbrisk_core::{InbriskError, Result};
use parking_lot::Mutex;
use windows::core::PWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT};
use windows::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, Process32FirstW, Process32NextW, PROCESSENTRY32W, TH32CS_SNAPPROCESS,
};
use windows::Win32::System::ProcessStatus::EnumProcesses;
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, QueryFullProcessImageNameW, WaitForSingleObject,
    PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
};

/// Cached process metadata. Process names never change for a live pid, so the
/// cache is only invalidated by pid reuse (handled by the TTL).
static NAME_CACHE: OnceLock<Mutex<HashMap<u32, (u64, String)>>> = OnceLock::new();
const NAME_TTL_MS: u64 = 5_000;

fn cache() -> &'static Mutex<HashMap<u32, (u64, String)>> {
    NAME_CACHE.get_or_init(|| Mutex::new(HashMap::new()))
}

pub fn current_pid() -> u32 {
    unsafe { GetCurrentProcessId() }
}

/// Full image path of a process, or `None` when it is protected/gone.
pub fn process_path(pid: u32) -> Option<String> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 512];
        let mut len = buf.len() as u32;
        let res = QueryFullProcessImageNameW(
            handle,
            PROCESS_NAME_WIN32,
            PWSTR(buf.as_mut_ptr()),
            &mut len,
        );
        let _ = CloseHandle(handle);
        res.ok()?;
        Some(String::from_utf16_lossy(&buf[..len as usize]))
    }
}

/// Base image name, e.g. `notepad.exe`.
pub fn process_name(pid: u32) -> Option<String> {
    let now = now_ms();
    {
        let c = cache().lock();
        if let Some((at, name)) = c.get(&pid) {
            if now.saturating_sub(*at) < NAME_TTL_MS {
                return Some(name.clone());
            }
        }
    }
    let path = process_path(pid)?;
    let name = path
        .rsplit(['\\', '/'])
        .next()
        .unwrap_or(path.as_str())
        .to_string();
    cache().lock().insert(pid, (now, name.clone()));
    Some(name)
}

pub fn is_alive(pid: u32) -> bool {
    if pid == 0 {
        return false;
    }
    unsafe {
        match OpenProcess(PROCESS_SYNCHRONIZE, false, pid) {
            Ok(h) => {
                let r = WaitForSingleObject(h, 0);
                let _ = CloseHandle(h);
                r == WAIT_TIMEOUT
            }
            Err(_) => false,
        }
    }
}

/// Block until the process exits (or the timeout elapses). This is the
/// event-driven primitive behind `wait until process exit` — no polling.
pub fn wait_for_exit(pid: u32, timeout_ms: u32) -> bool {
    unsafe {
        let Ok(h) = OpenProcess(PROCESS_SYNCHRONIZE, false, pid) else {
            return true; // already gone
        };
        let r = WaitForSingleObject(h, timeout_ms);
        let _ = CloseHandle(h);
        r == WAIT_OBJECT_0
    }
}

/// A live SYNCHRONIZE handle used for event-driven waits.
#[derive(Debug)]
pub struct ProcessHandle(HANDLE);

impl ProcessHandle {
    pub fn open(pid: u32) -> Option<Self> {
        unsafe {
            OpenProcess(PROCESS_SYNCHRONIZE, false, pid)
                .ok()
                .map(ProcessHandle)
        }
    }

    pub fn wait(&self, timeout_ms: u32) -> bool {
        unsafe { WaitForSingleObject(self.0, timeout_ms) == WAIT_OBJECT_0 }
    }
}

impl Drop for ProcessHandle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

#[derive(Debug, Clone)]
pub struct ProcessInfo {
    pub pid: u32,
    pub parent_pid: u32,
    pub name: String,
    pub path: Option<String>,
    pub alive: bool,
}

/// One pass over the process table (Toolhelp), used for identity/disambiguation
/// and for the process tree of a browser instance.
pub fn snapshot(include_paths: bool) -> Vec<ProcessInfo> {
    let mut out = Vec::with_capacity(256);
    unsafe {
        let Ok(snap) = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) else {
            return out;
        };
        let mut entry = PROCESSENTRY32W {
            dwSize: std::mem::size_of::<PROCESSENTRY32W>() as u32,
            ..Default::default()
        };
        if Process32FirstW(snap, &mut entry).is_ok() {
            loop {
                let name = String::from_utf16_lossy(
                    &entry
                        .szExeFile
                        .iter()
                        .copied()
                        .take_while(|c| *c != 0)
                        .collect::<Vec<u16>>(),
                );
                let pid = entry.th32ProcessID;
                out.push(ProcessInfo {
                    pid,
                    parent_pid: entry.th32ParentProcessID,
                    name,
                    path: if include_paths {
                        process_path(pid)
                    } else {
                        None
                    },
                    alive: true,
                });
                if Process32NextW(snap, &mut entry).is_err() {
                    break;
                }
            }
        }
        let _ = CloseHandle(snap);
    }
    out
}

pub fn parent_pid(pid: u32) -> Option<u32> {
    snapshot(false)
        .into_iter()
        .find(|p| p.pid == pid)
        .map(|p| p.parent_pid)
        .filter(|p| *p != 0)
}

/// Descendants of `root`, used to keep a browser instance's identity honest.
pub fn descendants(root: u32) -> Vec<u32> {
    let table = snapshot(false);
    let mut out = vec![root];
    let mut changed = true;
    while changed {
        changed = false;
        for p in &table {
            if out.contains(&p.parent_pid) && !out.contains(&p.pid) {
                out.push(p.pid);
                changed = true;
            }
        }
    }
    out
}

pub fn all_pids() -> Vec<u32> {
    let mut buf = vec![0u32; 4096];
    let mut needed = 0u32;
    unsafe {
        if EnumProcesses(
            buf.as_mut_ptr(),
            (buf.len() * std::mem::size_of::<u32>()) as u32,
            &mut needed,
        )
        .is_err()
        {
            return Vec::new();
        }
    }
    let count = needed as usize / std::mem::size_of::<u32>();
    buf.truncate(count.min(buf.len()));
    buf.retain(|p| *p != 0);
    buf
}

/// Processes whose window set changed — used right after a shell launch to
/// discover which pid actually appeared.
pub fn new_pids_since(before: &[u32]) -> Vec<u32> {
    all_pids()
        .into_iter()
        .filter(|p| !before.contains(p))
        .collect()
}

/// Ask a process to terminate.
///
/// This is the blunt instrument behind `force`; the policy engine decides
/// whether it may ever be used. Callers must close the handle themselves? No —
/// `TerminateProcess` needs no cleanup beyond closing the handle, done here.
pub fn terminate_process(pid: u32, exit_code: u32) -> Result<()> {
    use windows::Win32::System::Threading::{TerminateProcess, PROCESS_TERMINATE};
    if pid == 0 || pid == current_pid() {
        return Err(InbriskError::denied(
            "refusing to terminate pid 0 or the runtime itself",
        ));
    }
    unsafe {
        let handle = OpenProcess(PROCESS_TERMINATE, false, pid).map_err(|e| {
            InbriskError::denied(format!("cannot open pid {pid} for termination: {e}"))
        })?;
        let r = TerminateProcess(handle, exit_code);
        let _ = CloseHandle(handle);
        r.map_err(|e| InbriskError::internal(format!("TerminateProcess({pid}) failed: {e}")))
    }
}

/// `ProcessTable` is the cheap, cached facade the runtime holds.
#[derive(Debug, Default)]
pub struct ProcessTable;

impl ProcessTable {
    pub fn new() -> Self {
        Self
    }

    pub fn name(&self, pid: u32) -> Option<String> {
        process_name(pid)
    }

    pub fn path(&self, pid: u32) -> Option<String> {
        process_path(pid)
    }

    pub fn alive(&self, pid: u32) -> bool {
        is_alive(pid)
    }

    pub fn snapshot(&self) -> Vec<ProcessInfo> {
        snapshot(false)
    }

    pub fn command_line(&self, pid: u32) -> Option<String> {
        command_line(pid)
    }
}

/// `ProcessCommandLineInformation`. Works with `PROCESS_QUERY_LIMITED_INFORMATION`
/// on Windows 8.1 and later. `None` means the line could not be proven.
const PROCESS_COMMAND_LINE_INFORMATION: u32 = 60;

#[link(name = "ntdll")]
extern "system" {
    fn NtQueryInformationProcess(
        process_handle: HANDLE,
        process_information_class: u32,
        process_information: *mut core::ffi::c_void,
        process_information_length: u32,
        return_length: *mut u32,
    ) -> i32;
}

pub fn command_line(pid: u32) -> Option<String> {
    if pid == 0 {
        return None;
    }
    unsafe {
        let handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut cap = 1024usize;
        let mut decoded = None;
        for _ in 0..8 {
            let mut buf = vec![0u8; cap];
            let mut ret = 0u32;
            let status = NtQueryInformationProcess(
                handle,
                PROCESS_COMMAND_LINE_INFORMATION,
                buf.as_mut_ptr() as *mut core::ffi::c_void,
                cap as u32,
                &mut ret,
            );
            if status == 0 {
                decoded = decode_command_line_buffer(&buf);
                break;
            }
            let grow = status == 0xC0000004u32 as i32
                || status == 0xC0000023u32 as i32
                || status == 0x80000005u32 as i32;
            if !grow {
                break;
            }
            let next = (ret as usize)
                .max(cap.saturating_mul(2))
                .clamp(cap + 256, 1 << 20);
            if next <= cap {
                break;
            }
            cap = next;
        }
        let _ = CloseHandle(handle);
        decoded
    }
}

/// Decode the `UNICODE_STRING` that class 60 writes. The `Buffer` field is an
/// absolute pointer into `buf` on current Windows; older builds leave the
/// characters immediately after the 16-byte header.
pub fn decode_command_line_buffer(buf: &[u8]) -> Option<String> {
    if buf.len() < 16 {
        return None;
    }
    let length = u16::from_le_bytes([buf[0], buf[1]]) as usize;
    if length == 0 || length % 2 != 0 || length > buf.len() {
        return None;
    }
    let ptr = usize::from_le_bytes(buf[8..16].try_into().ok()?);
    let base = buf.as_ptr() as usize;
    let start = if ptr >= base && ptr.saturating_add(length) <= base + buf.len() {
        ptr - base
    } else if 16 + length <= buf.len() {
        16
    } else {
        return None;
    };
    if start + length > buf.len() {
        return None;
    }
    let chars =
        unsafe { std::slice::from_raw_parts(buf.as_ptr().add(start) as *const u16, length / 2) };
    let text = String::from_utf16_lossy(chars);
    let text = text.trim_end_matches('\0').trim().to_string();
    if text.is_empty() {
        None
    } else {
        Some(text)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn command_line_buffer_honors_an_interior_pointer() {
        let text = "chrome.exe --remote-debugging-port=9222";
        let units: Vec<u16> = text.encode_utf16().collect();
        let byte_len = (units.len() * 2) as u16;
        let mut buf = vec![0u8; 16 + units.len() * 2];
        buf[0..2].copy_from_slice(&byte_len.to_le_bytes());
        let address = buf.as_ptr() as usize + 16;
        buf[8..16].copy_from_slice(&address.to_le_bytes());
        for (i, unit) in units.iter().enumerate() {
            buf[16 + i * 2..16 + i * 2 + 2].copy_from_slice(&unit.to_le_bytes());
        }
        assert_eq!(decode_command_line_buffer(&buf).as_deref(), Some(text));
    }

    #[test]
    fn current_process_command_line_is_readable() {
        let line = command_line(current_pid());
        assert!(
            line.as_ref().map(|s| !s.is_empty()).unwrap_or(false),
            "NtQueryInformationProcess class 60 returned {line:?}"
        );
    }
}
