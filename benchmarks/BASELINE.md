# C# Baseline — 2026-10-06

Machine-readable data: `baseline_2026-10-06.json`. Harness: `tools/bench/bench_csharp_baseline.py`, corpus: `tools/bench/bench_tasks.py`, UIA probe: `tools/bench/bench_uia_probe.py`.

## Environment

- OS: Windows 11 10.0.26200 · Python 3.12.10 · machine DESKTOP-LMFAO
- Serving: `dotnet src\Inbrisk.Cli\bin\Release\net8.0-windows10.0.19041.0\inbrisk.dll mcp` — the apphost `inbrisk.exe` was denied LocalAppData writes under the agent sandbox ("untrusted image"); identical managed assemblies hosted via `dotnet`. **Note for future runs: re-measure with the real apphost outside the sandbox.**
- Server: `inbrisk 0.5.0+4271b3f4…`, protocol `2025-03-26`, in-process server (no named-pipe daemon present at bench time)
- An installed C# instance (`%LOCALAPPDATA%\Programs\Inbrisk\inbrisk.exe`) was running; measurements used a fresh in-process server, not the daemon.
- Rust-installed processes (`inbrisk-bridge.exe`, `inbrisk-desktop.exe` from `%LOCALAPPDATA%\inbrisk\bin\`) were also running on this machine — separate install dir, unaffected by the source freeze.

## Startup / footprint

| Metric | Value |
|---|---|
| Cold start (spawn → initialize result), n=5 | p50 **497 ms** (min 491.5, p95 501.7) |
| Working set after warm run (dotnet host) | **321 MB** |
| tools/list — full profile | 54 tools, **115,455 B** |
| tools/list — ambient (dynamic toolset) | recorded in JSON (`toolsListAmbient`) |
| Server instructions block | 2,593 B |

## Per-tool latency (n=15 wall ms unless noted)

| Tool call | p50 ms | p95 ms | resp bytes | image bytes |
|---|---|---|---|---|
| computer_app_status | 0.5 | 0.7 | 363 | — |
| computer_windows | 0.6 | 1.1 | 1,633 | — |
| computer_find (button, window scope) | 0.8 | 0.9 | 1,553 | — |
| computer_observe (semantic, window) | 28.7 | 33.7 | ~7,472 | — |
| computer_observe (semantic, desktop) | 30.9 | 36.0 | ~7,155 | — |
| computer_screenshot (desktop), n=8 | 67.5 | 70.6 | ~321,474 | **223,209 PNG** |
| computer_screenshot (window), n=8 | (in JSON) | | | 27,084 PNG |
| tools/list | 1.9 | 2.3 | 71,197 | — |
| **computer_launch (notepad, existing)** | **13,029** | 13,039 | 1,018 | — |
| bench notepad fresh launch → window | **13,622** | — | — | — |

## UIA COM-call counts (from `find-perf.jsonl` offset-sliced — see `uiaProbe` in JSON)

| Probe | comCalls | cachedReads | elements enumerated |
|---|---|---|---|
| computer_observe desktop ×3 | 54 | 0 / 356 (inspect) | 27 |
| per-call attribution for find/act | partial — shared-log slicing under-attributes | | |

COM counts are best-effort from the shared perf log; dedicated per-call counters would need instrumentation (deferred to Phase 2).

## Task corpus — ALL 6 FAILED (baseline incomplete)

| Task | Status | Wall | Notes |
|---|---|---|---|
| notepad | FAIL | 43.8 s | 7 calls completed, then transport hang >30 s |
| calculator_uwp | FAIL | 30.0 s | transport: no response |
| edge_cdp | FAIL | 31.2 s | transport: no response |
| explorer | FAIL | 30.0 s | transport: no response |
| vscode | FAIL | 30.0 s | transport: no response |
| custom_app (Inbrisk.TestApp WPF) | FAIL | 30.0 s | transport: no response |

Causes (per `tasksNote` + run artifacts):
- `explorer`/`code.exe` are **ProtectedProcesses in user settings** → `computer_launch` may be policy-denied — corpus must handle Deny outcomes.
- The MCP transport hung (no response within 30 s) mid-corpus; the bench agent was investigating a server hang (`hang_probe` scratch scripts) when an agent action **closed the Devin IDE host window**, killing the session. Leftover bench windows/processes were cleaned up manually.
- The corpus run must be re-executed after the hang root cause is understood (candidate: `computer_launch` wait path — a single notepad launch measures ~13 s, and ProtectedProcess-denied launches may wedge longer).

## Headline findings for Phase 2

1. **`computer_launch` ≈ 13 s** — the dominant measured latency by ~400×. Suspects: window-readiness wait, initialMap build, AppPath resolution. Phase 2 item C5.
2. **Screenshot payload ≈ 223 KB PNG / 321 KB response** per desktop shot — the token-cost driver; no client `maxWidth` exists (F17 residual).
3. **tools/list ≈ 71–115 KB** schema payload — every session pays it; dynamic toolsets already shrink it (ambient list recorded).
4. Semantic observe/find calls are already fast (sub-30 ms) — the latency problem is concentrated in launch and screenshots, not UIA reads.
