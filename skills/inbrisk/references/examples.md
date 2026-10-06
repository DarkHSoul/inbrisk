# Examples (C# MCP)

All calls are MCP tool calls on the `inbrisk` server.

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
  {action:"launch", app:"notepad", waitFor:"window", timeoutMs:15000},
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
