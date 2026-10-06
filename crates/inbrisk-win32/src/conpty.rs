//! Native Windows pseudo console (ConPTY) wrapper.
//!
//! A [`ConPty`] owns: the pseudo console (`HPCON`), the two anonymous pipes
//! that feed it, the child process attached to it, and a Job Object with
//! `KILL_ON_JOB_CLOSE` so that the whole process tree dies with the session.
//!
//! Concurrency contract (see Microsoft's ConPTY docs on deadlocks):
//! * [`ConPty::read`] is meant to be called from ONE dedicated reader thread
//!   that drains output continuously;
//! * [`ConPty::write`] is serialised by an internal mutex and never touches
//!   the output pipe, so a slow/blocked write cannot stall draining;
//! * [`ConPty::close_pseudo_console`] must not be called from the reader
//!   thread (it may block until the output pipe is drained).
//!
//! Raw handles are stored as `isize` so the type is `Send + Sync`. The reader
//! thread must hold an `Arc<ConPty>`: pipe handles are closed only in `Drop`,
//! i.e. after every user of the handles is gone.

use std::ffi::c_void;
use std::io;
use std::mem::size_of;
use std::path::Path;
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use parking_lot::Mutex;
use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Storage::FileSystem::{ReadFile, WriteFile};
use windows::Win32::System::Console::{
    ClosePseudoConsole, CreatePseudoConsole, ResizePseudoConsole, COORD, HPCON,
};
use windows::Win32::System::JobObjects::{
    AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
    SetInformationJobObject, TerminateJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
    JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
};
use windows::Win32::System::Pipes::CreatePipe;
use windows::Win32::System::Threading::{
    CreateProcessW, DeleteProcThreadAttributeList, GetExitCodeProcess,
    InitializeProcThreadAttributeList, ResumeThread, UpdateProcThreadAttribute,
    WaitForSingleObject, CREATE_SUSPENDED, CREATE_UNICODE_ENVIRONMENT,
    EXTENDED_STARTUPINFO_PRESENT, LPPROC_THREAD_ATTRIBUTE_LIST, PROCESS_INFORMATION,
    STARTF_USESTDHANDLES, STARTUPINFOEXW,
};

/// `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE`.
const PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE: usize = 0x0002_0016;
const STILL_ACTIVE: u32 = 259;
const ERROR_BROKEN_PIPE: i32 = 109;

fn h(raw: isize) -> HANDLE {
    HANDLE(raw as *mut c_void)
}

fn raw(handle: HANDLE) -> isize {
    handle.0 as isize
}

fn win_err(e: windows::core::Error) -> io::Error {
    io::Error::other(e.to_string())
}

/// A live pseudo console with a child process attached.
pub struct ConPty {
    hpcon: Mutex<Option<isize>>,
    in_write: isize,
    out_read: isize,
    process: isize,
    job: isize,
    pid: u32,
    write_lock: Mutex<()>,
    size: Mutex<(i16, i16)>,
    pc_closed: AtomicBool,
}

impl std::fmt::Debug for ConPty {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ConPty").field("pid", &self.pid).finish()
    }
}

fn to_wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

impl ConPty {
    /// Spawn `command_line` (full command line, already quoted) attached to a
    /// new pseudo console of `cols` x `rows`.
    pub fn spawn(command_line: &str, cwd: Option<&Path>, cols: i16, rows: i16) -> io::Result<Self> {
        unsafe {
            // Pipe A: our writes -> console input.   Pipe B: console output -> our reads.
            let mut in_read = HANDLE::default();
            let mut in_write = HANDLE::default();
            let mut out_read = HANDLE::default();
            let mut out_write = HANDLE::default();
            CreatePipe(&mut in_read, &mut in_write, None, 0).map_err(win_err)?;
            if let Err(e) = CreatePipe(&mut out_read, &mut out_write, None, 0) {
                let _ = CloseHandle(in_read);
                let _ = CloseHandle(in_write);
                return Err(win_err(e));
            }
            let close_all = |hs: &[HANDLE]| {
                for x in hs {
                    let _ = CloseHandle(*x);
                }
            };

            let hpcon = match CreatePseudoConsole(COORD { X: cols, Y: rows }, in_read, out_write, 0)
            {
                Ok(p) => p,
                Err(e) => {
                    close_all(&[in_read, in_write, out_read, out_write]);
                    return Err(win_err(e));
                }
            };
            // The pseudo console owns its copies of these ends now.
            let _ = CloseHandle(in_read);
            let _ = CloseHandle(out_write);

            // Attribute list carrying the HPCON.
            let mut attr_size: usize = 0;
            let _ = InitializeProcThreadAttributeList(None, 1, None, &mut attr_size);
            let mut attr_buf = vec![0u8; attr_size.max(1)];
            let attr_list = LPPROC_THREAD_ATTRIBUTE_LIST(attr_buf.as_mut_ptr() as *mut c_void);
            let fail = |e: io::Error, hp: HPCON, hs: &[HANDLE]| -> io::Error {
                ClosePseudoConsole(hp);
                for x in hs {
                    let _ = CloseHandle(*x);
                }
                e
            };
            if let Err(e) =
                InitializeProcThreadAttributeList(Some(attr_list), 1, None, &mut attr_size)
            {
                return Err(fail(win_err(e), hpcon, &[in_write, out_read]));
            }
            if let Err(e) = UpdateProcThreadAttribute(
                attr_list,
                0,
                PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                Some(hpcon.0 as *const c_void),
                size_of::<HPCON>(),
                None,
                None,
            ) {
                DeleteProcThreadAttributeList(attr_list);
                return Err(fail(win_err(e), hpcon, &[in_write, out_read]));
            }

            let mut si = STARTUPINFOEXW::default();
            si.StartupInfo.cb = size_of::<STARTUPINFOEXW>() as u32;
            // Parent stdio may be redirected pipes; null std handles force the
            // child onto the pseudo console instead of inheriting them.
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            si.lpAttributeList = attr_list;

            let mut cmd = to_wide(command_line);
            let cwd_w = cwd.map(|p| to_wide(&p.to_string_lossy()));
            let cwd_ptr = cwd_w
                .as_ref()
                .map(|v| PCWSTR(v.as_ptr()))
                .unwrap_or(PCWSTR::null());
            let mut pi = PROCESS_INFORMATION::default();
            let created = CreateProcessW(
                PCWSTR::null(),
                Some(PWSTR(cmd.as_mut_ptr())),
                None,
                None,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
                None,
                cwd_ptr,
                &si.StartupInfo,
                &mut pi,
            );
            DeleteProcThreadAttributeList(attr_list);
            drop(attr_buf);
            if let Err(e) = created {
                return Err(fail(win_err(e), hpcon, &[in_write, out_read]));
            }

            // Job object: kill the whole tree when the session ends.
            let mut job = HANDLE::default();
            if let Ok(j) = CreateJobObjectW(None, PCWSTR::null()) {
                let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                let ok = SetInformationJobObject(
                    j,
                    JobObjectExtendedLimitInformation,
                    &info as *const _ as *const c_void,
                    size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
                )
                .is_ok()
                    && AssignProcessToJobObject(j, pi.hProcess).is_ok();
                if ok {
                    job = j;
                } else {
                    let _ = CloseHandle(j);
                }
            }
            ResumeThread(pi.hThread);
            let _ = CloseHandle(pi.hThread);

            Ok(ConPty {
                hpcon: Mutex::new(Some(hpcon.0 as isize)),
                in_write: raw(in_write),
                out_read: raw(out_read),
                process: raw(pi.hProcess),
                job: raw(job),
                pid: pi.dwProcessId,
                write_lock: Mutex::new(()),
                size: Mutex::new((cols, rows)),
                pc_closed: AtomicBool::new(false),
            })
        }
    }

    pub fn pid(&self) -> u32 {
        self.pid
    }

    pub fn size(&self) -> (i16, i16) {
        *self.size.lock()
    }

    /// Blocking read of console output. `Ok(0)` means EOF (pseudo console
    /// closed / broken pipe).
    pub fn read(&self, buf: &mut [u8]) -> io::Result<usize> {
        let mut n: u32 = 0;
        let r = unsafe { ReadFile(h(self.out_read), Some(buf), Some(&mut n), None) };
        match r {
            Ok(()) => Ok(n as usize),
            Err(e) if e.code().0 & 0xFFFF == ERROR_BROKEN_PIPE => Ok(0),
            Err(e) => Err(win_err(e)),
        }
    }

    /// Write all of `data` to the console input. Serialised per session.
    pub fn write(&self, data: &[u8]) -> io::Result<usize> {
        let _g = self.write_lock.lock();
        let mut off = 0usize;
        while off < data.len() {
            let mut n: u32 = 0;
            unsafe { WriteFile(h(self.in_write), Some(&data[off..]), Some(&mut n), None) }
                .map_err(win_err)?;
            if n == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::WriteZero,
                    "conpty write returned 0",
                ));
            }
            off += n as usize;
        }
        Ok(off)
    }

    pub fn resize(&self, cols: i16, rows: i16) -> io::Result<()> {
        let g = self.hpcon.lock();
        let Some(p) = *g else {
            return Err(io::Error::new(
                io::ErrorKind::BrokenPipe,
                "pseudo console closed",
            ));
        };
        unsafe { ResizePseudoConsole(HPCON(p), COORD { X: cols, Y: rows }) }.map_err(win_err)?;
        *self.size.lock() = (cols, rows);
        Ok(())
    }

    /// Exit code of the child, or `None` while it is still running.
    pub fn exit_code(&self) -> Option<u32> {
        let mut code: u32 = 0;
        unsafe {
            if GetExitCodeProcess(h(self.process), &mut code).is_err() {
                return None;
            }
        }
        if code == STILL_ACTIVE {
            None
        } else {
            Some(code)
        }
    }

    pub fn is_alive(&self) -> bool {
        self.exit_code().is_none()
    }

    /// Wait for the child process to exit. Returns `true` if it exited.
    pub fn wait_exit(&self, timeout_ms: u32) -> bool {
        unsafe { WaitForSingleObject(h(self.process), timeout_ms) == WAIT_OBJECT_0 }
    }

    /// Terminate the child and every process in its job.
    pub fn terminate(&self) {
        unsafe {
            if self.job != 0 {
                let _ = TerminateJobObject(h(self.job), 1);
            } else {
                let _ = windows::Win32::System::Threading::TerminateProcess(h(self.process), 1);
            }
        }
    }

    /// Close the pseudo console (idempotent). This makes the output pipe EOF
    /// once buffered output is drained. Never call from the reader thread.
    pub fn close_pseudo_console(&self) {
        let taken = self.hpcon.lock().take();
        if let Some(p) = taken {
            unsafe { ClosePseudoConsole(HPCON(p)) };
            self.pc_closed.store(true, Ordering::SeqCst);
        }
    }

    pub fn pseudo_console_closed(&self) -> bool {
        self.pc_closed.load(Ordering::SeqCst)
    }

    /// Cancel all pending synchronous I/O operations in the specified thread.
    pub fn cancel_sync_io(thread_handle: isize) -> io::Result<()> {
        if thread_handle != 0 {
            unsafe {
                if let Err(e) = windows::Win32::System::IO::CancelSynchronousIo(h(thread_handle)) {
                    if e.code().0 & 0xFFFF == 1168 { // ERROR_NOT_FOUND
                        return Ok(());
                    }
                    return Err(win_err(e));
                }
            }
        }
        Ok(())
    }
}

impl Drop for ConPty {
    fn drop(&mut self) {
        // Kill the tree first so nothing keeps the console alive, then close.
        self.terminate();
        self.close_pseudo_console();
        unsafe {
            let _ = CloseHandle(h(self.in_write));
            let _ = CloseHandle(h(self.out_read));
            let _ = CloseHandle(h(self.process));
            if self.job != 0 {
                let _ = CloseHandle(h(self.job));
            }
        }
        let _ = ptr::null::<u8>();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::time::{Duration, Instant};

    fn drain_until(pty: &Arc<ConPty>, needle: &str, secs: u64) -> String {
        let mut out = Vec::new();
        let deadline = Instant::now() + Duration::from_secs(secs);
        let p = pty.clone();
        let (tx, rx) = std::sync::mpsc::channel::<Vec<u8>>();
        std::thread::spawn(move || {
            let mut buf = [0u8; 4096];
            while let Ok(n) = p.read(&mut buf) {
                if n == 0 || tx.send(buf[..n].to_vec()).is_err() {
                    break;
                }
            }
        });
        while Instant::now() < deadline {
            if let Ok(chunk) = rx.recv_timeout(Duration::from_millis(100)) {
                out.extend_from_slice(&chunk);
                if String::from_utf8_lossy(&out).contains(needle) {
                    break;
                }
            }
        }
        String::from_utf8_lossy(&out).into_owned()
    }

    #[test]
    fn cmd_echo_roundtrip_and_exit() {
        let pty = Arc::new(
            ConPty::spawn("cmd.exe /Q /K", None, 120, 30).expect("spawn cmd under conpty"),
        );
        assert!(pty.is_alive());
        pty.write(b"echo conpty_marker_42\r").unwrap();
        let text = drain_until(&pty, "conpty_marker_42", 10);
        assert!(text.contains("conpty_marker_42"), "got: {text:?}");
        pty.write(b"exit 7\r").unwrap();
        assert!(pty.wait_exit(10_000));
        assert_eq!(pty.exit_code(), Some(7));
        pty.close_pseudo_console();
        pty.close_pseudo_console(); // idempotent
    }

    #[test]
    fn oneshot_output_reaches_pty_not_parent_stdio() {
        let pty = Arc::new(ConPty::spawn("cmd.exe /c echo hi_there_77", None, 120, 30).unwrap());
        let p = pty.clone();
        let t = std::thread::spawn(move || {
            let mut all = Vec::new();
            let mut buf = [0u8; 4096];
            while let Ok(n) = p.read(&mut buf) {
                if n == 0 {
                    break;
                }
                all.extend_from_slice(&buf[..n]);
            }
            String::from_utf8_lossy(&all).into_owned()
        });
        assert!(pty.wait_exit(10_000));
        std::thread::sleep(Duration::from_millis(300));
        pty.close_pseudo_console();
        let out = t.join().unwrap();
        assert!(out.contains("hi_there_77"), "got: {out:?}");
    }

    #[test]
    fn terminate_kills_child() {
        let pty = ConPty::spawn("cmd.exe /K", None, 80, 24).unwrap();
        assert!(pty.is_alive());
        pty.terminate();
        assert!(pty.wait_exit(10_000));
        assert!(!pty.is_alive());
    }
}
