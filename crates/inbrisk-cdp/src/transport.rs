//! Localhost-only HTTP and a dependency-free WebSocket text frame codec.
//!
//! Nothing in this module opens a socket except `http_get_localhost`, and that
//! function can only dial `127.0.0.1`. A DevTools port is accepted only after
//! the caller has already proven it belongs to the target process.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpStream};
use std::time::Duration;

use inbrisk_core::{InbriskError, Result};

const MAX_BODY: usize = 2 * 1024 * 1024;

/// GET `http://127.0.0.1:{port}{path}`. `path` must start with `/`.
pub fn http_get_localhost(port: u16, path: &str) -> Result<Vec<u8>> {
    if port == 0 {
        return Err(InbriskError::not_found("refusing DevTools port 0"));
    }
    if !path.starts_with('/') || path.contains("://") {
        return Err(InbriskError::internal(
            "DevTools HTTP path must be a localhost absolute path",
        ));
    }
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let mut stream = TcpStream::connect_timeout(&addr, Duration::from_millis(800)).map_err(|e| {
        InbriskError::not_found(format!(
            "DevTools on 127.0.0.1:{port} did not accept a connection ({e})"
        ))
        .with_hint("start that browser with --remote-debugging-port; this runtime will not use UIA for the document")
    })?;
    stream
        .set_read_timeout(Some(Duration::from_millis(1500)))
        .ok();
    stream
        .set_write_timeout(Some(Duration::from_millis(800)))
        .ok();
    let request = format!(
        "GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\nAccept: application/json\r\n\r\n"
    );
    stream
        .write_all(request.as_bytes())
        .map_err(|e| InbriskError::internal(format!("DevTools write failed: {e}")))?;
    let mut raw = Vec::new();
    let mut chunk = [0u8; 8192];
    loop {
        if raw.len() >= MAX_BODY {
            break;
        }
        match stream.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => raw.extend_from_slice(&chunk[..n]),
            Err(e)
                if e.kind() == std::io::ErrorKind::WouldBlock
                    || e.kind() == std::io::ErrorKind::TimedOut =>
            {
                if !raw.is_empty() {
                    break;
                }
                return Err(InbriskError::new(
                    inbrisk_core::ErrorCode::Timeout,
                    format!("DevTools on 127.0.0.1:{port} did not answer"),
                ));
            }
            Err(e) => {
                return Err(InbriskError::internal(format!("DevTools read failed: {e}")));
            }
        }
    }
    let (status, body) = split_http(&raw)?;
    if status != 200 {
        return Err(InbriskError::not_found(format!(
            "DevTools {path} on 127.0.0.1:{port} returned HTTP {status}"
        )));
    }
    Ok(body)
}

pub fn split_http(raw: &[u8]) -> Result<(u16, Vec<u8>)> {
    let split = raw
        .windows(4)
        .position(|w| w == b"\r\n\r\n")
        .ok_or_else(|| InbriskError::internal("DevTools response had no header terminator"))?;
    let header = std::str::from_utf8(&raw[..split])
        .map_err(|_| InbriskError::internal("DevTools headers were not UTF-8"))?;
    let status = header
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|code| code.parse::<u16>().ok())
        .ok_or_else(|| InbriskError::internal("DevTools response had no HTTP status"))?;
    Ok((status, raw[split + 4..].to_vec()))
}

/// Build the client opening handshake. The host must be loopback.
pub fn websocket_upgrade_request(host: &str, path: &str, key_b64: &str) -> Result<String> {
    if host != "127.0.0.1" && !host.starts_with("127.0.0.1:") {
        return Err(InbriskError::denied(
            "DevTools WebSocket host must be 127.0.0.1",
        ));
    }
    if !path.starts_with('/') {
        return Err(InbriskError::internal(
            "DevTools WebSocket path must start with /",
        ));
    }
    Ok(format!(
        "GET {path} HTTP/1.1\r\n\
         Host: {host}\r\n\
         Upgrade: websocket\r\n\
         Connection: Upgrade\r\n\
         Sec-WebSocket-Key: {key_b64}\r\n\
         Sec-WebSocket-Version: 13\r\n\r\n"
    ))
}

pub fn http_status_is_switching(head: &str) -> bool {
    head.lines()
        .next()
        .map(|line| line.contains(" 101 "))
        .unwrap_or(false)
}

/// Masked client text frame. `mask` is the 4-byte key required by the spec.
pub fn encode_client_text_frame(payload: &str, mask: [u8; 4]) -> Vec<u8> {
    let data = payload.as_bytes();
    let mut out = Vec::with_capacity(data.len() + 14);
    out.push(0x81); // FIN + text
    let len = data.len();
    if len < 126 {
        out.push(0x80 | len as u8);
    } else if len <= u16::MAX as usize {
        out.push(0x80 | 126);
        out.extend_from_slice(&(len as u16).to_be_bytes());
    } else {
        out.push(0x80 | 127);
        out.extend_from_slice(&(len as u64).to_be_bytes());
    }
    out.extend_from_slice(&mask);
    for (i, byte) in data.iter().enumerate() {
        out.push(byte ^ mask[i % 4]);
    }
    out
}

/// Decode one server frame. Server frames are unmasked. Returns
/// `(opcode, text, bytes_consumed)` for a complete frame.
pub fn decode_server_frame(buf: &[u8]) -> Option<(u8, String, usize)> {
    if buf.len() < 2 {
        return None;
    }
    let opcode = buf[0] & 0x0F;
    let masked = buf[1] & 0x80 != 0;
    let mut len = (buf[1] & 0x7F) as usize;
    let mut offset = 2;
    if len == 126 {
        if buf.len() < 4 {
            return None;
        }
        len = u16::from_be_bytes([buf[2], buf[3]]) as usize;
        offset = 4;
    } else if len == 127 {
        if buf.len() < 10 {
            return None;
        }
        len = u64::from_be_bytes(buf[2..10].try_into().ok()?) as usize;
        offset = 10;
    }
    let mask = if masked {
        if buf.len() < offset + 4 {
            return None;
        }
        let key = [
            buf[offset],
            buf[offset + 1],
            buf[offset + 2],
            buf[offset + 3],
        ];
        offset += 4;
        Some(key)
    } else {
        None
    };
    if buf.len() < offset + len || len > MAX_BODY {
        return None;
    }
    let mut payload = buf[offset..offset + len].to_vec();
    if let Some(key) = mask {
        for (i, byte) in payload.iter_mut().enumerate() {
            *byte ^= key[i % 4];
        }
    }
    let text = String::from_utf8_lossy(&payload).into_owned();
    Some((opcode, text, offset + len))
}

#[allow(dead_code)]
pub fn encode_base64(data: &[u8]) -> String {
    const TABLE: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = String::new();
    let mut i = 0;
    while i + 3 <= data.len() {
        let n = ((data[i] as u32) << 16) | ((data[i + 1] as u32) << 8) | data[i + 2] as u32;
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        out.push(TABLE[((n >> 6) & 63) as usize] as char);
        out.push(TABLE[(n & 63) as usize] as char);
        i += 3;
    }
    if i < data.len() {
        let remain = data.len() - i;
        let mut n = (data[i] as u32) << 16;
        if remain == 2 {
            n |= (data[i + 1] as u32) << 8;
        }
        out.push(TABLE[((n >> 18) & 63) as usize] as char);
        out.push(TABLE[((n >> 12) & 63) as usize] as char);
        if remain == 2 {
            out.push(TABLE[((n >> 6) & 63) as usize] as char);
            out.push('=');
        } else {
            out.push('=');
            out.push('=');
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn client_text_frame_is_masked() {
        let frame = encode_client_text_frame("Hi", [1, 2, 3, 4]);
        assert_eq!(frame[0], 0x81);
        assert_eq!(frame[1], 0x82);
        assert_eq!(&frame[2..6], &[1, 2, 3, 4]);
        assert_eq!(frame[6], b'H' ^ 1);
        assert_eq!(frame[7], b'i' ^ 2);
    }

    #[test]
    fn server_text_frame_round_trips() {
        let buf = [0x81, 0x02, b'H', b'i'];
        let (opcode, text, used) = decode_server_frame(&buf).unwrap();
        assert_eq!(opcode, 0x01);
        assert_eq!(text, "Hi");
        assert_eq!(used, 4);
    }

    #[test]
    fn upgrade_request_stays_on_loopback() {
        let req =
            websocket_upgrade_request("127.0.0.1:9222", "/devtools/page/abc", "abc=").unwrap();
        assert!(req.contains("Upgrade: websocket"));
        assert!(req.contains("Host: 127.0.0.1:9222"));
        assert!(http_status_is_switching(
            "HTTP/1.1 101 Switching Protocols\r\n"
        ));
        assert!(websocket_upgrade_request("example.com", "/devtools/page/abc", "abc=").is_err());
    }

    #[test]
    fn http_split_reads_the_body() {
        let raw = b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n[]";
        let (status, body) = split_http(raw).unwrap();
        assert_eq!(status, 200);
        assert_eq!(body, b"[]");
    }

    #[test]
    fn base64_encodes_the_websocket_key_width() {
        assert_eq!(encode_base64(&[0u8; 16]).len(), 24);
    }
}
