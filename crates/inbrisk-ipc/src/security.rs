//! User-scoped security for the shared region.
//!
//! Three layers:
//! 1. the object name lives in the `Local\` namespace (per logon session) and
//!    embeds the current user's SID, so another user cannot even find it;
//! 2. the mapping is created with a DACL that grants access to *only* that
//!    user's SID, so another principal cannot open it even if it guesses the
//!    name;
//! 3. the mandatory label is Low. A section otherwise inherits the creator's
//!    integrity, and Windows then denies `FILE_MAP_ALL_ACCESS` to anything
//!    below that level. Antigravity launches `inbrisk-mcp.exe` at Low
//!    integrity, so a Medium or High label looks exactly like "no runtime is
//!    listening". Low means that sandboxed adapter can open the region. The
//!    DACL still limits it to this user.
//!
//! If the DACL cannot be built the runtime falls back to layer 1 and logs it —
//! connectivity is never sacrificed for hardening, but the fallback is loud.
//! If only the Low label cannot be applied, the DACL is kept and a warning is
//! logged: same-integrity clients still connect, sandboxed ones do not.

use std::mem::size_of;

use inbrisk_core::{log_warn, Result};
use windows::core::PWSTR;
use windows::Win32::Foundation::{CloseHandle, LocalFree, HANDLE};
use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
use windows::Win32::Security::{
    AddAccessAllowedAce, AddMandatoryAce, CreateWellKnownSid, GetLengthSid, GetTokenInformation,
    InitializeAcl, InitializeSecurityDescriptor, SetSecurityDescriptorDacl,
    SetSecurityDescriptorSacl, TokenUser, WinLowLabelSid, ACCESS_ALLOWED_ACE, ACE_FLAGS, ACL,
    ACL_REVISION, PSID, SECURITY_ATTRIBUTES, SECURITY_DESCRIPTOR, TOKEN_QUERY, TOKEN_USER,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};

/// Rights for both objects that share this descriptor.
///
/// `FILE_MAP_ALL_ACCESS` is `0x000F001F`. `EVENT_ALL_ACCESS` is `0x001F0003`
/// (`STANDARD_RIGHTS_REQUIRED | SYNCHRONIZE | 0x3`). A section ACE that omits
/// `SYNCHRONIZE` makes `CreateEventW` on the wake events fail with access
/// denied, which is what a sandboxed client sees. The union covers an open of
/// either object.
const IPC_OBJECT_MASK: u32 = 0x001F_001F;
const SECURITY_DESCRIPTOR_REVISION: u32 = 1;
/// `SYSTEM_MANDATORY_LABEL_NO_WRITE_UP`. Callers below the label cannot write.
const MANDATORY_NO_WRITE_UP: u32 = 1;

/// The current user's SID as `S-1-5-...`.
pub fn current_user_sid_string() -> Option<String> {
    let sid = current_user_sid()?;
    unsafe {
        let mut raw = PWSTR::null();
        ConvertSidToStringSidW(PSID(sid.as_ptr() as *mut core::ffi::c_void), &mut raw).ok()?;
        if raw.is_null() {
            return None;
        }
        let s = raw.to_string().ok();
        let _ = LocalFree(Some(windows::Win32::Foundation::HLOCAL(
            raw.0 as *mut core::ffi::c_void,
        )));
        s
    }
}

/// Raw SID bytes of the current process token's user.
fn current_user_sid() -> Option<Vec<u8>> {
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).ok()?;
        let mut needed = 0u32;
        let _ = GetTokenInformation(token, TokenUser, None, 0, &mut needed);
        if needed == 0 {
            let _ = CloseHandle(token);
            return None;
        }
        let mut buf = vec![0u8; needed as usize];
        let res = GetTokenInformation(
            token,
            TokenUser,
            Some(buf.as_mut_ptr() as *mut core::ffi::c_void),
            needed,
            &mut needed,
        );
        let _ = CloseHandle(token);
        res.ok()?;
        let token_user = &*(buf.as_ptr() as *const TOKEN_USER);
        let sid_ptr = token_user.User.Sid.0 as *const u8;
        if sid_ptr.is_null() {
            return None;
        }
        let len = GetLengthSid(PSID(sid_ptr as *mut core::ffi::c_void)) as usize;
        if len == 0 {
            return None;
        }
        Some(std::slice::from_raw_parts(sid_ptr, len).to_vec())
    }
}

/// Owns every buffer a `SECURITY_ATTRIBUTES` points at, so the pointers stay
/// valid for as long as the structure is alive.
pub struct UserOnlyAttributes {
    sid: Vec<u8>,
    /// Empty when the Low mandatory label could not be attached.
    label_sid: Vec<u8>,
    acl: Vec<u8>,
    /// Empty when the Low mandatory label could not be attached.
    sacl: Vec<u8>,
    sd: Vec<u8>,
    sa: SECURITY_ATTRIBUTES,
    valid: bool,
}

impl UserOnlyAttributes {
    /// Returns `None` when the platform refuses to build a user-only DACL.
    pub fn new() -> Option<Self> {
        let sid = current_user_sid()?;
        let sid_len = sid.len() as u32;

        // sizeof(ACL) + sizeof(ACCESS_ALLOWED_ACE) + sid - sizeof(DWORD) + slack
        let acl_len = (size_of::<ACL>() + size_of::<ACCESS_ALLOWED_ACE>() + sid_len as usize
            - size_of::<u32>()
            + 32) as u32;
        let mut acl = vec![0u8; acl_len as usize];
        let mut sd = vec![0u8; size_of::<SECURITY_DESCRIPTOR>()];

        let acl_ptr = acl.as_mut_ptr() as *mut ACL;
        let sd_ptr = sd.as_mut_ptr() as *mut core::ffi::c_void;
        let sid_psid = PSID(sid.as_ptr() as *mut core::ffi::c_void);

        let ok = unsafe {
            let a = InitializeAcl(acl_ptr, acl_len, ACL_REVISION);
            if a.is_err() {
                false
            } else {
                let b = AddAccessAllowedAce(acl_ptr, ACL_REVISION, IPC_OBJECT_MASK, sid_psid);
                if b.is_err() {
                    false
                } else {
                    let c = InitializeSecurityDescriptor(
                        windows::Win32::Security::PSECURITY_DESCRIPTOR(sd_ptr),
                        SECURITY_DESCRIPTOR_REVISION,
                    );
                    if c.is_err() {
                        false
                    } else {
                        SetSecurityDescriptorDacl(
                            windows::Win32::Security::PSECURITY_DESCRIPTOR(sd_ptr),
                            true,
                            Some(acl_ptr as *const ACL),
                            false,
                        )
                        .is_ok()
                    }
                }
            }
        };

        if !ok {
            log_warn!(
                "ipc.security",
                "could not build a user-only DACL; relying on the per-user Local\\ namespace"
            );
            return None;
        }

        let (label_sid, sacl) = match attach_low_label(sd_ptr) {
            Some(pair) => pair,
            None => {
                log_warn!(
                    "ipc.security",
                    "could not label the region Low; a sandboxed MCP adapter cannot open it"
                );
                (Vec::new(), Vec::new())
            }
        };

        let sa = SECURITY_ATTRIBUTES {
            nLength: size_of::<SECURITY_ATTRIBUTES>() as u32,
            lpSecurityDescriptor: sd_ptr,
            bInheritHandle: windows::core::BOOL(0),
        };

        Some(Self {
            sid,
            label_sid,
            acl,
            sacl,
            sd,
            sa,
            valid: true,
        })
    }

    pub fn as_ptr(&self) -> *const SECURITY_ATTRIBUTES {
        &self.sa
    }

    pub fn is_valid(&self) -> bool {
        self.valid
    }

    /// Keep the backing buffers alive for the lifetime of the mapping call.
    pub fn touch(&self) {
        std::hint::black_box((
            self.sid.len(),
            self.label_sid.len(),
            self.acl.len(),
            self.sacl.len(),
            self.sd.len(),
        ));
    }
}

/// Point the descriptor's SACL at a Low integrity label.
///
/// The returned buffers own the SID and the SACL. Their heap addresses are
/// already stored in the descriptor, and moving the `Vec`s does not move
/// those allocations.
fn attach_low_label(sd_ptr: *mut core::ffi::c_void) -> Option<(Vec<u8>, Vec<u8>)> {
    let label = low_integrity_sid()?;
    let sacl_len = (size_of::<ACL>() + size_of::<ACCESS_ALLOWED_ACE>() + label.len()
        - size_of::<u32>()
        + 32) as u32;
    let mut sacl = vec![0u8; sacl_len as usize];
    let sacl_ptr = sacl.as_mut_ptr() as *mut ACL;
    let label_psid = PSID(label.as_ptr() as *mut core::ffi::c_void);
    let ok = unsafe {
        InitializeAcl(sacl_ptr, sacl_len, ACL_REVISION).is_ok()
            && AddMandatoryAce(
                sacl_ptr,
                ACL_REVISION,
                ACE_FLAGS(0),
                MANDATORY_NO_WRITE_UP,
                label_psid,
            )
            .is_ok()
            && SetSecurityDescriptorSacl(
                windows::Win32::Security::PSECURITY_DESCRIPTOR(sd_ptr),
                true,
                Some(sacl_ptr as *const ACL),
                false,
            )
            .is_ok()
    };
    if ok {
        Some((label, sacl))
    } else {
        None
    }
}

fn low_integrity_sid() -> Option<Vec<u8>> {
    let mut buf = vec![0u8; 68];
    let mut len = buf.len() as u32;
    let ok = unsafe {
        CreateWellKnownSid(
            WinLowLabelSid,
            None,
            Some(PSID(buf.as_mut_ptr() as *mut core::ffi::c_void)),
            &mut len,
        )
        .is_ok()
    };
    if !ok || len == 0 || len as usize > buf.len() {
        return None;
    }
    buf.truncate(len as usize);
    Some(buf)
}

/// Build the per-user object name: `Local\Inbrisk.Runtime.<SID>`.
pub fn region_name() -> Result<String> {
    let sid = current_user_sid_string().unwrap_or_else(fallback_scope);
    Ok(format!("Local\\Inbrisk.Runtime.{sid}"))
}

fn fallback_scope() -> String {
    // Extremely defensive: if the token query fails we still avoid a
    // machine-global name.
    let user = std::env::var("USERNAME").unwrap_or_else(|_| "unknown".into());
    let domain = std::env::var("USERDOMAIN").unwrap_or_default();
    format!("{domain}-{user}")
}
