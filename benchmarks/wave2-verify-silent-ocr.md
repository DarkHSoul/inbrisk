# wave2-verify-silent-ocr — verification plan

Scope: `silent:true` WM-message input (no focus theft, structured `NotSupported`, never SendInput fallback), OCR overlay/targeting (`ocr:true`, `ocrText`, `ocr:<hwnd>:<idx>` ids), `map:` selectors in `wait_for`, wave-2 perf events. Reference impl: `tools/benchmark_batching_verify.py` (same stdio/env pattern).

## 0. Harness contract

- Server: `dotnet <repo>\src\Inbrisk.Cli\bin\Release\net8.0-windows10.0.19041.0\inbrisk.dll mcp` — build Release first (`dotnet build src\Inbrisk.Cli -c Release`); stale binaries void results.
- Transport: newline-delimited JSON-RPC 2.0 on stdin/stdout. Every check below is a `tools/call`:
  `{"jsonrpc":"2.0","id":<n>,"method":"tools/call","params":{"name":"<tool>","arguments":{...}}}`
- Spawn env (isolation — never touch the user's live instance):

| env | value |
|---|---|
| `INBRISK_PANIC_HOTKEY` | `Ctrl+Alt+F12` |
| `INBRISK_RESUME_HOTKEY` | `Ctrl+Alt+Shift+F12` |
| `INBRISK_EMERGENCY_STATE` | `<tmpdir>\emergency.flag` |
| `INBRISK_DATA_DIR` | `<tmpdir>\data` |
| `INBRISK_TOOL_PROFILE` | `full` |

- Perf sinks (record byte offsets BEFORE check 1; slice `file[offset:]` afterwards):
  - `PERF = $INBRISK_DATA_DIR\perf-trace.jsonl` — PerfLog + PerfTrace (`{kind,id,at,totalMs,stages[],counters,extra}` and flat `{kind,...}` events). Isolated.
  - `FINDP = %LOCALAPPDATA%\inbrisk\find-perf.jsonl` — UiaPerf, HARDCODED shared path; offset-slice only, flag best-effort.
- Vars: `<RND>` = 5-digit unique suffix; `<NP>` = spawned notepad hwnd (`0x…`); `<NPH>` = hex digits without `0x`; `<DOC>` = notepad document elementId (`uia_*`); `<MARK>` = marker-console hwnd; `<CALC>` = calc hwnd (if spawned).
- `CALL` blocks show `params.arguments` only. All response text blocks are either flat text or a JSON object — parse `result.content[*].text`.
- Known response shapes (assert against these):
  - error: `{"error":"<Kind>","detail":"…","durationMs":<n>}` + `isError=true`
  - action: `{"action","status","success","method","durationMs","changed":[],"next","nextObservationRequired","provenance","detail"?,"hint"?,"verificationHint"?,"evidence":{"method","expected","actual","detail"}?,"delta":{"focusChanged":{"fromHwnd","toHwnd",…},…}?,"post":{"id","role","name","value","state","focused","bounds"}?,"observation":{…}?}`
  - `computer_launch`: `{"status":"Verified","window":{"hwnd":"0x…","title":…},"launchState":"Ready|AlreadyRunning","durationMs",…}`
  - `computer_windows` lines: `0x<H>  "<title>"  app=<proc>  rect=(x,y WxH)  [foreground] [shown] [normal|maximized|minimized] …`
  - `computer_find` lines: `found N element(s)…:` then `  [<id>] <Role> "<name>" value="…" hwnd=0x… pid=… bounds=(x,y WxH) actions=[…]`
- Wave-presence probe (run first; marks every silent/OCR-wire check BLOCKED on failure):
  `computer_type{elementId:"<DOC>",text:"x",silent:true}` → if `error:"Malformed"` + `detail` ~ "unknown … silent" → wave not landed → STOP, all items `BLOCKED`.
- Global invariant (checked after EVERY silent call): `GetForegroundWindow()` (Win32, harness-side) == `<MARK>` AND the `computer_windows` `[foreground]` flag is on the `<MARK>` line, never on `<NP>`/`<CALC>`. A silent call whose result carries `delta.focusChanged.toHwnd` == `<NP>`/`<CALC>` is an automatic FAIL for that check.

## 1. W2-01 — `silent:true` type into self-spawned notepad edit control (no focus theft)

Preconditions:
1. Spawn marker console (the "caller's window"): `cmd /c "title W2MARKER<RND> && ping -n 60 127.0.0.1 >nul"` with `CREATE_NEW_CONSOLE`; resolve `<MARK>` via `EnumWindows` title match (`W2MARKER<RND>`).
2. `computer_launch{"executable":"notepad.exe","newInstance":true,"waitFor":"window","timeoutMs":20000}` → parse `window.hwnd` → `<NP>`; `launchState` ∈ {Ready, AlreadyRunning}.
3. `computer_find{"hwnd":"<NP>","role":"document","detail":"full"}` → `<DOC>` = first `[uia_…]`; if empty, retry `role:"edit"` (fallback `map:notepad.editor`).
4. Harness `SetForegroundWindow(<MARK>)`; confirm via `computer_windows` — `<MARK>` line carries `[foreground]`, `<NP>` line does not.

| step | CALL (params.arguments) | expected response fields | PASS criterion |
|---|---|---|---|
| 1.1 | `computer_type{"elementId":"<DOC>","text":"W2SIL<RND>","silent":true}` | `success:true`; `status` ∈ {Verified,ObservedChange,Unverified}; `method` matches `wm_settext|em_replacesel|bm_*|wm_command` (NOT `sendinput`/`send_input`); `post.value` may echo text | not `isError`; no `SendInput`/`focus` stage names in the matching `kind:"tool"` PERF record |
| 1.2 | `computer_find{"hwnd":"<NP>","role":"document"}` (role `edit` if that was the resolved role) | `value="…W2SIL<RND>…"` on the `[<DOC>]` line | typed text present in read-back (WM_GETTEXT-equivalent semantic value) |
| 1.3 | `computer_windows{}` | `<MARK>` line has `[foreground]`; `<NP>` line lacks `[foreground]` | focus never left the caller window |
| 1.4 | (fallback, only if 1.1 returns `NotSupported`/`Unsupported` with `detail` ~ `hwnd|HWND-less|not a live window|class`) | retry 1.1–1.3 against `role:"edit"` element | if both document+edit give structured `NotSupported` → mark `W2-01 BLOCKED (no hwnd-bearing edit control)`; W2-03 must still PASS |
| 1.5 | (repeat) ×5 samples of step 1.1 alternating text | each `durationMs` | feed to W2-06 latency comparison |

FAIL any of: text absent in read-back; `isError:true` with non-NotSupported error; foreground moved to `<NP>`; method shows `sendinput`.

## 2. W2-02 — `silent:true` click on calc `map:calculator.digit7`

Preconditions: `computer_launch{"app":"Calculator","waitFor":"window","timeoutMs":20000}` → `<CALC>` (skip+document `BLOCKED` if calc absent); marker console still foreground (re-assert `SetForegroundWindow(<MARK>)`).

| step | CALL | expected response fields | PASS criterion |
|---|---|---|---|
| 2.1 | `computer_click{"target":{"map":"calculator.digit7"},"silent":true}` | EITHER: `success:true` + `method` `bm_click`/`wm_command` OR: `isError:true` + `{"error":"NotSupported","detail":"…no reliable message-only equivalent…|…not a Win32 button…|…HWND-less…"}` (accept `error:"Unsupported"` — record which string the impl emits) | success path: display changed per 2.2 AND focus stayed on `<MARK>`; NotSupported path: structured error, `detail` names the reason, NO SendInput fallback ran (see 2.3) |
| 2.2 | `computer_find{"target":{"map":"calculator.result"}}` (only on 2.1 success) | result element `name`/`value` contains `7` (localized prefix allowed) | display reflects the click |
| 2.3 | `computer_windows{}` + 2.1's `delta` | `[foreground]` on `<MARK>`; `delta.focusChanged` absent or `toHwnd≠<CALC>` | XAML/UWP buttons are HWND-less → `NotSupported` is the DOCUMENTED expected outcome for that control type; `NotSupported` + focus intact = PASS |
| 2.4 | baseline: `computer_click{"target":{"map":"calculator.digit7"}}` (no silent) then `computer_click{"target":{"map":"calculator.clear"},"silent":true}` ×5 alt | both `durationMs` samples | feed to W2-06; reset display to `0` afterwards is not required |

FAIL: `success:true` but display unchanged AND no `NotSupported` reason (fake success); any SendInput/focus-steal evidence on the silent path.

## 3. W2-03 — `silent:true` on unsupported kinds → structured NotSupported, never SendInput

Precondition: `<NP>` open, `<MARK>` foreground. Each call must be an error, not an action.

| step | CALL | expected response | PASS criterion |
|---|---|---|---|
| 3.1 | `computer_hotkey{"keys":"ctrl+s","silent":true}` | `isError:true`, `{"error":"NotSupported","detail":"…keyboard input goes to the focused window — WM_KEYDOWN injection cannot emulate focus, IME state or accelerator tables reliably"}` (substring match: `WM_KEYDOWN` or `no message-only`/`silent`) | structured error; NOT executed |
| 3.2 | `computer_click{"elementId":"<DOC>","button":"right","silent":true}` | `isError:true`, `error` ∈ {NotSupported,Unsupported}, `detail` ~ `right|xBUTTON|no reliable message-only` | same |
| 3.3 | `computer_click{"elementId":"<DOC>","button":"double","silent":true}` | same shape | same |
| 3.4 | `computer_batch{"steps":[{"do":"hotkey","keys":"ctrl+s","silent":true},{"do":"wait","ms":100}],"failFast":false}` (per-step `silent`) | step result for the hotkey shows `ok:false` + error `NotSupported`; batch `success:false`, `results[0].error` contains `NotSupported` | Malformed `unknown field(s): silent` instead ⇒ wave-2 batch wiring missing ⇒ FAIL (not BLOCKED) |
| 3.5 | verify no fallback executed | `computer_find{"hwnd":"<NP>","role":"document"}` value unchanged (no `s`/`ctrl+s` side effects — no save dialog); `computer_windows` → `<MARK>` still `[foreground]`; PERF/FINDP slices contain no `sendinput`/`focus` stage attributable to 3.1–3.4 | FAIL if any: notepad content changed, a save/dialog window appeared (`systemDialogs:`/`[MODAL/DIALOG]` on `<NP>`), or foreground moved |

## 4. W2-04 — OCR observe → find → click chain

Preconditions: seed known text first — `computer_set_value{"elementId":"<DOC>","value":"OCRSEED<RND> alpha beta gamma"}` → `success:true`; `<NP>` visible (not minimized). If `computer_observe{ocr:true}` prints `ocr words: (OCR engine unavailable)` → whole item `BLOCKED (no recognizer)`.

| step | CALL | expected response fields | PASS criterion |
|---|---|---|---|
| 4.1 | `computer_observe{"hwnd":"<NP>","ocr":true}` | text contains `ocr words (N of M shown; ocr:<hwnd>:<idx> ids are clickable targets):` + lines `  ocr:0x<NPH>:<i> "<word>" (x,y WxH)` | a line exists whose word == `OCRSEED<RND>` (or contains it) with a non-empty rect inside `<NP>`'s bounds |
| 4.2 | `computer_find{"target":{"ocrText":"OCRSEED<RND>","hwnd":"<NP>"}}` (equivalent flat form: `{"name":"OCRSEED<RND>","hwnd":"<NP>","ocr":true}`) | `found N element(s) (K via ocr):` + `  [ocr:<NPH>:<i>] Text "OCRSEED<RND>" … bounds=(x,y WxH) actions=[click]` | ≥1 element id matching regex `ocr:0x?<NPH>:\d+` — NOTE: find mints `ocr:<hex>:<i>`, observe prints `ocr:0x<hex>:<i>`; both accepted, record which (normalization inconsistency → note, not fail) |
| 4.3 | `computer_click{"elementId":"<ocr-id from 4.2>"}` | `success:true`; `status` ∈ {Verified,ObservedChange,Unverified}; `delta.focusChanged.toHwnd`=="0x<NPH>" OR `post`/`observation` shows `<NP>` active | click physically landed on the notepad window — coordinate click raises it (this is the non-silent path; focus change to `<NP>` is the expected signal here, NOT a violation) |
| 4.4 | `computer_click{"target":{"ocrText":"OCRSEED<RND>","hwnd":"<NP>"}}` | same as 4.3; forces `ActionResolver.ResolveOcrTarget` → emits `ocr.resolve` perf event | `success:true`; produces `ocr.resolve` in PERF (needed by W2-06) |
| 4.5 | (stronger landing check, optional) `computer_type{"elementId":"<DOC>","text":"Z","mode":"insert"}` then `computer_find{"hwnd":"<NP>","role":"document"}` | `value` contains `Z` adjacent to/after `OCRSEED<RND>` | caret moved to the clicked word ⇒ PASS+; skip on `NotSupported`/caret-less controls |
| 4.6 | ambiguity: `computer_find{"target":{"ocrText":"alpha","hwnd":"<NP>"}}` after duplicating the word (set value to `alpha alpha`) | either `found 2+` hits OR `isError` `AmbiguousTarget` with `candidates`/`detail` listing both `@(rect)` positions | never silently picks one without reporting ambiguity (record actual) |

FAIL: `ocr:` ids unactionable (`Stale`/`TargetNotFound` on 4.3); click lands on wrong window (`focusChanged.toHwnd`≠`<NP>`); words section absent when engine available.

## 5. W2-05 — `wait_for{query:"map:notepad.document"}` resolves via map expansion

Precondition: `<NP>` open with its document present.

| step | CALL | expected response fields | PASS criterion |
|---|---|---|---|
| 5.1 | `computer_wait_for{"query":"map:notepad.document","ms":5000}` | `success:true`, `status:"Verified"`, `durationMs` ≪ 5000 (element exists → near-immediate or first event wake) | resolves; NOT `Timeout` — proves `map:` expanded to `{process:notepad, role:document}` instead of a literal-name wait |
| 5.2 | `computer_wait_for{"query":"map:notepad.__nonexistent<RND>","ms":8000}` | `isError:true`, `status`/`error` `Timeout` acceptable — `detail` must contain `unknown ui-map key 'notepad.__nonexistent<RND>'` or `invalid map selector` | FAIL-FAST: `durationMs` ≪ 8000 AND detail names the map key — a full 8s literal-name timeout ⇒ FAIL (no expansion) |
| 5.3 | `computer_wait_for{"query":"<DOC's literal name>","ms":5000}` (name read from the find line, e.g. `Metin düzenleyici`/`Text editor`) | `success:true` | literal-name waits unaffected (prefix-gated expansion, not a rename) |
| 5.4 | `computer_run{"steps":[{"action":"wait_for","query":"map:notepad.document","ms":5000}]}` | `status:"Completed"` (or per-step ok) | `map:` reaches `WaitCondition` through the run-step path too (`until.appears/disappears` share it — exercised again in 6.2) |

## 6. W2-06 — perf events + silent-vs-SendInput latency

Preconditions: offsets captured pre-check-1; all prior checks ran.

| step | action | PASS criterion |
|---|---|---|
| 6.1 | slice `PERF[perfOff:]` and `FINDP[findOff:]`, parse JSONL | all lines valid JSON |
| 6.2 | trigger batch-until: `computer_batch{"steps":[{"do":"wait","ms":150}],"until":{"appears":"map:notepad.document","timeoutMs":5000}}` → expect `success:true`, `until.wakeSource` ∈ {event,immediate,poll} | FINDP gains `{kind:"batch.until","runId","condition":"appears","ok":true,"wakeSource","ms"}` AND `{kind:"batch.summary","runId","status":"Completed","success":true,"stepMs":[…],"untilWakeSource"}` |
| 6.3 | event presence matrix | see table below — every required kind found in its file ⇒ PASS per row |
| 6.4 | latency: p50 of `silent.action` `ms` (or silent tool `durationMs` from 1.5/2.4) vs p50 of the non-silent `computer_type`/`computer_click` baseline samples | record both; PASS if silent p50 ≤ baseline p50 × 1.5 AND silent p50 < 2000 ms (WM path skips focus/foreground work — slower-than-baseline means the SendInput fallback ran) |

Required perf events:

| event `kind` | file | emitted by | required fields | source |
|---|---|---|---|---|
| `ocr.observe` | `PERF` | `Rt.OcrWindow` (observe `ocr:true`, cached-word path) | `hwnd`, `ms`, `words`, `cached` | Inbrisk.cs ~L734/761 |
| `ocr.resolve` | `PERF` | `ActionResolver.ResolveOcrTarget` (4.4) | `run`, `hwnd`, `text`, `lang`, `ms`, `words`, `matches`, `chosen`, `outcome:"resolved"` | ActionResolver.cs ~L442 |
| `ocr.find` | `FINDP` | `computer_find` OCR merge (4.2) | `hwnd:"0x…"`, `query`, `words`, `hits`, `ms` | InbriskTools.cs ~L2251 |
| `ocr.recognize` | `PERF` | OcrService / resolver stage — accept either a top-level `{kind:"ocr.recognize"}` event OR `stages[].name=="ocr.recognize"` inside a `{kind:"tool"}` record | `ms`/`totalMs` numeric | PerfTrace.Stage("ocr.recognize") |
| `silent.action` | `PERF` or `FINDP` (scan both slices) | silent dispatch — one per silent call from W2-01/02/03 | numeric `ms`; expected `action`/`method`/`ok`-style fields | wave-2 contract; ABSENT ⇒ FAIL |
| `batch.until` | `FINDP` | `computer_batch` until block (6.2) | `condition`, `ok`, `wakeSource`, `ms` | InbriskTools.cs ~L3687 |
| `batch.summary` | `FINDP` | batch completion (6.2) | `status`, `success`, `totalMs`, `stepMs[]` | InbriskTools.cs ~L3701 |

FAIL any: required event missing; `silent.action` absent ⇒ silent dispatch never recorded; OCR events with `words==0` on a non-empty notepad ⇒ recognition produced nothing.

## 7. Cleanup & verdict

- `computer_close_window{"hwnd":"<NP>"}` (agent-owned → allowed); same for `<CALC>` if spawned; kill marker console via `taskkill /pid <pid>` or `PostMessage(<MARK>, WM_CLOSE)` — only windows the harness itself spawned may be closed.
- `computer_app_status{}` final → `"running": true`, `emergencyState:"Active"` (no emergency triggered by the wave).

Verdict matrix (record per item): `W2-01` PASS/FAIL/BLOCKED · `W2-02` PASS/FAIL/`NotSupported-documented`/BLOCKED · `W2-03` PASS/FAIL · `W2-04` PASS/FAIL/BLOCKED · `W2-05` PASS/FAIL · `W2-06` PASS/FAIL — plus raw values: silent/baseline p50s, all sliced perf events, ocr-id format variant observed (`ocr:<hex>:` vs `ocr:0x<hex>:`), exact error-string emitted for unsupported kinds (`NotSupported` vs `Unsupported`).
