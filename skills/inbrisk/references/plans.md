# computer_run — multi-step plans (C# MCP)

`computer_run` executes an ordered `steps` plan in ONE call. Steps resolve
lazily, so a step can act on UI created by an earlier step. First failure
pauses the run — resume with the returned `runId` (bindings preserved).

## Canonical flow

1. One `computer_run` per task segment — not per action.
2. `find{as:"x"}` to bind elements; later steps use `elementId:"$x"`.
3. Read the per-step `delta` in the run report instead of re-observing.
4. On pause/failure: inspect `error` + `availableElements`, fix, resume
   via `computer_resume_run{runId}` (or `computer_run{runId}`).

## Example: open Notepad, type, save

```json
{"steps": [
  {"action":"launch","app":"notepad","waitFor":"window","timeoutMs":15000},
  {"action":"find","as":"doc","target":{"process":"notepad","role":"document"}},
  {"action":"set_value","elementId":"$doc","text":"hello"},
  {"action":"hotkey","keys":"ctrl+s"},
  {"action":"wait","ms":500}
]}
```

## Iterating a list server-side

```json
{"steps": [
  {"action":"scan","target":{"role":"listitem","within":"$list"},
   "as":"item","maxItems":50,
   "steps":[{"action":"click","elementId":"$item.id"}],
   "collect":["$item.name"]}
]}
```

## Conditional skips

`ifExists`/`ifNotExists`/`ifEnabled`/`ifValue` skip a step without failing —
use them for "only click if the dialog exists" logic.

## Do NOT

- Don't chain N single-tool calls when a plan expresses the same flow.
- Don't re-observe after every step — the `delta` reports what changed.
- Don't use raw coordinates when an `elementId`/semantic target exists.
