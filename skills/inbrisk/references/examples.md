# Inbrisk Automation Examples

## 1. Launch Notepad and Write Text
```json
{
  "steps": [
    { "action": "launch", "app": "notepad" },
    { "action": "wait", "window": { "title": "Notepad" } },
    { "action": "set_value", "target": { "role": "document" }, "value": "INBRISK_NATIVE_FAST_PATH_OK" }
  ]
}
```

## 2. Inspect Window Tree
```bash
inbrisk-cli observe --window "Notepad" --depth 2 --json
```

## 3. Verify Fast Path
```bash
inbrisk-cli fast-path verify --json
```
