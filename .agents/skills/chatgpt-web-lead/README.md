# chatgpt-web-lead

An [Agent Skill](https://agentskills.io) that pairs **ChatGPT Web** (lead / brain — no tool calls)
with **Antigravity** (sidekick / hands — real tools), connected through Windows UI automation.

No APIs, no SDKs, no token juggling — the courier script talks to the open ChatGPT tab and
the Antigravity desktop via [Inbrisk](https://github.com/) UI Automation.

## How it works

```
you -> Antigravity -> chatgpt_relay.py -> ChatGPT Web (thinks)
                                             |
            [ANTIGRAVITY_TASK] work order <--+
                |
                v
        Antigravity executes -> [ANTIGRAVITY_RESULT] -> back to ChatGPT (loop, max 4 hops)
```

The bridge is **invocation-based**: nothing runs until the skill is used — ask
Antigravity to use it explicitly (e.g. "lead'e sor", "chatgpt lead kullan",
"use chatgpt-web-lead"). For ordinary messages Antigravity answers directly;
there is no always-on mode and no flag files.

## Requirements

- Windows + [Inbrisk](https://github.com/) on PATH (`inbrisk` CLI)
- Python 3 (stdlib only)
- Google Antigravity IDE
- A `chatgpt.com` tab open in Chrome/Edge/Firefox, **logged in** — the relay drives the
  active conversation

> **Why Sohbet (chat) mode?** ChatGPT Web's chat mode serves **GPT-5.6** with
> near-unlimited usage on Plus and higher plans, so the bridge always operates
> there. `--send` automatically enforces it on both surfaces before relaying:
> the home-page **Sohbet | Çalışma** segment (toggles Sohbet when Çalışma is
> selected) and the sidebar **Modu değiştir** picker (switches back to ChatGPT
> when the app is in Codex mode). You don't need to manage it.

## Install

Clone into your Antigravity skills dir:

```
git clone https://github.com/DarkHSoul/chatgpt-web-lead "%USERPROFILE%\.gemini\antigravity\skills\chatgpt-web-lead"
```

or per-workspace: `<workspace>/.agents/skills/chatgpt-web-lead/`.

## Usage

1. Open a `chatgpt.com` conversation in your browser (or let `--send` auto-launch it).
   Optionally paste `assets/chatgpt_lead_instructions.txt` as the first message so the
   lead knows the protocol.
2. In Antigravity, invoke the skill — e.g. "use chatgpt-web-lead: <your request>" or
   "lead'e sor: ...". Antigravity relays to ChatGPT Web and executes its work orders.
3. For manual bootstrapping paste `assets/antigravity_sidekick_instructions.txt` into the
   Antigravity conversation (equivalent of the skill, inline).

## Message protocol

| Marker | Direction | Meaning |
|---|---|---|
| `[ANTIGRAVITY_TASK]...[/ANTIGRAVITY_TASK]` | lead → sidekick | execute this work order |
| `[ANTIGRAVITY_RESULT]...[/ANTIGRAVITY_RESULT]` | sidekick → lead | execution report |
| `[ANTIGRAVITY_QUESTION]...[/ANTIGRAVITY_QUESTION]` | sidekick → lead | clarification request |
| `[USER_QUESTION]...[/USER_QUESTION]` | lead → user | relayed question |

## Relay CLI

```
python scripts/chatgpt_relay.py --status    # diagnostics
python scripts/chatgpt_relay.py --send "…"  # send + wait, prints reply on stdout
flags: --timeout N  --new  --pid N  --no-launch  --file path.txt
```

`--send` prints **only** the ChatGPT reply on stdout; diagnostics go to stderr.
If no ChatGPT tab exists, `--send` auto-opens `chatgpt.com` in the default
browser and waits ~30s for it (disable with `--no-launch`).

Exit codes: `0` ok · `4` no ChatGPT tab · `5` reply timeout ·
`6` relay error · `7` login wall (sign in to chatgpt.com, then retry).
