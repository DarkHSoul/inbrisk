"""End-to-end MCP latency benchmark for Inbrisk.

Real stdio JSON-RPC driver (newline-delimited, MCP stdio transport) — no
in-process shortcuts. For every tools/call it records client wall time, the
server-reported durationMs, and correlates with the server's own
perf-trace.jsonl / find-perf.jsonl records via file-offset slicing.

Usage:
  python tools/bench_latency.py [--exe PATH] [--warm 20] [--cold 5]
      [--tasks all|csv] [--report bench-report.json]
"""
import argparse, ctypes, datetime, json, os, re, statistics, subprocess, sys, threading, time
from ctypes import wintypes

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_EXE = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Debug",
    "net8.0-windows10.0.19041.0", "inbrisk.exe")
DATA = os.path.join(os.environ["LOCALAPPDATA"], "inbrisk")
PERF = os.path.join(DATA, "perf-trace.jsonl")
FIND = os.path.join(DATA, "find-perf.jsonl")


# ------------------------------------------------------------- MCP driver

_stderr_log = os.path.join(REPO, "tools", "bench-server-stderr.log")

class Server:
    def __init__(self, exe):
        # NOTE: do NOT override INBRISK_EMERGENCY_STATE — the panic-authority
        # mutex name is scoped by marker path, so a custom path leaves this
        # process neither owner nor peer → permanent EmergencyStopped.
        self._err = open(_stderr_log, "a", encoding="utf-8", errors="replace")
        self.proc = subprocess.Popen(
            [exe, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self._err, text=True, bufsize=1,
            encoding="utf-8", errors="replace")
        self._id = 0
        init = self.call("initialize", {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "bench", "version": "0.0"}})
        assert "result" in init, f"initialize failed: {init}"
        self.send("notifications/initialized", is_notif=True)
        self._wait_control_ready()

    def _wait_control_ready(self, timeout=8.0):
        """A fresh server may start EmergencyStopped while the previous
        process still holds the panic chord — EmergencyControl re-acquires
        authority within ~3s. Poll a trivial run until Active."""
        t0 = time.time()
        while time.time() - t0 < timeout:
            _, _, txt = self.run([{"action": "wait", "ms": 1}])
            try:
                if json.loads(txt).get("status") != "EmergencyStopped":
                    return
            except Exception:
                return
            time.sleep(0.3)

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

    def _recv(self, timeout=120):
        box = {}
        def rd():
            box["m"] = self.proc.stdout.readline()
        t = threading.Thread(target=rd, daemon=True)
        t.start(); t.join(timeout)
        if t.is_alive():
            raise TimeoutError("no response within %ss" % timeout)
        line = box.get("m", "")
        if line == "":
            raise EOFError("server stdout closed (process died?)")
        return json.loads(line)

    def call(self, method, params=None):
        rid = self.send(method, params)
        while True:
            m = self._recv()
            if m.get("id") == rid:
                return m

    def tool(self, name, args=None):
        """Returns (result_dict, wall_ms, text_payload)."""
        t0 = time.perf_counter()
        r = self.call("tools/call", {"name": name, "arguments": args or {}})
        wall = (time.perf_counter() - t0) * 1000
        res = r.get("result", {})
        texts = "\n".join(c.get("text", "") for c in res.get("content", [])
                          if c.get("type") == "text")
        return res, wall, texts

    def run(self, steps, run_id=None):
        args = {"steps": steps}
        if run_id:
            args["runId"] = run_id
        return self.tool("computer_run", args)

    def close(self):
        try:
            self.proc.stdin.close()
            self.proc.wait(timeout=5)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass
        try:
            self._err.close()
        except Exception:
            pass


# ------------------------------------------------------------- trace reader

def _offset(path):
    try:
        return os.path.getsize(path)
    except OSError:
        return 0

def _read_from(path, off):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(off)
            return [json.loads(l) for l in f if l.strip()]
    except OSError:
        return []

def read_new(off_perf, off_find):
    return _read_from(PERF, off_perf), _read_from(FIND, off_find)


# ------------------------------------------------------------- stats

def pct(xs, p):
    if not xs:
        return 0.0
    xs = sorted(xs)
    k = max(0, min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1)))))
    return xs[k]

def stat_block(xs):
    xs = [x for x in xs if x is not None]
    if not xs:
        return None
    return {"count": len(xs), "median": round(statistics.median(xs), 1),
            "p95": round(pct(xs, 95), 1), "max": round(max(xs), 1),
            "totalMs": round(sum(xs), 1)}


# --------------------------------------------------- off-primary parking
# The user keeps screen 1 (primary) reserved — windows the bench launches
# must live on a secondary monitor. Find a point outside the primary rect
# but inside the virtual screen, then SetWindowPos the target windows there.

_user32 = ctypes.windll.user32
_kernel32 = ctypes.windll.kernel32

def _off_primary_point():
    vx = _user32.GetSystemMetrics(76)   # SM_XVIRTUALSCREEN
    vy = _user32.GetSystemMetrics(77)   # SM_YVIRTUALSCREEN
    vw = _user32.GetSystemMetrics(78)   # SM_CXVIRTUALSCREEN
    vh = _user32.GetSystemMetrics(79)   # SM_CYVIRTUALSCREEN
    pw = _user32.GetSystemMetrics(0)    # SM_CXSCREEN (primary)
    ph = _user32.GetSystemMetrics(1)    # SM_CYSCREEN
    if vh > ph:   # a monitor extends below the primary
        return (vx + pw // 2, vy + ph + 60)
    if vw > pw:   # a monitor extends to the side
        return (vx + pw + 60, vy + 120)
    return None   # single display — nothing we can do

OFF_PRIMARY = _off_primary_point()

def _pids_for(procs):
    """pid set for the given exe-name prefixes (case-insensitive)."""
    out = subprocess.run(
        ["tasklist", "/fo", "csv", "/nh"], capture_output=True, text=True)
    pids = set()
    for line in out.stdout.splitlines():
        m = re.match(r'"([^"]+)","(\d+)"', line)
        if m and m.group(1).lower().rpartition(".")[0] in procs:
            pids.add(int(m.group(2)))
    return pids

def _top_windows(pids):
    """Visible, non-tool, top-level hwnds owned by the given pids."""
    found = []
    WNDENUM = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    pid = wintypes.DWORD()
    def cb(hwnd, _):
        if not _user32.IsWindowVisible(hwnd):
            return True
        if _user32.GetWindow(hwnd, 4):  # GW_OWNER → skip owned/tool windows
            return True
        ex = _user32.GetWindowLongW(hwnd, -20)  # GWL_EXSTYLE
        if ex & 0x80:  # WS_EX_TOOLWINDOW
            return True
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids:
            found.append(hwnd)
        return True
    _user32.EnumWindows(WNDENUM(cb), 0)
    return found

def park_offscreen(*procs, extra_hwnds=()):
    """Move every matching top-level window onto a secondary monitor."""
    if OFF_PRIMARY is None:
        return
    x, y = OFF_PRIMARY
    hwnds = set(extra_hwnds) | set(_top_windows(_pids_for(procs)))
    for h in hwnds:
        _user32.SetWindowPos(h, 0, x, y, 0, 0, 0x0001 | 0x0004)
        # SWP_NOSIZE | SWP_NOACTIVATE — never steal focus

def _window_hwnds(sv):
    """All hwnds listed by computer_windows (set of ints)."""
    _, _, wins = sv.tool("computer_windows")
    return {int(m, 16) for m in re.findall(r'0x([0-9A-Fa-f]+)', wins)}

def launch_parked(sv, step, procs):
    """Run a launch step alone, find the NEW top-level window by hwnd delta,
    park it off-primary, return its hwnd as a 0x-string (None if unseen).
    Only freshly-launched windows are moved — pre-existing user windows of
    the same process are left untouched."""
    before = _window_hwnds(sv)
    sv.run([step])
    after = _window_hwnds(sv)
    new = after - before
    if new:
        if OFF_PRIMARY is not None:
            x, y = OFF_PRIMARY
            for h in new:  # SWP_NOSIZE | SWP_NOACTIVATE — never steal focus
                _user32.SetWindowPos(h, 0, x, y, 0, 0, 0x0001 | 0x0004)
        return "0x%X" % sorted(new)[-1]
    # no new window — launch reused an existing one; park it only if it
    # currently sits on the primary screen
    for h in _top_windows(_pids_for(procs)):
        rc = wintypes.RECT()
        if (_user32.GetWindowRect(h, ctypes.byref(rc)) and
                OFF_PRIMARY is not None and
                rc.left < _user32.GetSystemMetrics(0) and
                rc.top < _user32.GetSystemMetrics(1)):
            _user32.SetWindowPos(h, 0, *OFF_PRIMARY, 0, 0, 0x0001 | 0x0004)
        return "0x%X" % h
    return None


# ------------------------------------------------------------- benchmark tasks

def t_notepad_launch(sv):
    return sv.run([
        {"action": "launch", "executable": "notepad.exe", "as": "win"},
    ])

def t_notepad_type_save(sv):
    """Notepad → open bench file → type → Ctrl+S (direct save, no dialog).
    Uses a dedicated bench file + within:<hwnd> scope so the user's own
    notepad tabs are never touched. The window is parked off-primary so
    the bench never lands a window or keystroke on screen 1."""
    d = os.path.join(os.environ["TEMP"], "inbrisk-bench")
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, "bench-doc.txt")
    win = launch_parked(sv,
        {"action": "launch", "executable": "notepad.exe",
         "arguments": [path], "as": "win"}, ("notepad",))
    scope = win or "$win"
    steps = []
    if win is None:  # hwnd unseen — keep the binding in the same run
        steps.append({"action": "launch", "executable": "notepad.exe",
                      "arguments": [path], "as": "win"})
    return sv.run(steps + [
        {"action": "type",
         "target": {"process": "notepad", "role": "document",
                    "within": scope},
         "text": "inbrisk latency benchmark line", "mode": "replace"},
        {"action": "key", "keys": "ctrl+s"},
        {"action": "wait", "ms": 300},
    ])

def t_explorer(sv):
    win = launch_parked(sv,
        {"action": "launch", "executable": "explorer.exe",
         "arguments": ["C:\\Windows"], "as": "win"}, ("explorer",))
    scope = win or "$win"
    steps = []
    if win is None:
        steps.append({"action": "launch", "executable": "explorer.exe",
                      "arguments": ["C:\\Windows"], "as": "win"})
    return sv.run(steps + [
        {"action": "find", "as": "folder",
         "target": {"process": "explorer", "role": "listitem",
                    "within": scope}},
        {"action": "invoke", "elementId": "$folder", "retry": 2},
    ])

def t_find_repeat(sv, n=20):
    """20 consecutive computer_find on the same window."""
    res = []
    for _ in range(n):
        r, w, txt = sv.tool("computer_find",
            {"process": "notepad", "role": "document"})
        res.append((r, w, txt))
    return res

def t_prop_repeat(sv, n=20):
    """One run: bind the document then re-read its state 20 times."""
    steps = [{"action": "find", "as": "doc",
              "target": {"process": "notepad", "role": "document"}}]
    steps += [{"action": "assert", "elementId": "$doc", "enabled": True}
              for _ in range(n)]
    return sv.run(steps)

def t_run_find20(sv, n=20):
    """One run: 20 find steps against the same window."""
    steps = [{"action": "find", "as": f"e{i}",
              "target": {"process": "notepad", "role": "document"}}
             for i in range(n)]
    return sv.run(steps)

def t_observe(sv):
    """Observe notepad's window explicitly — foreground-independent."""
    _, _, wins = sv.tool("computer_windows")
    m = re.search(r'0x([0-9A-Fa-f]+)[^\n]*notepad', wins, re.I)
    args = {"mode": "semantic"}
    if m:
        args["hwnd"] = "0x" + m.group(1)
    return sv.tool("computer_observe", args)

def t_wait_for(sv):
    """Existence-only query path (FindFirst fast path) on notepad."""
    return sv.run([
        {"action": "wait_for", "query": "düzenleyici", "ms": 5000,
         "target": {"process": "notepad"}},
    ])

def t_ifexists(sv):
    """run-step ifExists condition — pure existence check."""
    return sv.run([
        {"action": "find", "as": "doc",
         "target": {"process": "notepad", "role": "document"}},
        {"action": "assert", "elementId": "$doc",
         "ifExists": {"role": "document", "process": "notepad"}},
    ])

def t_spotify(sv):
    """Spotify: open → Liked Songs → first row → first artist."""
    win = launch_parked(sv,
        {"action": "launch", "uri": "spotify:", "as": "win",
         "waitFor": "window", "timeout": 20000}, ("spotify",))
    scope = win or "$win"
    steps = []
    if win is None:
        steps.append({"action": "launch", "uri": "spotify:", "as": "win",
                      "waitFor": "window", "timeout": 20000})
    return sv.run(steps + [
        {"action": "wait", "ms": 2500},
        {"action": "find", "as": "liked", "select": "first",
         "target": {"process": "spotify", "role": "group",
                    "name": "beğenilen", "within": scope}},
        {"action": "invoke", "elementId": "$liked", "retry": 2},
        {"action": "wait", "ms": 1500},
        {"action": "find", "as": "row1", "select": "first",
         "target": {"process": "spotify", "role": "listitem",
                    "within": scope}},
        {"action": "find", "as": "artist", "select": "first",
         "target": {"process": "spotify", "role": "link",
                    "within": scope}},
    ])

def t_chrome_find(sv):
    """Element-heavy page find — only when a Chrome/Electron window exists."""
    _, _, wins = sv.tool("computer_windows")
    for proc in ("chrome", "msedge", "Code", "Cursor", "Spotify"):
        if re.search(r'"\s+' + proc, wins, re.I):
            return sv.tool("computer_find", {"process": proc, "role": "button"})
    return None

TASKS = {
    "find.repeat20":  dict(kind="micro", fn=t_find_repeat, needs_notepad=True),
    "prop.repeat20":  dict(kind="micro", fn=t_prop_repeat, needs_notepad=True),
    "run.find20":     dict(kind="micro", fn=t_run_find20, needs_notepad=True),
    "observe":        dict(kind="micro", fn=t_observe, needs_notepad=True),
    "wait.for":       dict(kind="micro", fn=t_wait_for, needs_notepad=True),
    "ifexists":       dict(kind="micro", fn=t_ifexists, needs_notepad=True),
    "notepad.save":   dict(kind="scenario", fn=t_notepad_type_save),
    "explorer":       dict(kind="scenario", fn=t_explorer),
    "spotify":        dict(kind="scenario", fn=t_spotify),
    "chrome.find":    dict(kind="scenario", fn=t_chrome_find),
}


def ensure_notepad(sv):
    launch_parked(sv,
        {"action": "launch", "executable": "notepad.exe", "as": "win"},
        ("notepad",))


def run_rep(spec, sv):
    """One benchmark rep → dict with wall/stage/uia stats."""
    p0, f0 = _offset(PERF), _offset(FIND)
    t0 = time.perf_counter()
    try:
        out = spec["fn"](sv)
    except TimeoutError:
        return {"wallMs": None, "timeout": True}
    except EOFError:
        return {"wallMs": None, "dead": True}
    wall = (time.perf_counter() - t0) * 1000
    time.sleep(0.15)  # let the perf writer flush
    perf, find = read_new(p0, f0)
    if out is None:
        return {"skipped": True}
    walls = [w for _, w, _ in out] if isinstance(out, list) else [out[1]]
    stages, uia, counters = summarize_rep(perf, find)
    return {"wallMs": wall, "innerMs": walls, "stages": stages,
            "uia": uia, "counters": counters}


def summarize_rep(perf_events, find_events):
    """Aggregate server-side stage/uia stats for one rep."""
    stages = {}
    for ev in perf_events:
        for s in ev.get("stages") or []:
            a = stages.setdefault(s["name"], {"count": 0, "totalMs": 0.0,
                                              "exclMs": 0.0, "maxMs": 0.0})
            a["count"] += s["count"]
            a["totalMs"] += s["totalMs"]
            a["exclMs"] += s["exclMs"]
            a["maxMs"] = max(a["maxMs"], s["maxMs"])
    uia = {"finds": 0, "comCalls": 0, "elementsEnumerated": 0,
           "liveReads": 0, "cachedReads": 0, "desktopGlobal": 0,
           "enumMs": 0.0, "rootsMs": 0.0, "inspectCalls": 0}
    counters = {}
    for ev in find_events:
        if ev.get("kind") == "uia.find":
            uia["finds"] += 1
            uia["comCalls"] += ev.get("comCalls") or 0
            uia["elementsEnumerated"] += ev.get("candidatesEnumerated") or 0
            uia["liveReads"] += ev.get("crossProcessPropertyReads") or 0
            uia["cachedReads"] += ev.get("cachedReads") or 0
            uia["desktopGlobal"] += 1 if ev.get("desktopGlobal") else 0
            uia["enumMs"] += ev.get("enumMs") or 0
            uia["rootsMs"] += ev.get("rootsMs") or 0
        elif ev.get("kind") == "uia.inspect":
            uia["inspectCalls"] += 1
            uia["comCalls"] += ev.get("comCalls") or 0
            uia["elementsEnumerated"] += ev.get("elements") or 0
            uia["liveReads"] += ev.get("crossProcessPropertyReads") or 0
    for ev in perf_events:
        for k, v in (ev.get("counters") or {}).items():
            counters[k] = counters.get(k, 0) + v
    return stages, uia, counters


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("--warm", type=int, default=20)
    ap.add_argument("--cold", type=int, default=5)
    ap.add_argument("--tasks", default="all")
    ap.add_argument("--report", default=None)
    ap.add_argument("--label", default="run")
    a = ap.parse_args()

    wanted = list(TASKS) if a.tasks == "all" else a.tasks.split(",")
    report = {"label": a.label, "exe": a.exe,
              "at": datetime.datetime.now().isoformat(), "tasks": {}}
    print(f"[bench] exe={a.exe} warm={a.warm} cold={a.cold} tasks={wanted}")

    for name in wanted:
        spec = TASKS[name]
        task = {"cold": [], "warm": [], "skipped": None}
        print(f"\n=== {name} ===")

        # --- cold reps: fresh server process per rep ---
        for i in range(a.cold):
            sv = Server(a.exe)
            try:
                if spec.get("needs_notepad"):
                    ensure_notepad(sv)
                rep = run_rep(spec, sv)
                if rep.get("skipped"):
                    task["skipped"] = "prerequisite app not present"
                    break
                task["cold"].append(rep)
                print(f"  cold[{i}] wall={rep['wallMs'] and round(rep['wallMs'])}ms "
                      f"uia.finds={rep.get('uia',{}).get('finds')} "
                      f"com={rep.get('uia',{}).get('comCalls')}", flush=True)
            finally:
                sv.close()

        # --- warm reps: one session, one process ---
        if task["skipped"] is None:
            sv = Server(a.exe)
            try:
                if spec.get("needs_notepad"):
                    ensure_notepad(sv)
                    spec["fn"](sv)  # priming rep (excluded)
                for i in range(a.warm):
                    rep = run_rep(spec, sv)
                    if rep.get("skipped"):
                        task["skipped"] = "prerequisite app not present"
                        break
                    task["warm"].append(rep)
                    if i % 5 == 0:
                        print(f"  warm[{i}] wall={rep['wallMs'] and round(rep['wallMs'])}ms",
                              flush=True)
            finally:
                sv.close()

        task["coldStats"] = stat_block([r["wallMs"] for r in task["cold"]])
        task["warmStats"] = stat_block([r["wallMs"] for r in task["warm"]])
        report["tasks"][name] = task

    out_path = a.report or os.path.join(
        REPO, "tools", f"bench-report-{a.label}.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=1)
    print(f"\n[bench] report -> {out_path}")
    print_summary(report)

def print_summary(rep):
    print("\n%-16s %22s %22s" % ("task", "cold median/p95/max", "warm median/p95/max"))
    for name, t in rep["tasks"].items():
        if t.get("skipped"):
            print("%-16s  SKIPPED: %s" % (name, t["skipped"]))
            continue
        def f(s): return s and f"{s['median']}/{s['p95']}/{s['max']}"
        print("%-16s %22s %22s" % (name, f(t["coldStats"]), f(t["warmStats"])))

if __name__ == "__main__":
    main()
