//! `inbrisk-cli`: the thin, fast-path client for the Inbrisk runtime.
//!
//! The CLI owns no automation logic. Every command is a one-shot mapping onto an
//! `inbrisk-sdk` call, which keeps agent skills on the cheapest possible path:
//! start the process, spend one shared-memory round trip, exit.
//!
//! Exit codes: `0` success, `1` runtime/operation error, `2` usage error,
//! `3` runtime unavailable, `4` timeout.

mod cli;
mod commands;
mod error;
mod output;
mod plan_input;
mod region;

use std::process::ExitCode;

use error::CliResult;
use output::FailStream;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    // Returning `ExitCode` (instead of calling `process::exit`) lets every
    // runtime handle drop, which releases this client's IPC session slot.
    ExitCode::from(run(&args) as u8)
}

/// Runs one invocation and returns its process exit code.
fn run(args: &[String]) -> i32 {
    let invocation = match cli::parse(args.iter().cloned()) {
        Ok(invocation) => invocation,
        // A usage error is never machine output: it is a typo, and it is
        // reported as text on stderr with exit code 2.
        Err(error) => return output::report(&error, false, FailStream::Stderr),
    };

    match invocation {
        cli::Invocation::Help => match output::print_line(cli::USAGE) {
            Ok(()) => error::EXIT_OK,
            Err(failure) => output::report(&failure, false, FailStream::Stderr),
        },
        cli::Invocation::Run(command) => dispatch(command),
    }
}

fn dispatch(command: cli::Command) -> i32 {
    use cli::Command;

    // Diagnostic commands answer on stdout: "the runtime is not running" is
    // their output, not a side effect. Everything else keeps stdout clean.
    match command {
        Command::Status(args) => finish(
            commands::status::execute(&args),
            args.json,
            FailStream::Stdout,
        ),
        Command::Ping(args) => finish(
            commands::ping::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Windows(args) => finish(
            commands::windows::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Observe(args) => finish(
            commands::observe::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Find(args) => finish(
            commands::find::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Read(args) => finish(
            commands::read::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Act(args) => finish(
            commands::act::execute(&args),
            args.out_json,
            FailStream::Stderr,
        ),
        Command::Run(args) => finish(commands::run::execute(&args), args.json, FailStream::Stderr),
        Command::Cancel(args) => finish(
            commands::cancel::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Events(args) => finish(
            commands::events::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Doctor(args) => finish(
            commands::doctor::execute(&args),
            args.json,
            FailStream::Stderr,
        ),
        Command::Endpoint(args) => finish(
            commands::endpoint::execute(&args),
            args.json,
            FailStream::Stdout,
        ),
        Command::FastPath(args) => finish(
            commands::fast_path::execute(&args),
            args.json,
            FailStream::Stdout,
        ),
        Command::Skill(args) => {
            let json = match &args {
                cli::SkillArgs::Install { json, .. } => *json,
                cli::SkillArgs::Status { json } => *json,
            };
            finish(commands::skill::execute(&args), json, FailStream::Stdout)
        }
        Command::Terminal(args) => finish(
            commands::terminal::execute(&args),
            commands::terminal::wants_json(&args),
            FailStream::Stderr,
        ),
    }
}

/// Success carries the exit code the command chose (a failed plan is an
/// operation error, not a CLI failure); failures are reported here.
fn finish(result: CliResult<i32>, json: bool, stream: FailStream) -> i32 {
    match result {
        Ok(code) => code,
        Err(error) => output::report(&error, json, stream),
    }
}
