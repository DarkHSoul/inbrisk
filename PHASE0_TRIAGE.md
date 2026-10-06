# Phase 0 Triage — REVIEW.md findings vs C# canonical code

**Decision:** C#/.NET 8 (`src/Inbrisk.*`) is canonical. Rust is archived to `../inbrisk-rust` (frozen experiment).
**Method:** every F01–F40 and P1–P7 re-verified against `src/*.cs` by two audit passes. `n/a, archived` = Rust-only issue with no C# equivalent.
**Verdict key:** APPLIES = real in C# / PARTIAL = real but narrower or different mechanism / NOT-APPLICABLE / UNKNOWN.

## Summary counts

- **APPLIES to C#:** F03, F04, F31, F32, F37, F38 (6)
- **PARTIAL in C#:** F01, F05, F06, F09, F11, F12, F13, F14, F18, F20, F23, F25, F36, F39, F40, P1, P3, P5, P7 (19)
- **NOT-APPLICABLE (C# already handles or Rust-only):** F02, F07, F08, F10, F15, F16, F17, F19, F21, F22, F24, F26, F27, F28, F29, F30, F33, F34, F35, P2, P4, P6 (22)

## Table

| ID | Verdict | C# evidence (file:line) | Note |
|----|---------|--------------------------|------|
| F01 panic-abort | PARTIAL | `UiaEventSubscriptionManager.cs:477,531`; `SendInputService.cs:684`; zero `async void`/global handlers in `src/` | No panic-abort equivalent — tool errors become MCP error results. Residual: no global guard for exceptions escaping WinEvent/UIA native-callback threads (runtime test needed). |
| F02 unwrap panics | NOT-APPLICABLE | `InbriskTools.cs:720-728` | Nullable params + `Malformed` on bad input. |
| F03 untrusted text | **APPLIES** | `InbriskTools.cs:2335,5840-5873`; browser tools `3576-3659`; only redaction is telemetry-side `TaskTelemetryAggregator.cs:326` | UIA names/values/titles + CDP content reach the model verbatim, unmarked. |
| F04 no confirm gate | **APPLIES** | `McpSession.cs:233-237` `AutoConfirm:true` hardcoded; `SafetyPolicy.cs:37-43`; `Executor.cs:154-176` | CONFIRM classes (TypeText, SetValue, Hotkey, KeyPress, Clipboard R/W) auto-satisfied by the tool call itself. Only Deny blocks. |
| F05 shell channels | PARTIAL | `AppService.cs:29-41,369-388`; `browser_evaluate` `InbriskTools.cs:3576-3590` | No pc_exec analog. `computer_launch` still spawns arbitrary binaries; shell hosts blocked by filename only. CDP JS eval gated only by emergency token. Python file-drop bridge remains (`chat_cli.py`). |
| F06 UIA batching | PARTIAL | `UiaBackend.cs:76-80,123-140,259-263,718-840` cached; uncached: `:493-499` desktop FindAll, `:691-695` GetParent, `:1303-1320` labelledBy/pattern reads | Main find/inspect walks use CacheRequest; residual live-RPC paths remain. |
| F07 stale element cache | NOT-APPLICABLE | `ElementRegistry.cs:37-74`; `WinEventService.cs:72-79` InvalidateWindow on DESTROY; `InbriskTools.cs:7115-7122` | `uia_<pid>_<n>` ids, EnsureAlive + recipe re-resolve end to end. |
| F08 stuck modifiers | NOT-APPLICABLE | `InputTransaction.cs:7-45`; `SendInputService.cs:181-216,429-446,567,684-745` | RAII + watchdog + SweepAll + `computer_reset_input`. |
| F09 ambiguous resolution | PARTIAL | `InbriskTools.cs:934-971` ScoreProcessMatch, `:759-798,7195-7249` AmbiguousTarget; **but** `:6283-6315` focus picks first/foreground | Scored matching + ambiguity errors exist except `computer_focus_window`. |
| F10 UWP launch | NOT-APPLICABLE | `AppService.cs:1106-1140` IApplicationActivationManager+AUMID, `:1289-1299` bounded wait, `:1342-1351` ApplicationFrameHost matching | Full packaged-app path in C#. |
| F11 emergency coverage | PARTIAL | `Executor.cs:80` gate; **ungated**: `computer_close_window`/`app_restart`/`app_shutdown` (`InbriskTools.cs:502+,6508+,6555+`) | Panic blocks executor/launch/CDP/screenshot; close/shutdown tools bypass it — post-panic `Process.Kill` reachable. |
| F12 IPC auth | PARTIAL | `InbriskRuntimeDaemon.cs:90-121` pipe DACL user+admins+SYSTEM, default IL; `:295-332` version-only handshake | Any same-user process gets a session — no client auth, but no Low-IL hole (unlike Rust). |
| F13 close kills host | PARTIAL | `WindowService.cs:360-477` ancestor-PID + name protection; kill sites `InbriskTools.cs:581,687,881,6627` behind `IsWindowProtected`; `force:true` bypasses (`:554`) | Ancestry IS checked — but force bypasses provenance, renamed hosts evade name list, `app_shutdown(pid)` only checks protection via window list (`:6614`), and explicit-hwnd close has **no `CanAgentClose` gate** (`:822-895`). **Demonstrated live: bench agent closed the Devin host during Phase 0.** |
| F14 head-of-line | PARTIAL | `InbriskRuntimeDaemon.cs:266,284-353`; `InbriskTools.cs:7822-7845` per-pid barriers | Concurrent sessions OK; a hung UIA provider stalls that pid's lane (timeout-bounded, not eliminated). |
| F15 oversize reply | NOT-APPLICABLE | `Agent.cs:335-350` ObservationBudget; `InbriskTools.cs:237,292,6696`; `ResponseDietFormatter.cs` | Budgeted; oversize → error not hang. |
| F16 per-call handshake | NOT-APPLICABLE | `ThinStdioProxy.cs:29-30`; `InbriskRuntimeDaemon.cs:284-353` | One persistent pipe per client; handshake once. |
| F17 screenshot/DPI | NOT-APPLICABLE | `app.manifest:5-8` PerMonitorV2; `GdiCapture.cs:20-54`; `McpSession.cs:405-439` frameId anchoring; `ActionResolver.cs:296-334` StaleFrame | Anchored coords + stale-frame rejection exist. Residual: no client `maxWidth` on `computer_screenshot` (`InbriskTools.cs:6692`). |
| F18 deletable kill switch | PARTIAL | `EmergencyGate.cs:17-27,66-69` marker file; `EmergencyControl.cs:354-387` local-only resume; `:167,204,317` watcher auto-sync | No MCP resume tool (good). Flag is a plain deletable file — any same-user process can delete it; watcher may auto-resume (HYPOTHESIS on auto-clear branch). |
| F19 IsPassword | NOT-APPLICABLE | `UiaBackend.cs:1288,1312,1317-1320`; `SafetyPolicy.cs:31-33` | Password Value withheld; SetValue/TypeText on password = Deny. |
| F20 protected list | PARTIAL | `WindowService.cs:360-477` ancestor PIDs live-checked; `Provenance.cs:372-388` unknown→deny; `InbriskTools.cs:554` force bypass | Late-opened host windows protected via ancestry. Renamed binaries evade name lists (HYPOTHESIS). |
| F21 within/ancestor | NOT-APPLICABLE | `InbriskTools.cs:7484-7553`; `UiaBackend.cs:449-462` | All four relations evaluated (scope, ancestry, LabeledBy, proximity). |
| F22 negative coords | NOT-APPLICABLE | `InbriskTools.cs:2588-2589,8288-8291`; `CoordinateMapper.cs:23-28` | Signed coords; virtual-desktop origin handled. |
| F23 lane timeout | PARTIAL | `UiaDispatcher.cs:59-67,133-144`; `SerialMutationLane.cs:294-296` (test-only) | Deadline-drop exists; residual: hung COM call still completes late on abandoned thread. |
| F24 close user windows | NOT-APPLICABLE | `InbriskTools.cs:519,859-894` | Close fully reachable in C# — actually lacks `CanAgentClose` on explicit-hwnd path (opposite bug). |
| F25 read-path side effects | PARTIAL | `GdiCapture.cs:62` throws on minimized instead of restore; `WindowService.cs:583-587` Alt-pulse under lease; no `Environment.SetEnvironmentVariable` in `src/` | Minor: Alt-pulse exists but leased; no env race. |
| F26 repeated EnumWindows | NOT-APPLICABLE | `RequestWindowSnapshot.cs:8-13`; `SessionSnapshotCache.cs:70-82`; `HwndMetadataCache.cs`; waits WinEvent-driven | Dedup'd per request + 500ms cache. Residual: launch polls `ListWindows()` (`AppService.cs:1079-1315`). |
| F27 stubs | NOT-APPLICABLE | grep `TODO|FIXME|stub` over `src/` | None in hot paths. |
| F28 dual window views | NOT-APPLICABLE | `ThinStdioProxy.cs:8-13,96-100` | Proxy never enumerates; one runtime per session. |
| F29 feature gaps | NOT-APPLICABLE | — | C# is the superset; Rust-only extras (ConPTY, FFI) aren't shipping surface. |
| F30 FFI surface | NOT-APPLICABLE | zero `UnmanagedCallersOnly`/`DllExport` in `src/` | Managed exe; no export surface. |
| F31 signing/update | **APPLIES** | `installer/inbrisk.iss` — no SignTool; `SelfInstaller.cs:290-356` sha256 inside same manifest | Feed URL = sole trust root; no pubkey signature. |
| F32 audit log | **APPLIES** | `McpSession.cs:241`; `JsonlTelemetrySink.cs:13-50`; `PerfTrace.cs:155` | JSONL in `%LOCALAPPDATA%\inbrisk\` — same-user writable, 5MB rotate, no integrity chain. Actor attribution via RunId/StepId exists. |
| F33 focus-before-click | NOT-APPLICABLE | `Executor.cs:510-526` GuardFocus throws on failure | Residual: `FocusElement` attempt not enforced (`:502-508`); raw coordinate clicks unguarded (`:320-330`). |
| F34 honest verified | NOT-APPLICABLE | `Executor.cs:437-438`; `ActionResolver.cs:351-366`; `InbriskTools.cs:8121-8155` | `Unverified`/`ObservedChange` used honestly. |
| F35 window deltas | NOT-APPLICABLE | `InbriskTools.cs:7884-7916` DetectNewWindow+GetModalPopup in result; `:5788-5855` delta engine; `Agent.cs:223` InterruptedByDialog | New/modal windows reported in action results. |
| F36 pid/hwnd reuse | PARTIAL | `HwndMetadataCache.cs:57-71,215-253` PID start-time verified; hwnd-reuse only `IsWindow` check | Recycled hwnd hits cache; `catch{}` swallow at `InbriskTools.cs:584,690`. |
| F37 hygiene | **APPLIES** | `agent_cli.py:17` GCP project id; `tools/*_transcript.txt`, `mcp_probe_requests.jsonl` committed | Confirmed. Also `run_command`/`shell=True` in agent_cli.py. |
| F38 license/notices | **APPLIES** | `LICENSE:1-31`; no `THIRD-PARTY-NOTICES*`/`NOTICE*` | Attribution gap for NuGet deps. |
| F39 doc drift | PARTIAL | `InbriskTools.cs:502-509` close-window description overstates policy vs `:822-895` no `CanAgentClose` | `JsonExtensionData` strict schema exists; tool-description vs behavior drift. |
| F40 minor robustness | PARTIAL | broad `catch {}` at `InbriskTools.cs:584,690,7365,7406,8246` | CLR handles COM lifetime; residual silent swallows. |
| P1 close kills host | PARTIAL | see F13 | Ancestor-PID protected, but force/windowless-pid/renamed-host edges remain — and a live incident occurred during Phase 0 benchmarking (IDE host closed by agent action). |
| P2 UWP timeout | NOT-APPLICABLE | `AppService.cs:1106-1351` | Full AUMID/AFH path. Corpus result pending re-run. |
| P3 ambiguous names | PARTIAL | `InbriskTools.cs:6283-6315` | Only `computer_focus_window` lacks ambiguity handling. |
| P4 invisible dialogs | NOT-APPLICABLE | `InbriskTools.cs:7884-7916,6081-6084` | Modal/popup detection + deltas in action results. |
| P5 disabled elements | PARTIAL | `InbriskTools.cs:7062-7069`; `Executor.cs:287-312` | Checked on most paths; `FocusElement` exempt; `TypeText`/`KeyPress`/`Hotkey` element intents bypass enabled check. |
| P6 screenshot/DPI | NOT-APPLICABLE | `Dpi.cs:11-16`; `McpSession.cs:405-439`; `GdiCapture.cs:20-54` | Anchored image-space coords make downscaling safe; no client maxWidth param. |
| P7 docs/benchmarks | PARTIAL | `PhaseFBenchmarkTests.cs` (fake services, not real desktop); `tools/bench_latency.py`; `tools/*.json`+`artifacts/` gitignored | Machinery exists; results were never committed — Phase 0 fixes this via `benchmarks/`. ARCHITECTURE.md "Rust rejected" is now accidentally correct. |

## New C#-specific issues surfaced during triage

1. **`computer_close_window` explicit-hwnd path has no `CanAgentClose` gate** — `InbriskTools.cs:822-895`: posts WM_CLOSE to any non-protected user window without force; description claims otherwise (F39 drift + F24 inverse).
2. **`app_shutdown(pid)` protection check gated on window presence** — `InbriskTools.cs:6614`: a windowless protected/ancestor pid can be killed.
3. **`computer_focus_window` silent ambiguity** — `InbriskTools.cs:6283-6315` picks foreground/first match where other tools return `AmbiguousTarget`.
4. **Ungated close/shutdown during emergency** — F11: `computer_close_window`/`app_shutdown`/`app_restart` have no `EmergencyGate.IsStopped` check.
5. **`TypeText`/`KeyPress`/`Hotkey` element intents skip the disabled check** — `Executor.cs:282-292` case list omits them.
6. **`SerialMutationLane` is dead production code** — instantiated only in tests; production uses `DesktopArbiter` + `UiaReadScheduler` (`InbriskTools.cs:7796-7845`).
7. **UiaDispatcher abandoned-thread hazard** — hung COM call completes late after caller timeout (`UiaDispatcher.cs:62-67`).
8. **hwnd-reuse cache gap** — `HwndMetadataCache.cs:57`: recycled hwnd passes validation (PID+start-time checked, hwnd create-time not).
9. **bench incident (unrecovered)** — during Phase 0 task-corpus execution, an agent action closed the Devin IDE host window, killing the bench session mid-run. Consistent with P1/F13 class; exact call not recoverable from artifacts. Treat as live proof that ancestry protection + close gating is the top Phase-1 item.
