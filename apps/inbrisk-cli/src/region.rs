//! The shared region the runtime is expected to publish.
//!
//! `status`, `doctor` and `endpoint` must be able to name the region even when
//! nothing is listening, so the user can tell "the runtime was never started"
//! apart from "it is running under a different user scope". The rule mirrors
//! the IPC layer (`inbrisk-ipc`): the object lives in the per-logon-session
//! `Local\` namespace and embeds the current user's SID.
//!
//! The SID lookup is repeated here instead of imported because the CLI is only
//! allowed to depend on `inbrisk-core`, `inbrisk-protocol` and `inbrisk-sdk`;
//! the runtime-facing name is produced by `IpcClient::region_name()` whenever a
//! connection succeeds, so this path is only used for the no-runtime report.

/// Object name the runtime publishes for the current user.
pub fn expected_region_name() -> String {
    format!("Local\\Inbrisk.Runtime.{}", user_scope())
}

/// Exact wording used whenever the runtime cannot be reached.
pub fn runtime_unavailable_message() -> String {
    "inbrisk runtime is not running (no shared region for this user). Start inbrisk.exe, then retry."
        .to_string()
}

fn user_scope() -> String {
    #[cfg(windows)]
    {
        if let Some(sid) = platform::current_user_sid_string() {
            return sid;
        }
    }
    fallback_scope()
}

/// Is a shared region published for this user right now?
///
/// This opens and immediately closes the mapping instead of using the SDK
/// probe: the probe claims a session (and therefore fails for reasons that are
/// *not* "no runtime"), while this answers exactly one question. It lets the CLI
/// tell "inbrisk.exe was never started" apart from "inbrisk.exe is up but
/// refused the handshake".
pub fn is_published() -> bool {
    #[cfg(windows)]
    let published = platform::is_region_published(&expected_region_name());
    #[cfg(not(windows))]
    let published = false;
    published
}

/// Last resort: never produce a machine-global name, even without a SID.
fn fallback_scope() -> String {
    let user = std::env::var("USERNAME").unwrap_or_else(|_| "unknown".into());
    let domain = std::env::var("USERDOMAIN").unwrap_or_default();
    format!("{domain}-{user}")
}

#[cfg(windows)]
mod platform {
    //! Token SID lookup, mirroring `inbrisk-ipc`'s security module.

    use windows::core::PWSTR;
    use windows::Win32::Foundation::{CloseHandle, LocalFree, HANDLE, HLOCAL};
    use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
    use windows::Win32::Security::{GetTokenInformation, TokenUser, TOKEN_QUERY, TOKEN_USER};
    use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

    /// `S-1-5-...` for this process's user, or `None` when Windows refuses.
    pub fn current_user_sid_string() -> Option<String> {
        unsafe {
            let mut token = HANDLE::default();
            OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).ok()?;

            // Two-call pattern: ask for the size, then for the data.
            let mut needed = 0u32;
            let _ = GetTokenInformation(token, TokenUser, None, 0, &mut needed);
            if needed == 0 {
                let _ = CloseHandle(token);
                return None;
            }
            let mut buffer = vec![0u8; needed as usize];
            let read = GetTokenInformation(
                token,
                TokenUser,
                Some(buffer.as_mut_ptr() as *mut core::ffi::c_void),
                needed,
                &mut needed,
            );
            let _ = CloseHandle(token);
            read.ok()?;

            let token_user = &*(buffer.as_ptr() as *const TOKEN_USER);
            let mut raw = PWSTR::null();
            ConvertSidToStringSidW(token_user.User.Sid, &mut raw).ok()?;
            if raw.is_null() {
                return None;
            }
            let text = raw.to_string().ok();
            let _ = LocalFree(Some(HLOCAL(raw.0 as *mut core::ffi::c_void)));
            text
        }
    }

    /// True when a mapping with this object name currently exists.
    pub fn is_region_published(name: &str) -> bool {
        use windows::core::PCWSTR;
        use windows::Win32::System::Memory::{OpenFileMappingW, FILE_MAP_READ};

        let wide: Vec<u16> = name.encode_utf16().chain(std::iter::once(0)).collect();
        unsafe {
            match OpenFileMappingW(FILE_MAP_READ.0, false, PCWSTR(wide.as_ptr())) {
                Ok(handle) => {
                    let _ = CloseHandle(handle);
                    true
                }
                Err(_) => false,
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn region_name_is_user_scoped() {
        let name = expected_region_name();
        assert!(name.starts_with("Local\\Inbrisk.Runtime."), "got {name}");
        assert!(name.len() > "Local\\Inbrisk.Runtime.".len());
    }

    #[test]
    fn unavailable_message_names_the_retry_step() {
        let message = runtime_unavailable_message();
        assert!(message.contains("is not running"));
        assert!(message.contains("inbrisk.exe"));
    }
}
