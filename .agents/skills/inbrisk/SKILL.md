---
name: inbrisk
description: Control and automate Windows desktop applications (Notepad, Chrome, Explorer, etc.) using the ultra-fast Rust-native Inbrisk runtime. Always use inbrisk-cli.exe instead of MCP tools.
---

# Inbrisk Windows Native Fast-Path Skill

Use `inbrisk-cli.exe` for Windows desktop automation.

> [!IMPORTANT]
> **DO NOT call Inbrisk MCP tools (`computer_*`) when this native skill is available.**
> Fast-path automation connects directly to the persistent Rust Inbrisk runtime over shared memory IPC using `inbrisk-cli.exe`.

## Binary Resolution Order
1. `%LOCALAPPDATA%\Inbrisk\bin\inbrisk-cli.exe` (Installed production fast path)
2. `target\release\inbrisk-cli.exe` (Repository development fallback)
3. `inbrisk-cli.exe` (System PATH)

## Core Principles

### Execution Strategy: Persistent Terminal vs One-Shot
**1. Persistent Terminal (DEFAULT FOR MULTI-STEP)**
Use a Persistent Terminal when a task requires 2+ dependent CLI operations, repeated observe/find/act loops, or when shell state must be preserved.

```powershell
# Open the persistent session (Do this via outer run_command)
inbrisk-cli terminal open --shell pwsh --no-profile --json
# -> Note the session_id (e.g., "term_01")

# Execute commands inside that specific session
inbrisk-cli terminal write term_01 "inbrisk-cli observe --window `"Notepad`" --json`n"
inbrisk-cli terminal read term_01 --wait-ms 2000

# Base your next action on the output, and run it inside the same session
inbrisk-cli terminal write term_01 "inbrisk-cli act --json '{ `"action`": `"focus`", `"target`": { `"window`": { `"name`": `"Notepad`" } } }'`n"
inbrisk-cli terminal read term_01 --wait-ms 2000

# Clean up when finished
inbrisk-cli terminal close term_01
```

> [!IMPORTANT] 
> **AGENT RULE**: Once a persistent terminal session has been opened for an Inbrisk task, do NOT bypass it with direct standalone Inbrisk CLI invocations (like running `inbrisk-cli observe` from a fresh outer `run_command`). Continue all dependent CLI operations through the persistent session until the workflow completes.

**2. One-Shot Direct CLI (ONLY FOR ISOLATED COMMANDS)**
Use standalone direct CLI calls only when exactly one isolated command is required (e.g., a simple `ping`, `status`, `doctor`), when shell state doesn't matter, and setting up a persistent session would be needless overhead.

- **Semantic First**: Prefer semantic actions (`set_value`, `click`, `select`, `toggle`) over raw physical coordinates.
- **Physical Fallback**: Use physical mouse/keyboard actions only when semantic controls are unavailable.
- **Preservation & Safety**:
  - Never close protected processes (`Antigravity.exe`, `Hermes.exe`, `node.exe`, `python.exe`, `Code.exe`).
  - Useful result windows must remain open. Do not close applications automatically unless requested.
  - Do not take visual screenshots unless accessibility inspection cannot resolve the target.

## Reference Documentation
- Plan Schema & Actions: [references/plan-schema.md](references/plan-schema.md)
- Safety & Protected Processes: [references/safety.md](references/safety.md)
- Common Examples: [references/examples.md](references/examples.md)

## ⚠️ Inbrisk OST vs Inbrisk PC

Inbrisk offers two distinct terminal automation modes:
1. **Inbrisk OST** (One-Shot Terminal via CLI): For atomic background tasks or single-command executions. You use `run_command("inbrisk-cli ...")` for these.
2. **Inbrisk PC** (Persistent Control via direct MCP Tools): For interactive, multi-step workflows where you need to preserve state, environment variables, or work sequentially within the same shell.

**CRITICAL RULE FOR INBRISK PC:**
For multi-step Inbrisk PC workflows, **NEVER** use the host `run_command` tool to proxy `inbrisk-cli terminal open/write/read`. 
You MUST use the native Inbrisk PC MCP tools directly:
- `pc_open`: Opens a persistent PC Control session. Returns a `session_id`.
- `pc_exec`: Executes a command inside the session, waits boundedly, and returns *only* the new output produced. Use this instead of `run_command` for dependent workflows.
- `pc_write` / `pc_read`: For interactive prompts.
- `pc_close`: Closes the session.

Example of a correct Inbrisk PC workflow:
1. `call_mcp_tool(ServerName="inbrisk", ToolName="pc_open", Arguments={})` -> yields `session_id="term_01"`
2. `call_mcp_tool(ServerName="inbrisk", ToolName="pc_exec", Arguments={"session_id":"term_01", "command":"cd my_project"})`
3. `call_mcp_tool(ServerName="inbrisk", ToolName="pc_exec", Arguments={"session_id":"term_01", "command":"npm install"})`
4. `call_mcp_tool(ServerName="inbrisk", ToolName="pc_close", Arguments={"session_id":"term_01"})`
