use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::time::{Duration, Instant};

use inbrisk_core::{InbriskError, Result};
use serde::{Deserialize, Serialize};
use serde_json::Value;

use crate::transport::{
    decode_server_frame, encode_client_text_frame, http_status_is_switching,
    websocket_upgrade_request,
};

/// Result of searching a text term inside DOM content via CDP.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CdpSearchResult {
    pub query: String,
    pub match_count: usize,
    pub found: bool,
    pub first_match_excerpt: String,
}

/// A hyperlink resolved from the DOM via CDP.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct CdpLink {
    pub text: String,
    pub href: String,
}

/// A synchronous WebSocket session to a single CDP page target on localhost.
pub struct CdpSession {
    stream: TcpStream,
    buf: Vec<u8>,
    next_id: u32,
    pub port: u16,
    pub target_id: String,
    pub cdp_requests_count: usize,
}

impl CdpSession {
    /// Connect to a specific target on `127.0.0.1:{port}`.
    pub fn connect(port: u16, target_id: &str) -> Result<Self> {
        if port == 0 {
            return Err(InbriskError::not_found("refusing DevTools port 0"));
        }
        let addr = SocketAddr::from(([127, 0, 0, 1], port));
        let mut stream = TcpStream::connect_timeout(&addr, Duration::from_millis(1500)).map_err(|e| {
            InbriskError::not_found(format!("CDP connect to 127.0.0.1:{port} failed: {e}"))
        })?;
        stream.set_read_timeout(Some(Duration::from_millis(10_000))).ok();
        stream.set_write_timeout(Some(Duration::from_millis(5_000))).ok();

        let path = format!("/devtools/page/{target_id}");
        let upgrade_req = websocket_upgrade_request(
            &format!("127.0.0.1:{port}"),
            &path,
            "dGhlIHNhbXBsZSBub25jZQ==",
        )?;

        stream
            .write_all(upgrade_req.as_bytes())
            .map_err(|e| InbriskError::internal(format!("CDP upgrade write failed: {e}")))?;

        // Read until \r\n\r\n
        let mut head_buf = Vec::new();
        let mut temp = [0u8; 1024];
        while head_buf.len() < 4096 {
            let n = stream
                .read(&mut temp)
                .map_err(|e| InbriskError::internal(format!("CDP upgrade read failed: {e}")))?;
            if n == 0 {
                break;
            }
            head_buf.extend_from_slice(&temp[..n]);
            if let Some(pos) = head_buf.windows(4).position(|w| w == b"\r\n\r\n") {
                let header = String::from_utf8_lossy(&head_buf[..pos]);
                if !http_status_is_switching(&header) {
                    return Err(InbriskError::internal(format!(
                        "CDP WebSocket upgrade rejected with: {header}"
                    )));
                }
                let remaining = head_buf[pos + 4..].to_vec();
                return Ok(Self {
                    stream,
                    buf: remaining,
                    next_id: 1,
                    port,
                    target_id: target_id.to_string(),
                    cdp_requests_count: 0,
                });
            }
        }

        Err(InbriskError::internal("CDP upgrade response was incomplete"))
    }

    /// Send a CDP command and await its correlated response.
    pub fn send_command(&mut self, method: &str, params: Value) -> Result<Value> {
        self.cdp_requests_count += 1;
        let id = self.next_id;
        self.next_id += 1;

        let req = serde_json::json!({
            "id": id,
            "method": method,
            "params": params,
        });

        let json_text = serde_json::to_string(&req).map_err(|e| {
            InbriskError::internal(format!("Failed to serialize CDP command: {e}"))
        })?;

        let frame = encode_client_text_frame(&json_text, [0x31, 0x32, 0x33, 0x34]);
        self.stream
            .write_all(&frame)
            .map_err(|e| InbriskError::internal(format!("Failed to write CDP frame: {e}")))?;

        let start = Instant::now();
        let timeout = Duration::from_millis(15_000);

        while start.elapsed() < timeout {
            while let Some((opcode, text, used)) = decode_server_frame(&self.buf) {
                self.buf.drain(..used);
                if opcode == 0x01 {
                    // Text frame
                    if let Ok(val) = serde_json::from_str::<Value>(&text) {
                        if val.get("id").and_then(|v| v.as_u64()) == Some(id as u64) {
                            if let Some(err) = val.get("error") {
                                return Err(InbriskError::internal(format!(
                                    "CDP error for {method}: {err}"
                                )));
                            }
                            return Ok(val.get("result").cloned().unwrap_or(Value::Null));
                        }
                    }
                }
            }

            let mut chunk = [0u8; 8192];
            match self.stream.read(&mut chunk) {
                Ok(0) => break,
                Ok(n) => self.buf.extend_from_slice(&chunk[..n]),
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock || e.kind() == std::io::ErrorKind::TimedOut => {
                    continue;
                }
                Err(e) => return Err(InbriskError::internal(format!("CDP stream read error: {e}"))),
            }
        }

        Err(InbriskError::new(
            inbrisk_core::ErrorCode::Timeout,
            format!("CDP command {method} timed out awaiting response id {id}"),
        ))
    }

    /// Evaluate a JavaScript expression in the page and return the raw result value.
    pub fn evaluate(&mut self, expression: &str) -> Result<Value> {
        let res = self.send_command(
            "Runtime.evaluate",
            serde_json::json!({
                "expression": expression,
                "returnByValue": true,
                "awaitPromise": true,
            }),
        )?;

        if let Some(exc) = res.get("exceptionDetails") {
            return Err(InbriskError::internal(format!(
                "CDP JS exception evaluating '{expression}': {exc}"
            )));
        }

        let val = res
            .get("result")
            .and_then(|r| r.get("value"))
            .cloned()
            .unwrap_or(Value::Null);
        Ok(val)
    }

    /// Navigate page to URL via CDP.
    pub fn navigate(&mut self, url: &str) -> Result<()> {
        self.send_command("Page.enable", serde_json::json!({}))?;
        self.evaluate("window.__inbrisk_nav = 1;").ok();
        self.send_command(
            "Page.navigate",
            serde_json::json!({
                "url": url,
            }),
        )?;
        Ok(())
    }

    /// Wait for document.readyState === "complete" and new execution context.
    pub fn wait_for_ready(&mut self, timeout: Duration) -> Result<()> {
        let start = Instant::now();
        std::thread::sleep(Duration::from_millis(60));
        while start.elapsed() < timeout {
            let res = self.evaluate(
                "(() => typeof window.__inbrisk_nav === 'undefined' && document.readyState === 'complete')()",
            )?;
            if res.as_bool() == Some(true) {
                return Ok(());
            }
            std::thread::sleep(Duration::from_millis(100));
        }
        Ok(())
    }

    /// Read document title via CDP.
    pub fn get_title(&mut self) -> Result<String> {
        let val = self.evaluate("document.title")?;
        Ok(val.as_str().unwrap_or("").to_string())
    }

    /// Read visible page content text via CDP (Zero Clipboard).
    pub fn get_text(&mut self) -> Result<String> {
        let val = self.evaluate("document.body ? document.body.innerText : ''")?;
        Ok(val.as_str().unwrap_or("").to_string())
    }

    /// Perform structured DOM text search via CDP (no Ctrl+F).
    pub fn search_text(&mut self, term: &str) -> Result<CdpSearchResult> {
        let js = format!(
            r#"(() => {{
                const text = document.body ? document.body.innerText : '';
                const term = {};
                const lowerText = text.toLowerCase();
                const lowerTerm = term.toLowerCase();
                let count = 0;
                let pos = 0;
                let firstExcerpt = '';
                while ((pos = lowerText.indexOf(lowerTerm, pos)) !== -1) {{
                    if (count === 0) {{
                        const start = Math.max(0, pos - 40);
                        const end = Math.min(text.length, pos + term.length + 40);
                        firstExcerpt = text.slice(start, end).replace(/\s+/g, ' ').trim();
                    }}
                    count++;
                    pos += Math.max(1, lowerTerm.length);
                }}
                return {{
                    query: term,
                    match_count: count,
                    found: count > 0,
                    first_match_excerpt: firstExcerpt
                }};
            }})()"#,
            serde_json::to_string(term).unwrap()
        );

        let res = self.evaluate(&js)?;
        let query = res.get("query").and_then(|v| v.as_str()).unwrap_or(term).to_string();
        let match_count = res.get("match_count").and_then(|v| v.as_u64()).unwrap_or(0) as usize;
        let found = res.get("found").and_then(|v| v.as_bool()).unwrap_or(match_count > 0);
        let first_match_excerpt = res
            .get("first_match_excerpt")
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();

        Ok(CdpSearchResult {
            query,
            match_count,
            found,
            first_match_excerpt,
        })
    }

    /// Retrieve hyperlinks in page content via CDP DOM query.
    pub fn get_content_links(&mut self, limit: usize) -> Result<Vec<CdpLink>> {
        let js = format!(
            r#"(() => {{
                const links = Array.from(document.querySelectorAll('a[href]'));
                const out = [];
                for (const a of links) {{
                    const href = a.getAttribute('href') || '';
                    const fullHref = a.href || '';
                    const text = (a.innerText || a.textContent || '').trim();
                    if (href && !href.startsWith('#') && !href.startsWith('javascript:') && text.length > 2) {{
                        out.push({{ text: text.slice(0, 100), href: fullHref }});
                        if (out.length >= {limit}) break;
                    }}
                }}
                return out;
            }})()"#
        );

        let res = self.evaluate(&js)?;
        let mut links = Vec::new();
        if let Some(arr) = res.as_array() {
            for item in arr {
                let text = item.get("text").and_then(|v| v.as_str()).unwrap_or("").to_string();
                let href = item.get("href").and_then(|v| v.as_str()).unwrap_or("").to_string();
                if !href.is_empty() {
                    links.push(CdpLink { text, href });
                }
            }
        }
        Ok(links)
    }

    /// Read current page URL via CDP.
    pub fn get_url(&mut self) -> Result<String> {
        let val = self.evaluate("window.location.href")?;
        Ok(val.as_str().unwrap_or("").to_string())
    }

    /// Navigate back in browser history via CDP.
    pub fn navigate_back(&mut self) -> Result<()> {
        self.evaluate("window.__inbrisk_nav = 1;").ok();
        self.evaluate("window.history.back()")?;
        Ok(())
    }
}
