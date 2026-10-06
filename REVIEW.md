# Inbrisk — Principal Systems Review

**Scope:** full read-only audit of `C:\Users\Ahmet\Documents\inbrisk` — C#/.NET 8 tree (`src/`, `tests/`, `installer/`), Rust workspace (`crates/*` ×13, `apps/*` ×5, `bindings/`), Python bridge/agent scripts, docs, tests, installer. ~35k LOC Rust, ~130k LOC C# (incl. generated), ~3.2k LOC Python.
**Method:** 8 parallel audit passes + direct verification. `cargo check --workspace --all-targets` = clean. `dotnet build inbrisk.sln` = 0 errors / 88 warnings. Rust tests enumerated: 281. C# xunit facts: ~909. No production code modified.
**Convention:** every claim cites `file:line`. `[V]` = verified in source. `[H]` = hypothesis. `[U]` = unmeasured / could not verify.

---

## 1. System map (one screen)

```text
                        ┌──────────────────────── LIVE SHIPPING PATH (C#) ───────────────────────┐
  MCP host (Claude/etc) │  inbrisk.exe mcp  = C# Inbrisk.Cli → Inbrisk.Mcp McpHost (stdio)       │
  ──tools/call─────────►│    54 [McpServerTool]s + resources/prompts + DynamicToolsetManager     │
                        │    → Inbrisk.Runtime (Executor/ActionResolver/SafetyPolicy/Verifier)   │
                        │    → Inbrisk.Platform.Windows (UIA COM, SendInput, WGC/GDI capture,    │
                        │      Windows.Media.Ocr, vision backends, CDP adapter, WinEvent+UIA     │
                        │      events, GlobalHotkey Ctrl+Alt+Pause, tray/HUD/perimeter)          │
                        │    daemon transport: named pipe inbrisk-runtime-<user>                 │
                        └────────────────────────────────────────────────────────────────────────┘

                        ┌──────────────────── NEW PATH (Rust, in-progress) ──────────────────────┐
  MCP host ──stdio─────► inbrisk-mcp.exe (thin proxy, 19 tools, GDI BitBlt+GDI+ PNG capture      │
                        │  inside the proxy process itself)                                      │
  inbrisk-cli.exe ─────►│                                                                        │
  inbrisk-ffi.dll ─────►│   shared-memory IPC  Local\Inbrisk.Runtime.<SID>  (SPSC rings + named  │
  inbrisk-bridge.exe ──►│   Win32 events; WaitOnAddress fns are stubs; ~25 MiB region; 8         │
  (Python inbrisk_      │   sessions; JSON framed)                                               │
   mcp_server.py)      │         ▲                                                              │
                        │         │ req_event / resp_event                                       │
                        │   inbrisk.exe (persistent runtime: IPC server, dispatch loop, Engine,  │
                        │   UiaService[3-thread read pool + 1 mutation lane], PolicyEngine,      │
                        │   ConPTY terminals, WinEvent monitor→waits only, 1s EnumWindows        │
                        │   watcher→event ring, tray/overlay UI, emergency flag)                 │
                        │         │ broker channel (2nd shm region + events)                     │
                        │   inbrisk-desktop.exe (physical-input broker: serial executor,         │
                        │   SendInput, SetCursorPos, foreground guard, stuck-modifier cleanup)   │
                        └────────────────────────────────────────────────────────────────────────┘

  Backends used: Win32 EnumWindows / UIA (unbatched, ~15-18 COM calls per element) /
  brokered SendInput / CDP 127.0.0.1 read-only (Rust) or full browser_* tools (C#) /
  ConPTY (Rust-only) / OCR+vision (C#-only) / GDI screenshot in-proxy (Rust) or WGC+GDI (C#).
```

**Key facts:** the C# build is what installer + `.continue` config actually ship (`tools/publish.ps1:21`, `installer/inbrisk.iss:46`, `.continue/mcpServers/inbrisk.yaml:6`); Rust is a parallel, smaller surface (19 tools vs 54) that adds shared-memory IPC, ConPTY terminals, and FFI bindings but lacks OCR, vision, recipes, hotkeys, and an installer. Both binaries are named `inbrisk.exe` — whichever installed last owns the MCP registration. `[V]`

---

## 2. Findings table

Severity: **C**ritical / **H**igh / **M**edium / **L**ow. Evidence is `file:line`.

| ID | Sev | Area | Evidence | Impact | Fix (in words) |
|---|---|---|---|---|---|
| F01 | C | Reliability | `Cargo.toml:85` `panic="abort"` vs `crates/inbrisk-runtime/src/dispatch.rs:95-96` `catch_unwind` | The panic boundary is dead code in release; any panic aborts `inbrisk.exe` — all sessions, terminals, broker | Build with `unwind`, and/or remove every panic site in request paths |
| F02 | C | Reliability | `crates/inbrisk-mcp/src/lib.rs:1667-1668,1757,1770,1791` — `opt_string(...)?.unwrap()` | Missing `session_id` arg in `pc_exec/pc_write/pc_read/pc_close` panics the proxy → MCP host sees dead server = "session ended" | Replace with argument-error returns (`required()`) |
| F03 | C | Security | zero `untrusted`/sanitize/marker logic in `crates/` or tool-result path; `inbrisk-mcp` serializes UIA names/values, titles, CDP text, terminal output verbatim; `src/Inbrisk.Mcp/McpHost.cs:202-217` | Screen content (web page, doc, chat) can instruct the model — prompt injection straight into actions | Mark all observed content untrusted + provenance; server-side confirm gate for mutating steps whose trigger came from tool output |
| F04 | C | Security | `src/Inbrisk.Mcp/McpSession.cs:233-237` `AutoConfirm:true`; Rust has no confirm class at all (`engine.rs:2208-2236` `step_human` opt-in only) | No human checkpoint exists before typing/clicks/shell/file ops; "confirmation" is delegated to the injectable model | Mandatory server-side confirm for classified dangerous actions; CONFIRM must not be client-satisfiable |
| F05 | C | Security | `crates/inbrisk-mcp` `pc_open/pc_exec/pc_write` → ConPTY shell (`terminal.rs:340-364`); `chat_cli.py:89-99` `subprocess.run(shell=True)` on `.chat_bridge` file drop; `agent_cli.py:92-99` | Any MCP client — or any process writing one JSON file — gets arbitrary command execution as the user | Gate shell tools behind human approval; delete the file-drop bridge; drop `run_command` from `agent_cli.py` |
| F06 | C | Performance | no `CacheRequest`/`FindAllBuildCache` anywhere in `crates/`; `apartment.rs:326-363,381-420` ~15-18 COM calls/element; `find_match_pairs` `apartment.rs:254-280` describes every descendant; selector actions re-scan (`apartment.rs:424,450,479,...`) | A 400-element observe ≈ ~6,800 cross-process UIA calls; a selector click ≈ two full tree scans before input — orders of magnitude slower than the C# path which used `BuildUpdatedCache` (`UiaBackend.cs:533-1241`) | Port the C# CacheRequest bundle; one `FindAllBuildCache` per query; cached accessors after |
| F07 | H | Correctness | `world.rs:50` `invalidate()` + `state.rs:87` `invalidate_all()` have zero call sites; `engine.rs:2242` staleness check can never fire | Element handles are immortal; coordinate actions use stale cached bounds; `computer_read` returns snapshots of destroyed elements | Wire invalidation to WinEvent destroy/show + fingerprint change; bound the cache (LRU) |
| F08 | H | Correctness | `input_raw.rs:351-373` drag `?` between button-down/up; `input_executor.rs:243-253` no cleanup on error; `engine.rs:596-601` | Mid-drag/chord SendInput failure leaves mouse button / modifiers physically held until emergency stop | Unconditional `cleanup_stuck_modifiers`/`release_mouse_buttons` on every broker error path |
| F09 | H | Correctness | `engine.rs:710-750` `window_for_selector` falls back to `ctx.default_window` then foreground; `find_window` `engine.rs:668-679` first-match `(foreground, hwnd)` sort, no ambiguity error | Element-level selector fields never constrain the window; focus/click/type silently lands on the wrong window | Require window constraint for window steps; return `AmbiguousTarget` on >1 match |
| F10 | H | Correctness | `launch.rs:217-338` ShellExecute → `pid:None`; `engine.rs:3005-3060` requires `image_matches` = ApplicationFrameHost.exe vs app token → never matches; `window.rs:297` `root_owner` unused | UWP/Store app launches always time out at 10 s ("no usable window"); proven in `tools/bird_camelotia_transcript.txt:1127` | Treat ApplicationFrameHost frames as candidates; resolve child/AUMID; auto-bypass image check for ShellExecute launches |
| F11 | H | Safety | no `RegisterHotKey` in `crates/`; emergency is tray-only (`lib.rs:231-258`); flag checked only for physical input + terminal (`engine.rs:574`, `lib.rs:150-168`) | The documented `Ctrl+Alt+Pause` kill switch doesn't exist in Rust; new UIA/close/launch plans still run during "emergency" | Register the panic hotkey in the runtime; gate `Dispatcher` on `is_emergency` for all mutating ops |
| F12 | H | Safety | `security.rs:9-14,165-174` — shm region DACL has deliberately **Low** integrity label so sandboxed clients can attach; Hello is self-reported name+pid, no auth | Any same-user process — incl. Low-IL compromised renderers — can drive input/screenshots/shell; sandbox-escape amplifier | Drop the Low label; authenticate clients (peer PID/token check); fail closed on DACL build failure |
| F13 | H | Safety | `InbriskTools.cs:576-584,686,880` force-close → `proc.Kill()`; protection list `WindowService.cs:381-385` omits windsurf/zed/renamed hosts | `computer_close_window(force)` can kill the AI host process → session ends (C# path; see §3-P1) | Ancestry-based protection (walk process tree to session root), not name lists; never `Kill` shared processes |
| F14 | H | Perf/correctness | `dispatch.rs:49-55` `is_long_running` excludes Observe/Find/Read; `lib.rs:434-441` runs them on the single loop thread; UIA read timeout 8 s | One heavy tree read blocks all short ops for all 8 sessions — head-of-line blocking | Move Observe/Find/Read to worker threads or bound them |
| F15 | H | Perf | `server.rs:305-315` + `layout.rs:39` 256 KiB response cap: oversized reply → publish fails → client blocks to 30 s timeout | A >256 KiB observe manifests as a hang, not an error | Publish an error reply on oversize; or chunk |
| F16 | H | Perf | `inbrisk-sdk/src/lib.rs:73-74` + `inbrisk-mcp/lib.rs:1108-1113`: `available()` does a full map/Hello/teardown handshake **per tool call** | Every MCP call pays a second connect round trip | Cache availability; probe via `runtime_pid` liveness |
| F17 | H | Snapshot pipeline | `capture.rs:150` `max_width` parsed but ignored — full-res PNG always; `inbrisk-mcp/main.rs` never sets DPI awareness; `capture.rs:187-193` `SW_RESTORE` inside a read | Huge base64 payloads every step (cloud vision cost); on >100% DPI screenshot coords diverge from input coords — the "game clicks land wrong" mechanism | Honor `max_width`; set `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2`; anchor coords to frame IDs like the C# `frameId`/`observationId` scheme (`InbriskTools.cs:6699-6721`) |
| F18 | H | Security | C# `EmergencyGate.cs:24-26,69` panic state = deletable file `emergency-stop.flag`; peers auto-resume when it vanishes (`EmergencyControl.cs:341-373`) | The agent (via F05 shell) can delete the flag and un-panic itself — kill switch self-defeatable | Keep stop state in a kernel object/agent-unwritable store; resume only via local UI gesture |
| F19 | H | Security | `UiaBackend.cs:1312` C# withholds password `Value`; zero `IsPassword` handling in `crates/` (`apartment.rs:365-379`) | Rust runtime reads credential-field values into LLM output and types into them undifferentiated | Port IsPassword redact + deny set_value/type |
| F20 | M | Correctness | `policy.rs:81-106` protected list lacks windowsterminal/conhost/cmd/powershell/pwsh/cursor/devin/claude/windsurf (regression vs `WindowService.cs:381-385`); `lib.rs:173` `ensure_protected` runs once at startup | Host/IDE windows opened later — or launched as `intent:ephemeral` — can be mutated or force-terminated | Port the full C# list + ancestor-PID protection; stamp new windows via `policy.classify` in the watcher |
| F21 | M | Correctness | `Selector.within`/`ancestor` parsed (`selector.rs:9-57`) + advertised in MCP schema (`lib.rs:248-249`) but never evaluated (`apartment.rs:817-875`) | Scoped selectors silently match wider than asked | Implement or reject loudly |
| F22 | M | Correctness | `inbrisk-mcp/lib.rs:847-858` `opt_u64` for x/y rejects negatives | Clicks on monitors left/above primary (negative virtual coords) always fail | Parse signed coordinates |
| F23 | M | Correctness | `service.rs:287-321` MutationLane timeout returns error while queued job still executes | A "timed out" Invoke fires later against changed state — delayed side-effect storm | Cancellable jobs / drop before reply timeout |
| F24 | M | Correctness | `state.rs:353` `mark_explicit_user_close` zero callers → `decide_close` only ever allows Agent+Ephemeral | `close` on any normal user window is unreachable — silent functional dead-end vs C# | Thread an explicit-close flag from the request |
| F25 | M | Correctness | `capture.rs:187-193` ShowWindow(SW_RESTORE)+50 ms sleep in a read path; `launch.rs:276-294` `std::env::set_var("__COMPAT_LAYER")` unsynchronized process-global; `window.rs:313-316` Alt pulses outside input lease | Side effects inside "read" ops; env race across launches; focus injection bypasses the input arbiter | Move restore out of capture; serialize compat-layer env or use ShellExecuteEx; route focus through broker lease |
| F26 | M | Perf | `engine.rs:934-957` launch discovery = full broker `EnumWindows` every 60 ms up to 10/25 s; `WorldFingerprint::capture` `engine.rs:333` = full enum before+after every physical op; 1 s watcher `lib.rs:325-374` | Hundreds of full desktop enumerations per task; each ≈12-14 Win32 calls per window | Shared short-TTL snapshot cache; event-driven launch detection via existing WinEvent stream |
| F27 | M | Perf | `wake.rs:82-94` `wait_on_u64`/`wake_*` are stubs (sleep 50 ms/no-op) while docs claim WaitOnAddress (`layout.rs:23-26`, `server.rs:507`) | Ring-full waits poll at 50 ms granularity; docs lie about mechanism | Implement real WaitOnAddress or fix docs and remove dead fns |
| F28 | M | Perf | `desktop_broker.rs:654-658` broker enum hardcodes `maximized/foreground/tool_window:false`; `engine.rs:625-666` broker enum vs `engine.rs:2954,333` local enum = two inconsistent window views | Foreground ordering wrong in broker mode; double enumeration everywhere | One window source of truth; extend `BrokerWindowInfo` flags |
| F29 | M | Features | Rust lacks: OCR, vision grounding, recipes, screen memory, app catalog/AUMID launch, clipboard ops, dynamic toolsets, `within`-aware relational queries, scan/for_each/collect loops, pause/resume tools, wait_for_change/stable, telemetry (all present in C# — see parity table §3.P0) | Rust is not yet a feature replacement; regression on migration | Port highest-value gaps first (hotkey, launch, OCR) |
| F30 | M | Security | `inbrisk-ffi/lib.rs:19-76` `inbrisk_run_json` executes arbitrary plans, no panic guard, no auth; `bindings/python/inbrisk.py:11-25` loads DLL via env path with no signature check | Any process loading the DLL runs plans; env-path DLL hijack vector | Pin DLL path, verify signature, add `catch_unwind` |
| F31 | M | Security | `installer/inbrisk.iss` no `SignTool`; `SelfInstaller.cs:285-328` update manifest trusts sha256 inside the same manifest; no `requestedExecutionLevel`/`uiAccess` manifest | Update = RCE gated by whoever controls the feed URL; unsigned SendInput binary = SmartScreen/EDR/AppLocker blocks | Authenticode-sign all binaries; verify manifest signature with embedded pubkey |
| F32 | M | Ops | logs = 2000-line memory ring + user-writable file (`logging.rs:48-125`); C# `*.jsonl` telemetry same-user writable; no actor attribution | For a tool that can type/click/shell there is no tamper-evident audit trail | Per-action audit log to agent-unwritable location (Event Log/admin dir), integrity-chained |
| F33 | M | Correctness | `engine.rs:1377-1384` `let _ = step_focus(...)`; `lib.rs:1557-1565` MCP sends extra Focus act then ignores result | Click/type proceeds when foreground move failed → input lands in the wrong window | Fail the action when focus fails (`ForegroundChanged` exists) |
| F34 | M | Correctness | `engine.rs:1413` coordinate clicks return `verified:true` unconditionally (contract: verified means effect confirmed) | Model trusts clicks that may not have landed | Report honest verification status |
| F35 | M | Design | `ActionOutcome` has no window/dialog delta (`dispatch.rs:607-616`); WinEvents feed waits only; MCP never subscribes to the event ring; `WindowOpened` from 1 s poll goes nowhere | Model is blind to save prompts/new dialogs until it re-observes (see §3-P4) | Attach opened/closed-hwnd delta to every action result; stream WinEvents as MCP notifications |
| F36 | L | Correctness | `engine.rs:1750-1756` scroll `unwrap_or(Ok(false))?` swallows lane errors → physical scroll at cursor; `process.rs:20-21` 5 s process-name cache keyed by recycled pid; hwnd-reuse stamp race `lib.rs:353-361` | Wrong-target scrolls; misclassified windows; stale stamps | Propagate lane errors; key caches by pid+start-time / hwnd+create-time |
| F37 | L | Hygiene | `agent_cli.py:17` hardcoded GCP project id; `google.antigravity` import makes `inbrisk_pc_agent.py`/`harness.py`/py-tests non-portable; committed session transcripts `tools/*_transcript.txt` | Leaks project id; tooling un-runnable outside the IDE; session data in git | Move id to config/env; purge transcripts; secret-scan CI |
| F38 | L | Compliance | `LICENSE:1-31` all-rights-reserved vs product shipping binaries; no THIRD-PARTY-NOTICES for MIT/Apache deps | Attribution obligations likely unmet; internal-business-use rights unclear | Ship NOTICE file; clarify license terms |
| F39 | L | Docs | `layout.rs:23-26`, `server.rs:507` claim WaitOnAddress; `request.rs:55-57` `include_screenshot` dead field; `include_tree` "Desktop" scope only walks foreground window (`dispatch.rs:381-411`) | Dead/misleading protocol surface | Implement or delete; document actual scope semantics |
| F40 | L | Robustness | `apartment.rs:38-49` COM init never `CoUninitialize`d; `lib.rs:320,372,461` `.lock().unwrap()` poison-panics (abort in release); `engine.rs:1669-1694` Alt+F4 shell guard exists | Minor; noted for completeness | Acceptable; fix unwraps |

---

## 3. Root-cause verdicts — the 7 known problems

### P1. `computer_close_window` ends the MCP session — **CONFIRMED (C#); different mechanism in Rust**

- **C# path [V]:** `computer_close_window` exists only in C# (`InbriskTools.cs:502`). Host protection is a **name list** (`WindowService.cs:381-385`) that omits `windsurf.exe`, `zed.exe`, and any renamed host. With `force:true`, failed WM_CLOSE falls through to `Process.GetProcessById(pid).Kill()` (`InbriskTools.cs:576-584,686,880`) — which kills the **entire process**, so closing "a window" of Windows Terminal / the IDE kills every tab and the MCP server with it. Even without force, `WM_CLOSE` on the last window of an unlisted host quits it. The resource text claims the tool "protects these windows" (`InbriskResources.cs:221`) — true only for the names in the list.
- **Rust path [V]:** no `computer_close_window` tool exists; `Step::Close` is over-restricted (see F24) and cannot kill the host. The more plausible session-killers in Rust are the `pc_*` `.unwrap()` panics (F02) and `panic="abort"` (F01): one malformed tool call or any panic aborts the proxy/runtime → host reports session ended.
- **Residual Rust risk [V]:** the protected-process list regressed vs C# (F20); a host launched as `intent:ephemeral` becomes eligible for `force` termination.

### P2. UWP/Store apps time out at launch (~10 s "no usable window") — **CONFIRMED, root cause found**

Chain `[V]`: Store-app tokens have no filesystem path → `resolve_app` fails → `ShellExecuteW` → `LaunchResult{pid:None}` (`launch.rs:319-338`). Window selection (`engine.rs:3005-3060`) then requires `image_matches(process_name, app_token)` — but UWP top-level windows belong to **`ApplicationFrameHost.exe`**, which never equals the token → candidate filtered forever → 60 ms poll loop burns the full 10 s (`engine.rs:934-967`). Supporting evidence: `root_owner` UWP-unwrap exists (`window.rs:297`) with **zero call sites**; C# had dedicated ApplicationFrameHost/AUMID logic (`AppService.cs:1342`, `Provenance.cs:290-362`) that was dropped in migration; a real transcript shows the failure (`tools/bird_camelotia_transcript.txt:1127`). Workaround that exists today: pass `wait_for_window` — it bypasses the image check (`engine.rs:3030-3031`).

### P3. Ambiguous app names (Edge vs GoAwayEdge) — **CONFIRMED, and worse than reported**

`window_matches` uses substring `contains` on title for `name`/`text` (`engine.rs:297-320`); `find_window` sorts `(foreground, hwnd)` and silently takes the first — **no ambiguity detection at all** (`engine.rs:668-679`). Worse, `window_for_selector` (`engine.rs:710-750`) ignores element-level selector fields for window choice and falls back to the default/foreground window — `{"focus", target:{name:"Mail"}}` can focus the wrong window entirely. `computer_windows` `process` filter is also substring (`inbrisk-mcp/lib.rs:1510-1513`). C# had scored fuzzy matching + `AmbiguousTarget` errors (`InbriskTools.cs:934-971`); Rust has no `AmbiguousTarget` code path for windows.

### P4. Model unaware of new windows/dialogs after an action — **CONFIRMED**

`ActionOutcome`/`StepDone` carry no window delta (`dispatch.rs:607-616`, `engine.rs:3081-3089`); `wait_window` doesn't even return the hwnd (`engine.rs:1933-1939`). WinEvents exist but feed only `WaitService` wake conditions (`engine.rs:147-203`); the client-visible event ring is fed by a separate 1 s `EnumWindows` diff (`lib.rs:325-374`) — and the MCP proxy **never subscribes** to it (no `poll_event`/`subscribe` in `crates/inbrisk-mcp`). Net: a "Save?" dialog after a click is invisible until the model burns another observe; `state_version` bumps so it can tell *that* something changed, never *what*.

### P5. Disabled elements attempted and fail silently — **MOSTLY REFUTED**

`[V]` Enabled checks are comprehensive: all UIA mutations reject `!enabled` with `element_disabled` (`apartment.rs:426,452,491,521,716`), engine checks precede physical click/type (`engine.rs:1431-1436,1499-1505`), and a disabled error cannot silently degrade to a coordinate click (`engine.rs:1349-1351`). Offscreen is rejected (`engine.rs:1448-1454`). Residual gaps: `UiaApartment::focus` has no enabled check (`apartment.rs:478-486`); coordinate-only paths can't check (inherent); enabled state is read from an immortal cache snapshot (F07).

### P6. Every step costs a screenshot round trip; downscaling breaks game clicks — **CONFIRMED, and the root cause is deeper than stated**

Rust returns only full-res PNGs: `max_width` is parsed and ignored (`capture.rs:150`), so every screenshot is a full-virtual-screen base64 PNG [U: byte/token cost unmeasured]. **The "downscale breaks clicks" claim is a coordinate-model problem, not a resize problem** — and the code shows why: `inbrisk-mcp` never sets DPI awareness (`apps/inbrisk-mcp/src/main.rs` — absent, vs `apps/inbrisk/src/main.rs:48`), so on scaled displays the GDI screenshot is already in *virtualized* coordinates that don't match the physical pixels SendInput uses. C# solved exactly this with `frameId`/`observationId`-anchored image-space coordinates + `StaleFrame`/`WindowGeometryChanged` errors (`InbriskTools.cs:2576,6648,6699-6721`); the Rust port dropped that machinery and went back to raw screen-absolute coords. The fix is to re-adopt anchored coordinates (or numbered-mark overlay), which makes downscaling safe.

### P7. Docs/benchmarks/tests missing or empty — **PARTIALLY REFUTED on tests, CONFIRMED on docs+benchmarks**

- **Docs: CONFIRMED stale at headline level.** `ARCHITECTURE.md:40-68` declares C# the implementation language and Rust "rejected for now" while an 18-member Rust workspace exists; `ARCHITECTURE.md:72-81` claims "one C# process — no IPC yet" while the Rust design is multi-process shared-memory; README tool/safety features (`computer_pause_run`, `computer_save_recipe`, `Ctrl+Alt+Pause`, scan/for_each) are all C#-only (`README.md:62-74`); README dev section mentions only `dotnet`, never `cargo` (`README.md:79-81`); `.continue` config points at the C# `inbrisk.exe mcp` contract that the Rust binary does not implement.
- **Tests: REFUTED that they're missing — 281 Rust tests + ~909 C# facts + 2 Python e2e tests exist.** The real gap is *coverage shape*: Rust has **zero** tests driving a real application's UIA (no Rust `TestApp`), `inbrisk-uia` has 3 tests, `inbrisk-input` 3, FFI/bindings 1; C#'s ~12 `[Collection("desktop")]` classes require an interactive session and can't run headless; the two CDP tests need a live debug browser + internet.
- **Benchmarks: CONFIRMED nearly absent.** One persisted result file (`crates/inbrisk-cdp/scratch/controlled_5_sites_result.json`); no criterion/BenchmarkDotNet/`benches/`; measurement machinery exists (`elapsed_us` on every reply `dispatch.rs:98`, IPC p50/p95/p99 `end_to_end.rs:154-202`, `tools/bench_latency.py`, six C# `PhaseF/G` benchmark test classes) but outputs are gitignored/none committed. All latency numbers in this review: **unmeasured**.

---

## 4. Top 10 improvements, ranked by impact ÷ effort

| # | Improvement | Expected gain | Effort | Verify by |
|---|---|---|---|---|
| 1 | **Kill the panic paths**: `panic="abort"` → `unwind`; fix the 4 `pc_*` `.unwrap()` sites (F01+F02) | Eliminates whole-class "session ended" failures — likely the user's P1 on the Rust path | S | malformed `pc_exec` returns `InvalidArguments` instead of dead proxy |
| 2 | **UIA CacheRequest/FindAllBuildCache** batching, ported from `UiaBackend.cs` (F06) | ~15× fewer cross-process COM calls per element; observe/find/act all get faster at once — the single largest latency lever | M | measure COM calls + wall time per `computer_observe` before/after |
| 3 | **Packaged-app launch**: treat `ApplicationFrameHost` frames as candidates, wire `root_owner`, add AUMID/`IApplicationActivationManager` resolution, auto-relax image check for ShellExecute launches (P2) | Store apps launch reliably; also shortens all ShellExecute launches (no 10 s burn) | M | launch Calculator/Store/Spotify: window found <2 s |
| 4 | **Window delta in every action result** + stream WinEvents→MCP notifications (P4, F35) | Removes the mandatory re-observe after every action — roughly halves observation round trips per task; model sees dialogs instantly | M | click that opens a "Save?" dialog returns `opened_hwnds` in the same result |
| 5 | **Wire element invalidation**: call `world.invalidate()`/`invalidate_all()` on WinEvent destroy/show + fingerprint change; bound `ElementCache` (F07) | Stale-handle bugs become catchable `StaleState` instead of silent wrong-coord clicks | S-M | mutate → destroy window → `computer_read` returns `StaleState` |
| 6 | **Anchored/image-space coordinates + real downscale + DPI-aware capture** (P6, F17, F22): honor `max_width`, mint frame IDs like C#, accept signed coords, `PER_MONITOR_AWARE_V2` in `inbrisk-mcp` | Screenshot tokens drop by the downscale factor (e.g. 4-8×) without breaking clicks; multi-monitor + scaled displays work | M | click via frame coords at 150% DPI lands correctly; PNG bytes shrink |
| 7 | **Emergency stop parity + coverage**: `RegisterHotKey` Ctrl+Alt+Pause in Rust runtime; gate all mutating ops on `is_emergency`; kill-switch state in agent-unwritable storage (F11, F18) | The documented flagship safety feature actually exists; "stop" stops everything | S-M | panic during `computer_run` halts mid-plan; resume only via local gesture |
| 8 | **Security floor**: untrusted-content markers on all observed text; server-side confirm gate for dangerous classes; remove Low-IL label + authenticate IPC clients; `IsPassword` redaction (F03-F05,F12,F19) | Prompt-injection gets a second line of defense; sandboxed processes can't drive the desktop | M-L | injection test page cannot trigger actions; Low-IL client attach fails |
| 9 | **Fast launch + ambiguous-name fixes**: indexed cached app resolver (PATH + App Paths + Start menu + AUMID), fused launch-and-wait, `AmbiguousTarget` on >1 window match, no foreground fallback for named windows (P3, F26) | Launch latency down (no O(dirs×names) probing + no poll burn); wrong-window actions eliminated | M | `computer_apps`-style index resolves "edge" in ms; `name:"Edge"` with 2 matches errors |
| 10 | **Benchmark suite + docs rewrite**: reuse `elapsed_us`, `bench_latency.py`, `harness.py` recorder; task corpus w/ programmatic verifiers (Notepad/Explorer/Edge/VS Code/game); rewrite README/ARCHITECTURE for the dual codebase (P7) | Every number in this review becomes measured; docs stop lying | M | `artifacts/benchmarks/` populated; p50/p95 per tool reported |

---

## 5. 30/60/90-day roadmap (measurable)

**30 days — stop the bleeding (reliability + safety floor)**
- Items #1, #5, #7 above + F08 (stuck modifiers), F20 (protection list + late-window stamping), F22 (signed coords), F23-F24 (lane timeout, close path), F33-F34 (honest focus/verified).
- Measurable goals: zero panics in `cargo test` + a 500-call fuzz of malformed tool args with the proxy still alive; `Ctrl+Alt+Pause` halts a running plan ≤200 ms; no `SendInput` error path can leave a button held (assert via `GetAsyncKeyState` in test).
- Decide the migration direction explicitly: pick ONE `inbrisk.exe` owner and one MCP surface; document which of C#/Rust is canonical. This decision gates everything below.

**60 days — performance + observation architecture**
- Items #2, #3, #4, #6, #9 + F14-F16 (dispatch threading, oversize replies, per-call handshake), F26-F28 (enum cache, wake stubs, single window view).
- Measurable goals: `computer_observe` on a 400-element window <500 ms p50 (baseline: unmeasured — establish first); UWP launch-to-window <2 s; screenshots per representative task reduced ≥50% via action-result deltas; ≥1 benchmark run committed per release.
- Port the C# CacheRequest pattern; add `AmbiguousTarget`; fused launch-and-wait.

**90 days — trust + completeness**
- Item #8 fully + F18/F19/F30-F32 (signing, audit log, FFI hardening), then feature-parity ports ranked by usage: OCR, `within`/`ancestor`, wait_for_change, clipboard ops, recipes/screen memory (or explicitly retire them in C#).
- Measurable goals: signed installer + signed-update verification; audit log capturing every mutating action; ≥80% of the 54-tool C# surface either ported or formally retired; task benchmark: ≥90% success on the deterministic corpus (Notepad/Explorer/Edge/VS Code), screenshots/task and tokens/task reported; docs rewritten with zero contradictions found by a doc-vs-code check.

---

## 6. Could not verify / would need

- **Which binary is actually installed** on this machine (`%LOCALAPPDATA%\Programs\Inbrisk\inbrisk.exe` — C# or Rust). Both share the name; determines which P1 mechanism and which tool surface the user actually experiences. Need: `Get-Item` on the installed exe / version info.
- **Runtime reproduction of all runtime claims.** This pass was source-only — no binaries exercised, no live Store-app launch, no close_window against a real host. A live repro pass would confirm P1/P2 end-to-end.
- **All latency/size numbers.** No committed baselines exist; per-call latencies, PNG sizes, tree JSON sizes, token costs are all unmeasured. Need: the benchmark suite in §4-#10.
- **Whether `cargo test` suite passes** (281 tests enumerated, not run — some need interactive desktop/ConPTY/pwsh; C# desktop tests need a real session).
- **Completeness of secrets scan** — targeted patterns only, not a dedicated scanner (gitleaks/trufflehog recommended). No live creds found; `agent_cli.py:17` hardcodes a GCP project id.
- **`inbrisk-ui` tray/overlay behavior under runaway full-screen apps** and `inbrisk-desktop` behavior on the secure desktop (best-effort by code, unverified live).
- **Whether any host config launches `inbrisk-mcp.exe`** — no checked-in config references it.
- **C# `ActionResolver` fallback internals** — confirmed UIA-pattern-first + SendInput, not traced line-by-line.
- **`computer_act` schema vs doc drift** on element targeting (`computer_click` description mentions targets the schema lacks — `inbrisk-mcp/lib.rs` vs `schema_click`).

---

**If I could do only ONE thing first, it would be _fix the panic surface (`panic="abort"` + the `pc_*` `.unwrap()` sites)_, because _a single malformed tool call currently kills the entire MCP session — it's the cheapest fix that eliminates the most user-visible failure mode, and it blocks trustworthy testing of everything else._**
