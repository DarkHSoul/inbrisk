//! `inbrisk-mcp.exe`: the stdio transport for the MCP compatibility layer.
//!
//! This binary is deliberately trivial: it owns no protocol knowledge at all.
//! It reads newline-delimited JSON-RPC from stdin, hands each line to
//! [`inbrisk_mcp::Proxy`], and writes whatever comes back to stdout.
//!
//! Framing is newline-delimited JSON, one object per line, with no
//! `Content-Length` headers: that is what MCP's stdio transport specifies.
//!
//! Lifecycle rules that matter:
//!
//! * stdout carries the protocol and *only* the protocol. Every diagnostic goes
//!   to stderr (see `INBRISK_MCP_DEBUG`).
//! * stdin EOF ends the process. Nothing is torn down in the runtime, which
//!   keeps running as a system service; the proxy's session is simply released.
//! * A failing tool call is still a successful JSON-RPC response, so the loop
//!   never exits because a call went wrong. The one way out is `exit`/EOF.

use std::io::{self, BufRead, Write};

use inbrisk_mcp::{Flow, Proxy};

fn main() {
    // No `--help`, no argument parsing: the only supported interface is stdio,
    // and the runtime picks up configuration from the environment.
    let proxy = Proxy::new();
    let stdin = io::stdin();
    let mut stdout = io::stdout();

    if run(&proxy, stdin.lock(), &mut stdout).is_err() {
        // A broken stdout means the host is gone; there is nobody left to tell,
        // so exiting quietly is the correct behaviour.
        std::process::exit(1);
    }
}

/// Read/handle/write until `exit` or EOF.
///
/// Split out from `main` so the loop can be pointed at any reader/writer pair.
fn run<R: BufRead, W: Write>(proxy: &Proxy, reader: R, writer: &mut W) -> io::Result<()> {
    for line in reader.lines() {
        let line = match line {
            Ok(line) => line,
            Err(err) => {
                // A malformed byte sequence on stdin must not kill the proxy;
                // report it and keep serving well-formed messages.
                eprintln!("inbrisk-mcp: stdin read error: {err}");
                continue;
            }
        };

        // Blank lines are not messages (some hosts pad); silently skipping them
        // avoids emitting a spurious parse error.
        if line.trim().is_empty() {
            continue;
        }

        // `handle_line` reports transport-level problems as a ready-made
        // JSON-RPC error envelope, so both arms write the same way.
        let handled = match proxy.handle_line(&line) {
            Ok(handled) => handled,
            Err(error) => {
                if proxy.debug() {
                    eprintln!("inbrisk-mcp: {error}");
                }
                inbrisk_mcp::Handled {
                    reply: Some(error.envelope()),
                    flow: Flow::Continue,
                }
            }
        };

        if let Some(reply) = handled.reply {
            writer.write_all(reply.as_bytes())?;
            writer.write_all(b"\n")?;
            // Flush per message: an MCP host blocks waiting for each reply, so
            // buffering here would deadlock the conversation.
            writer.flush()?;
        }

        if handled.flow == Flow::Stop {
            break;
        }
    }

    // EOF: the client is gone. Drop the session, leave the runtime alone.
    Ok(())
}
