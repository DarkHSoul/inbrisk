//! Real-ConPTY integration tests for the terminal session manager.
//! Each test uses its own `TerminalManager`, so they are independent.

use std::time::{Duration, Instant};

use inbrisk_protocol::terminal::*;
use inbrisk_runtime::terminal::TerminalManager;

fn mgr(max: usize) -> TerminalManager {
    TerminalManager::new(max, Box::new(|| Ok(())))
}

fn open(m: &TerminalManager) -> String {
    m.open(&TerminalOpen {
        no_profile: true,
        ..Default::default()
    })
    .expect("open pwsh session")
    .session_id
}

fn write(m: &TerminalManager, id: &str, text: &str) {
    m.handle(&TerminalRequest::Write(TerminalWrite {
        session_id: id.into(),
        text: text.into(),
        normalize_newlines: true,
        append_enter: true,
    }))
    .expect("write");
}

fn read(m: &TerminalManager, id: &str, wait_ms: u64) -> TerminalOutput {
    match m
        .handle(&TerminalRequest::Read(TerminalRead {
            session_id: id.into(),
            after: None,
            wait_ms,
            settle_ms: 0,
            max_bytes: None,
            peek: false,
        }))
        .expect("read")
    {
        TerminalReply::Output(o) => o,
        other => panic!("unexpected {other:?}"),
    }
}

/// Accumulate implicit-cursor output until `needle` appears.
fn read_until(m: &TerminalManager, id: &str, needle: &str, secs: u64) -> String {
    let mut acc = String::new();
    let end = Instant::now() + Duration::from_secs(secs);
    while Instant::now() < end {
        acc.push_str(&read(m, id, 300).text);
        if acc.contains(needle) {
            return acc;
        }
    }
    panic!("timeout waiting for {needle:?}; got {acc:?}");
}

#[test]
fn persistent_shell_keeps_env_and_cwd_across_writes() {
    let m = mgr(4);
    let id = open(&m);
    // Wait for the prompt so later output is attributable.
    read_until(&m, &id, "PS ", 30);
    write(&m, &id, "$env:INBRISK_PERSIST_TEST=\"alive\"");
    write(&m, &id, "cd C:\\Windows");
    write(&m, &id, "echo \"ENV=$env:INBRISK_PERSIST_TEST\"");
    let out = read_until(&m, &id, "ENV=alive", 20);
    assert!(out.contains("ENV=alive"), "{out}");
    write(&m, &id, "echo \"CWD=$((Get-Location).Path)\"");
    let out = read_until(&m, &id, "CWD=C:\\Windows", 20);
    assert!(out.contains("CWD=C:\\Windows"), "{out}");
    let closed = m.close(&id).unwrap();
    assert!(closed.was_alive);
    assert_eq!(m.session_count(), 0);
}

#[test]
fn interactive_child_process_receives_later_stdin() {
    let m = mgr(4);
    let id = open(&m);
    read_until(&m, &id, "PS ", 30);
    let dir = std::env::temp_dir().join("inbrisk_term_fixture");
    std::fs::create_dir_all(&dir).unwrap();
    let script = dir.join("interactive.ps1");
    std::fs::write(
        &script,
        "Write-Output 'READY'\n$a = Read-Host\nWrite-Output \"CONTINUED:$a\"\n$b = Read-Host\nWrite-Output \"DONE:$b\"\n",
    )
    .unwrap();
    write(
        &m,
        &id,
        &format!("pwsh -NoProfile -File '{}'", script.display()),
    );
    read_until(&m, &id, "READY", 30);
    write(&m, &id, "YES");
    read_until(&m, &id, "CONTINUED:YES", 20);
    write(&m, &id, "SECOND");
    let out = read_until(&m, &id, "DONE:SECOND", 20);
    assert!(out.contains("DONE:SECOND"), "{out}");
    m.close(&id).unwrap();
}

#[test]
fn large_output_does_not_deadlock_and_small_buffer_truncates() {
    let m = mgr(4);
    let id = m
        .open(&TerminalOpen {
            no_profile: true,
            buffer_bytes: Some(TERMINAL_MIN_BUFFER_BYTES),
            ..Default::default()
        })
        .unwrap()
        .session_id;
    read_until(&m, &id, "PS ", 30);
    // ~1 MB of output while we never read: the reader thread must keep draining.
    write(
        &m,
        &id,
        "1..20000 | ForEach-Object { \"line $_ padding padding padding padding\" }; 'ALLDONE'",
    );
    // Do NOT read while it floods: wait until the output sequence stops moving
    // (proves the reader keeps draining while nobody consumes), then read.
    let next_seq = |m: &TerminalManager| match m.status(Some(&id)).unwrap() {
        TerminalReply::Status { sessions } => sessions[0].next_seq,
        _ => unreachable!(),
    };
    let deadline = Instant::now() + Duration::from_secs(60);
    let (mut last, mut stable_since) = (0u64, Instant::now());
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(200));
        let n = next_seq(&m);
        if n != last {
            last = n;
            stable_since = Instant::now();
        } else if n > 500_000 && stable_since.elapsed() > Duration::from_millis(1500) {
            break;
        }
    }
    assert!(last > 500_000, "output was not drained (deadlock?): {last}");
    let o = read(&m, &id, 500);
    let (saw_truncated, tail) = (o.truncated, o.text);
    assert!(tail.contains("ALLDONE"), "tail of output lost: {tail:?}");
    assert!(saw_truncated, "4 KiB ring must report truncation");
    // Status still answers and the buffer is bounded.
    if let TerminalReply::Status { sessions } = m.status(Some(&id)).unwrap() {
        assert!(sessions[0].buffered_bytes <= TERMINAL_MIN_BUFFER_BYTES);
        assert!(sessions[0].base_seq > 0);
    }
    m.close(&id).unwrap();
}

#[test]
fn explicit_cursor_reads_are_repeatable() {
    let m = mgr(4);
    let id = open(&m);
    read_until(&m, &id, "PS ", 30);
    write(&m, &id, "echo CURSOR_MARK");
    read_until(&m, &id, "CURSOR_MARK", 20);
    let a = m
        .handle(&TerminalRequest::Read(TerminalRead {
            session_id: id.clone(),
            after: Some(0),
            wait_ms: 0,
            settle_ms: 0,
            max_bytes: None,
            peek: false,
        }))
        .unwrap();
    let b = m
        .handle(&TerminalRequest::Read(TerminalRead {
            session_id: id.clone(),
            after: Some(0),
            wait_ms: 0,
            settle_ms: 0,
            max_bytes: None,
            peek: false,
        }))
        .unwrap();
    assert_eq!(a, b);
    m.close(&id).unwrap();
}

#[test]
fn exit_dead_session_close_and_limit() {
    let m = mgr(2);
    let a = open(&m);
    let b = open(&m);
    // Limit reached while both alive.
    let e = m
        .open(&TerminalOpen {
            no_profile: true,
            ..Default::default()
        })
        .unwrap_err();
    assert_eq!(e.code, inbrisk_core::ErrorCode::BudgetExhausted);

    // Shell self-exit: session reports dead, write is refused, output readable.
    read_until(&m, &a, "PS ", 30);
    write(&m, &a, "exit 3");
    let end = Instant::now() + Duration::from_secs(20);
    loop {
        let o = read(&m, &a, 200);
        if !o.process_alive {
            assert_eq!(o.exit_code, Some(3));
            break;
        }
        assert!(Instant::now() < end, "shell did not exit");
    }
    let e = m
        .handle(&TerminalRequest::Write(TerminalWrite {
            session_id: a.clone(),
            text: "x".into(),
            normalize_newlines: true,
            append_enter: false,
        }))
        .unwrap_err();
    assert_eq!(e.code, inbrisk_core::ErrorCode::TargetGone);

    // A dead session is reaped when a new one needs the slot.
    let c = open(&m);
    assert_ne!(c, a);
    assert!(m.status(Some(&a)).is_err());

    // Close, duplicate close, invalid id.
    assert!(m.close(&b).unwrap().was_alive);
    assert_eq!(
        m.close(&b).unwrap_err().code,
        inbrisk_core::ErrorCode::TargetNotFound
    );
    assert_eq!(
        m.handle(&TerminalRequest::Read(TerminalRead {
            session_id: "term_99".into(),
            after: None,
            wait_ms: 0,
            settle_ms: 0,
            max_bytes: None,
            peek: false,
        }))
        .unwrap_err()
        .code,
        inbrisk_core::ErrorCode::TargetNotFound
    );

    // Runtime shutdown closes everything deterministically.
    m.shutdown_all();
    assert_eq!(m.session_count(), 0);
}

#[test]
fn sessions_do_not_block_each_other() {
    let m = std::sync::Arc::new(mgr(4));
    let slow = open(&m);
    let fast = open(&m);
    read_until(&m, &slow, "PS ", 30);
    read_until(&m, &fast, "PS ", 30);
    // A long blocking read on `slow` must not delay `fast`.
    let m2 = m.clone();
    let slow2 = slow.clone();
    let h = std::thread::spawn(move || read(&m2, &slow2, 3_000));
    std::thread::sleep(Duration::from_millis(100));
    let t0 = Instant::now();
    write(&m, &fast, "echo FAST_OK");
    read_until(&m, &fast, "FAST_OK", 20);
    assert!(
        t0.elapsed() < Duration::from_secs(3),
        "fast session was blocked"
    );
    let _ = h.join();
    m.shutdown_all();
}

#[test]
fn blocked_write_is_cancelled_and_session_closed_on_timeout() {
    let m = std::sync::Arc::new(mgr(4));
    let id = open(&m);
    read_until(&m, &id, "PS ", 30);

    // Start a command that reads one character and then sleeps indefinitely without reading more.
    write(
        &m,
        &id,
        "powershell -NoProfile -Command 'Read-Host; Start-Sleep -Seconds 30'",
    );
    std::thread::sleep(Duration::from_millis(500));

    // Flood the input queue concurrently to trigger BudgetExhausted (backpressure)
    let flood_text = "x".repeat(32 * 1024);
    let mut got_timeout = false;
    let (tx, rx) = std::sync::mpsc::channel();

    let t0 = Instant::now();
    for _ in 0..10 {
        let id = id.clone();
        let text = flood_text.clone();
        let tx = tx.clone();
        let m = m.clone();
        std::thread::spawn(move || {
            let req = TerminalRequest::Write(TerminalWrite {
                session_id: id.clone(),
                text,
                normalize_newlines: false,
                append_enter: false,
            });
            let _ = tx.send(m.handle(&req));
        });
    }

    // Wait for either a BudgetExhausted, TargetGone or Timeout error.
    for _ in 0..10 {
        if let Ok(res) = rx.recv_timeout(Duration::from_secs(10)) {
            match res {
                Ok(_) => {}
                Err(e) => {
                    if e.code == inbrisk_core::ErrorCode::BudgetExhausted
                        || e.code == inbrisk_core::ErrorCode::Timeout
                        || e.code == inbrisk_core::ErrorCode::TargetGone
                    {
                        got_timeout = true;
                        // Now explicitly close it to unblock the writer thread (if it's not TargetGone already)
                        let close_t0 = Instant::now();
                        let _ = m.close(&id);
                        assert!(
                            close_t0.elapsed() < Duration::from_secs(2),
                            "close took too long"
                        );
                        break;
                    }
                }
            }
        }
    }
    let elapsed = t0.elapsed();

    assert!(
        got_timeout,
        "write should have blocked and timed out/exhausted"
    );
    assert!(
        elapsed < Duration::from_secs(20),
        "timeout took too long: {:?}",
        elapsed
    );

    // Ensure the runtime is responsive (status works)
    assert!(m.status(None).is_ok());

    // Ensure another session can be created and works normally
    let id2 = open(&m);
    read_until(&m, &id2, "PS ", 30);
    write(&m, &id2, "echo ALIVE");
    read_until(&m, &id2, "ALIVE", 10);
    
    m.shutdown_all();
}

#[test]
fn runtime_shutdown_during_blocked_write() {
    let m = std::sync::Arc::new(mgr(4));
    let id = open(&m);
    read_until(&m, &id, "PS ", 30);
    write(&m, &id, "powershell -NoProfile -Command 'Read-Host; Start-Sleep -Seconds 30'");
    std::thread::sleep(Duration::from_millis(500));
    
    let flood_text = "x".repeat(32 * 1024);
    let (tx, _rx) = std::sync::mpsc::channel();
    
    for _ in 0..10 {
        let id = id.clone();
        let text = flood_text.clone();
        let tx = tx.clone();
        let m = m.clone();
        std::thread::spawn(move || {
            let req = TerminalRequest::Write(TerminalWrite {
                session_id: id.clone(),
                text,
                normalize_newlines: false,
                append_enter: false,
            });
            let _ = tx.send(m.handle(&req));
        });
    }
    
    // Wait slightly to ensure at least one write is blocked in WriteFile
    std::thread::sleep(Duration::from_millis(500));
    
    let shutdown_t0 = Instant::now();
    m.shutdown_all();
    let elapsed = shutdown_t0.elapsed();
    
    assert!(elapsed < Duration::from_secs(2), "shutdown_all during blocked write took too long: {:?}", elapsed);
    assert_eq!(m.session_count(), 0);
}
