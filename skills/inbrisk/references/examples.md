# Examples (C# MCP)

All calls are MCP tool calls on the `inbrisk` server.

## Batch-first: one call, many actions

**Rule: 2+ known actions → `computer_batch` or `computer_run`. Never a
chain of `computer_click`/`computer_type`/`computer_hotkey` calls.**

### Linear click/type/key sequence → `computer_batch`

```json
{"steps":[
  {"do":"click","t":"File","role":"MenuItem"},
  {"do":"click","t":"Save As…"},
  {"do":"type","v":"report.txt","role":"Edit"},
  {"do":"hotkey","keys":"ctrl+s"}
]}
```

Batch step fields: `do` (action: `click|invoke|type|key|hotkey|wait|
launch|focus|toggle|select|set_value|scroll`), `t` (element name,
automationId, or `uia_…` elementId — a plain string, NOT a target
object), `v` (text/value), `role`, `hwnd`, `ms`, `submit`, `keys`.

### Fill a whole form → `computer_batch` with `set`

```json
{"set":[
  {"target":"File name:","value":"report.txt"},
  {"target":"Encoding:","value":"UTF-8","role":"ComboBox"}
]}
```

`set` is mutually exclusive with `steps`. Add `until` to wait for an
outcome and `read` to pull values back in the same call:

```json
{"steps":[{"do":"click","t":"Save"}],
 "until":{"windowAppears":"Save As","timeoutMs":5000},
 "read":[{"target":"Status","props":["value","isEnabled"]}]}
```

### One-shot launch + type + hotkey → `computer_do`

```json
{"app":"notepad","type":"hello world","hotkey":"ctrl+s","submit":true}
```

### Conditions, loops, bindings → `computer_run`

Needs `find{as:"x"}` bindings, `ifExists`/`ifValue`, `scan`/`for_each`,
`wait_for*`, or pause/resume → use the full plan engine (see below).

### ❌ Wrong — three round trips for one known sequence

```
computer_click  {target:{name:"Save"}}
computer_type   {text:"x", target:{role:"edit"}}
computer_hotkey {keys:"ctrl+s"}
```

→ collapse into a single `computer_batch` call as shown above. Reserve
single-action tools for genuinely isolated one-offs.

## Find an element and click it

```
computer_find {process:"notepad", role:"document", limit:3}
  → [uia_11076_40] Document "Metin düzenleyici" …
computer_click {elementId:"uia_11076_40"}
  → success, method:"UIA.LegacyIAccessible.DoDefaultAction", durationMs:317,
    delta:{…}, provenance:{untrusted:true,…}
```

## Multi-step in one call

```
computer_run {steps:[
  {action:"launch", app:"notepad", waitFor:"window", timeout:15000},
  {action:"find", as:"doc", target:{process:"notepad", role:"document"}},
  {action:"set_value", elementId:"$doc", text:"hello"},
  {action:"hotkey", keys:"ctrl+s"}
]}
```

## Screenshot with marks → click by ID

```
computer_screenshot {target:"window", hwnd:"0x310516", maxWidth:1200, marks:true}
  → frameId=12 … marks: 1 → uia_… "Save" button, 2 → uia_… "Cancel" …
computer_click {elementId:"uia_…"}
```

## Ambiguity

```
computer_focus_window {process:"notepad"}
  → AmbiguousTarget, candidates:[{hwnd,title,pid} ×4]
  → retry: computer_focus_window {process:"notepad", window:"*Untitled"}
```

## Refusals — final, do not retry harder

```
computer_app_shutdown {pid:<explorer>}      → PolicyDenied (host/ancestor)
computer_close_window {hwnd:<foreign>}      → PolicyDenied (CanAgentClose)
computer_close_window {hwnd:X, force:true}  → SharedProcessKillRefused
computer_hotkey {modifiers:["alt"],key:"F4"}→ ConfirmationDenied (local consent)
```

## Emergency

Any result `{"error":"EmergencyStopped"}` → control is halted machine-wide.
Only the local user resumes (Ctrl+Alt+Shift+Pause). Stop and inform them.

## Silent (no-focus) input

`silent:true` sends background WM messages to the target window — no focus
theft. Unsupported kinds return `NotSupported` (never a SendInput fallback).

```json
computer_batch {"steps":[
  {"do":"type","v":"text","role":"Edit","silent":true},
  {"do":"hotkey","keys":"ctrl+s","silent":true}
]}
```

Also on single tools: `computer_click {target:{name:"OK"}, silent:true}`.
In `computer_run` steps: `{"action":"type","text":"x","silent":true}`.

## OCR text targeting (UIA-less windows)

```
computer_observe {hwnd:"0x…", ocr:true}     → word list: "PLAY" "OPTIONS" …
computer_click   {target:{ocrText:"PLAY"}}  → hits ocr:<hwnd>:<idx>
computer_type    {target:{ocrText:"Username"}, ocr:true, text:"ahmet"}
```

OCR hits become `ocr:<hwnd>:<idx>` elementIds — use them like UIA ids.
For windows with no UIA tree (games, canvas-drawn apps) this is the only
semantic targeting available.

## Auto-save a run as a recipe

```
computer_run {save_as_recipe:"save-notepad", steps:[…]}
  → on success the run is saved as recipe "save-notepad";
    dynamic values (urls/paths/typed text) become {{param}}
computer_run_recipe {name:"save-notepad", params:{filename:"b.txt"}}
```

`save_as_recipe` also works on `computer_batch` and `computer_do`.

## Session cleanup

```
computer_cleanup {}  → reaps processes this session spawned
  (PID-reuse guarded; protected processes are skipped, not killed)
```

## Blender bridge

```
computer_adapter {adapter:"blender", action:"auto_install"}  → one-time install
computer_launch  {app:"blender"}   → auto-injects the --python bridge
```
