# Inbrisk

> [!WARNING]
> **Early Alpha:** This project is currently in early alpha. It is under active development and may be unstable, undergo frequent breaking changes, or exhibit unexpected behavior and rough edges. Supervised usage is recommended.

Local Windows computer-control MCP server. Your AI host sees the screen and
drives apps over a local stdio connection — no network listener, no cloud.

## Install

1. Download `InbriskSetup.exe` from the release.
2. Run it — a setup window opens (no console). Choose the install scope,
   press **Install**, then pick which AI hosts to connect on the **Hosts**
   page. The same window is also the manager: Doctor, Status, Update,
   Safety (emergency stop + hotkeys), Appearance (theme + indicator colors),
   Logs, and config Backups.
3. Restart (or start a new session in) your AI host.
4. Done — ask your AI to open an app or type something.

After install, reopen the manager anytime: double-click `inbrisk.exe`
or run `inbrisk ui`.

Silent install for scripted/AI-driven setup (no GUI, console flow):

```
InbriskSetup.exe /quiet
```

Already installed? `inbrisk.exe` is in `%LOCALAPPDATA%\Programs\Inbrisk`:

```
inbrisk setup          # detect hosts + connect interactively
inbrisk setup --non-interactive --host devin
inbrisk doctor         # health check incl. a real MCP handshake
inbrisk status --json  # machine-readable state for automation
inbrisk hosts          # which MCP hosts are detected/configured
inbrisk connect cursor
inbrisk disconnect cursor
inbrisk repair         # fix stale/missing registrations
inbrisk ghost tray     # Ghost OS: isolated agent desktop + PiP overlay
inbrisk uninstall      # removes Inbrisk entries, then the binaries
```

## Manual MCP config (any stdio host)

```json
{
  "mcpServers": {
    "inbrisk": {
      "command": "%LOCALAPPDATA%\\Programs\\Inbrisk\\inbrisk.exe",
      "args": ["mcp"]
    }
  }
}
```

Note: VS Code uses `servers` (not `mcpServers`) in `%APPDATA%\Code\User\mcp.json`
— `inbrisk connect vscode` handles this. Portable mode: unzip
`Inbrisk-x64.zip` anywhere and point the `command` at that `inbrisk.exe`.

## Safety & Human Takeover

- **Emergency Panic:** `Ctrl+Alt+Pause` stops all computer control instantly across the entire machine.
- **Human-only Resume:** `Ctrl+Alt+Shift+Pause` resumes (local user only — the AI cannot resume itself).
- **Clean Safe-Point Pause:** `computer_pause_run` and plan-level `human` actions pause execution cleanly, showing an amber status pill with the pause reason and next step.
- **Seamless Resume:** `computer_resume_run` invalidates stale handles, detects human changes, and continues remaining steps without restarting.
- **Trust Indicators:** Perimeter light border + floating status pill reflect live connection and execution state.

## Core Capabilities

- **Behavioral Mini-Programs:** Submit structured plans with local loops (`scan`, `for_each`), property extraction (`collect`), and conditionals in a single turn.
- **Relational Selectors:** Target elements unambiguously via `within`, `ancestor`, `labelledBy`, and `nearText`.
- **Desktop Memory & Recipes:** Record proven flows with `computer_save_recipe` and replay them instantly with `computer_run_recipe`.
- **Actionable Diagnosis:** Zero guessing — unresolvable targets report root causes (`TargetOffscreen`, `BudgetExhausted`, `CloseMatchesFound`) with concrete fixes.
- **Desktop Concurrency:** Multi-client arbiter guarantees exclusive physical input leases while allowing concurrent read-only observations.
- **Ghost OS (isolated agent desktop):** Run the agent's work inside an isolated Windows session instead of your interactive desktop.

## Ghost OS

Ghost OS hosts a second, unattended desktop so automation doesn't share or
disturb the interactive session. It runs under a dedicated local user
(`InbriskAgent`, auto-provisioned) via a headless loopback RDP session and/or
an isolated Win32 desktop; a worker inside that session drives its own
mouse/keyboard over the `inbrisk_ghost_bus` named pipe, and a live
Picture-in-Picture window shows what the agent sees.

```
inbrisk ghost tray      # background ghost session + PiP overlay + tray icon
inbrisk ghost pip       # control the PiP viewer (position, opacity, fps, interactive)
inbrisk ghost status    # live session + overlay status
inbrisk ghost stop      # stop session; wipe ghost credentials and temp files
inbrisk ghost logs      # tail %LOCALAPPDATA%\inbrisk\logs\ghost-os.log
```

MCP hosts get the matching tools `computer_ghost_start`, `computer_ghost_status`,
`computer_ghost_batch`, `computer_ghost_control`, and `computer_ghost_stop`.

## For developers

The canonical implementation is **C# / .NET 8** (`src/Inbrisk.*`,
`inbrisk.sln`). An experimental Rust workspace was explored earlier and
archived to `../inbrisk-rust` as a frozen experiment — it is not part of
this repository and is not built or shipped.

```
dotnet build inbrisk.sln            # build
dotnet test tests/Inbrisk.Tests     # regression suite (xunit; desktop tests need an interactive session)
tools\publish.ps1 -Version 1.0.0    # release artifacts → artifacts\release\
```

`tests/Inbrisk.TestApp` is a deterministic WPF fixture app used by the
end-to-end driver tests. The MCP entry point is `inbrisk.exe mcp`
(`src/Inbrisk.Cli` → `src/Inbrisk.Mcp`).

See ARCHITECTURE.md for the computer-control design.

## License

Copyright (c) 2026 DarkHSoul. All rights reserved.

This project is licensed under a proprietary / source-available license. See the [LICENSE](LICENSE) file for detailed restrictions and terms. Source is view/review only; distributed binaries (InbriskSetup.exe / inbrisk.exe) may be installed and run for your own internal business or personal use. Unauthorized copying, modification, or redistribution is prohibited. Third-party components are listed in [NOTICE](NOTICE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
