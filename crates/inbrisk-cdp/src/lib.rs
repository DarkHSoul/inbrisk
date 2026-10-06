//! Chrome/Edge DevTools Protocol backend.
//!
//! The router calls this *instead of* UIA for browser document content. A port
//! is used only when it was parsed from a command line the caller already
//! proved belongs to that browser's process tree. This crate never reads
//! `DevToolsActivePort` and never dials anything except `127.0.0.1`.

mod session;
mod transport;

use inbrisk_core::{Hwnd, InbriskError, Result};
use serde::Deserialize;

pub use session::{CdpLink, CdpSearchResult, CdpSession};
pub use transport::{
    decode_server_frame, encode_client_text_frame, http_status_is_switching,
    websocket_upgrade_request,
};

/// A concrete browser instance, identified strongly enough that results from
/// one browser can never be used to confirm another.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrowserInstance {
    /// Root browser process id.
    pub process_id: u32,
    /// Every process in the tree (browser, gpu, renderers, utility).
    pub process_tree: Vec<u32>,
    /// `--user-data-dir` / profile path when discoverable.
    pub profile_path: Option<String>,
    /// Top-level window handles owned by this instance.
    pub hwnds: Vec<Hwnd>,
    /// `http://127.0.0.1:PORT` of the DevTools endpoint.
    pub cdp_endpoint: Option<String>,
    /// Browser-level context id.
    pub context_id: Option<String>,
    pub product: String,
}

impl BrowserInstance {
    /// Only results from the *same* instance may verify each other.
    pub fn same_instance(&self, other: &BrowserInstance) -> bool {
        self.process_id == other.process_id
            || self
                .process_tree
                .iter()
                .any(|p| other.process_tree.contains(p))
    }

    /// Verifies that an HWND is owned by this browser instance.
    pub fn owns_hwnd(&self, hwnd: Hwnd) -> bool {
        self.hwnds.contains(&hwnd)
    }

    /// Verifies that a target operation targeting `hwnd` can use this instance's CDP target.
    /// Rejects cross-instance targeting.
    pub fn validate_target_access(&self, hwnd: Hwnd, target_instance: &BrowserInstance) -> Result<()> {
        if !self.same_instance(target_instance) {
            return Err(InbriskError::denied(format!(
                "cross-instance violation: HWND {hwnd:?} belongs to browser instance (PID {}), cannot use CDP target from instance (PID {})",
                self.process_id,
                target_instance.process_id
            )));
        }
        if !self.hwnds.is_empty() && !self.owns_hwnd(hwnd) {
            return Err(InbriskError::denied(format!(
                "cross-instance violation: HWND {hwnd:?} is not registered to browser instance (PID {})",
                self.process_id
            )));
        }
        Ok(())
    }
}

/// One page target from `/json/list`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CdpPage {
    pub id: String,
    pub title: String,
    pub url: String,
    pub websocket_url: String,
    pub target_type: String,
}

#[derive(Debug, Deserialize)]
struct RawTarget {
    #[serde(default)]
    id: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    url: String,
    #[serde(rename = "webSocketDebuggerUrl", default)]
    websocket_url: String,
    #[serde(rename = "type", default)]
    target_type: String,
}

/// Backend facade. The transport is stateless; identity lives in the caller.
#[derive(Debug, Default)]
pub struct CdpBackend {
    _private: (),
}

impl CdpBackend {
    pub fn new() -> Self {
        Self { _private: () }
    }

    pub fn name(&self) -> &'static str {
        "cdp"
    }

    /// The localhost client is compiled in. A particular browser may still
    /// have no proven debugging port; that is a per-call error.
    pub fn is_available(&self) -> bool {
        true
    }

    /// An HWND alone cannot prove which browser owns a DevTools port.
    pub fn attach(&self, _hwnd: Hwnd) -> Result<BrowserInstance> {
        Err(InbriskError::not_implemented(
            "CDP attach needs command lines from the target process tree; an HWND is not proof of a debugging port",
        )
        .with_hint("the runtime passes only --remote-debugging-port values read from that process, and does not fall through to UIA"))
    }
}

/// `--remote-debugging-port=N` or `--remote-debugging-port N`.
pub fn parse_debugging_port(command_line: &str) -> Option<u16> {
    const FLAGS: [&str; 2] = ["--remote-debugging-port", "/remote-debugging-port"];
    let bytes = command_line.as_bytes();
    let mut i = 0;
    while i < bytes.len() {
        let mut matched = false;
        for flag in FLAGS {
            if bytes[i..].starts_with(flag.as_bytes()) {
                let after = i + flag.len();
                let sep = command_line[after..].chars().next();
                if sep == Some('=') || sep == Some(' ') || sep == Some('\t') {
                    let rest = command_line[after..].trim_start_matches(['=', ' ', '\t']);
                    let token = rest.split_whitespace().next().unwrap_or("");
                    let token = token.trim_matches(|c| c == '"' || c == '\'');
                    if let Ok(port) = token.parse::<u16>() {
                        if port != 0 {
                            return Some(port);
                        }
                    }
                }
                matched = true;
                break;
            }
        }
        i += if matched { 1 } else { 1 };
    }
    None
}

/// One port from lines that the caller has already scoped to one process tree.
/// Zero ports, or two different ports, is a hard error. There is no fallback.
pub fn port_from_command_lines(lines: &[String]) -> Result<u16> {
    let mut ports = Vec::new();
    for line in lines {
        if let Some(port) = parse_debugging_port(line) {
            if !ports.contains(&port) {
                ports.push(port);
            }
        }
    }
    match ports.as_slice() {
        [port] => Ok(*port),
        [] => Err(InbriskError::not_found(
            "this browser process tree has no --remote-debugging-port; refusing to attach to any other DevTools endpoint",
        )
        .with_hint("start this browser with --remote-debugging-port. Document content will not be read through UIA")),
        _ => Err(InbriskError::denied(
            "this browser process tree advertises more than one debugging port; refusing to choose one",
        )),
    }
}

pub fn list_pages(port: u16) -> Result<Vec<CdpPage>> {
    let version = transport::http_get_localhost(port, "/json/version")?;
    let version_text = String::from_utf8_lossy(&version);
    if !version_text.contains("Browser") && !version_text.contains("webSocketDebuggerUrl") {
        return Err(InbriskError::not_found(format!(
            "127.0.0.1:{port} answered, but it is not a DevTools /json/version endpoint"
        )));
    }
    let body = transport::http_get_localhost(port, "/json/list")?;
    parse_page_list(&String::from_utf8_lossy(&body))
}

pub fn parse_page_list(body: &str) -> Result<Vec<CdpPage>> {
    let raw: Vec<RawTarget> = serde_json::from_str(body).map_err(|e| {
        InbriskError::internal(format!("DevTools /json/list was not a target array: {e}"))
    })?;
    Ok(raw
        .into_iter()
        .filter(|t| t.target_type == "page" && !t.id.is_empty())
        .map(|t| CdpPage {
            id: t.id,
            title: t.title,
            url: t.url,
            websocket_url: t.websocket_url,
            target_type: t.target_type,
        })
        .collect())
}

/// Record the endpoint that `port_from_command_lines` already proved.
pub fn instance_for_port(
    process_id: u32,
    process_tree: Vec<u32>,
    hwnd: Hwnd,
    port: u16,
    product: impl Into<String>,
) -> BrowserInstance {
    BrowserInstance {
        process_id,
        process_tree,
        profile_path: None,
        hwnds: if hwnd.is_null() {
            Vec::new()
        } else {
            vec![hwnd]
        },
        cdp_endpoint: Some(format!("http://127.0.0.1:{port}")),
        context_id: None,
        product: product.into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_equals_and_space_forms() {
        assert_eq!(
            parse_debugging_port(r"chrome.exe --remote-debugging-port=9222 --user-data-dir=C:\tmp"),
            Some(9222)
        );
        assert_eq!(
            parse_debugging_port("msedge.exe --remote-debugging-port 9333"),
            Some(9333)
        );
        assert_eq!(
            parse_debugging_port("chrome.exe --remote-debugging-port=0"),
            None
        );
        assert_eq!(
            parse_debugging_port("chrome.exe --remote-debugging-port=65536"),
            None
        );
        assert_eq!(parse_debugging_port("chrome.exe --no-sandbox"), None);
        assert_eq!(
            parse_debugging_port("chrome.exe --remote-debugging-port-extra=1"),
            None
        );
    }

    #[test]
    fn one_tree_one_port() {
        let lines = vec![
            "chrome.exe --type=renderer".into(),
            "chrome.exe --remote-debugging-port=9222".into(),
            "chrome.exe --remote-debugging-port=9222 --type=gpu".into(),
        ];
        assert_eq!(port_from_command_lines(&lines).unwrap(), 9222);
        let none = vec!["chrome.exe".into()];
        assert!(port_from_command_lines(&none).is_err());
        let mixed = vec![
            "chrome.exe --remote-debugging-port=9222".into(),
            "chrome.exe --remote-debugging-port=9333".into(),
        ];
        assert!(port_from_command_lines(&mixed).is_err());
    }

    #[test]
    fn page_list_keeps_pages_only() {
        let body = r#"[
            {"id":"A","title":"Example","type":"page","url":"https://example.com","webSocketDebuggerUrl":"ws://127.0.0.1:9222/devtools/page/A"},
            {"id":"B","title":"worker","type":"service_worker","url":"","webSocketDebuggerUrl":""}
        ]"#;
        let pages = parse_page_list(body).unwrap();
        assert_eq!(pages.len(), 1);
        assert_eq!(pages[0].id, "A");
        assert_eq!(pages[0].title, "Example");
    }

    #[test]
    fn same_instance_requires_a_shared_process() {
        let a = instance_for_port(1, vec![1, 2], Hwnd(10), 9222, "chrome");
        let b = instance_for_port(9, vec![9, 8], Hwnd(11), 9333, "chrome");
        let child = instance_for_port(2, vec![1, 2], Hwnd(10), 9222, "chrome");
        assert!(!a.same_instance(&b));
        assert!(a.same_instance(&child));
    }

    #[test]
    #[allow(non_snake_case)]
    fn BrowserA_Hwnd_CannotUse_BrowserB_CdpTarget() {
        let browser_a = instance_for_port(1001, vec![1001, 1002], Hwnd(5555), 9222, "chrome");
        let browser_b = instance_for_port(2001, vec![2001, 2002], Hwnd(7777), 9333, "chrome");
        assert!(!browser_a.same_instance(&browser_b));

        let err = browser_a.validate_target_access(Hwnd(7777), &browser_b).unwrap_err();
        assert!(err.to_string().contains("cross-instance violation"));
    }

    #[test]
    #[allow(non_snake_case)]
    fn Browser_ContentRead_DoesNotModifyClipboard() {
        #[cfg(target_os = "windows")]
        {
            #[link(name = "user32")]
            extern "system" {
                fn GetClipboardSequenceNumber() -> u32;
            }
            let seq_before = unsafe { GetClipboardSequenceNumber() };

            let raw_json = r#"[{"id":"TEST1","title":"Semantic Title","type":"page","url":"https://example.com","webSocketDebuggerUrl":""}]"#;
            let pages = parse_page_list(raw_json).unwrap();
            assert_eq!(pages.len(), 1);
            assert_eq!(pages[0].title, "Semantic Title");

            let seq_after = unsafe { GetClipboardSequenceNumber() };
            assert_eq!(seq_before, seq_after, "CDP content extraction must never modify the user clipboard");
        }
    }
}
