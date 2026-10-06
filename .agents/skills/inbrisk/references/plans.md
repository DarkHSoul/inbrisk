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
  {"action":"launch","app":"notepad","waitFor":"window","timeout":15000},
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

## UI-map selectors (`map:`)

Any `target` may reference the permanent UI map instead of hand-written
selectors: `{"target":{"map":"app.element"}}`, or `map:app.element` inside a
string target (e.g. computer_batch `t`, computer_find `name`). Covers
notepad/calculator/explorer/taskmgr/mspaint — e.g. `map:notepad.document`,
`map:calculator.equals`. Explicit target fields override map defaults;
entries may be `verify`-flagged, so fall back to observe if one misses.

## Silent steps & `map:` in waits

- Per-step `silent:true` on `click`/`type`/`invoke`/`key`/`hotkey` (and
  `silent` on `computer_batch` steps) uses background WM messages — no
  focus theft. Unsupported kinds return `NotSupported`, never a SendInput
  fallback; all safety gates still apply.
- `map:` selectors also work inside `wait_for`/`wait_for_gone` queries:
  `{"action":"wait_for","query":{"map":"notepad.status"},"ms":5000}`.
- `save_as_recipe:"name"` on `computer_run` (also `computer_batch`/
  `computer_do`) auto-saves a successful run; dynamic values
  (urls/paths/typed text) become `{{param}}` — replay via
  `computer_run_recipe{name, params:{…}}`.

## Conditional skips

`ifExists`/`ifNotExists`/`ifEnabled`/`ifValue` skip a step without failing —
use them for "only click if the dialog exists" logic.

## Reflex Engine — modal dialogs mid-run

`enableReflex` (default **true**) on `computer_run`/`computer_batch`/
`computer_do` watches for modal dialogs while the plan executes and
dismisses recognized ones in the background via WM messages (no focus
steal): update prompts, save confirmations, error dialogs, cookie
banners. The plan then resumes automatically.

`autoDismissModals` controls the disposition:

| Value | Behavior |
|---|---|
| `"closeOnly"` (default) | Cancel/close only — save prompts are cancelled, **never committed** |
| `"save"` | Save prompts click Save/Kaydet |
| `"discard"` | Save prompts click Don't Save/Kaydetme — **only** when the task explicitly discards the work |
| `"off"` | Reflex disabled; modals block the run as before |

UAC/credential/security dialogs and unknown modals are never clicked —
the run aborts with `error:"InterruptedByDialog"` plus the modal's
title/buttons. The result carries `reflex:{modals:[{title,action,…}],
aborted}` so you can see exactly what was dismissed or what stopped the
run. On `InterruptedByDialog`: read the modal details, then surface to
the user or replan — don't retry blindly (the same dialog will re-fire).

## Do NOT

- Don't chain N single-tool calls when a plan expresses the same flow —
  this is a hard rule (see SKILL.md "Batch-First contract"). When the
  sequence is simple and linear with no conditions/bindings,
  `computer_batch` (compact `do`/`t`/`v` steps, or `set` for forms) is
  the lighter-weight alternative to `computer_run`.
- Don't re-observe after every step — the `delta` reports what changed.
- Don't use raw coordinates when an `elementId`/semantic target exists.
- Don't set `autoDismissModals:"discard"` unless the task explicitly
  discards the work — it clicks Don't Save/Kaydetme for real.
