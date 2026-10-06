# Wave-3 Verification Checklist — Reflex Engine (modal auto-dismiss mid-run)

Runnable pass/fail verification for the reflex wave:
`ModalInterruptInterceptor` (`src/Inbrisk.Platform.Windows/Events/`),
`ReflexPolicy` + `ReflexEngine` (`src/Inbrisk.Runtime/`), and the
`computer_run` / `computer_batch` / `computer_do` integration in
`InbriskTools.cs` (`enableReflex`, `autoDismissModals`, `reflex:{modals…}`
result telemetry, `reflex.*` perf events).

Documented contract (skills/inbrisk/references/plans.md §"Reflex Engine",
SKILL.md §142-157): `enableReflex` default **true**;
`autoDismissModals` = `"off"` | `"save"` | `"discard"` | `"closeOnly"`
(default `"closeOnly"` — save prompts cancelled, never committed);
dangerous + unknown modals → `InterruptedByDialog` with
`reflex:{modals:[…], aborted:true}`; dismissed buttons are clicked via
background WM messages — never coordinate/SendInput blind clicks.

**Landing status when written:** all reflex files on disk and the full
solution builds clean — `ModalInterruptInterceptor`
(`src/Inbrisk.Platform.Windows/Events/`), `ReflexPolicy` + `ReflexEngine`
(`src/Inbrisk.Runtime/`), Core contracts (`src/Inbrisk.Core/Services.cs`),
the MCP seam with the real `RuntimeReflexEngine` adapter + inert
`NullReflexEngine` fallback (`src/Inbrisk.Mcp/ReflexSeam.cs`), the
`enableReflex`/`autoDismissModals` params + pause-gate + `reflex{}`
report in `InbriskTools.cs` (`RunPlanCore` ~:5444+, gate ~:5686-5737,
`ReflexAbortResult` ~:7804), `McpSession.Reflex` create/dispose
(`McpSession.cs:263`, `:644`). Note the wave landed from parallel
streams: a mid-landing compile break (duplicate contract types) was
reconciled in-place — re-verify §0.1 before trusting e2e results.

## 0. Setup

- Server (pick one):
  - Installed: `"%LOCALAPPDATA%\Programs\Inbrisk\inbrisk.exe" mcp`
  - Build tree: `dotnet src\Inbrisk.Cli\bin\Release\net8.0-windows10.0.19041.0\inbrisk.dll mcp`
- Protocol: newline-delimited JSON-RPC 2.0 over stdio. Handshake:
  `initialize` (`protocolVersion:"2025-03-26"`) → `notifications/initialized`
  → `tools/call` with `{"name":<tool>,"arguments":{…}}`.
  Harness refs: `tools/mcp_probe.py`, `tools/mcp_stdio_smoke.mjs`.
- **Isolation env on the server process (required):**
  - `INBRISK_TOOL_PROFILE=full` — reflex params land on
    computer_run/batch/do; full profile keeps every tool available.
  - `INBRISK_DATA_DIR=%TEMP%\inbrisk-verify` — redirects settings,
    recipes, perf jsonl (`find-perf.jsonl`, `perf-trace.jsonl`).
  - `INBRISK_AUDIT_DIR=%TEMP%\inbrisk-verify\audit` — audit mirror.
  - `INBRISK_PANIC_HOTKEY=Ctrl+Alt+F12` +
    `INBRISK_RESUME_HOTKEY=Ctrl+Alt+Shift+F12` — a panic chord that
    collides with nothing real, so §7 can trigger EmergencyStopped
    deliberately without touching the production chord.
- Preconditions: no emergency stop latched
  (`INBRISK_EMERGENCY_STATE` unset/absent marker), notepad closed.
- Result convention: `result.isError` + `result.content[0].text`
  carrying the JSON body.

### 0.1 Wiring gate — confirm the seam really drives the engine

- [ ] `ReflexEngines.CreateDefault` (`ReflexSeam.cs:259-263`) returns
      `RuntimeReflexEngine` (`:119-251`), not `NullReflexEngine` — the
      fallback is reserved for construction faults. If a run result ever
      shows `reflex{enabled:true, modals:[]}` after a modal that SHOULD
      have been intercepted, check whether the silent fallback was hit.
- [ ] `RuntimeReflexEngine` constructs with the real `parts.Windows`
      (`:184` — the interceptor's `IsWindowProtected` veto needs it),
      a real `SilentInputService` (`:189`), `DelegateReflexPolicy`
      over `ReflexPolicy.Decide`/`IsDangerous` (`:190`), and
      `parts.EventBuffer` for dismiss short-circuit (`:192`).
- [ ] The `InterruptSourceAdapter` (`:124-145`) maps the interceptor's
      Platform `ModalInterrupt` → the Core contract record preserving
      `At`/`Detection`, and `Arm`/`Disarm` drive BOTH
      `_interceptor.Start/Stop` and `_engine.Arm/Disarm` (`:213-225`) —
      an armed engine over an unarmed interceptor silently sees nothing.
- [ ] Contract dedup: `Inbrisk.Core/Services.cs:238+` and
      `ReflexPolicy.cs:6-23` still define SAME-NAMED but DIFFERENT types
      (`ModalDisposition`, `ReflexAction`, `AutoDismissMode`,
      `ModalInfo`, `ModalButton`, `ReflexDecision`, `ReflexAbort`,
      `ReflexInterception`, `IReflexPolicy`). Inside
      `namespace Inbrisk.Runtime` the local ones shadow Core's — a
      latent hazard that already produced a compile break mid-landing.
      PASS = one canonical set (delete/reconcile the unused half); the
      MCP surface uses its own `ReflexDismissMode` (`ReflexSeam.cs:13-24`)
      deliberately — fine.
- [ ] `dotnet build inbrisk.sln` clean (0 errors).

### 0.2 Modal helper (run from the OPERATOR's shell, never via MCP)

`powershell.exe`/`mshta`/`python` are launch-gated/dangerous inside MCP —
the helper is a harness-side tool. It pops a real Win32 `MessageBox`
(`#32770`, real Button-class children, `BM_CLICK`-able) **owned by the
run's target hwnd**, which is exactly what the interceptor's owner-chain
detection (`ModalInterruptInterceptor.cs:161-163`) keys on.

Save as `%TEMP%\inbrisk-verify\pop_modal.ps1`:

```powershell
param([long]$OwnerHwnd, [string]$Text, [string]$Title, [int]$Buttons = 1, [int]$DelayMs = 1200)
Add-Type -AssemblyName System.Windows.Forms
Add-Type '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr FindWindowEx(System.IntPtr p1, System.IntPtr p2, string c, string w);' -Name U32 -Namespace N
Start-Sleep -Milliseconds $DelayMs
# Native MessageBox so we can set an arbitrary owner hwnd cross-process:
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class MB { [DllImport("user32.dll", CharSet=CharSet.Unicode)]
  public static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type); }
'@
[MB]::MessageBoxW([IntPtr]$OwnerHwnd, $Text, $Title, [uint32]($Buttons + 0x40))  # 0x40 = MB_ICONINFORMATION
```

Buttons: `0`=OK, `1`=OKCancel, `3`=YesNoCancel, `4`=YesNo.
Blocks until dismissed — the modal outlives the reflex decision, so a
leaked/late process means the modal was NOT clicked (see §3, §4).
Launcher: `Start-Process powershell -ArgumentList '-File',"$env:TEMP\inbrisk-verify\pop_modal.ps1",'-OwnerHwnd',<HWND>,'-Text','…','-Title','…','-Buttons',<n>` — **delayed** so the
modal lands mid-run, never before arming.

---

## 1. Happy path — reflex dismisses a real modal mid-run, run completes

Target: notepad. The run types into it; mid-run the helper pops an
owned "update" modal which policy maps to a **Cancel** defer-click
(`ReflexPolicy.cs:106-124`, `DeferButtonExact` contains "Cancel").

- [ ] Launch the run's target and record its hwnd `H`:
      ```json
      {"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"computer_launch","arguments":{"executable":"notepad.exe","waitFor":"window","timeoutMs":15000,"newInstance":true}}}
      ```
      Capture `window.hwnd` (hex). Convert to decimal for `-OwnerHwnd`.
- [ ] In the operator shell, START the delayed modal (do not wait):
      `Start-Process powershell -ArgumentList '-File',"$env:TEMP\inbrisk-verify\pop_modal.ps1",'-OwnerHwnd',<H-dec>,'-Text','An update is available for Notepad. Restart to update.','-Title','Update available','-Buttons',1,'-DelayMs',800`
      (`"update available"` + `"restart to update"` hit
      `UpdateTextContains`, `ReflexPolicy.cs:106-111`.)
- [ ] Immediately issue the batch (modal lands during the `wait`):
      ```json
      {"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"computer_batch","arguments":{
        "enableReflex":true,
        "steps":[
          {"do":"type","t":{"process":"notepad","role":"document"},"v":"wave3-reflex "},
          {"do":"wait","ms":3500},
          {"do":"type","t":{"process":"notepad","role":"document"},"v":"after-modal"}
        ]}}}
      ```
      (Same coverage applies verbatim to `computer_run` with the
      equivalent `steps` array — run at least one of the two.)
- [ ] **PASS** when all hold:
  - `success:true` / run completes (`status:"Completed"`), both `type`
    steps `ok:true` — the run resumed after dismissal.
  - Result body carries `reflex.modals` — a non-empty array with an
    entry whose hwnd is the helper's modal; its `action` records a
    dismiss/click (e.g. `"clickedButton":"Cancel"` / `"action":"dismiss"`
    — accept any field naming that proves the **decision and the target
    button** are recorded); `reflex.aborted:false`.
  - The helper powershell exited on its own within ~5 s of the modal
    appearing (BM_CLICK on Cancel answered the MessageBox — a still-
    running helper with a live dialog = the reflex did NOT click).
  - Notepad document ends with `wave3-reflex after-modal`
    (`computer_find{process:"notepad",role:"document"}` → value).
- [ ] FAIL if: run pauses `UnexpectedModalOpened`, hangs past the batch
      timeout, or the modal was dismissed by focus/SendInput (helper
      window flashed to foreground — check `computer_windows` z-order /
      event stream for a FocusWindow on the modal hwnd).

## 2. Save-confirm disposition honours `autoDismissModals`

Mid-run owned modal with `"Do you want to save your changes?"`,
YesNoCancel — classified `SaveConfirm` via `SaveTextContains`
(`ReflexPolicy.cs:127-133`).

- [ ] `autoDismissModals:"closeOnly"` (and a second run with the param
      omitted — the documented default must equal closeOnly):
      helper `-Text 'Do you want to save your changes to the document?'`
      `-Title 'Notepad' -Buttons 3`.
      **PASS** = modal dismissed by **Cancel** (helper exits; run
      completes; `reflex.modals[i].button/action` names Cancel/İptal —
      `ReflexPolicy.cs:313-317`). **FAIL** if Yes or No was clicked.
- [ ] `autoDismissModals:"save"` on the same shape: YesNoCancel has no
      `SaveButtonExact` match → `ClickOrFail` degrades to FailPlan
      (`ReflexPolicy.cs:238-245,307-309`) → run aborts
      `InterruptedByDialog`. **PASS** = abort, modal left open (kill the
      helper afterwards). This doubles as proof that no-match never
      blind-clicks.
- [ ] `autoDismissModals:"discard"` on an MB_YESNOCANCEL whose buttons
      are literally `Save`/`Don't Save`/`Cancel` (C# helper variant or
      TestApp dialog): PASS = "Don't Save" clicked. If only a Yes/No
      shape is available → degrade = FailPlan, same as `save`.
      Record which shape was exercised.

## 3. Unknown modal → abort `InterruptedByDialog`, zero clicks

- [ ] Owned modal that matches NO table:
      `-Text 'Completely foreign prompt body' -Title 'Weird prompt' -Buttons 4`
      (Yes/No → `GenericConfirm` → FailPlan, `ReflexPolicy.cs:341-356`).
      Re-run §1's batch with `enableReflex:true`.
- [ ] **PASS** when all hold:
  - Result `isError:true` (or `status:"Paused"`/aborted) with
    `error:"InterruptedByDialog"` and the modal's title/buttons in the
    detail (contract: `reflex:{modals:[…], aborted:true}`).
  - The helper process is STILL ALIVE with the dialog open the moment
    the tool call returns — i.e., the reflex recorded the interception
    but sent nothing. Then `Stop-Process` the helper to clean up.
  - No `reflex.modals[*]` entry records a click/dismiss for this hwnd.

## 4. `enableReflex:false` — regression, modal blocks as before

- [ ] Same helper modal as §1 (update shape), batch with
      `"enableReflex":false`.
- [ ] **PASS** = pre-reflex behaviour: run pauses/fails with
      `pauseStatus:"UnexpectedModalOpened"` (or InterruptedByDialog via
      `stopOnDialog` waits) — the modal is reported, NOT dismissed
      (helper still alive); `reflex` block absent or empty.
      Reference: `InbriskTools.cs:6012-6036` (step.modalCheck → Paused).
- [ ] Repeat with `"autoDismissModals":"off"` → same blocked outcome.
- [ ] Cleanup: `Stop-Process` the helper.

## 5. UAC / credential / security surfaces — never clicked (code review)

Do NOT trigger a real UAC prompt. Static assertions only:

- [ ] `ReflexPolicy.IsDangerous` (`ReflexPolicy.cs:257-268`) runs FIRST
      — both inside `Decide` (`:286-289`) and ahead of it in the engine
      (`ReflexEngine.cs:326-331`, a throwing policy = dangerous →
      FailPlan) — before update/save/error/cookie/generic classification.
- [ ] Coverage present: class prefix `"$$Secure UAP"` (`:81-82`, UAC
      secure-desktop classes), class contains `"Credential Dialog XAML
      Host"` + `"ConsolidatedView"` (`:84-86`), and the text table
      (`:88-103`: User Account Control / Kullanıcı Hesabı Denetimi,
      Windows Security / Güvenliği, credentials / kimlik bilgisi,
      SmartScreen, BitLocker, certificate/sertifika, consent.exe).
- [ ] Security dialogs are still REPORTED to the policy — not filtered
      upstream: `SystemDialogProcs` includes consent.exe,
      useraccountcontrol.exe, credentialuibroker.exe, smartscreen.exe
      (`ModalInterruptInterceptor.cs:45-50`). Assert these reach
      `Decide` → FailPlan rather than being dropped (a dropped security
      modal would leave the run blocked silently instead of aborting
      loudly).
- [ ] Delivery path never falls back to coordinates: the engine's only
      actuation is `_silent.TryClick(ElementHwnd: button hwnd)`
      (BM_CLICK, `ReflexEngine.cs:602-603`) and `_silent.TryClose`
      (WM_CLOSE, `:614`), gated by an `IsChild(modal, button)`
      containment re-check (`:595-601`). Grep the engine for
      `SendInput`/`mouse_event`/`SetCursorPos`: **zero hits = PASS**.
- [ ] **KNOWN GAP to close or accept:** the engine does NOT re-run
      `IsWindowProtected(modal.Hwnd)` / `IsAgentHostOrAncestorPid`
      inside `Handle`/`TryAct` — protection rests entirely on the
      interceptor's pre-raise veto (`ModalInterruptInterceptor.cs:143,175`)
      plus the `Reverify` ownership scope (`ReflexEngine.cs:430-442`).
      Recommend a defense-in-depth recheck before `TryAct` (a modal
      owned by the armed window but belonging to a protected/critical
      process — e.g. a "critical session" browser window — passes the
      ours-check today). PASS = recheck present, or risk documented.
- [ ] Same gap on elevation: the engine bypasses `Executor`/
      `SafetyPolicy` entirely (it calls `ISilentInputService` directly),
      so `window.IsElevated → Deny` (`SafetyPolicy.cs:117`) and the
      disabled-element/element-liveness gates never run on reflex clicks.
      `IsDangerous` text/class tables are the only screen. If the modal
      is elevated (consent.exe surface reaching the default desktop),
      the run should FailPlan — add `w.IsElevated` to the dangerous
      check or accept the residual explicitly.
- [ ] Every decided disposition lacking a concrete matched button
      degrades to FailPlan (`ClickOrFail`, `ReflexPolicy.cs:238-245`);
      `Unknown` and `GenericConfirm` return FailPlan unconditionally
      (`:352`,`:359`); a `null`/throwing decision → FailPlan
      (`ReflexEngine.cs:336-349`).

## 6. Protected/host windows & emergency stop — code review

- [ ] Interceptor vetoes before raising: `pid == SelfPid` dropped
      (`ModalInterruptInterceptor.cs:142`), `IsAgentHostOrAncestorPid`
      veto (`:143`), `IsWindowProtected` veto (`:175`). PASS = all three
      present and consulted for every detection branch.
- [ ] EmergencyStopped during reflex handling: the run-side gate is
      panic-safe — the reflex check runs AFTER the per-step cancellation
      check (`InbriskTools.cs` step loop, `linked.Token` → epoch), the
      abort path re-checks `Control.State == EmergencyStopped`/
      `epoch.IsCancellationRequested` before honouring `Abort` (panic
      outranks a modal abort), the pause wait rides `linked.Token` with
      a 30 s `gateCts` bound, and the post-run abort check is
      EmergencyStopped-first. **Residual to accept or fix:** the engine
      worker itself takes no cancellation input — a BM_CLICK already
      inside `TryAct` when panic latches still lands (one message; the
      worker exits on `_shutdown` only). PASS = documented residual, or
      the engine consults the epoch/`EmergencyGate.IsStopped`
      (`EmergencyGate.cs:41`) inside `Handle` before actuating.
- [ ] Bounded wait: per-modal work is hard-capped — child scan ≤300 ms
      / ≤100 children / ≤50 ms per WM_GETTEXT (`ReflexEngine.cs:80-87`),
      dismiss watch ≤5 s (`DismissWatchMs`, `:90`,`:638-650`), and the
      run-side pause gate times out into a synthetic `InterruptedByDialog`
      abort after ~30 s. Every cross-window send is `SendMessageTimeoutW`
      (engine `Native.TextOf` + `SilentInput.cs:32,639`). A wedged modal
      must produce `reflex.abort`, never a stalled run.
- [ ] Serialization: single worker thread drains a
      `BlockingCollection` (`ReflexEngine.cs:215-244`); `_inflight` +
      2 s `_recent` dedup (`:245-249`); a latched `Abort` drops later
      interrupts (`:243`,`:291`,`:353`); pause depth is refcounted
      (`BeginPause`/`EndPause` CAS loop `:682+`).
- [ ] **KNOWN GAP — abort can be cleared by re-arm:** `Arm` unconditionally
      nulls `_abort` (`ReflexEngine.cs:200`) and `RunPlanCore` re-arms on
      a step-target-window change BEFORE the gate reads `Abort`
      (`EnsureReflexArmed(stepTargetHwnd)` at `InbriskTools.cs:5608`,
      gate at ~:5685). A modal that FailPlan'd between steps is silently
      cleared when the next step targets a different window — the run
      proceeds over an unhandled modal (the per-step `modalCheck`
      backstop at ~:6240 may still catch it as UnexpectedModalOpened,
      but the structured InterruptedByDialog abort is lost). PASS =
      reorder (gate before arm) or preserve a latched abort on same-leg
      re-arm.
- [ ] Session-end: `McpSession.Dispose` disposes `Reflex`
      (`McpSession.cs:641-646`); engine `Dispose` unsubscribes the source
      event, completes the queue, joins the worker ≤2 s, releases the
      pause gate (`ReflexEngine.cs:760-775`-ish `Dispose`); the
      interceptor adds NO `SetWinEventHook` (it rides the shared
      `IEventSource`, `ModalInterruptInterceptor.cs:96-100`) and
      unsubscribes on `Dispose` (`:240-244`). Grep `SetWinEventHook` —
      only `WinEventService.cs:55` may match.

## 7. Emergency stop mid-reflex (manual / optional)

- [ ] Spawn a §1-style modal, then press `Ctrl+Alt+F12` (the
      INBRISK_PANIC_HOTKEY) while the run is mid-wait.
- [ ] **PASS** = run returns `EmergencyStopped` (with the resume chord
      in detail); any reflex decision still in flight is cancelled —
      the modal is either already clicked or left open, but no reflex
      click lands AFTER the latch. `computer_observe{}` reports
      `controlState: EmergencyStopped`.
- [ ] Resume with `Ctrl+Alt+Shift+F12` before continuing.

## 8. Timing — intercept→dismiss latency in perf events

Perf sinks: `%INBRISK_DATA_DIR%\perf-trace.jsonl` (`PerfLog`,
`PerfTrace.cs:149-155`) and `%INBRISK_DATA_DIR%\find-perf.jsonl`
(`UiaPerf.Write`, `UiaPerf.cs:20`).

- [ ] After §1, `perf-trace.jsonl` contains `kind:"reflex.arm"`,
      `reflex.intercept` (with hwnd, title, button count, detection,
      `ms`), and `reflex.dismiss` (disposition, action, method =
      `"bm_click"`/`"wm_close"`, `ms` = inspect→dismissed latency);
      `find-perf.jsonl` contains `kind:"run.reflexAbort"` on §3/§4 abort
      paths. Emission sites: `ReflexEngine.cs` `reflex.arm`/`disarm`/
      `intercept`/`dismiss`/`abort`/`error` + `InbriskTools.cs`
      `run.reflexAbort`.
- [ ] **PASS** = at least one event per interception carrying the modal
      hwnd + elapsed `ms`; measured intercept→dismiss latency is sane
      for the event-driven path (target < 1500 ms for a `#32770` +
      BM_CLICK round trip; a value dominated by the helper's fixed
      `DelayMs` is fine — subtract it).
- [ ] FAIL if dismissals are invisible to telemetry — a reflex click
      must be as observable as an explicit action (`silent.action`
      events already exist at `Executor.cs:705-727`; reuse is fine).

## 9. Race — two modals / modal replacing modal

- [ ] Run §1's batch but spawn TWO delayed helpers: modal A at
      `DelayMs 800`, modal B at `DelayMs 2500` (different titles/texts,
      both dismissible update shape).
- [ ] **PASS** = both appear in `reflex.modals` in interception order,
      each dismissed exactly once, run completes. A second
      `InterruptedByDialog` on B is acceptable only if B was unknown —
      same shape must not flip outcome.
- [ ] Modal-replacing-modal: single helper script that loops
      `MessageBoxW` twice (second pops ~300 ms after the first is
      answered). PASS = two `reflex.modals` entries, no deadlock, run
      completes within the batch timeout.

---

## Sign-off

| # | Item | Result |
|---|------|--------|
| 0.1 | seam wired — real engine, interceptor adapter, silent svc, contract dedup, clean build | ☐ |
| 1 | update modal auto-dismissed mid-batch, run completes, reflex.modals | ☐ |
| 2 | autoDismissModals disposition (closeOnly default → Cancel; no-match → FailPlan) | ☐ |
| 3 | unknown modal → InterruptedByDialog, zero clicks | ☐ |
| 4 | enableReflex:false / "off" → modal blocks as before | ☐ |
| 5 | UAC/credential/security → FailPlan, never clicked (code review) | ☐ |
| 6 | protected/host veto, emergency checks, bounded wait, serialization, dispose | ☐ |
| 7 | panic mid-reflex → EmergencyStopped, no post-latch clicks | ☐ |
| 8 | reflex.* perf events with intercept→dismiss latency | ☐ |
| 9 | two modals / replacing modal serialize cleanly | ☐ |
