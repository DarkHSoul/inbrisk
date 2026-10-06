# computer_run Plan Schema (C# MCP)

Plans are submitted via the MCP tool `computer_run` with a `steps` array.
The authoritative live reference is `computer_capabilities{detail:"full"}`.

## Step actions

- `launch` — `{action:"launch", app:"notepad" | executable:"notepad.exe" | aumid:"…", waitFor:"window|process|none", timeoutMs}`
- `click` / `rightclick` / `doubleclick` / `invoke` / `toggle` / `select` / `hover`
- `set_value` — `{elementId|target, text|value}`
- `type` — `{elementId|target, text, mode:"replace|append|insert", position, submit}`
- `key` — `{key, count}`, `hotkey` — `{key, modifiers} or {keys:"ctrl+s"}`
- `scroll{delta,target}` · `scroll_into_view{elementId|target}` · `drag{target,toX,toY}`
- `focus` / `focus_window`
- `wait{ms}` · `wait_for{query,ms,within|process|window}` · `wait_for_gone{query|target|elementId,ms}` · `wait_for_change{ms}` · `wait_for_stable{ms}`
- `find{as:"x"}` — resolves and binds `elementId:"$x"` for later steps
- `scan` / `for_each` — `{target, as:"item", steps:[…], where, collect, maxItems, maxPages, stopOn}` — server-side iteration, binds `$item.id/.name/.value/.role`
- `checkpoint` — pause for model reasoning · `human` / `pause_for_human{reason}` — hand control to the local user
- `adapter{adapter,args}` · media actions (`play`,`pause`,`next`,`previous`,`volume_up`,`volume_down`,`mute`)

## Conditions & flow control

- `ifExists` / `ifNotExists` / `ifEnabled` / `ifValue` — skip step (never fail)
- `retry` + `retryInterval`, per-step `timeout`
- self-heal hints: `autoScroll`, `autoNavigate`
- `observe:true` — piggyback a scoped observation on the step result
- Unexpected modal → run pauses with `UnexpectedModalOpened`; resume via `runId`

## Targets

```json
{"elementId":"uia_…|"$x"", "process":"notepad", "window":"title substr", "hwnd":"0x…",
 "role":"button|document|edit|…", "name":"OK", "nameContains":"Sav", "automationId":"…",
 "labelledBy":"Name", "nearText":"Total:", "within":"$win", "ancestor":"Toolbar", "x":0,"y":0}
```

## Bindings

`$var`, `{{var}}`, `$item.name/.value/.role/.id` interpolate inside names, text, values, elementIds.

## Result shape

Each step reports `success/method/durationMs/detail` plus:
- `delta` — `windowsOpened`/`windowsClosed`/`dialogs`/`focusChanged`/`targetElement`
- `provenance` — `{"untrusted":true,"source":"uia"}`
- on failure: `step`, `error`, `observationDelta`, `availableElements`, `runId` (resume token)

A completed run (`≥2` steps) may include `recipeHint` — persist it with
`computer_save_recipe{fromRunId}` → replay via `computer_run_recipe`.

## STRICT

Unknown or wrong-action fields are `Malformed` — never silently ignored.
