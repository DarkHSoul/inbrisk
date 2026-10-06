# Inbrisk Plan Execution Reference

Inbrisk plans allow executing multi-step desktop automation without chat round-trips.

## Plan Schema

A plan consists of an array of `steps`:

```json
{
  "steps": [
    { "action": "launch", "app": "notepad" },
    { "action": "wait", "condition": { "window": "Notepad" }, "timeout_ms": 5000 },
    { "action": "set_value", "target": { "role": "document" }, "value": "INBRISK_NATIVE_TEST" }
  ]
}
```

## Step Types

### 1. `launch`
Launches an application by binary name or full path.
```json
{ "action": "launch", "app": "notepad" }
```

### 2. `focus`
Focuses the target window.
```json
{ "action": "focus", "target": { "window": "Notepad" } }
```

### 3. `set_value`
Sets the text value of an input field or text area via UIA ValuePattern (cheaper than typing).
```json
{ "action": "set_value", "target": { "role": "document" }, "value": "INBRISK_NATIVE_TEST" }
```

### 4. `type`
Types characters into the currently focused element.
```json
{ "action": "type", "text": "Hello world" }
```

### 5. `click`
Invokes or clicks an element.
```json
{ "action": "click", "target": { "role": "button", "name": "File" } }
```

### 6. `key`
Sends a keyboard shortcut.
```json
{ "action": "key", "key": "s", "modifiers": ["ctrl"] }
```

### 7. `sleep`
Pauses execution for a specified duration in milliseconds.
```json
{ "action": "sleep", "ms": 500 }
```

### 8. `close`
Requests a window to close.
```json
{ "action": "close", "target": { "window": "Notepad" } }
```
