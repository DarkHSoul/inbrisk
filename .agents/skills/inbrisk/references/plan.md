# Plan wire format

`inbrisk-cli run` accepts one of these bodies. Stdin is the default. `--file PATH` and `--json-arg JSON` are the other sources. Pass exactly one.

```json
{ "steps": [ ] }
```

```json
{ "op": "run", "steps": [ ] }
```

```json
{ "plan": { "steps": [ ] } }
```

`name` is optional. Each step is one object with an `action` tag.

## Notepad, one round trip

```json
{
  "name": "notepad-hello",
  "steps": [
    {
      "action": "launch",
      "app": "notepad",
      "intent": "ephemeral",
      "timeout_ms": 10000,
      "store": "win"
    },
    {
      "action": "find",
      "selector": { "role": "document" },
      "store": "doc"
    },
    {
      "action": "set_value",
      "target": "$doc",
      "value": "Hello"
    }
  ]
}
```

`launch` makes that window the default for later selectors. `$doc` is the element id from `find`. A launch result is a window record (`hwnd`, `pid`). It is not an element id, so do not `invoke` `$win`.

Windows Notepad exposes the text area as `document` (`RichEditD2DPT`, name like "Text editor"). Classic Notepad exposes `edit`. If `find` returns nothing, `observe --tree` on that window and use the role that is actually there.

`set_value` uses ValuePattern. If the control has no ValuePattern, the runtime types the text and reports that fallback. `verified` is true only when a read-back matches.

## Notepad, save in the same plan

`Ctrl+S` on an untitled document opens a `#32770` Save dialog. The key step returns before that window exists, so the next step waits for it. The wait makes that dialog the default window for the following steps.

```json
{
  "name": "notepad-save",
  "steps": [
    { "action": "launch", "app": "notepad", "intent": "ephemeral", "timeout_ms": 12000 },
    { "action": "find", "selector": { "role": "document" }, "store": "doc" },
    { "action": "set_value", "target": "$doc", "value": "Hello" },
    { "action": "key", "keys": ["ctrl", "s"], "target": "$doc" },
    {
      "action": "wait",
      "until": "window",
      "selector": { "class_name": "#32770", "process": "notepad.exe" },
      "state": "exists",
      "timeout_ms": 8000
    },
    {
      "action": "set_value",
      "target": { "role": "edit", "automation_id": "1001" },
      "value": "C:\\Users\\Ahmet\\AppData\\Local\\Temp\\note.txt"
    },
    {
      "action": "invoke",
      "target": { "role": "button", "name": "Save", "automation_id": "1", "exact": true }
    },
    {
      "action": "wait",
      "until": "window",
      "selector": { "class_name": "#32770", "process": "notepad.exe" },
      "state": "gone",
      "timeout_ms": 8000
    }
  ]
}
```

The file-name edit is automation id `1001`. The confirm button is automation id `1`. On a Turkish Windows install that button's name is still `Save`, and the dialog title is `Farklı kaydet`. Match the class and the ids, not the title.

## Actions

`launch`, `focus`, `find`, `invoke`, `set_value`, `click`, `type`, `key`, `scroll`, `select`, `toggle`, `drag`, `wait`, `sleep`, `close`, `human`.

Prefer `invoke` for a button and `set_value` for a field. `click` and `type` are the physical fallback.

## Selector

At least one constraint. Empty selectors are rejected.

`role`, `name`, `name_regex`, `automation_id`, `class_name`, `text`, `within`, `ancestor`, `index`, `window`, `hwnd`, `process`, `backend` (`uia` | `win32` | `cdp`), `exact`, `timeout_ms`, `policy` (`strict` | `first` | `best`).

`name` is a substring unless `exact` is true.

## Target

A target is either a selector object or a string reference: `"$0"`, `"$1"`, `"$doc"`.

`$0` is the output of step 0. `store` on `launch` and `find` also publishes `$name`.

## Wait

```json
{ "action": "wait", "until": "window", "selector": { "name": "Save As" }, "state": "exists", "timeout_ms": 8000 }
```

```json
{ "action": "wait", "until": "element", "selector": { "role": "button", "name": "Save" }, "state": "enabled" }
```

```json
{ "action": "wait", "until": "value", "target": "$doc", "equals": "Hello", "timeout_ms": 5000 }
```

```json
{ "action": "wait", "until": "process_exit", "name": "notepad.exe", "timeout_ms": 5000 }
```

Window states: `exists`, `visible`, `foreground`, `gone`. Element states: `exists`, `visible`, `enabled`, `gone`.

## Launch intent

`ephemeral`, `reusable`, `task_artifact`, `user_useful`, `unknown`.

Omitted is `unknown`. The runtime keeps it.

## Expect

Mutating steps accept `expect`.

```json
{ "action": "invoke", "target": "$save", "expect": { "stateVersion": 812 } }
```

If the world version moved, the step returns `StaleState` and does not act.
