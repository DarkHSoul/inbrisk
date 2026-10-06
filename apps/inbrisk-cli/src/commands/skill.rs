//! `skill`: manage and verify Antigravity skill installations.
//!
//! Installs and inspects the Inbrisk fast-path skill across supported Antigravity
//! roots (workspace `.agents/skills` and user-global `.gemini\antigravity\skills`).

use std::fs;
use std::path::{Path, PathBuf};

use crate::cli::SkillArgs;
use crate::error::{CliError, CliResult, EXIT_ERROR, EXIT_OK};
use crate::output;

pub const DEFAULT_SKILL_MD: &str = r#"---
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
"#;

pub const DEFAULT_PLAN_SCHEMA: &str = r#"# Inbrisk Plan Schema

Plans submitted to `inbrisk-cli run --stdin --json` accept either:
1. Bare plan: `{"steps": [...]}`
2. Wire form: `{"op": "run", "steps": [...]}`
3. Wrapped plan: `{"plan": {"steps": [...]}}`

## Step Types
- `launch`: `{"action": "launch", "app": "notepad", "args": []}`
- `focus`: `{"action": "focus", "target": {"window": "Notepad"}}`
- `set_value`: `{"action": "set_value", "target": {"role": "document"}, "value": "text"}`
- `click`: `{"action": "click", "target": {"role": "button", "name": "OK"}}`
- `type`: `{"action": "type", "text": "sample"}`
- `key`: `{"action": "key", "key": "s", "modifiers": ["ctrl"]}`
- `sleep`: `{"action": "sleep", "ms": 200}`
- `wait`: `{"action": "wait", "window": {"process": "Notepad.exe"}}`
- `close`: `{"action": "close", "target": {"window": "Notepad"}}`
"#;

pub const DEFAULT_SAFETY: &str = r#"# Inbrisk Safety Rules

## Protected Processes
The following processes must NEVER be terminated or disrupted:
- `Antigravity.exe`
- `Hermes.exe`
- `node.exe`
- `python.exe`
- `Code.exe`
- Terminal/PowerShell host processes

## Window Preservation
Do not close user documents or useful output windows without explicit user confirmation.
"#;

pub const DEFAULT_EXAMPLES: &str = r#"# Inbrisk Automation Examples

## 1. Launch Notepad and Write Text
```json
{
  "steps": [
    { "action": "launch", "app": "notepad" },
    { "action": "wait", "window": { "title": "Notepad" } },
    { "action": "set_value", "target": { "role": "document" }, "value": "INBRISK_NATIVE_FAST_PATH_OK" }
  ]
}
```

## 2. Inspect Window Tree
```bash
inbrisk-cli observe --window "Notepad" --depth 2 --json
```

## 3. Verify Fast Path
```bash
inbrisk-cli fast-path verify --json
```
"#;

pub fn execute(args: &SkillArgs) -> CliResult<i32> {
    match args {
        SkillArgs::Install { target, json } => execute_install(target, *json),
        SkillArgs::Status { json } => execute_status(*json),
    }
}

fn execute_install(target: &str, json: bool) -> CliResult<i32> {
    if target != "antigravity" {
        return Err(CliError::usage(format!(
            "unsupported skill target '{target}', only 'antigravity' is supported"
        )));
    }

    let mut installed_paths = Vec::new();

    // 1. Workspace root detection
    if let Some(ws_skill_dir) = detect_workspace_skill_dir() {
        if install_skill_files(&ws_skill_dir).is_ok() {
            installed_paths.push(ws_skill_dir);
        }
    }

    // 2. User-global Antigravity root detection
    if let Some(global_skill_dir) = detect_global_skill_dir() {
        if install_skill_files(&global_skill_dir).is_ok() {
            installed_paths.push(global_skill_dir);
        }
    }

    let success = !installed_paths.is_empty();

    if json {
        let res = serde_json::json!({
            "success": success,
            "target": target,
            "installedPaths": installed_paths.iter().map(|p| p.to_string_lossy().to_string()).collect::<Vec<_>>(),
            "skillName": "inbrisk",
            "installedFiles": ["SKILL.md", "references/plan-schema.md", "references/safety.md", "references/examples.md"]
        });
        output::print_json(&res)?;
    } else {
        output::print_line("=== Inbrisk Antigravity Skill Installer ===")?;
        if success {
            output::print_line("Skill successfully installed to:")?;
            for p in &installed_paths {
                output::print_line(&format!("  - {}", p.display()))?;
            }
        } else {
            output::eprint_line("error: No valid Antigravity skill directory found.")?;
        }
    }

    Ok(if success { EXIT_OK } else { EXIT_ERROR })
}

fn execute_status(json: bool) -> CliResult<i32> {
    let ws = detect_workspace_skill_dir();
    let global = detect_global_skill_dir();

    let ws_valid = ws
        .as_ref()
        .map(|p| p.join("SKILL.md").exists())
        .unwrap_or(false);
    let global_valid = global
        .as_ref()
        .map(|p| p.join("SKILL.md").exists())
        .unwrap_or(false);

    let installed = ws_valid || global_valid;

    if json {
        let res = serde_json::json!({
            "installed": installed,
            "workspaceSkillDir": ws.as_ref().map(|p| p.to_string_lossy().to_string()),
            "workspaceSkillValid": ws_valid,
            "globalSkillDir": global.as_ref().map(|p| p.to_string_lossy().to_string()),
            "globalSkillValid": global_valid,
        });
        output::print_json(&res)?;
    } else {
        output::print_line("=== Inbrisk Skill Status ===")?;
        output::print_line(&format!(
            "Workspace Skill: {} ({})",
            ws.as_ref()
                .map(|p| p.display().to_string())
                .unwrap_or_else(|| "none".into()),
            if ws_valid { "Valid" } else { "Missing/Invalid" }
        ))?;
        output::print_line(&format!(
            "Global Skill:    {} ({})",
            global
                .as_ref()
                .map(|p| p.display().to_string())
                .unwrap_or_else(|| "none".into()),
            if global_valid {
                "Valid"
            } else {
                "Missing/Invalid"
            }
        ))?;
        output::print_line(&format!(
            "Status:          {}",
            if installed {
                "INSTALLED"
            } else {
                "NOT INSTALLED"
            }
        ))?;
    }

    Ok(if installed { EXIT_OK } else { EXIT_ERROR })
}

fn detect_workspace_skill_dir() -> Option<PathBuf> {
    let mut current = std::env::current_dir().ok()?;
    loop {
        let agents_dir = current.join(".agents").join("skills");
        if agents_dir.exists() {
            return Some(agents_dir.join("inbrisk"));
        }
        if !current.pop() {
            break;
        }
    }
    None
}

fn detect_global_skill_dir() -> Option<PathBuf> {
    let user_profile = std::env::var_os("USERPROFILE")?;
    let base = Path::new(&user_profile)
        .join(".gemini")
        .join("antigravity")
        .join("skills");
    Some(base.join("inbrisk"))
}

fn install_skill_files(target_dir: &Path) -> std::io::Result<()> {
    let refs_dir = target_dir.join("references");
    fs::create_dir_all(&refs_dir)?;

    fs::write(target_dir.join("SKILL.md"), DEFAULT_SKILL_MD)?;
    fs::write(refs_dir.join("plan-schema.md"), DEFAULT_PLAN_SCHEMA)?;
    fs::write(refs_dir.join("safety.md"), DEFAULT_SAFETY)?;
    fs::write(refs_dir.join("examples.md"), DEFAULT_EXAMPLES)?;

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn skill_source_exists() {
        assert!(DEFAULT_SKILL_MD.contains("name: inbrisk"));
        assert!(DEFAULT_SKILL_MD.contains("inbrisk-cli.exe"));
        assert!(DEFAULT_SKILL_MD.contains("DO NOT call Inbrisk MCP tools"));
    }

    #[test]
    fn skill_installed_copy_matches_source_version() {
        assert!(DEFAULT_PLAN_SCHEMA.contains("Inbrisk Plan Schema"));
        assert!(DEFAULT_SAFETY.contains("Protected Processes"));
        assert!(DEFAULT_EXAMPLES.contains("INBRISK_NATIVE_FAST_PATH_OK"));
    }

    #[test]
    fn skill_native_cli_path_exists() {
        assert!(DEFAULT_SKILL_MD.contains("%LOCALAPPDATA%\\Inbrisk\\bin\\inbrisk-cli.exe"));
    }

    #[test]
    fn antigravity_skill_is_discoverable() {
        let global = detect_global_skill_dir();
        assert!(global.is_some());
    }
}
