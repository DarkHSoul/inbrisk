# Wave-2 Verification Checklist — Cleanup, Gates, Recipes, Blender Bridge

Runnable pass/fail verification for: `computer_cleanup`, session-end reap,
PID-reuse guard, `browser_evaluate` script-exec gate, script-launch gate,
`save_as_recipe`/`computer_run_recipe`, Blender `auto_install` + bridge
injection. Every item is a checkbox with an exact JSON call and pass
criteria. Items marked **code review** are verified by reading source, not
by driving the UI.

## 0. Setup

- Server (pick one):
  - Installed: `"%LOCALAPPDATA%\Programs\Inbrisk\inbrisk.exe" mcp`
  - Build tree: `dotnet src\Inbrisk.Cli\bin\Release\net8.0-windows10.0.19041.0\inbrisk.dll mcp`
    (or `dotnet src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.dll`)
- Protocol: newline-delimited JSON-RPC 2.0 over stdio. Handshake:
  `initialize` (`protocolVersion:"2025-03-26"`) → `notifications/initialized`
  → `tools/call` with `{"name":<tool>,"arguments":{…}}`.
  Harness references: `tools/mcp_probe.py`, `tools/mcp_stdio_smoke.mjs`.
- **Tool profile:** `computer_cleanup`, `computer_adapter`, `browser_*` are
  NOT in the core profile (`CoreToolNames`, `src/Inbrisk.Mcp/McpHost.cs:47`).
  Spawn the server with `INBRISK_TOOL_PROFILE=full`.
- Isolation env on the **server process** (recommended for every run):
  - `INBRISK_DATA_DIR=%TEMP%\inbrisk-verify` — redirects settings, recipes
    (`task-recipes\`) and audit mirrors.
  - `INBRISK_AUDIT_DIR=%TEMP%\inbrisk-verify\audit` — redirects the primary
    audit dir (else `%ProgramData%\Inbrisk\audit`, fallback = data dir;
    `AuditLog.Resolve`, `src/Inbrisk.Core/AuditLog.cs:64`).
- Result convention: `result.isError` + `result.content[0].text` containing
  a JSON body (`{error, detail, …}` on failure).
- Preconditions: no emergency stop latched; record pre-existing notepad
  pids with `Get-Process notepad` before starting §1.

---

## 1. `computer_cleanup` reaps only session-spawned processes

Code: `InbriskTools.Cleanup` (`src/Inbrisk.Mcp/InbriskTools.cs` ~L4227),
`ProvenanceBackedSessionProcessTracker` (~L4344): WM_CLOSE → 1500 ms grace
→ `Kill` only-if-ours; vetoes in order: `CanAgentCloseProcess` provenance,
`IsAgentHostOrAncestorPid`, `IsProcessProtected`,
`IsSharedMultiWindowProcess`, per-window `IsWindowProtected`. No force flag.

- [ ] Manually open one notepad (user-owned control). Record its pid `U`.
- [ ] Spawn two test notepads — one call each, `newInstance` forces distinct pids:
      ```json
      {"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"computer_launch","arguments":{"executable":"notepad.exe","waitFor":"window","timeoutMs":15000,"newInstance":true}}}
      ```
      Record `process.pid` → `P1`; repeat → `P2`. Assert both differ from `U`.
- [ ] Call:
      ```json
      {"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"computer_cleanup","arguments":{}}}
      ```
      Expect `isError:false`, body `{pending, reaped, skipped:[{pid,reason}], tracker}`.
- [ ] **PASS** when all hold:
  - `pending` ≥ 2 and `reaped` == 2 (`reaped` is a count in the current
    provenance-backed tracker; if a later tracker returns a pid list,
    assert it equals {P1,P2}).
  - `skipped[*].pid` contains ONLY pids the session spawned (never `U`,
    never an arbitrary system pid); empty is ideal.
  - `Get-Process -Id P1,P2` → both gone; `Get-Process -Id U` → still alive.
  - `computer_windows{}` no longer lists P1/P2 hwnds.
- [ ] Idempotence: a second `computer_cleanup{}` → `reaped == 0`,
      `skipped == []`, `pending == 0`.
- [ ] Negative: `computer_do{"app":"notepad","cleanup":true}` result's
      `cleanup` node has the same `{pending,reaped,skipped}` shape
      (`WithCleanupReap`, ~L4266).

## 2. Session-end reap on dispose

Expected: tearing down the MCP session reaps still-open session-spawned
processes. Expected implementation site: `McpSession.Dispose()`
(`src/Inbrisk.Mcp/McpSession.cs` ~L619) must invoke the session tracker
(`SessionProcessTrackerRegistry.For(_s).Reap()` — reconcile seam described
at InbriskTools.cs ~L4216-4225) **before** `Rt.Dispose()`.

- [ ] Fresh server session (same env as §1).
      `computer_launch{"executable":"notepad.exe","waitFor":"window","newInstance":true}`
      → record pid `P3` and hwnd.
- [ ] Close the session: close stdin / terminate the client so the server
      process exits (for `inbrisk mcp`, kill the host process; daemon
      proxy mode is out of scope — use the direct in-process host).
- [ ] Within ~6 s (1500 ms WM_CLOSE grace + 2000 ms kill wait):
      `Get-Process -Id P3` fails → **PASS**.
- [ ] If P3 survives → **FAIL** (lifecycle workstream hasn't landed the
      Dispose-time reap). Code-level fallback: a `Reap()`/`ReapAll` call on
      the session tracker inside `McpSession.Dispose` before `Rt.Dispose()`
      counts as implemented-pending-e2e.

## 3. PID-reuse guard — code review

- [ ] `LaunchCreationEvidenceEvaluator.EvaluateAgentOwnership`
      (`src/Inbrisk.Mcp/LaunchCreationEvidenceEvaluator.cs:13-98`):
      Rule 4 rejects a final pid present in the pre-launch snapshot;
      Rule 5 rejects when `finalPidStartTime < snapshotTime − 100 ms`
      (a reused pid whose "new" process predates the launch can never be
      claimed agent-owned); missing start time requires `spawnedPid == finalPid`;
      Rule 6 demands positive correlation. **PASS** = all rules present.
- [ ] `McpSession.RecordWindowLaunch` (`McpSession.cs:113-150`) stamps
      `ProcessStartTime` into `SessionWindowProvenance`
      (`McpSession.cs:704-720`); `CapturePreLaunchSnapshot` (`:70-111`)
      records `pid → StartTime` for every live process.
- [ ] Reaper re-validates at kill time, not at registration time:
      `ProvenanceBackedSessionProcessTracker.TryReap` calls
      `CanAgentCloseProcess(pid)` fresh per reap.
- [ ] **Review flag:** `ProcessProvenanceService.GetProvenance(pid)`
      (`src/Inbrisk.Core/Provenance.cs:251-281`) can ADOPT an unregistered
      pid by process-name match via `_launchedProcessNames` — a recycled
      pid running a same-named binary would still be judged agent-owned.
      `ProcessProvenance` carries no StartTime. Confirm either the real
      `SessionProcessTracker` pins `(pid, StartTime)` at registration and
      rechecks StartTime before `Kill`, or record this as a known gap.
- [ ] Manual probe (optional): kill `P` from §1-style launch, immediately
      `Start-Process notepad` from the same pid space is not reliably
      reproducible — the code-level checks above are the gate.

## 4. `browser_evaluate` gate (script execution)

Code: `BrowserCall` gate (`InbriskTools.cs` ~L4176-4185),
`ScriptExecAllowed()` (~L4088-4098), `ScriptExecActions` =
{evaluate,eval,exec,execute} (~L4081). Audit: `mcp-scriptexec.jsonl`
(~L4070-4164).

- [ ] Server spawned with `INBRISK_TOOL_PROFILE=full`, and NEITHER
      `INBRISK_ALLOW_SCRIPT_EXECUTION` set NOR `"AllowScriptExecution":true`
      in `%INBRISK_DATA_DIR%\settings.json` (default off).
- [ ] Call:
      ```json
      {"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"browser_evaluate","arguments":{"expression":"1+1"}}}
      ```
      **PASS** = `isError:true`, body `error:"ConfirmationDenied"`, detail
      mentions `AllowScriptExecution` / `INBRISK_ALLOW_SCRIPT_EXECUTION`.
      (`PolicyDenied`/`ConfirmationRequired` also acceptable — any
      non-success refusal; never a result value.)
- [ ] Audit: tail `%INBRISK_AUDIT_DIR%\mcp-scriptexec.jsonl` (primary;
      else `%ProgramData%\Inbrisk\audit\mcp-scriptexec.jsonl`) — or the
      mirror `%INBRISK_DATA_DIR%\mcp-scriptexec.jsonl`. Last line has
      `category:"script_execution"`, `tool:"browser_evaluate"`,
      `action:"evaluate"`, `ok:false`,
      `reason:"denied — allowScriptExecution off"`, `codeHash` (SHA-256,
      never the raw code), `codeLength`, plus chain fields `prev`+`h`.
- [ ] Flip consent: respawn with `INBRISK_ALLOW_SCRIPT_EXECUTION=1`.
      Same call → gate open: either `success:true` (debug Chrome
      auto-spawns on port 9222; result 2) or a non-gate failure
      (adapter/target unreachable). **FAIL only if still
      `ConfirmationDenied`.**
- [ ] Audit again: second record `ok:true`/`reason:"allowed — …"`;
      `prev` equals prior line's `h`.
- [ ] Env wins both ways: respawn with `INBRISK_ALLOW_SCRIPT_EXECUTION=0`
      AND `"AllowScriptExecution":true` in settings.json → still denied.
- [ ] Same gate on the adapter surface:
      `computer_adapter{"adapter":"chrome_devtools","action":"evaluate","args":{"expression":"1+1"}}`
      → `ConfirmationDenied` while consent is off.

## 5. Script-launch gate

Code: `LaunchPathGate` (`src/Inbrisk.Platform.Windows/Apps/LaunchPathGate.cs`),
invoked from `AppService.ResolveDirect` (`AppService.cs:483-494` path lane,
`:535-546` executable lane) — before any existence probe. Audit:
`launch-gate.jsonl`. Consent: `INBRISK_ALLOW_SCRIPT_LAUNCH` env
(1/true/yes/on) or `"allowScriptLaunch":true` in settings.json.

- [ ] Fixture: `echo exit /b 0 > %TEMP%\inbrisk-verify\test.bat`
- [ ] Denied by default:
      ```json
      {"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"computer_launch","arguments":{"path":"%TEMP%\\inbrisk-verify\\test.bat","waitFor":"none"}}}
      ```
      **PASS** = `isError:true`; the embedded detail JSON contains
      `"error":"ConfirmationRequired"` and text `script/installer — set
      INBRISK_ALLOW_SCRIPT_LAUNCH=1 or "allowScriptLaunch": true`.
      (Outer `error` may read `Failed` — the inner `error` field is the
      gate verdict.)
- [ ] Same denial via the executable lane:
      `computer_launch{"executable":"test.bat","waitFor":"none"}` →
      ConfirmationRequired (gate runs before File.Exists — a nonexistent
      `.bat` still classifies).
- [ ] Audit: last `launch-gate.jsonl` line has
      `category:"launch_path_gate"`, `verdict:"ScriptRequiresConsent"`,
      `extension:".bat"`, `prev`+`h` chained.
- [ ] `.exe` unaffected: `computer_launch{"executable":"notepad.exe",
      "waitFor":"window"}` → `status:"Verified"`. (Clean up afterwards.)
- [ ] Consent: respawn with `INBRISK_ALLOW_SCRIPT_LAUNCH=1` → the `.bat`
      call is no longer gate-blocked (success or a downstream error like
      TargetNotFound, never ConfirmationRequired). Audit line reads
      `verdict:"AllowedScriptByConsent"`, `via:"env:INBRISK_ALLOW_SCRIPT_LAUNCH"`.
- [ ] Hard denial never consented away — even with the env set:
      `computer_launch{"path":"\\\\server\\share\\x.bat"}` →
      `PolicyDenied` (UNC), `verdict:"Denied"` in audit.
- [ ] `app:"cmd"`/`executable:"powershell.exe"` → `PolicyDenied`
      (ShellHosts blocklist, `AppService.cs:32-41`) regardless of env.

## 6. `save_as_recipe` → `computer_run_recipe`

Code: `saveAsRecipe`/`save_as_recipe` params on `computer_run`
(`InbriskTools.cs` ~L3046-3066; also `computer_do`/`computer_batch`),
`AttachSavedRecipe`/`SaveStepsAsRecipe` (~L3896-3993), store =
`TaskRecipeStore` → `%INBRISK_DATA_DIR%\task-recipes\<sanitized>.json`
(`src/Inbrisk.Runtime/TaskRecipeStore.cs:21-28,84-94`).

- [ ] Run:
      ```json
      {"jsonrpc":"2.0","id":41,"method":"tools/call","params":{"name":"computer_run","arguments":{
        "save_as_recipe":"t1",
        "steps":[
          {"action":"launch","app":"notepad","waitFor":"window","timeout":15000},
          {"action":"find","as":"doc","target":{"process":"notepad","role":"document"}},
          {"action":"set_value","elementId":"$doc","text":"wave2-probe"}
        ]}}}
      ```
- [ ] **PASS** = `status:"Completed"` (or `success:true`) AND result body
      contains `recipe:{name:"t1", path, steps:3, parameterized:…}`;
      `recipeWarning` absent.
- [ ] File exists at `recipe.path` ==
      `%INBRISK_DATA_DIR%\task-recipes\t1.json`
      (default `%LOCALAPPDATA%\inbrisk\task-recipes\t1.json`).
- [ ] In `t1.json`: `StepsJson` contains `{{text}}` (or another `{{…}}`
      placeholder) in place of `"wave2-probe"`, and `Parameters` lists a
      `text` entry. If `parameterized:false` and the literal remains, the
      `RecipeParameterizer` component (reflection seam,
      `TryParameterizeSteps` ~L4001-4077) hasn't merged — record FAIL/PENDING.
- [ ] Replay:
      ```json
      {"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"computer_run_recipe","arguments":{"name":"t1","parameters":{"text":"replayed-x"}}}}
      ```
      **PASS** = Completed; the notepad document contains `replayed-x`
      (verify via `computer_find{process:"notepad",role:"document"}` or
      `computer_read`). Note the argument is `parameters`, not `params`.
- [ ] `computer_list_recipes{}` shows `t1` with `Params: [text]` and an
      updated success count (`RecordRun`).
- [ ] Teardown: `computer_cleanup{}` reaps the recipe-spawned notepads.

## 7. Blender `auto_install` + bridge arg construction

Code: `BlenderAdapter.AutoInstall` (`src/Inbrisk.Runtime/Adapters/
BlenderAdapter.cs:231-329`) → writes `inbrisk_bridge.py` into the first
`scripts\startup` candidate: `%APPDATA%\Blender Foundation\Blender\<ver>\
scripts\startup` (preferred), else Program Files install dirs, else beside
`blender.exe` from App Paths. `ApplyAdapterBridge` (`InbriskTools.cs`
~L6764-6832) appends `--python <BootstrapPath>` where
`BootstrapPath = %LOCALAPPDATA%\Inbrisk\adapters\blender_bridge.py`.

- [ ] Call:
      ```json
      {"jsonrpc":"2.0","id":51,"method":"tools/call","params":{"name":"computer_adapter","arguments":{"adapter":"blender","action":"auto_install"}}}
      ```
      **PASS** = `success:true`, `data.installedTo` ends with
      `scripts\startup\inbrisk_bridge.py`, `data.status` is `installed` or
      `already-installed`, `data.port` == 9877.
      If no Blender is installed → `NotFound` with guidance is an
      acceptable SKIP (note it).
- [ ] On disk: the file exists at `data.installedTo` and contains the
      bridge markers (`bpy.app.timers`, `inbrisk bridge listening`,
      `INBRISK_BLENDER_PORT`). Re-run → `status:"already-installed"`
      (idempotent, content-identical).
- [ ] `--python` arg construction — **code/log review only, DO NOT launch
      Blender**: in `ApplyAdapterBridge` confirm:
      - trigger: `adapter:"blender"` OR app/executable/path mentions
        "blender";
      - appended args are exactly `"--python"` + `BootstrapPath`;
      - skipped when `bridge:false`, `adapter:"none"`, a non-blender
        adapter, `aumid:`/`uri:` launches, or caller already passed a
        `--python`/`--python=` arg;
      - success path sets result field `adapterBridge:{adapter:"blender",
        script:<path>, port:9877}` (`computer_launch` result ~L1776).
      - a failed script materialization never blocks the launch.
- [ ] Same injection wired for `computer_run` launch steps via the step's
      `adapter` field (`StepToLaunchSpec`, ~L6753-6761) and `computer_do`'s
      `adapter`/`bridge` params (~L3097-3111).
- [ ] Bootstrap alone (no Blender needed) also materializes the script:
      `computer_adapter{"adapter":"blender","action":"bootstrap"}` →
      `data.path` == `%LOCALAPPDATA%\Inbrisk\adapters\blender_bridge.py`
      exists on disk.

---

## Sign-off

| # | Item | Result |
|---|------|--------|
| 1 | computer_cleanup reaps only tracked pids | ☐ |
| 2 | session-end reap on Dispose | ☐ |
| 3 | PID-reuse guard (code review) | ☐ |
| 4 | browser_evaluate gate + audit | ☐ |
| 5 | script-launch gate + audit | ☐ |
| 6 | save_as_recipe → run_recipe roundtrip | ☐ |
| 7 | Blender auto_install + --python wiring | ☐ |
