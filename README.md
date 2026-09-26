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

## For developers

```
dotnet build inbrisk.sln            # build
dotnet test tests/Inbrisk.Tests     # regression suite
tools\publish.ps1 -Version 1.0.0    # release artifacts → artifacts\release\
```

See ARCHITECTURE.md for the computer-control design.
