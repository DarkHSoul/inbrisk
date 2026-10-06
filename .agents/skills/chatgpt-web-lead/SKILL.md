---
name: chatgpt-web-lead
description: Use ONLY when the user explicitly asks to route a request through ChatGPT Web as the lead brain (e.g. "lead'e sor", "chatgpt lead kullan", "bridge ile yap", "use chatgpt-web-lead"). Antigravity acts as the sidekick (hands), relays the request to ChatGPT Web (lead, no tools) via the relay script, executes any [ANTIGRAVITY_TASK] work orders, and loops results back. Do NOT activate for ordinary requests — there is no always-on mode.
---

# ChatGPT Web (Lead) <-> Antigravity (Sidekick) Bridge

ChatGPT Web is the LEAD ARCHITECT — it thinks and plans but has NO tool calls.
You (Antigravity) are the SIDEKICK — you execute with real tools.

The bridge activates **only when this skill is invoked** — either by the user
naming it or by you deciding the request explicitly asks for the lead pipeline.
No flag files, no toggles, nothing runs between invocations.

The relay courier script ships inside this skill package:
`<SKILL_DIR>\scripts\chatgpt_relay.py` where `<SKILL_DIR>` is the directory
containing this SKILL.md.

## Step 1 — Think, then prompt the lead

Read the user's message. Think briefly, then compose a clean, self-contained prompt for ChatGPT: restate the goal, include relevant context (file paths, errors, constraints), and ask for the decision/plan.

- If YOU cannot understand the user's intent, still relay — phrase it as `[ANTIGRAVITY_QUESTION] ...your clarifying question about the user request... [/ANTIGRAVITY_QUESTION]` so the lead can help you interpret or ask the user back.
- Send with: `python "<SKILL_DIR>\scripts\chatgpt_relay.py" --send "<prompt>"`
- For long/multiline prompts pipe via stdin: `type tmp.txt | python "<SKILL_DIR>\scripts\chatgpt_relay.py" --send`
- The script prints ONLY ChatGPT's reply on stdout (exit 0). Non-zero = relay failure (see Step 4).

## Step 2 — Act on the lead's reply

- Reply contains `[ANTIGRAVITY_TASK] ... [/ANTIGRAVITY_TASK]` → this is a work order. Execute it with your tools (files, shell, MCP, UI automation). Then send the outcome back:
  `python "<SKILL_DIR>\scripts\chatgpt_relay.py" --send "[ANTIGRAVITY_RESULT]\n<what you did + verification evidence>\n[/ANTIGRAVITY_RESULT]"`
  and treat the NEW stdout reply as the next lead instruction. Loop — max 4 hops per user turn.
- Reply contains `[USER_QUESTION] ... [/USER_QUESTION]` → relay that question to the user verbatim and end your turn (the user's answer goes through the bridge again next turn if the user invokes it).
- Plain reply with no markers → present it to the user prefixed with `[ChatGPT Lead]:`.

## Step 3 — Hop limit

If you hit 4 task/result hops and the lead still emits `[ANTIGRAVITY_TASK]`, stop, summarize progress so far to the user, and end the turn. The user can continue the loop with a new message.

## Step 4 — Relay failure

If `--send` exits non-zero: report the stderr reason once to the user, note the lead is unreachable, then answer the user yourself as a fallback.

- Exit `7` = **login wall** — the ChatGPT tab is signed out. Tell the user to log in to chatgpt.com, then retry the turn.
- Exit `4` = no ChatGPT tab found and auto-launch failed or was disabled (`--no-launch`).
- Exit `5` = reply timeout; exit `6` = other relay error.

Note: `--send` auto-launches chatgpt.com in the default browser when no ChatGPT tab exists (waits ~30s), and enforces **Sohbet (chat) mode** before sending — both the home-page `Sohbet | Çalışma` segment and the sidebar `Modu değiştir` (Codex) picker are corrected automatically.

## Rules

- Never invent ChatGPT replies; always call the script.
- Keep Antigravity focused on execution; ChatGPT Web does reasoning/planning only.
- The bridge runs in **Sohbet (chat) mode** on purpose: ChatGPT Web's chat mode serves GPT-5.6 with near-unlimited usage on Plus and higher plans — this is a deliberate choice for the lead role. The relay enforces it automatically; do not switch the tab to Codex/work mode.
- Never auto-activate: if the user did not ask for the lead pipeline, answer normally without calling the relay.
