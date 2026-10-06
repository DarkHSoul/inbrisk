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
- **One Observe -> One Compound Run**: Prefer composing a batch plan over multi-turn conversational loops.
  ```powershell
  # Step 1: Inspect (if needed)
  & "$env:LOCALAPPDATA\Inbrisk\bin\inbrisk-cli.exe" observe --window "Notepad" --json
  
  # Step 2: Execute batch plan
  @'
  {
    "steps": [
      { "action": "launch", "app": "notepad" },
      { "action": "set_value", "target": { "role": "document" }, "value": "Hello from Inbrisk" }
    ]
  }
  '@ | & "$env:LOCALAPPDATA\Inbrisk\bin\inbrisk-cli.exe" run --stdin --json
  ```
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
