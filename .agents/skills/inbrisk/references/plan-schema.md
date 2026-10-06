# Inbrisk Plan Schema

Plans submitted to `inbrisk-cli run --stdin --json` accept either:
1. Bare plan: `{"steps": [...]}`
2. Wire form: `{"op": "run", "steps": [...]}`
3. Wrapped plan: `{"plan": {"steps": [...]}}`

## Step Types
- `launch`: `{"action": "launch", "app": "notepad", "args": []}`
- `focus`: `{"action": "focus", "target": {"window": {"name": "Notepad"}}}`
- `set_value`: `{"action": "set_value", "target": {"role": "document"}, "value": "text"}`
- `click`: `{"action": "click", "target": {"role": "button", "name": "OK"}}`
- `type`: `{"action": "type", "text": "sample"}`
- `key`: `{"action": "key", "keys": ["ctrl", "s"]}` (keys is an array; single key: `["enter"]`)
- `sleep`: `{"action": "sleep", "ms": 200}`
- `wait`: `{"action": "wait", "window": {"process": "Notepad.exe"}}`
- `close`: `{"action": "close", "target": {"window": {"name": "Notepad"}}}`
