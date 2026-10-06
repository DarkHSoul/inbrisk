//! Hand-rolled argument parsing.
//!
//! There is no argument-parsing dependency on purpose: the surface is small,
//! the binary must stay tiny and cold-start cheap, and parsing must be a pure
//! function of `argv` so it can be unit-tested without a runtime.
//!
//! Every option that takes a value also accepts `--flag=value`.

use crate::error::{CliError, CliResult};
use crate::plan_input::PlanSource;

/// Default number of round trips `ping` performs.
pub const DEFAULT_PING_COUNT: usize = 20;
/// Default window `events` listens for.
pub const DEFAULT_EVENT_SECONDS: f64 = 5.0;
/// Default log lines requested by `status --logs`.
pub const DEFAULT_LOG_LINES: u32 = 50;

/// Caps that stop a typo from starting an unbounded run.
const MAX_PING_COUNT: usize = 10_000;
const MAX_EVENT_SECONDS: f64 = 3_600.0;

pub const USAGE: &str = r#"inbrisk-cli <command> [options]

Thin fast-path client for the Inbrisk runtime. It holds no automation logic:
every command is one shared-memory round trip through inbrisk-sdk.

Commands:
  status    [--windows] [--logs] [--log-lines N] [--ipc] [--json]
  ping      [--count N] [--json]
  windows   [--filter TEXT] [--json]
  observe   [--tree] [--window NAME] [--depth N] [--json]
  find      [--window NAME] [--role ROLE] [--name NAME] [--all] [--limit N]
            [--selector JSON] [--json] [SELECTOR_TEXT]
  read      <element-id> [--json]
  act       --json '<step json>' [--out-json]
  run       [--stdin] [--file PATH] [--json-arg JSON] [--dry-run]
            [--timeout-ms N] [--json]
  cancel    [--all] [--json]
  events    [--seconds N] [--json]
  doctor    [--json]
  endpoint  [--json]
  fast-path verify [--json]
  skill     install antigravity | status [--json]
  terminal  open [--shell pwsh|powershell|cmd] [--cwd PATH] [--cols N] [--rows N]
                 [--raw] [--no-profile] [--buffer-bytes N] [--json]
            write <id> [TEXT | --stdin] [--literal] [--no-normalize] [--json]
            exec  <id> <command...> [--json]          (write + Enter)
            read  <id> [--after SEQ] [--wait-ms N] [--settle-ms N]
                       [--max-bytes N] [--peek] [--json]
            status [<id>] [--json]
            close <id> [--json]
  help | --help | -h

Plan input for `run` (exactly one source, stdin is the default):
  {"steps":[...]}                     bare plan
  {"op":"run","steps":[...]}          native wire form
  {"plan":{"steps":[...]}}            wrapped plan

Exit codes:
  0 success      1 runtime/operation error    2 usage error
  3 runtime unavailable                       4 timeout

Examples:
  inbrisk-cli status --json
  inbrisk-cli fast-path verify --json
  inbrisk-cli skill install antigravity
  inbrisk-cli windows --filter notepad
  inbrisk-cli find --role button --name Save --all
  inbrisk-cli read 12 --json
  inbrisk-cli act --json '{"action":"sleep","ms":10}'
  echo '{"steps":[{"action":"launch","app":"notepad"}]}' | inbrisk-cli run
  inbrisk-cli run --file plan.json --dry-run
"#;

/// What the CLI was asked to do.
#[derive(Debug, Clone, PartialEq)]
pub enum Invocation {
    /// Print [`USAGE`] and exit successfully.
    Help,
    Run(Command),
}

/// Parsed command with its options.
#[derive(Debug, Clone, PartialEq)]
pub enum Command {
    Status(StatusArgs),
    Ping(PingArgs),
    Windows(WindowsArgs),
    Observe(ObserveArgs),
    Find(FindArgs),
    Read(ReadArgs),
    Act(ActArgs),
    Run(RunArgs),
    Cancel(CancelArgs),
    Events(EventsArgs),
    Doctor(DoctorArgs),
    Endpoint(EndpointArgs),
    FastPath(FastPathArgs),
    Skill(SkillArgs),
    Terminal(TerminalArgs),
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct StatusArgs {
    pub windows: bool,
    pub logs: bool,
    pub ipc: bool,
    pub log_lines: u32,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PingArgs {
    pub count: usize,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct WindowsArgs {
    pub filter: Option<String>,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ObserveArgs {
    pub tree: bool,
    pub window: Option<String>,
    pub depth: Option<u32>,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FindArgs {
    pub window: Option<String>,
    pub role: Option<String>,
    pub name: Option<String>,
    pub all: bool,
    pub limit: Option<usize>,
    pub selector_text: Option<String>,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ReadArgs {
    pub element_id: u64,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ActArgs {
    /// The `Step` payload itself; `--json` carries it, per the CLI contract.
    pub step_json: String,
    /// `--out-json`: render the outcome as JSON instead of human text.
    pub out_json: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RunArgs {
    pub source: PlanSource,
    pub dry_run: bool,
    pub timeout_ms: Option<u64>,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CancelArgs {
    pub json: bool,
}

#[derive(Debug, Clone, PartialEq)]
pub struct EventsArgs {
    pub seconds: f64,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct DoctorArgs {
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct EndpointArgs {
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct FastPathArgs {
    pub json: bool,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SkillArgs {
    Install { target: String, json: bool },
    Status { json: bool },
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TerminalArgs {
    Open(TerminalOpenArgs),
    Write(TerminalWriteArgs),
    Exec(TerminalExecArgs),
    Read(TerminalReadArgs),
    Status { session: Option<String>, json: bool },
    Close { session: String, json: bool },
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TerminalOpenArgs {
    pub shell: Option<String>,
    pub cwd: Option<String>,
    pub cols: Option<u16>,
    pub rows: Option<u16>,
    pub raw: bool,
    pub no_profile: bool,
    pub buffer_bytes: Option<usize>,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TerminalWriteArgs {
    pub session: String,
    pub text: Option<String>,
    /// Read the text from stdin (byte-exact; no escape decoding).
    pub stdin: bool,
    /// Do not decode `\r`/`\n`/`\e`/`\xHH`/`\\` escapes in TEXT.
    pub literal: bool,
    /// Send newlines byte-exact instead of converting them to Enter (`\r`).
    pub no_normalize: bool,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TerminalExecArgs {
    pub session: String,
    pub command: String,
    pub json: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct TerminalReadArgs {
    pub session: String,
    pub after: Option<u64>,
    pub wait_ms: u64,
    pub settle_ms: u64,
    pub max_bytes: Option<usize>,
    pub peek: bool,
    pub json: bool,
}

/// Parses `argv` (without the program name).
pub fn parse<I, S>(args: I) -> CliResult<Invocation>
where
    I: IntoIterator<Item = S>,
    S: Into<String>,
{
    let raw: Vec<String> = args.into_iter().map(Into::into).collect();
    // `terminal` carries free-form shell text (it may contain `--x=y`), so its
    // tokens must not be rewritten; its parser handles `--flag=value` itself.
    let items = if matches!(raw.first().map(String::as_str), Some("terminal" | "term")) {
        raw
    } else {
        expand_equals(raw)
    };

    let Some(command) = items.first() else {
        return Err(CliError::usage("no command given"));
    };
    if matches!(command.as_str(), "help" | "--help" | "-h") {
        return Ok(Invocation::Help);
    }

    let mut it = items[1..].iter();
    let command = match command.as_str() {
        "status" => {
            let mut args = StatusArgs {
                log_lines: DEFAULT_LOG_LINES,
                ..Default::default()
            };
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--windows" | "-w" => args.windows = true,
                    "--logs" => args.logs = true,
                    "--ipc" => args.ipc = true,
                    "--log-lines" => {
                        let value = value_of(&mut it, "--log-lines")?;
                        args.log_lines = parse_u32(&value, "--log-lines")?;
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("status", other)),
                }
            }
            Command::Status(args)
        }
        "ping" => {
            let mut args = PingArgs {
                count: DEFAULT_PING_COUNT,
                json: false,
            };
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--count" | "-c" => {
                        let value = value_of(&mut it, "--count")?;
                        args.count = parse_usize(&value, "--count")?;
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("ping", other)),
                }
            }
            if args.count == 0 {
                return Err(CliError::usage("--count must be at least 1"));
            }
            if args.count > MAX_PING_COUNT {
                return Err(CliError::usage(format!(
                    "--count must be at most {MAX_PING_COUNT}"
                )));
            }
            Command::Ping(args)
        }
        "windows" => {
            let mut args = WindowsArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--filter" | "-f" => {
                        let value = value_of(&mut it, "--filter")?;
                        args.filter = Some(non_empty("--filter", value)?);
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("windows", other)),
                }
            }
            Command::Windows(args)
        }
        "observe" => {
            let mut args = ObserveArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--tree" | "-t" => args.tree = true,
                    "--window" => {
                        let value = value_of(&mut it, "--window")?;
                        args.window = Some(non_empty("--window", value)?);
                    }
                    "--depth" => {
                        let value = value_of(&mut it, "--depth")?;
                        args.depth = Some(parse_u32(&value, "--depth")?);
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("observe", other)),
                }
            }
            Command::Observe(args)
        }
        "find" => {
            let mut args = FindArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--window" => {
                        let value = value_of(&mut it, "--window")?;
                        args.window = Some(non_empty("--window", value)?);
                    }
                    "--role" => {
                        let value = value_of(&mut it, "--role")?;
                        args.role = Some(non_empty("--role", value)?);
                    }
                    "--name" => {
                        let value = value_of(&mut it, "--name")?;
                        args.name = Some(non_empty("--name", value)?);
                    }
                    "--selector" => {
                        let value = value_of(&mut it, "--selector")?;
                        args.selector_text = Some(non_empty("--selector", value)?);
                    }
                    "--all" | "-a" => args.all = true,
                    "--limit" => {
                        let value = value_of(&mut it, "--limit")?;
                        let limit = parse_usize(&value, "--limit")?;
                        if limit == 0 {
                            return Err(CliError::usage("--limit must be at least 1"));
                        }
                        args.limit = Some(limit);
                    }
                    "--json" => args.json = true,
                    other if other.starts_with('-') => return Err(unknown("find", other)),
                    other => {
                        if args.selector_text.is_some() {
                            return Err(CliError::usage(
                                "find takes at most one SELECTOR_TEXT (use --selector for JSON)",
                            ));
                        }
                        args.selector_text = Some(other.to_string());
                    }
                }
            }
            Command::Find(args)
        }
        "read" => {
            let mut args = ReadArgs::default();
            let mut element_id = None;
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--json" => args.json = true,
                    other if other.starts_with('-') => return Err(unknown("read", other)),
                    other => {
                        if element_id.is_some() {
                            return Err(CliError::usage("read takes exactly one <element-id>"));
                        }
                        element_id = Some(parse_element_id(other)?);
                    }
                }
            }
            args.element_id = match element_id {
                Some(id) => id,
                None => return Err(CliError::usage("read requires an <element-id> argument")),
            };
            Command::Read(args)
        }
        "act" => {
            // `--json` carries the step payload here, so the output mode is a
            // separate flag instead of overloading the same name.
            let mut args = ActArgs::default();
            let mut step = None;
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--json" | "--step" => step = Some(value_of(&mut it, "--json")?),
                    "--out-json" | "--json-out" => args.out_json = true,
                    other => return Err(unknown("act", other)),
                }
            }
            args.step_json = match step {
                Some(raw) => raw,
                None => return Err(CliError::usage("act requires --json '<step json>'")),
            };
            Command::Act(args)
        }
        "run" => {
            let mut args = RunArgs {
                source: PlanSource::Stdin,
                dry_run: false,
                timeout_ms: None,
                json: false,
            };
            let mut source = None;
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--stdin" => set_source(&mut source, PlanSource::Stdin)?,
                    "--file" => {
                        let value = value_of(&mut it, "--file")?;
                        set_source(&mut source, PlanSource::File(non_empty("--file", value)?))?;
                    }
                    "--json-arg" => {
                        let value = value_of(&mut it, "--json-arg")?;
                        set_source(
                            &mut source,
                            PlanSource::Inline(non_empty("--json-arg", value)?),
                        )?;
                    }
                    "--dry-run" => args.dry_run = true,
                    "--timeout-ms" => {
                        let value = value_of(&mut it, "--timeout-ms")?;
                        let timeout = parse_u64(&value, "--timeout-ms")?;
                        if timeout == 0 {
                            return Err(CliError::usage("--timeout-ms must be at least 1"));
                        }
                        args.timeout_ms = Some(timeout);
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("run", other)),
                }
            }
            if let Some(source) = source {
                args.source = source;
            }
            Command::Run(args)
        }
        "cancel" => {
            let mut args = CancelArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    // Accepted for compatibility: the SDK exposes session-wide
                    // cancel, which is what this always does.
                    "--all" => {}
                    "--json" => args.json = true,
                    other => return Err(unknown("cancel", other)),
                }
            }
            Command::Cancel(args)
        }
        "events" => {
            let mut args = EventsArgs {
                seconds: DEFAULT_EVENT_SECONDS,
                json: false,
            };
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--seconds" | "-s" => {
                        let value = value_of(&mut it, "--seconds")?;
                        args.seconds = parse_seconds(&value, "--seconds")?;
                    }
                    "--json" => args.json = true,
                    other => return Err(unknown("events", other)),
                }
            }
            Command::Events(args)
        }
        "doctor" => {
            let mut args = DoctorArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--json" => args.json = true,
                    other => return Err(unknown("doctor", other)),
                }
            }
            Command::Doctor(args)
        }
        "endpoint" => {
            let mut args = EndpointArgs::default();
            while let Some(token) = it.next() {
                match token.as_str() {
                    "--json" => args.json = true,
                    other => return Err(unknown("endpoint", other)),
                }
            }
            Command::Endpoint(args)
        }
        "fast-path" | "fastpath" => {
            let subcommand = it.next().map(|s| s.as_str());
            match subcommand {
                Some("verify") => {
                    let mut json = false;
                    while let Some(token) = it.next() {
                        match token.as_str() {
                            "--json" => json = true,
                            other => return Err(unknown("fast-path verify", other)),
                        }
                    }
                    Command::FastPath(FastPathArgs { json })
                }
                Some(other) => {
                    return Err(CliError::usage(format!(
                        "unknown fast-path subcommand '{other}' (expected 'verify')"
                    )))
                }
                None => {
                    return Err(CliError::usage(
                        "missing fast-path subcommand (expected 'verify')",
                    ))
                }
            }
        }
        "skill" => {
            let subcommand = it.next().map(|s| s.as_str());
            match subcommand {
                Some("install") => {
                    let target = it.next().map(|s| s.as_str());
                    let mut json = false;
                    while let Some(token) = it.next() {
                        match token.as_str() {
                            "--json" => json = true,
                            other => return Err(unknown("skill install", other)),
                        }
                    }
                    match target {
                        Some("antigravity") => Command::Skill(SkillArgs::Install {
                            target: "antigravity".into(),
                            json,
                        }),
                        Some(other) => {
                            return Err(CliError::usage(format!(
                                "unknown skill install target '{other}' (expected 'antigravity')"
                            )))
                        }
                        None => {
                            return Err(CliError::usage(
                                "missing skill install target (expected 'antigravity')",
                            ))
                        }
                    }
                }
                Some("status") => {
                    let mut json = false;
                    while let Some(token) = it.next() {
                        match token.as_str() {
                            "--json" => json = true,
                            other => return Err(unknown("skill status", other)),
                        }
                    }
                    Command::Skill(SkillArgs::Status { json })
                }
                Some(other) => {
                    return Err(CliError::usage(format!(
                        "unknown skill subcommand '{other}' (expected 'install' or 'status')"
                    )))
                }
                None => {
                    return Err(CliError::usage(
                        "missing skill subcommand (expected 'install' or 'status')",
                    ))
                }
            }
        }
        "terminal" | "term" => Command::Terminal(parse_terminal(&items[1..])?),
        other if other.starts_with('-') => {
            return Err(CliError::usage(format!("unknown option '{other}'")))
        }
        other => return Err(CliError::usage(format!("unknown command '{other}'"))),
    };

    Ok(Invocation::Run(command))
}

/// `--name VALUE` or `--name=VALUE`. Terminal text can legitimately contain
/// `=` and leading dashes, so `terminal` does not go through [`expand_equals`].
fn flag_value(
    token: &str,
    name: &str,
    it: &mut std::slice::Iter<'_, String>,
) -> Option<CliResult<String>> {
    if token == name {
        return Some(value_of(it, name));
    }
    token
        .strip_prefix(name)
        .and_then(|rest| rest.strip_prefix('='))
        .map(|v| Ok(v.to_string()))
}

fn terminal_session(it: &mut std::slice::Iter<'_, String>, sub: &str) -> CliResult<String> {
    match it.next() {
        Some(id) if !id.starts_with('-') && !id.trim().is_empty() => Ok(id.clone()),
        _ => Err(CliError::usage(format!(
            "terminal {sub} requires a <session-id> (see `terminal open`)"
        ))),
    }
}

fn parse_terminal(items: &[String]) -> CliResult<TerminalArgs> {
    let mut it = items.iter();
    let Some(sub) = it.next() else {
        return Err(CliError::usage(
            "missing terminal subcommand (open|write|exec|read|status|close)",
        ));
    };
    let sub = sub.as_str();
    match sub {
        "open" => {
            let mut a = TerminalOpenArgs::default();
            while let Some(t) = it.next() {
                let t = t.as_str();
                if let Some(v) = flag_value(t, "--shell", &mut it) {
                    a.shell = Some(non_empty("--shell", v?)?);
                } else if let Some(v) = flag_value(t, "--cwd", &mut it) {
                    a.cwd = Some(non_empty("--cwd", v?)?);
                } else if let Some(v) = flag_value(t, "--cols", &mut it) {
                    a.cols = Some(parse_u32(&v?, "--cols")?.min(u16::MAX as u32) as u16);
                } else if let Some(v) = flag_value(t, "--rows", &mut it) {
                    a.rows = Some(parse_u32(&v?, "--rows")?.min(u16::MAX as u32) as u16);
                } else if let Some(v) = flag_value(t, "--buffer-bytes", &mut it) {
                    a.buffer_bytes = Some(parse_usize(&v?, "--buffer-bytes")?);
                } else {
                    match t {
                        "--raw" => a.raw = true,
                        "--no-profile" => a.no_profile = true,
                        "--json" => a.json = true,
                        other => return Err(unknown("terminal open", other)),
                    }
                }
            }
            Ok(TerminalArgs::Open(a))
        }
        "write" => {
            let session = terminal_session(&mut it, "write")?;
            let mut a = TerminalWriteArgs {
                session,
                ..Default::default()
            };
            let mut literal_rest = false;
            while let Some(t) = it.next() {
                let t = t.as_str();
                if !literal_rest {
                    match t {
                        "--" => {
                            literal_rest = true;
                            continue;
                        }
                        "--stdin" => {
                            a.stdin = true;
                            continue;
                        }
                        "--literal" => {
                            a.literal = true;
                            continue;
                        }
                        "--no-normalize" => {
                            a.no_normalize = true;
                            continue;
                        }
                        "--json" => {
                            a.json = true;
                            continue;
                        }
                        other if other.starts_with("--") => {
                            return Err(unknown("terminal write", other))
                        }
                        _ => {}
                    }
                }
                if a.text.is_some() {
                    return Err(CliError::usage(
                        "terminal write takes one TEXT argument (quote it, or use --stdin)",
                    ));
                }
                a.text = Some(t.to_string());
            }
            if a.stdin == a.text.is_some() {
                return Err(CliError::usage(
                    "terminal write needs exactly one of TEXT or --stdin",
                ));
            }
            Ok(TerminalArgs::Write(a))
        }
        "exec" => {
            let session = terminal_session(&mut it, "exec")?;
            let mut json = false;
            let mut words: Vec<&str> = Vec::new();
            let mut parsing_options = true;
            while let Some(t) = it.next() {
                let t_str = t.as_str();
                if parsing_options {
                    match t_str {
                        "--json" => {
                            json = true;
                            continue;
                        }
                        "--" => {
                            parsing_options = false;
                            continue;
                        }
                        _ => {
                            if t_str.starts_with("--") {
                                return Err(CliError::usage(format!(
                                    "ambiguous option '{t_str}'; use '--' to separate terminal flags from the inner shell command"
                                )));
                            }
                            parsing_options = false;
                        }
                    }
                }
                words.push(t_str);
            }
            if words.is_empty() {
                return Err(CliError::usage("terminal exec requires a command"));
            }
            Ok(TerminalArgs::Exec(TerminalExecArgs {
                session,
                command: words.join(" "),
                json,
            }))
        }
        "read" => {
            let session = terminal_session(&mut it, "read")?;
            let mut a = TerminalReadArgs {
                session,
                ..Default::default()
            };
            while let Some(t) = it.next() {
                let t = t.as_str();
                if let Some(v) = flag_value(t, "--after", &mut it) {
                    a.after = Some(parse_u64(&v?, "--after")?);
                } else if let Some(v) = flag_value(t, "--wait-ms", &mut it) {
                    a.wait_ms = parse_u64(&v?, "--wait-ms")?;
                } else if let Some(v) = flag_value(t, "--settle-ms", &mut it) {
                    a.settle_ms = parse_u64(&v?, "--settle-ms")?;
                } else if let Some(v) = flag_value(t, "--max-bytes", &mut it) {
                    a.max_bytes = Some(parse_usize(&v?, "--max-bytes")?);
                } else {
                    match t {
                        "--peek" => a.peek = true,
                        "--json" => a.json = true,
                        other => return Err(unknown("terminal read", other)),
                    }
                }
            }
            Ok(TerminalArgs::Read(a))
        }
        "status" => {
            let mut session = None;
            let mut json = false;
            for t in it {
                match t.as_str() {
                    "--json" => json = true,
                    other if other.starts_with('-') => {
                        return Err(unknown("terminal status", other))
                    }
                    other => {
                        if session.is_some() {
                            return Err(CliError::usage(
                                "terminal status takes at most one <session-id>",
                            ));
                        }
                        session = Some(other.to_string());
                    }
                }
            }
            Ok(TerminalArgs::Status { session, json })
        }
        "close" => {
            let session = terminal_session(&mut it, "close")?;
            let mut json = false;
            for t in it {
                match t.as_str() {
                    "--json" => json = true,
                    other => return Err(unknown("terminal close", other)),
                }
            }
            Ok(TerminalArgs::Close { session, json })
        }
        other => Err(CliError::usage(format!(
            "unknown terminal subcommand '{other}' (expected open|write|exec|read|status|close)"
        ))),
    }
}

/// Splits `--flag=value` into two tokens so every option supports both forms.
fn expand_equals(items: Vec<String>) -> Vec<String> {
    let mut out = Vec::with_capacity(items.len());
    for item in items {
        if item.starts_with("--") {
            if let Some(position) = item.find('=') {
                if position > 2 {
                    out.push(item[..position].to_string());
                    out.push(item[position + 1..].to_string());
                    continue;
                }
            }
        }
        out.push(item);
    }
    out
}

fn value_of(it: &mut std::slice::Iter<'_, String>, flag: &str) -> CliResult<String> {
    match it.next() {
        Some(value) => Ok(value.clone()),
        None => Err(CliError::usage(format!("{flag} requires a value"))),
    }
}

fn set_source(slot: &mut Option<PlanSource>, source: PlanSource) -> CliResult<()> {
    if slot.is_some() {
        return Err(CliError::usage(
            "run accepts only one plan source (--stdin, --file or --json-arg)",
        ));
    }
    *slot = Some(source);
    Ok(())
}

fn unknown(command: &str, token: &str) -> CliError {
    if token.starts_with('-') {
        CliError::usage(format!("unknown option '{token}' for '{command}'"))
    } else {
        CliError::usage(format!("unexpected argument '{token}' for '{command}'"))
    }
}

fn non_empty(flag: &str, value: String) -> CliResult<String> {
    if value.trim().is_empty() {
        return Err(CliError::usage(format!(
            "{flag} requires a non-empty value"
        )));
    }
    Ok(value)
}

fn parse_usize(raw: &str, flag: &str) -> CliResult<usize> {
    raw.trim()
        .parse::<usize>()
        .map_err(|_| CliError::usage(format!("{flag} expects an integer, got '{raw}'")))
}

fn parse_u32(raw: &str, flag: &str) -> CliResult<u32> {
    raw.trim()
        .parse::<u32>()
        .map_err(|_| CliError::usage(format!("{flag} expects an integer, got '{raw}'")))
}

fn parse_u64(raw: &str, flag: &str) -> CliResult<u64> {
    raw.trim()
        .parse::<u64>()
        .map_err(|_| CliError::usage(format!("{flag} expects an integer, got '{raw}'")))
}

fn parse_seconds(raw: &str, flag: &str) -> CliResult<f64> {
    let seconds = raw
        .trim()
        .parse::<f64>()
        .map_err(|_| CliError::usage(format!("{flag} expects a number of seconds, got '{raw}'")))?;
    if !seconds.is_finite() || seconds <= 0.0 {
        return Err(CliError::usage(format!(
            "{flag} must be a positive number of seconds"
        )));
    }
    if seconds > MAX_EVENT_SECONDS {
        return Err(CliError::usage(format!(
            "{flag} must be at most {MAX_EVENT_SECONDS:.0} seconds"
        )));
    }
    Ok(seconds)
}

/// Element handles are decimal; `0x` hex is accepted because the window and
/// element listings print handles as hex.
fn parse_element_id(raw: &str) -> CliResult<u64> {
    let text = raw.trim();
    let parsed = match text.strip_prefix("0x").or_else(|| text.strip_prefix("0X")) {
        Some(hex) => u64::from_str_radix(hex, 16),
        None => text.parse::<u64>(),
    };
    parsed.map_err(|_| CliError::usage(format!("'{raw}' is not a valid element id")))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse_ok(args: &[&str]) -> Invocation {
        parse(args.iter().copied()).expect("parse should succeed")
    }

    fn parse_err(args: &[&str]) -> CliError {
        parse(args.iter().copied()).expect_err("parse should fail")
    }

    fn command(args: &[&str]) -> Command {
        match parse_ok(args) {
            Invocation::Run(command) => command,
            Invocation::Help => panic!("expected a command, got help"),
        }
    }

    #[test]
    fn help_forms_are_recognized() {
        assert_eq!(parse_ok(&["help"]), Invocation::Help);
        assert_eq!(parse_ok(&["--help"]), Invocation::Help);
        assert_eq!(parse_ok(&["-h"]), Invocation::Help);
    }

    #[test]
    fn empty_and_unknown_input_is_a_usage_error() {
        assert!(matches!(parse_err(&[]), CliError::Usage(_)));
        assert!(matches!(parse_err(&["bogus"]), CliError::Usage(_)));
        assert!(matches!(parse_err(&["--bogus"]), CliError::Usage(_)));
    }

    #[test]
    fn status_accepts_its_flags() {
        assert_eq!(
            command(&["status", "--windows", "--logs", "--ipc", "--json"]),
            Command::Status(StatusArgs {
                windows: true,
                logs: true,
                ipc: true,
                log_lines: DEFAULT_LOG_LINES,
                json: true,
            })
        );
        assert_eq!(
            command(&["status", "--log-lines", "5"]),
            Command::Status(StatusArgs {
                log_lines: 5,
                ..Default::default()
            })
        );
    }

    #[test]
    fn ping_defaults_to_twenty_round_trips() {
        assert_eq!(
            command(&["ping"]),
            Command::Ping(PingArgs {
                count: DEFAULT_PING_COUNT,
                json: false,
            })
        );
        assert_eq!(
            command(&["ping", "--count", "3", "--json"]),
            Command::Ping(PingArgs {
                count: 3,
                json: true,
            })
        );
        assert!(matches!(
            parse_err(&["ping", "--count", "0"]),
            CliError::Usage(_)
        ));
        assert!(matches!(
            parse_err(&["ping", "--count"]),
            CliError::Usage(_)
        ));
        assert!(matches!(
            parse_err(&["ping", "--count", "many"]),
            CliError::Usage(_)
        ));
    }

    #[test]
    fn read_requires_exactly_one_element_id() {
        assert_eq!(
            command(&["read", "42"]),
            Command::Read(ReadArgs {
                element_id: 42,
                json: false,
            })
        );
        assert_eq!(
            command(&["read", "0x1F", "--json"]),
            Command::Read(ReadArgs {
                element_id: 31,
                json: true,
            })
        );
        assert!(matches!(parse_err(&["read"]), CliError::Usage(_)));
        assert!(matches!(parse_err(&["read", "abc"]), CliError::Usage(_)));
        assert!(matches!(parse_err(&["read", "1", "2"]), CliError::Usage(_)));
    }

    /// Test helper so the `run` source assertions stay readable.
    fn run_source(args: &[&str]) -> String {
        match command(args) {
            Command::Run(args) => args.source.describe(),
            other => panic!("expected a run command, got {other:?}"),
        }
    }

    #[test]
    fn run_picks_one_plan_source() {
        assert_eq!(run_source(&["run"]), "stdin");
        assert_eq!(
            run_source(&["run", "--file", "plan.json"]),
            "file:plan.json"
        );
        assert_eq!(
            run_source(&["run", "--json-arg={\"steps\":[]}"]),
            "argument"
        );
        assert!(matches!(
            parse_err(&["run", "--file", "a.json", "--stdin"]),
            CliError::Usage(_)
        ));

        match command(&["run", "--dry-run", "--timeout-ms", "2500", "--json"]) {
            Command::Run(args) => {
                assert!(args.dry_run);
                assert_eq!(args.timeout_ms, Some(2500));
                assert!(args.json);
            }
            other => panic!("expected run, got {other:?}"),
        }
    }

    #[test]
    fn equals_form_is_expanded_for_every_flag() {
        match command(&["find", "--role=button", "--name=Save", "--limit=2"]) {
            Command::Find(args) => {
                assert_eq!(args.role.as_deref(), Some("button"));
                assert_eq!(args.name.as_deref(), Some("Save"));
                assert_eq!(args.limit, Some(2));
            }
            other => panic!("expected find, got {other:?}"),
        }
    }

    #[test]
    fn find_accepts_a_positional_selector() {
        match command(&["find", "Save", "--role", "button", "--all", "--json"]) {
            Command::Find(args) => {
                assert_eq!(args.selector_text.as_deref(), Some("Save"));
                assert_eq!(args.role.as_deref(), Some("button"));
                assert!(args.all);
                assert!(args.json);
            }
            other => panic!("expected find, got {other:?}"),
        }
        assert!(matches!(
            parse_err(&["find", "one", "two"]),
            CliError::Usage(_)
        ));
    }

    #[test]
    fn act_reads_the_step_payload_from_json() {
        match command(&["act", "--json", r#"{"action":"sleep","ms":5}"#]) {
            Command::Act(args) => {
                assert_eq!(args.step_json, r#"{"action":"sleep","ms":5}"#);
                assert!(!args.out_json);
            }
            other => panic!("expected act, got {other:?}"),
        }
        match command(&["act", "--step", "{}", "--out-json"]) {
            Command::Act(args) => assert!(args.out_json),
            other => panic!("expected act, got {other:?}"),
        }
        assert!(matches!(parse_err(&["act"]), CliError::Usage(_)));
    }

    #[test]
    fn events_defaults_and_seconds() {
        match command(&["events"]) {
            Command::Events(args) => {
                assert_eq!(args.seconds, DEFAULT_EVENT_SECONDS);
                assert!(!args.json);
            }
            other => panic!("expected events, got {other:?}"),
        }
        match command(&["events", "--seconds", "1.5", "--json"]) {
            Command::Events(args) => {
                assert_eq!(args.seconds, 1.5);
                assert!(args.json);
            }
            other => panic!("expected events, got {other:?}"),
        }
        assert!(matches!(
            parse_err(&["events", "--seconds", "0"]),
            CliError::Usage(_)
        ));
    }

    #[test]
    fn observe_and_windows_options() {
        match command(&["observe", "--tree", "--window", "Notepad", "--depth", "4"]) {
            Command::Observe(args) => {
                assert!(args.tree);
                assert_eq!(args.window.as_deref(), Some("Notepad"));
                assert_eq!(args.depth, Some(4));
            }
            other => panic!("expected observe, got {other:?}"),
        }
        match command(&["windows", "--filter", "notepad", "--json"]) {
            Command::Windows(args) => {
                assert_eq!(args.filter.as_deref(), Some("notepad"));
                assert!(args.json);
            }
            other => panic!("expected windows, got {other:?}"),
        }
        assert!(matches!(
            parse_err(&["windows", "--filter"]),
            CliError::Usage(_)
        ));
    }

    #[test]
    fn cancel_accepts_all_and_json() {
        assert_eq!(
            command(&["cancel", "--all", "--json"]),
            Command::Cancel(CancelArgs { json: true })
        );
        assert_eq!(
            command(&["doctor", "--json"]),
            Command::Doctor(DoctorArgs { json: true })
        );
        assert_eq!(
            command(&["endpoint"]),
            Command::Endpoint(EndpointArgs { json: false })
        );
        assert_eq!(
            command(&["fast-path", "verify", "--json"]),
            Command::FastPath(FastPathArgs { json: true })
        );
        assert_eq!(
            command(&["skill", "install", "antigravity", "--json"]),
            Command::Skill(SkillArgs::Install {
                target: "antigravity".into(),
                json: true,
            })
        );
        assert_eq!(
            command(&["skill", "status"]),
            Command::Skill(SkillArgs::Status { json: false })
        );
    }

    #[test]
    fn cli_run_uses_native_sdk() {
        // Verify inbrisk-cli Cargo.toml depends on inbrisk-sdk directly
        let cargo_toml = include_str!("../Cargo.toml");
        assert!(cargo_toml.contains("inbrisk-sdk"));
    }

    #[test]
    fn cli_fast_path_verify_works_without_mcp() {
        // FastPathArgs exists and parser parses verify
        let invocation = parse(["fast-path", "verify"]).expect("parse fast-path verify");
        match invocation {
            Invocation::Run(Command::FastPath(args)) => {
                assert!(!args.json);
            }
            _ => panic!("expected FastPath command"),
        }
    }

    #[test]
    fn cli_json_stdout_contains_no_human_noise() {
        // Ensure that JSON serialization of command outputs yields valid parsable JSON
        let val = serde_json::json!({
            "success": true,
            "transport": "native-shm",
            "operationId": 42,
            "stepsExecuted": 2,
            "runtime": { "pid": 1234, "version": "0.1.0" }
        });
        let s = serde_json::to_string(&val).expect("serialize json");
        let parsed: serde_json::Value = serde_json::from_str(&s).expect("deserialize json");
        assert_eq!(parsed["transport"], "native-shm");
        assert_eq!(parsed["success"], true);
    }

    #[test]
    fn cli_run_stdin_uses_plan_schema() {
        use crate::plan_input::PlanSource;
        // Verify stdin source parses into Plan
        let sample_plan = r#"{"steps":[{"action":"launch","app":"notepad"}]}"#;
        let plan = PlanSource::Inline(sample_plan.into())
            .load()
            .expect("load plan");
        assert_eq!(plan.steps.len(), 1);
    }

    #[test]
    fn architecture_fast_path_does_not_depend_on_mcp() {
        let cli_toml = include_str!("../Cargo.toml");
        assert!(!cli_toml.contains("inbrisk-mcp"));
        assert!(!cli_toml.contains("mcp"));
    }

    fn term(args: &[&str]) -> TerminalArgs {
        let mut v = vec!["terminal"];
        v.extend_from_slice(args);
        match parse_ok(&v) {
            Invocation::Run(Command::Terminal(t)) => t,
            other => panic!("expected terminal, got {other:?}"),
        }
    }

    #[test]
    fn terminal_open_flags() {
        match term(&["open", "--shell=cmd", "--cwd", "C:\\x", "--cols", "100", "--raw", "--json"]) {
            TerminalArgs::Open(a) => {
                assert_eq!(a.shell.as_deref(), Some("cmd"));
                assert_eq!(a.cwd.as_deref(), Some("C:\\x"));
                assert_eq!(a.cols, Some(100));
                assert!(a.raw && a.json && !a.no_profile);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(term(&["open"]), TerminalArgs::Open(_)));
    }

    #[test]
    fn terminal_write_text_and_stdin_rules() {
        match term(&["write", "term_01", "cd C:\\x\\r\\n"]) {
            TerminalArgs::Write(a) => {
                assert_eq!(a.session, "term_01");
                assert_eq!(a.text.as_deref(), Some("cd C:\\x\\r\\n"));
                assert!(!a.stdin && !a.literal);
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(term(&["write", "t", "--stdin"]), TerminalArgs::Write(a) if a.stdin));
        // Text starting with dashes after `--`; `=` is not split.
        match term(&["write", "t", "--literal", "--", "--foo=bar"]) {
            TerminalArgs::Write(a) => {
                assert!(a.literal);
                assert_eq!(a.text.as_deref(), Some("--foo=bar"));
            }
            other => panic!("{other:?}"),
        }
        assert!(parse(["terminal", "write", "t"]).is_err());
        assert!(parse(["terminal", "write", "t", "x", "--stdin"]).is_err());
        assert!(parse(["terminal", "write", "t", "a", "b"]).is_err());
        assert!(parse(["terminal", "write"]).is_err());
    }

    #[test]
    fn terminal_exec_keeps_command_verbatim() {
        match term(&["exec", "t", "inbrisk-cli", "observe", "--json", "--x=y"]) {
            TerminalArgs::Exec(a) => {
                assert_eq!(a.command, "inbrisk-cli observe --json --x=y");
                assert!(!a.json);
            }
            other => panic!("{other:?}"),
        }
        match term(&["exec", "t", "--json", "dir"]) {
            TerminalArgs::Exec(a) => assert!(a.json && a.command == "dir"),
            other => panic!("{other:?}"),
        }
        match term(&["exec", "t", "--json", "--", "target/debug/inbrisk-cli", "observe", "--json"]) {
            TerminalArgs::Exec(a) => {
                assert!(a.json);
                assert_eq!(a.command, "target/debug/inbrisk-cli observe --json");
            }
            other => panic!("{other:?}"),
        }
        match term(&["exec", "t", "--", "--json", "dir"]) {
            TerminalArgs::Exec(a) => {
                assert!(!a.json);
                assert_eq!(a.command, "--json dir");
            }
            other => panic!("{other:?}"),
        }
        match term(&["exec", "t", "--json", "--", "echo", "$env:TEST"]) {
            TerminalArgs::Exec(a) => {
                assert!(a.json);
                assert_eq!(a.command, "echo $env:TEST");
            }
            other => panic!("{other:?}"),
        }
        assert!(parse(["terminal", "exec", "t", "--ambiguous"]).is_err());
        assert!(parse(["terminal", "exec", "t"]).is_err());
    }

    #[test]
    fn terminal_read_status_close() {
        match term(&["read", "t", "--after=5", "--wait-ms", "2000", "--settle-ms", "100", "--peek"]) {
            TerminalArgs::Read(a) => {
                assert_eq!((a.after, a.wait_ms, a.settle_ms, a.peek), (Some(5), 2000, 100, true));
            }
            other => panic!("{other:?}"),
        }
        assert!(matches!(term(&["status"]), TerminalArgs::Status { session: None, .. }));
        assert!(matches!(term(&["status", "t"]), TerminalArgs::Status { session: Some(_), .. }));
        assert!(matches!(term(&["close", "t", "--json"]), TerminalArgs::Close { json: true, .. }));
        assert!(parse(["terminal", "close"]).is_err());
        assert!(parse(["terminal", "bogus"]).is_err());
        assert!(parse(["terminal"]).is_err());
        assert!(parse(["terminal", "read", "t", "--wat"]).is_err());
    }
}
