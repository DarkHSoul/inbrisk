//! `inbrisk.exe` — the one real runtime.
//!
//! Responsibilities, in order:
//! 1. own the shared-memory region (the single entry point for every client);
//! 2. run the request loop and the execution engine;
//! 3. expose exactly the human controls that must never be agent-controlled
//!    (pause, resume, emergency stop, exit) through the tray and the window.
//!
//! There is no second engine anywhere: the CLI, the MCP proxy and the SDK are
//! all clients of this process.

#![windows_subsystem = "windows"]

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;

use inbrisk_core::{log_error, log_info, logging, ActivityState};
use inbrisk_runtime::{Runtime, RuntimeConfig, RUNTIME_VERSION};
use inbrisk_ui::{UiCommand, UiHandle, UiState};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if args.iter().any(|a| a == "--help" || a == "-h") {
        console_write(HELP);
        return;
    }

    // A normal launch must return to the caller. The process that owns the
    // tray is a detached service; this process only starts it.
    let stay = args
        .iter()
        .any(|a| a == "--service" || a == "--foreground" || a == "--headless" || a == "--no-ui");
    if !stay && detach(&args) {
        return;
    }

    setup_logging(&args);
    if args
        .iter()
        .any(|a| a == "--foreground" || a == "--headless")
    {
        logging::set_mirror_stderr(true);
    } else if args.iter().any(|a| a == "--service") {
        logging::set_mirror_stderr(false);
    }
    inbrisk_win32::init_dpi_awareness();

    let headless = args.iter().any(|a| a == "--headless" || a == "--no-ui");
    let show_window = args.iter().any(|a| a == "ui" || a == "--show");

    log_info!(
        "inbrisk",
        "starting inbrisk {RUNTIME_VERSION} (headless={headless})"
    );

    let runtime = match Runtime::start(RuntimeConfig::default()) {
        Ok(runtime) => runtime,
        Err(e) => {
            log_error!("inbrisk", "could not start the runtime: {e}");
            console_write(&format!(
                "inbrisk: could not start the runtime: {e}\n\
                 hint: another inbrisk.exe may already own this session.\n"
            ));
            std::process::exit(2);
        }
    };

    if headless {
        finish(runtime.run());
        runtime.join_workers();
        return;
    }

    let toggle_request = Arc::new(AtomicBool::new(false));
    let window_visible = Arc::new(AtomicBool::new(show_window));
    let ui = match prepare_ui(
        &runtime,
        Arc::clone(&toggle_request),
        Arc::clone(&window_visible),
    ) {
        Ok(ui) => ui,
        Err(e) => {
            log_error!("inbrisk", "UI unavailable ({e}); continuing headless");
            finish(runtime.run());
            runtime.join_workers();
            return;
        }
    };

    spawn_status_pump(
        Arc::clone(&runtime),
        ui.clone(),
        Arc::clone(&toggle_request),
        Arc::clone(&window_visible),
    );

    // Main thread: window and tray. The wait loop cannot live here.
    let worker = {
        let rt = Arc::clone(&runtime);
        match std::thread::Builder::new()
            .name("inbrisk-runtime".into())
            .spawn(move || rt.run())
        {
            Ok(handle) => handle,
            Err(e) => {
                log_error!("inbrisk", "cannot start the runtime thread: {e}");
                finish(runtime.run());
                runtime.join_workers();
                return;
            }
        }
    };

    ui.run(show_window);
    runtime.request_shutdown();
    let result = worker.join().unwrap_or(Ok(()));
    runtime.join_workers();
    finish(result);
}

fn finish(result: inbrisk_core::Result<()>) {
    match result {
        Ok(()) => log_info!("inbrisk", "stopped cleanly"),
        Err(e) => {
            log_error!("inbrisk", "runtime loop failed: {e}");
            std::process::exit(1);
        }
    }
}

const HELP: &str = "\
inbrisk - the Inbrisk Windows automation runtime

usage: inbrisk [ui|--show] [--headless] [--foreground] [--log-level LEVEL] [--log-file PATH] [--help]

  (no flags)        start the runtime in the tray and return
  ui | --show       same, and open the status window
  --foreground      stay in this process (a terminal will wait)
  --headless        no tray or window; stay in this process
  --log-level L     error | warn | info | debug | trace (default: info)
  --log-file PATH   append logs to PATH in addition to the default log

Clients: inbrisk-cli (fast path), inbrisk-mcp (MCP compatibility), the SDK.
The runtime keeps running when every client exits.
";

fn setup_logging(args: &[String]) {
    let level = args
        .iter()
        .position(|a| a == "--log-level")
        .and_then(|i| args.get(i + 1))
        .and_then(|v| logging::Level::parse(v))
        .unwrap_or(logging::Level::Info);
    logging::set_level(level);

    let mut path = args
        .iter()
        .position(|a| a == "--log-file")
        .and_then(|i| args.get(i + 1))
        .map(PathBuf::from);
    if path.is_none() {
        if let Ok(local) = std::env::var("LOCALAPPDATA") {
            let dir = PathBuf::from(local).join("Inbrisk").join("logs");
            let _ = std::fs::create_dir_all(&dir);
            path = Some(dir.join("inbrisk-runtime.log"));
        }
    }
    if let Some(path) = path {
        logging::set_file(Some(path));
    }
    logging::set_mirror_stderr(true);
}

fn prepare_ui(
    runtime: &Arc<Runtime>,
    toggle_request: Arc<AtomicBool>,
    _window_visible: Arc<AtomicBool>,
) -> inbrisk_core::Result<UiHandle> {
    let initial = UiState {
        headline: format!("Inbrisk runtime {RUNTIME_VERSION}"),
        activity: Some(ActivityState::Idle),
        body: "starting...".to_string(),
        tray_tip: "Inbrisk - idle".to_string(),
        emergency: false,
        paused: false,
        clients: 0,
        active_operations: 0,
        target_hwnd: 0,
        label: String::new(),
    };

    let rt = Arc::clone(runtime);
    UiHandle::prepare(initial, move |command| match command {
        UiCommand::Pause => rt.pause("paused by the human"),
        UiCommand::Resume => rt.resume(),
        UiCommand::EmergencyStop => {
            rt.emergency_stop("emergency stop from the tray");
        }
        UiCommand::ClearEmergency => rt.clear_emergency(),
        // The status pump owns the actual show/hide, because only it holds the
        // handle; the message loop just raises the request.
        UiCommand::ToggleWindow => toggle_request.store(true, Ordering::Release),
        UiCommand::Exit | UiCommand::ShutdownRuntime => {
            rt.request_shutdown();
            inbrisk_ui::request_exit();
        }
    })
}

/// Push a compact status snapshot into the window/tray a few times a second.
fn spawn_status_pump(
    runtime: Arc<Runtime>,
    ui: UiHandle,
    toggle_request: Arc<AtomicBool>,
    window_visible: Arc<AtomicBool>,
) {
    std::thread::Builder::new()
        .name("inbrisk-ui-status".into())
        .spawn(move || loop {
            if runtime.is_shutting_down() {
                break;
            }

            if toggle_request.swap(false, Ordering::AcqRel) {
                let next = !window_visible.load(Ordering::Acquire);
                window_visible.store(next, Ordering::Release);
                if next {
                    ui.show();
                } else {
                    ui.hide();
                }
            }

            let activity = runtime.activity().snapshot();
            let sessions = runtime.ipc.sessions();
            let stats = runtime.ipc.stats();
            let body = format!(
                "region:   {region}\n\
                 state:    {activity} ({label})\n\
                 version:  {sv} / gen {gen}\n\
                 uptime:   {uptime}s   in-flight: {inflight}\n\
                 clients:  {clients}\n\
                 \n\
                 ipc:      {transport}\n\
                 requests: {requests}   responses: {responses}\n\
                 events:   {events}   wakes: {wakes}\n\
                 rtt us:   p50 {p50} / p95 {p95} / max {max}\n\
                 \n\
                 clients:\n{client_list}\n\
                 recent log:\n{log}",
                region = runtime.region_name(),
                activity = activity.state.as_str(),
                label = if activity.label.is_empty() {
                    "-".to_string()
                } else {
                    activity.label.clone()
                },
                sv = runtime.state.state_version(),
                gen = runtime.state.generation(),
                uptime = runtime.state.uptime_ms() / 1000,
                inflight = runtime.in_flight(),
                clients = sessions.len(),
                transport = stats.transport,
                requests = stats.requests_seen,
                responses = stats.responses_sent,
                events = stats.events_published,
                wakes = stats.wake_signals,
                p50 = stats.rtt_p50_us,
                p95 = stats.rtt_p95_us,
                max = stats.rtt_max_us,
                client_list = if sessions.is_empty() {
                    "  (none)".to_string()
                } else {
                    sessions
                        .iter()
                        .map(|s| {
                            format!(
                                "  #{} {} {} pid {} ({} req)",
                                s.session_id, s.name, s.version, s.client_pid, s.requests
                            )
                        })
                        .collect::<Vec<_>>()
                        .join("\n")
                },
                log = logging::recent(6).join("\n"),
            );

            let tip = format!("Inbrisk - {}", activity.state.as_str());
            ui.update(|state| {
                state.activity = Some(activity.state);
                state.emergency = activity.state == ActivityState::Emergency;
                state.paused = activity.state == ActivityState::Paused;
                state.clients = sessions.len() as u32;
                state.active_operations = activity.active_operations;
                state.target_hwnd = activity.target_hwnd;
                state.label = activity.label.clone();
                state.tray_tip = tip;
                state.body = body;
            });
            std::thread::sleep(Duration::from_millis(100));
        })
        .ok();
}

/// Start a detached service process and return. The caller does not wait.
fn detach(args: &[String]) -> bool {
    let Ok(exe) = std::env::current_exe() else {
        return false;
    };
    let mut cmd = quote(&exe.to_string_lossy());
    for arg in args {
        if arg == "--service" {
            continue;
        }
        cmd.push(' ');
        cmd.push_str(&quote(arg));
    }
    cmd.push_str(" --service");

    spawn_detached(&exe, &cmd, true) || spawn_detached(&exe, &cmd, false)
}

fn spawn_detached(exe: &std::path::Path, cmd: &str, breakaway: bool) -> bool {
    use windows::core::{PCWSTR, PWSTR};
    use windows::Win32::Foundation::CloseHandle;
    use windows::Win32::System::Threading::{
        CreateProcessW, CREATE_BREAKAWAY_FROM_JOB, CREATE_NEW_PROCESS_GROUP, DETACHED_PROCESS,
        PROCESS_INFORMATION, STARTUPINFOW,
    };

    let program = wide(&exe.to_string_lossy());
    let mut command = wide(cmd);
    let mut desktop = wide("WinSta0\\Default");
    let si = STARTUPINFOW {
        cb: std::mem::size_of::<STARTUPINFOW>() as u32,
        lpDesktop: PWSTR(desktop.as_mut_ptr()),
        ..Default::default()
    };
    let mut pi = PROCESS_INFORMATION::default();
    let mut flags = CREATE_NEW_PROCESS_GROUP | DETACHED_PROCESS;
    if breakaway {
        flags |= CREATE_BREAKAWAY_FROM_JOB;
    }
    let created = unsafe {
        CreateProcessW(
            PCWSTR(program.as_ptr()),
            Some(PWSTR(command.as_mut_ptr())),
            None,
            None,
            false,
            flags,
            None,
            PCWSTR::null(),
            &si,
            &mut pi,
        )
    };
    if created.is_err() {
        return false;
    }
    unsafe {
        let _ = CloseHandle(pi.hThread);
        let _ = CloseHandle(pi.hProcess);
    }
    true
}

fn quote(text: &str) -> String {
    format!("\"{}\"", text.replace('"', "\\\""))
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

fn console_write(text: &str) {
    println!("{text}");
}
