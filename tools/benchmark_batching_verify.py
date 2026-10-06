"""Batching verification bench — REAL notepad, REAL MCP stdio server.

Phase-B verification harness for the action-batching refactor. Drives
`dotnet inbrisk.dll mcp` over newline-delimited JSON-RPC, against a notepad
window this script itself launches (and a uniquely-titled console window it
spawns for the until/event-wake test).

Isolation from the user's live inbrisk instance (mirrors McpSelfTest):
  INBRISK_PANIC_HOTKEY  = Ctrl+Alt+F12      (never fights the real chord)
  INBRISK_RESUME_HOTKEY = Ctrl+Alt+Shift+F12
  INBRISK_EMERGENCY_STATE = <temp flag>     (never sees/prod the real marker)
  INBRISK_DATA_DIR      = <temp dir>        (private perf-trace.jsonl,
                                             settings, element recipes)
Note: UiaPerf (find-perf.jsonl) is HARDCODED to %LOCALAPPDATA%\\inbrisk —
it stays shared with the live instance, so those counters are sliced by
file offset + timestamped window and flagged as best-effort.

Output: benchmarks/batching_verification.json + console summary table.
"""
import ctypes
import json
import os
import platform
import random
import re
import statistics
import subprocess
import sys
import tempfile
import threading
import time
from ctypes import wintypes

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DLL = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.dll")
SHARED_DATA = os.path.join(os.environ["LOCALAPPDATA"], "inbrisk")
FINDLOG = os.path.join(SHARED_DATA, "find-perf.jsonl")  # UiaPerf hardcodes this
TESTROOT = tempfile.mkdtemp(prefix="inbrisk-batchverify-")
DATADIR = os.path.join(TESTROOT, "data")
EMERGFLAG = os.path.join(TESTROOT, "emergency.flag")
PERFLOG = os.path.join(DATADIR, "perf-trace.jsonl")     # PerfLog honours DATA_DIR
OUT = os.path.join(REPO, "benchmarks", "batching_verification.json")
ERRLOG = os.path.join(REPO, "artifacts", "batch-verify-stderr.log")
CALL_TIMEOUT = 60

ENV = {
    "INBRISK_PANIC_HOTKEY": "Ctrl+Alt+F12",
    "INBRISK_RESUME_HOTKEY": "Ctrl+Alt+Shift+F12",
    "INBRISK_EMERGENCY_STATE": EMERGFLAG,
    "INBRISK_DATA_DIR": DATADIR,
    "INBRISK_TOOL_PROFILE": "full",
}

# ------------------------------------------------------------- MCP driver

class Server:
    """Stdio MCP server: `dotnet inbrisk.dll mcp` with isolated env."""

    def __init__(self, extra_env=None):
        os.makedirs(os.path.dirname(ERRLOG), exist_ok=True)
        os.makedirs(DATADIR, exist_ok=True)
        self._err = open(ERRLOG, "a", encoding="utf-8", errors="replace")
        env = dict(os.environ)
        env.update(ENV)
        if extra_env:
            env.update(extra_env)
        self.proc = subprocess.Popen(
            ["dotnet", DLL, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self._err, text=True, bufsize=1, encoding="utf-8",
            errors="replace", env=env)
        self._id = 0
        self._pending = {}   # id -> decoded response line (interleaved-safe)
        self.spawn_ts = time.perf_counter()
        init = self.call("initialize", {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "batching-verify", "version": "0.1"}})
        self.init_ms = (time.perf_counter() - self.spawn_ts) * 1000
        self.init_result = init.get("result", {})
        assert "result" in init, f"initialize failed: {init}"
        self.send("notifications/initialized", is_notif=True)

    def send(self, method, params=None, is_notif=False):
        self._id += 1
        msg = {"jsonrpc": "2.0", "method": method}
        if not is_notif:
            msg["id"] = self._id
        if params is not None:
            msg["params"] = params
        self.proc.stdin.write(json.dumps(msg) + "\n")
        self.proc.stdin.flush()
        return self._id

    def _recv(self, timeout=CALL_TIMEOUT):
        box = {}
        def rd():
            box["m"] = self.proc.stdout.readline()
        t = threading.Thread(target=rd, daemon=True)
        t.start(); t.join(timeout)
        if t.is_alive():
            raise TimeoutError(f"no response within {timeout}s")
        line = box.get("m", "")
        if line == "":
            raise EOFError("server stdout closed")
        return line

    def _await_id(self, rid, timeout=CALL_TIMEOUT):
        """Read lines until the response for rid arrives; stash others."""
        t_dead = time.perf_counter() + timeout
        while True:
            if rid in self._pending:
                return self._pending.pop(rid)
            line = self._recv(max(1, t_dead - time.perf_counter()))
            m = json.loads(line)
            if m.get("id") is not None and "method" not in m:
                self._pending[m["id"]] = (m, len(line.encode("utf-8", "replace")))
            # notifications (no id, or has method) are dropped on the floor

    def call(self, method, params=None):
        rid = self.send(method, params)
        m, rb = self._await_id(rid)
        m["_lineBytes"] = rb
        return m

    def tool(self, name, args=None, timeout=CALL_TIMEOUT):
        """Send a tools/call and capture request+response wire bytes.
        Returns dict(text, isError, wallMs, reqBytes, respBytes, raw)."""
        self._id += 1
        rid = self._id
        msg = {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
               "params": {"name": name, "arguments": args or {}}}
        wire = json.dumps(msg)
        req_bytes = len(wire.encode("utf-8"))
        t0 = time.perf_counter()
        self.proc.stdin.write(wire + "\n")
        self.proc.stdin.flush()
        m, resp_bytes = self._await_id(rid, timeout)
        wall = (time.perf_counter() - t0) * 1000
        res = m.get("result", {})
        text = ""
        for c in res.get("content", []):
            if c.get("type") == "text":
                text += c.get("text", "")
        return {"text": text, "isError": bool(res.get("isError")),
                "wallMs": round(wall, 1), "reqBytes": req_bytes,
                "respBytes": resp_bytes, "raw": res}

    def close(self):
        """Polite stdin-EOF, then kill OUR spawned pid only."""
        try:
            self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.wait(timeout=4)
        except Exception:
            pass
        if self.proc.poll() is None:
            try:
                self.proc.kill()
                self.proc.wait(timeout=5)
            except Exception:
                pass
        for f in (self.proc.stdout, self._err):
            try:
                f.close()
            except Exception:
                pass

# ------------------------------------------------------------- log slicing

def _offset(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0

def _read_from(path, off):
    out = []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(off)
            for line in f:
                if not line.strip():
                    continue
                try:
                    out.append(json.loads(line))
                except Exception:
                    continue
    except OSError:
        pass
    return out

def first_id(text):
    m = re.search(r"\[(uia_\d+_\d+)\]", text)
    return m.group(1) if m else None

def hwnd_from_text(text):
    m = re.search(r'"hwnd"\s*:\s*"(0x[0-9A-Fa-f]+)"', text)
    return m.group(1) if m else None

def jparse(text):
    try:
        return json.loads(text)
    except Exception:
        return None

# ------------------------------------------------------------- own windows

_user32 = ctypes.windll.user32

def spawn_marker_console(title):
    """Spawn MY OWN console window titled <title> (ping keeps it alive)."""
    return subprocess.Popen(
        ["cmd", "/c", f"title {title} && ping -n 60 127.0.0.1 >nul"],
        creationflags=subprocess.CREATE_NEW_CONSOLE)

def find_hwnd_by_title(substr, timeout=8.0):
    """EnumWindows for a top-level window whose title contains substr."""
    found = []
    WNDENUM = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    buf = ctypes.create_unicode_buffer(512)
    def cb(hwnd, _):
        if not _user32.IsWindowVisible(hwnd):
            return True
        n = _user32.GetWindowTextW(hwnd, buf, 512)
        if n > 0 and substr.lower() in buf.value.lower():
            found.append(hwnd)
        return True
    deadline = time.time() + timeout
    while time.time() < deadline:
        found.clear()
        _user32.EnumWindows(WNDENUM(cb), 0)
        if found:
            return found[0]
        time.sleep(0.25)
    return None

def wm_close(hwnd):
    """WM_CLOSE — graceful, only ever sent to windows this script spawned."""
    _user32.PostMessageW(hwnd, 0x0010, 0, 0)

def type_unicode(text, delay=0.03):
    """SendInput KEYEVENTF_UNICODE — real keystrokes into the FOREGROUND
    window. Only ever used while MY notepad is focused.
    sizeof(INPUT) must be 40 on x64 (type+pad+32B union) or SendInput
    silently returns 0."""
    INPUT_KEYBOARD = 1
    KEYEVENTF_UNICODE = 0x0004
    KEYEVENTF_KEYUP = 0x0002
    class KEYBDINPUT(ctypes.Structure):
        _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD),
                    ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                    ("dwExtraInfo", ctypes.c_size_t)]  # ULONG_PTR
    class INPUT_UNION(ctypes.Union):
        _fields_ = [("ki", KEYBDINPUT), ("_pad", ctypes.c_byte * 32)]
    class INPUT(ctypes.Structure):
        _fields_ = [("type", wintypes.DWORD), ("u", INPUT_UNION)]
    assert ctypes.sizeof(INPUT) == 40, ctypes.sizeof(INPUT)
    sent_total = 0
    for ch in text:
        code = ord(ch)
        for flags in (KEYEVENTF_UNICODE, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP):
            inp = INPUT(type=INPUT_KEYBOARD)
            inp.u.ki = KEYBDINPUT(0, code, flags, 0, 0)
            sent_total += _user32.SendInput(
                1, ctypes.byref(inp), ctypes.sizeof(inp))
        time.sleep(delay)
    return sent_total

def esc(sv, n=1):
    for _ in range(n):
        sv.tool("computer_key", {"key": "escape"})
        time.sleep(0.25)

def probe_ok(sv, el):
    """Cheap element-targeted probe — returns (ok, errorText)."""
    r = sv.tool("computer_click", {"elementId": el})
    return (not r["isError"], r["text"][:200])

# ------------------------------------------------------------- main

def main():
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    rnd = random.randint(10000, 99999)
    R = {"label": "batching-verification",
         "date": time.strftime("%Y-%m-%d %H:%M:%S"),
         "env": {"os": platform.platform(), "python": platform.python_version(),
                 "machine": platform.node(),
                 "servingCommand": f"dotnet {DLL} mcp",
                 "isolation": ENV},
         "items": {}, "notes": []}
    items = R["items"]

    # =============== item 10: cold start (fresh spawn -> init -> app_status)
    print("[10] cold start x4 ...")
    cold = []
    for i in range(4):
        sv = Server()
        t0 = time.perf_counter()
        r = sv.tool("computer_app_status")
        app_ms = (time.perf_counter() - t0) * 1000
        cold.append({"initMs": round(sv.init_ms, 1),
                     "firstAppStatusMs": round(app_ms, 1),
                     "appStatusOk": '"running": true' in r["text"]})
        sv.close()
        time.sleep(0.4)
    items["cold_start"] = {
        "samples": cold,
        "spawnToInitialize": {"min": min(c["initMs"] for c in cold),
                              "p50": round(statistics.median(c["initMs"] for c in cold), 1)},
        "firstAppStatus": {"min": min(c["firstAppStatusMs"] for c in cold),
                           "p50": round(statistics.median(c["firstAppStatusMs"] for c in cold), 1)},
        "baselineNote": "recent baseline ~561-650ms for first computer_app_status"}

    # =============== warm session
    print("[*] spawning warm session server ...")
    sv = Server()
    try:
        st = sv.tool("computer_app_status")
        items["warm_server"] = {"initMs": round(sv.init_ms, 1),
                                "appStatus": st["text"][:400],
                                "serverInfo": sv.init_result.get("serverInfo")}
        if "EmergencyStopped" in st["text"]:
            R["notes"].append("server started EmergencyStopped — isolated "
                              "marker/hotkeys should prevent this; RESULTS SUSPECT")
            time.sleep(4)

        # ---- launch MY notepad ------------------------------------------
        print("[setup] launching notepad ...")
        lr = sv.tool("computer_launch",
                     {"executable": "notepad.exe", "newInstance": True,
                      "waitFor": "window", "timeoutMs": 20000})
        launch_json = jparse(lr["text"]) or {}
        hwnd = hwnd_from_text(lr["text"])
        items["setup_launch"] = {"wallMs": lr["wallMs"], "isError": lr["isError"],
                                 "launchState": launch_json.get("launchState"),
                                 "hwnd": hwnd, "respBytes": lr["respBytes"]}
        if not hwnd:
            R["notes"].append("notepad launch returned no hwnd — aborting")
            raise RuntimeError("no hwnd")
        hwnd_i = int(hwnd, 16)

        # pre-existing notepads (for map-selector isolation decision)
        wins = sv.tool("computer_windows")
        other_notepads = [l for l in wins["text"].splitlines()
                          if "notepad" in l.lower() and hwnd.lower() not in l.lower()]
        items["setup_launch"]["otherNotepadWindows"] = other_notepads

        # ---- find document element --------------------------------------
        print("[setup] finding document element ...")
        fr = sv.tool("computer_find",
                     {"hwnd": hwnd, "role": "document", "detail": "full"})
        el = first_id(fr["text"])
        doc_name = None
        m_name = re.search(r"\[uia_\d+_\d+\]\s+\w+\s+\"([^\"]+)\"", fr["text"])
        if m_name:
            doc_name = m_name.group(1)
        if not el:
            fr = sv.tool("computer_find", {"hwnd": hwnd, "role": "edit",
                                           "detail": "full"})
            el = first_id(fr["text"])
        items["setup_find_doc"] = {"elementId": el, "docName": doc_name,
                                   "wallMs": fr["wallMs"],
                                   "head": fr["text"][:300]}
        if not el:
            R["notes"].append("no document/edit element in notepad — "
                              "element-scoped items skipped")

        # ---- dismiss any notepad popup/flyout before measured actions ----
        # (a Notepad "PopupHost" flyout appeared during the first run and
        # InputRejected every element-targeted action — probe + Escape)
        for _try in range(4):
            ok, err = probe_ok(sv, el)
            if ok:
                break
            if "PopupHost" in err or "BLOCKED" in err or "popup" in err.lower():
                esc(sv)
            else:
                R["notes"].append(f"probe failed non-popup: {err[:120]}")
                time.sleep(0.5)
        items["popup_dismissals"] = {"probeOk": ok, "tries": _try + 1,
                                     "lastErr": None if ok else err}

        # =============== item 5: until wake (runs BEFORE mutations) ========
        # The until wait is scoped to the run's active hwnd (ActionResolver
        # maps wait_for -> WaitCondition.Hwnd = ctx.ActiveHwnd) — so the
        # trigger must fire INSIDE my notepad, not a foreign window. Design:
        # batch = [focus my notepad] + until{appears:<doc name>, value:
        # <marker>} — the doc matches the name immediately but its value
        # lacks the marker, so the wait parks; then this script types the
        # marker into the (focused) notepad via SendInput mid-wait. The UIA
        # property/structure event on my notepad's hwnd should produce
        # wakeSource "event" (poll = 200ms fallback cadence).
        # NOTE: this runs before item 1's set_value — a content change can
        # rename the Notepad document element, making a stale name query
        # unmatchable (observed: title became "*alias####" after set_value).
        print("[5] until event-wake ...")
        marker = f"wakemark{rnd}"
        # refresh the doc name right before the wait — it may have drifted
        df = sv.tool("computer_find", {"hwnd": hwnd, "role": "document"})
        until_doc_name = doc_name
        m_dn = re.search(r'"name"\s*:\s*"([^"]+)"', df["text"])
        if m_dn:
            until_doc_name = m_dn.group(1)
        until_args = {"steps": [{"do": "focus", "t": hwnd}],
                      "until": {"appears": until_doc_name, "value": marker,
                                "timeoutMs": 12000},
                      "detail": "slim"}
        if not until_doc_name:
            until_args = {"steps": [{"do": "wait", "ms": 300}],
                          "until": {"stable": True, "timeoutMs": 4000},
                          "detail": "slim"}
            R["notes"].append("doc element had no name — until test fell "
                              "back to stable (no event-wake assertion)")
        off = _offset(FINDLOG)
        sv_id = sv.send("tools/call", {"name": "computer_batch",
                                       "arguments": until_args})
        time.sleep(0.9)   # focus step done; until loop parked on first check
        fg_before = None; fg_after = None; sent = 0
        if until_doc_name:
            fg_before = _user32.GetForegroundWindow()
            sent = type_unicode(marker)   # into MY notepad (it is foreground)
            time.sleep(0.4)
            fg_after = _user32.GetForegroundWindow()
        m_resp, m_bytes = sv._await_id(sv_id, timeout=30)
        m_text = ""
        for c in m_resp.get("result", {}).get("content", []):
            if c.get("type") == "text":
                m_text += c.get("text", "")
        until_res = jparse(m_text) or {}
        time.sleep(0.3)
        until_evs = [e for e in _read_from(FINDLOG, off)
                     if e.get("kind") in ("batch.until", "batch.summary")]
        # diagnostic: is the name still matching / did the value land?
        diag = sv.tool("computer_find", {"hwnd": hwnd, "role": "document",
                                       "detail": "full"})
        items["until_wake"] = {
            "marker": marker,
            "docNameUsed": until_doc_name,
            "sendInputSent": sent,
            "fgHwndBeforeTyping": (f"0x{fg_before:X}" if fg_before else None),
            "fgHwndAfterTyping": (f"0x{fg_after:X}" if fg_after else None),
            "myHwnd": hwnd,
            "diagDocFind": diag["text"][:600],
            "untilBlock": until_res.get("until"),
            "success": until_res.get("success"),
            "status": until_res.get("status"),
            "results": until_res.get("results"),
            "batchUntilEvents": until_evs}

        # =============== item 1: sequential vs batch (same task) ===========
        # task: focus window -> click doc -> set_value marker -> press End.
        # set_value (ValuePattern) is used rather than SendInput type because
        # the executor's GuardFocus refuses keystrokes when the target window
        # cannot be raised (SetForegroundWindow fails for background callers).
        seq_text = f"SEQ{rnd} "
        bat_text = f"BAT{rnd} "
        print("[1] sequential 4-call ...")
        seq_calls = [
            ("computer_focus_window", {"hwnd": hwnd}),
            ("computer_click", {"elementId": el}),
            ("computer_set_value", {"elementId": el, "value": seq_text}),
            ("computer_key", {"key": "End"}),
        ]
        seq = {"calls": [], "roundTrips": 0, "wallTotalMs": 0.0,
               "reqBytes": 0, "respBytes": 0}
        for name, args in seq_calls:
            r = sv.tool(name, args)
            seq["calls"].append({"tool": name, "wallMs": r["wallMs"],
                                 "reqBytes": r["reqBytes"],
                                 "respBytes": r["respBytes"],
                                 "isError": r["isError"],
                                 "head": r["text"][:160]})
            seq["roundTrips"] += 1
            seq["wallTotalMs"] += r["wallMs"]
            seq["reqBytes"] += r["reqBytes"]
            seq["respBytes"] += r["respBytes"]
        seq["wallTotalMs"] = round(seq["wallTotalMs"], 1)

        print("[1] batched equivalent ...")
        b_steps = [{"do": "focus", "t": hwnd},
                   {"do": "click", "t": el},
                   {"do": "set_value", "t": el, "v": bat_text},
                   {"do": "key", "keys": "End"}]
        t0 = time.perf_counter()
        br = sv.tool("computer_batch", {"steps": b_steps, "detail": "slim"})
        bj = jparse(br["text"]) or {}
        bat = {"roundTrips": 1, "wallTotalMs": br["wallMs"],
               "reqBytes": br["reqBytes"], "respBytes": br["respBytes"],
               "isError": br["isError"], "result": bj}
        # secondary probe: does a SendInput `type` step succeed once the
        # batch itself raised the window? (GuardFocus path)
        ty = sv.tool("computer_batch", {"steps": [
            {"do": "focus", "t": hwnd},
            {"do": "type", "t": el, "v": f"TYP{rnd} "}]})
        tyj = jparse(ty["text"]) or {}
        bat["typeProbe"] = {"ok": tyj.get("success"),
                            "results": tyj.get("results")}
        items["seq_vs_batch"] = {
            "task": "focus+click+set_value marker+End key, same notepad elementId",
            "sequential": seq, "batch": bat,
            "speedup": round(seq["wallTotalMs"] / max(0.1, bat["wallTotalMs"]), 2),
            "wireBytes": {"seq": seq["reqBytes"] + seq["respBytes"],
                          "batch": bat["reqBytes"] + bat["respBytes"]},
            "batchStepOk": [r.get("ok") for r in bj.get("results", [])]}

        # item 2: compactness = batch resp bytes vs sum of single resp bytes
        items["compactness"] = {
            "batchRespBytes": bat["respBytes"],
            "seqRespBytesSum": seq["respBytes"],
            "deltaPct": round((bat["respBytes"] - seq["respBytes"])
                              / max(1, seq["respBytes"]) * 100, 1),
            "batchReqBytes": bat["reqBytes"],
            "seqReqBytesSum": seq["reqBytes"]}

        # =============== item 3: re-resolve pool ==========================
        print("[3] re-resolve pool ...")
        pool = {"warmClicks": [], "forcedReResolve": [], "verdict": None}
        # two clicks on same elementId — second should be a live-map hit
        # (NO reresolve event) unless something invalidated it
        off = _offset(FINDLOG)
        for i in range(2):
            t0 = time.perf_counter()
            cr = sv.tool("computer_click", {"elementId": el})
            pool["warmClicks"].append({"click": i + 1, "wallMs": cr["wallMs"],
                                       "isError": cr["isError"],
                                       "head": cr["text"][:120]})
            time.sleep(0.15)
        time.sleep(0.4)
        evs = [e for e in _read_from(FINDLOG, off)
               if e.get("kind") == "uia.reresolve"
               and e.get("elementId") == el]
        pool["warmClickReResolveEvents"] = evs

        # forced re-resolve: computer_inspect{elementId} always re-resolves
        # (InbriskTools.cs:2494); set_value's PostVerify re-resolves too
        # (Executor.cs:514). Vary args so the snapshot cache cannot absorb
        # the re-resolve. pool outcome + poolMs is what matters.
        probe_calls = [
            ("computer_inspect", {"elementId": el, "detail": "slim"}),
            ("computer_inspect", {"elementId": el, "detail": "full"}),
            ("computer_inspect", {"elementId": el, "detail": "slim",
                                  "relational": True}),
            ("computer_set_value", {"elementId": el,
                                    "value": f"P{rnd} pool probe"}),
        ]
        for probe, (tname, targs) in enumerate(probe_calls):
            off = _offset(FINDLOG)
            r = sv.tool(tname, targs)
            time.sleep(0.4)
            evs = [e for e in _read_from(FINDLOG, off)
                   if e.get("kind") == "uia.reresolve"]
            mine = [e for e in evs
                    if e.get("elementId") in (el,)
                    or True]  # any reresolve in the window is ours to report
            pool["forcedReResolve"].append({
                "probe": probe, "toolMs": r["wallMs"], "isError": r["isError"],
                "events": [{"elementId": e.get("elementId"),
                            "resolved": e.get("resolved"),
                            "pool": e.get("pool"),
                            "poolMs": e.get("poolMs"),
                            "uiaTimeMs": e.get("uiaTimeMs"),
                            "liveReads": e.get("crossProcessPropertyReads"),
                            "cachedReads": e.get("cachedReads")}
                           for e in mine]})
        hits = [e["pool"] for pr in pool["forcedReResolve"]
                for e in pr["events"] if str(e.get("pool", "")).startswith("hit")]
        hit_ms = [e["poolMs"] for pr in pool["forcedReResolve"]
                  for e in pr["events"]
                  if str(e.get("pool", "")).startswith("hit")]
        pool["verdict"] = {
            "hits": hits,
            "poolHitMs": hit_ms,
            "under5ms": all(m is not None and m < 5 for m in hit_ms) if hit_ms else None}
        items["reresolve_pool"] = pool

        # =============== item 4: observe prune ============================
        print("[4] observe slim + prune ...")
        off = _offset(PERFLOG)
        ob = sv.tool("computer_observe",
                     {"hwnd": hwnd, "detail": "slim", "mode": "semantic"})
        m_ph = re.search(r"(\d+) passive hidden", ob["text"])
        m_el = re.search(r"elements \((\d+) of (\d+) shown", ob["text"])
        time.sleep(0.4)
        prune_evs = [e for e in _read_from(PERFLOG, off)
                     if e.get("kind") == "observation.prune"]
        items["observe_prune"] = {
            "wallMs": ob["wallMs"], "respBytes": ob["respBytes"],
            "headerPassiveHidden": int(m_ph.group(1)) if m_ph else None,
            "headerShown": int(m_el.group(1)) if m_el else None,
            "headerTotal": int(m_el.group(2)) if m_el else None,
            "perfTracePath": PERFLOG,
            "pruneEvents": prune_evs[-3:] if prune_evs else [],
            "note": "perf-trace.jsonl is private (INBRISK_DATA_DIR) — no "
                    "cross-talk with the live instance"}

        # =============== item 6: fail_fast ================================
        print("[6] fail_fast ...")
        esc(sv)  # clear any popup a prior step raised
        ok, err = probe_ok(sv, el)
        if not ok:
            esc(sv, 2)
        ff_marker = f"FF{rnd}MARK"
        ff = sv.tool("computer_batch", {"steps": [
            {"do": "set_value", "t": el, "v": "ff-pre "},
            {"do": "click", "t": f"__nonexistent_el_{rnd}__"},
            {"do": "set_value", "t": el, "v": ff_marker},
        ], "detail": "slim"})
        ffj = jparse(ff["text"]) or {}
        # read doc BEFORE the failFast:false run — marker must be absent
        rd1 = sv.tool("computer_read", {"targets": [el], "props": ["value"]})
        marker_after_ff = ff_marker in rd1["text"]
        nff = sv.tool("computer_batch", {"steps": [
            {"do": "click", "t": f"__nonexistent_el_{rnd}__"},
            {"do": "set_value", "t": el, "v": ff_marker},
        ], "failFast": False, "detail": "slim"})
        nffj = jparse(nff["text"]) or {}
        time.sleep(0.3)
        rd = sv.tool("computer_read", {"targets": [el], "props": ["value"]})
        doc_val = rd["text"]
        items["fail_fast"] = {
            "failFastTrue": {"success": ffj.get("success"),
                             "status": ffj.get("status"),
                             "failedStep": ffj.get("failedStep"),
                             "stepsExecuted": ffj.get("stepsExecuted"),
                             "totalSteps": ffj.get("totalSteps"),
                             "results": ffj.get("results"),
                             "isError": ff["isError"]},
            "failFastFalse": {"success": nffj.get("success"),
                              "status": nffj.get("status"),
                              "failedStep": nffj.get("failedStep"),
                              "stepsExecuted": nffj.get("stepsExecuted"),
                              "results": nffj.get("results"),
                              "isError": nff["isError"]},
            "markerInDocAfterFailFastRun": marker_after_ff,
            "markerInDocAfter": ff_marker in doc_val,
            "docValueTail": doc_val[-260:]}

        # =============== item 7: strict validation + aliases ==============
        print("[7] strict validation + aliases ...")
        bogus = sv.tool("computer_batch",
                        {"steps": [{"do": "click", "bogusField": 1}]})
        alias_wait = sv.tool("computer_batch",
                             {"steps": [{"action": "wait", "waitFor": 15}]})
        esc(sv)  # popup guard before mutating aliases
        # `text` is an alias of `v` — exercise it on set_value (UIA-native;
        # `type` needs the window raisable and is covered by the type probe)
        alias_type = sv.tool("computer_batch",
                             {"steps": [{"action": "set_value",
                                         "target": el,
                                         "text": f"alias{rnd}"}]})
        alias_target_obj = sv.tool("computer_batch",
                                   {"steps": [{"action": "click",
                                               "target": {"elementId": el}}]})
        items["strict_validation"] = {
            "bogusField": {"isError": bogus["isError"],
                           "names_bogusField": "bogusField" in bogus["text"],
                           "malformed": "Malformed" in bogus["text"],
                           "text": bogus["text"][:300]},
            "alias_action_waitFor": {
                "isError": alias_wait["isError"],
                "ok": (jparse(alias_wait["text"]) or {}).get("success")},
            "alias_text": {
                "isError": alias_type["isError"],
                "ok": (jparse(alias_type["text"]) or {}).get("success")},
            "alias_target_object": {
                "isError": alias_target_obj["isError"],
                "ok": (jparse(alias_target_obj["text"]) or {}).get("success")}}

        # =============== item 8: map selector =============================
        print("[8] map selector ...")
        # hwnd merged explicitly — explicit fields win over map defaults and
        # scope the resolve to MY notepad even if the user has one open
        esc(sv)  # popup guard
        mr = sv.tool("computer_batch", {"steps": [
            {"do": "click", "target": {"map": "notepad.document",
                                       "hwnd": hwnd}}]})
        mrj = jparse(mr["text"]) or {}
        items["map_selector"] = {
            "scoped": {"success": mrj.get("success"),
                       "results": mrj.get("results"),
                       "isError": mr["isError"], "text": mr["text"][:300]}}
        if not other_notepads:
            mr2 = sv.tool("computer_batch", {"steps": [
                {"do": "click", "target": {"map": "notepad.document"}}]})
            mr2j = jparse(mr2["text"]) or {}
            items["map_selector"]["unscoped"] = {
                "success": mr2j.get("success"),
                "results": mr2j.get("results"), "isError": mr2["isError"]}

        # =============== item 9: UIA CacheRequest counters ================
        print("[9] UIA cache-request counters ...")
        # uncached find on my notepad (valueContains defeats the find cache
        # only when it changes the spec key — use distinct existing-text
        # prefixes so the query still matches but the key differs)
        uia_meas = []
        for probe_args in (
                {"hwnd": hwnd, "role": "document", "detail": "full"},
                {"hwnd": hwnd, "role": "document", "detail": "full",
                 "enabled": True},
                {"hwnd": hwnd, "detail": "full", "limit": 60}):
            off = _offset(FINDLOG)
            t0 = time.perf_counter()
            r = sv.tool("computer_find", probe_args)
            dt = (time.perf_counter() - t0) * 1000
            time.sleep(0.4)
            evs = [e for e in _read_from(FINDLOG, off)
                   if e.get("kind") == "uia.find"]
            tot = {"finds": len(evs),
                   "liveReads": sum(e.get("crossProcessPropertyReads") or 0
                                    for e in evs),
                   "cachedReads": sum(e.get("cachedReads") or 0 for e in evs),
                   "enumerated": sum(e.get("candidatesEnumerated") or 0
                                     for e in evs),
                   "comCalls": sum(e.get("comCalls") or 0 for e in evs),
                   "cacheRequests": sum(e.get("cacheRequests") or 0
                                        for e in evs)}
            uia_meas.append({"args": probe_args, "wallMs": r["wallMs"],
                             "totals": tot,
                             "events": [{"scopeType": e.get("scopeType"),
                                         "method": e.get("method"),
                                         "enumerated": e.get(
                                             "candidatesEnumerated"),
                                         "converted": e.get(
                                             "candidatesConverted"),
                                         "live": e.get(
                                             "crossProcessPropertyReads"),
                                         "cached": e.get("cachedReads"),
                                         "comCalls": e.get("comCalls"),
                                         "cacheProps": e.get(
                                             "cacheRequestProps")}
                                        for e in evs]})
        # scoped inspect for uia.inspect counters
        off = _offset(FINDLOG)
        ir = sv.tool("computer_inspect", {"hwnd": hwnd, "detail": "full"})
        time.sleep(0.4)
        ievs = [e for e in _read_from(FINDLOG, off)
                if e.get("kind") == "uia.inspect"]
        items["uia_cache"] = {
            "finds": uia_meas,
            "inspect": {"wallMs": ir["wallMs"],
                        "events": [{"elements": e.get("elements"),
                                    "live": e.get("crossProcessPropertyReads"),
                                    "cached": e.get("cachedReads"),
                                    "comCalls": e.get("comCalls"),
                                    "cacheProps": e.get("cacheRequestProps")}
                                   for e in ievs]},
            "baselineNote": ("pre-CacheRequest window scan did ~2 live reads "
                             "per element (name+pid per window, per-element "
                             "sig reads). Shared log — may include live-"
                             "instance traffic; sliced by offset+time.")}

        # =============== cleanup ==========================================
        print("[cleanup] closing my notepad ...")
        cw = sv.tool("computer_close_window", {"hwnd": hwnd})
        time.sleep(0.8)
        still = sv.tool("computer_windows")
        items["cleanup"] = {"closeResp": cw["text"][:200],
                            "windowStillListed": hwnd.lower() in
                            still["text"].lower()}
        if items["cleanup"]["windowStillListed"]:
            # possible save prompt on classic notepad — my window only:
            # try the "Don't save" button by common names, then re-close
            for nm in ("Don't Save", "Kaydetme", "Don't save"):
                db = sv.tool("computer_find",
                             {"hwnd": hwnd, "name": nm})
                bid = first_id(db["text"])
                if bid:
                    sv.tool("computer_click", {"elementId": bid})
                    time.sleep(0.5)
                    break
            still2 = sv.tool("computer_windows")
            items["cleanup"]["windowStillListedAfterDialog"] = (
                hwnd.lower() in still2["text"].lower())
    finally:
        sv.close()

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(R, f, indent=2)
    print(f"\n[bench] wrote {OUT}")

    # ------------------------------------------------ summary table -------
    print("\n" + "=" * 78)
    print("BATCHING VERIFICATION SUMMARY")
    print("=" * 78)
    svb = items.get("seq_vs_batch", {})
    if svb:
        print(f"1. seq-vs-batch   rounds {svb['sequential']['roundTrips']}->"
              f"{svb['batch']['roundTrips']}  wall "
              f"{svb['sequential']['wallTotalMs']}ms -> "
              f"{svb['batch']['wallTotalMs']}ms  speedup "
              f"{svb['speedup']}x  wire "
              f"{svb['wireBytes']['seq']}B -> {svb['wireBytes']['batch']}B")
        print(f"   batch step ok flags: {svb.get('batchStepOk')}")
    c = items.get("compactness", {})
    if c:
        print(f"2. compactness    resp {c['seqRespBytesSum']}B -> "
              f"{c['batchRespBytes']}B ({c['deltaPct']:+.1f}%)  req "
              f"{c['seqReqBytesSum']}B -> {c['batchReqBytes']}B")
    p = items.get("reresolve_pool", {})
    if p:
        v = p.get("verdict") or {}
        print(f"3. reresolve pool warm-click events: "
              f"{len(p.get('warmClickReResolveEvents') or [])}  "
              f"forced resolves: {v.get('hits')}  poolMs={v.get('poolHitMs')} "
              f"<5ms={v.get('under5ms')}")
    op = items.get("observe_prune", {})
    if op:
        print(f"4. observe prune  passive hidden={op.get('headerPassiveHidden')} "
              f"shown={op.get('headerShown')}/{op.get('headerTotal')} "
              f"pruneEvents={len(op.get('pruneEvents') or [])}")
    uw = items.get("until_wake", {})
    if uw:
        print(f"5. until wake     success={uw.get('success')} "
              f"until={uw.get('untilBlock')}")
    ff = items.get("fail_fast", {})
    if ff:
        print(f"6. fail_fast      ff:true -> status={ff['failFastTrue'].get('status')} "
              f"failedStep={ff['failFastTrue'].get('failedStep')} "
              f"executed={ff['failFastTrue'].get('stepsExecuted')}/{ff['failFastTrue'].get('totalSteps')}  "
              f"ff:false -> status={ff['failFastFalse'].get('status')} "
              f"executed={ff['failFastFalse'].get('stepsExecuted')}  "
              f"markerAfterFF={ff.get('markerInDocAfterFailFastRun')} "
              f"markerFinal={ff.get('markerInDocAfter')}")
    stv = items.get("strict_validation", {})
    if stv:
        print(f"7. strict+alias   bogus named={stv['bogusField'].get('names_bogusField')} "
              f"malformed={stv['bogusField'].get('malformed')}  "
              f"wait alias ok={stv['alias_action_waitFor'].get('ok')}  "
              f"text alias ok={stv['alias_text'].get('ok')}  "
              f"target-obj ok={stv['alias_target_object'].get('ok')}")
    ms = items.get("map_selector", {})
    if ms:
        print(f"8. map selector   scoped success={ms.get('scoped', {}).get('success')} "
              f"unscoped={ms.get('unscoped')}")
    uc = items.get("uia_cache", {})
    if uc:
        for fm in uc.get("finds", []):
            print(f"9. uia.find {str(fm['args'])[:46]:46} "
                  f"live={fm['totals']['liveReads']} "
                  f"cached={fm['totals']['cachedReads']} "
                  f"enum={fm['totals']['enumerated']} "
                  f"com={fm['totals']['comCalls']}")
        for ev in (uc.get("inspect", {}).get("events") or []):
            print(f"   uia.inspect elements={ev.get('elements')} "
                  f"live={ev.get('live')} cached={ev.get('cached')} "
                  f"com={ev.get('comCalls')}")
    cs = items.get("cold_start", {})
    if cs:
        print(f"10. cold start    init p50={cs['spawnToInitialize']['p50']}ms "
              f"first-app-status p50={cs['firstAppStatus']['p50']}ms "
              f"(baseline ~561-650ms)")
    cl = items.get("cleanup", {})
    if cl:
        print(f"    cleanup       notepad closed={not cl.get('windowStillListed')} "
              f"({cl.get('closeResp', '')[:80]})")

if __name__ == "__main__":
    main()
