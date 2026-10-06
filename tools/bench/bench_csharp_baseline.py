"""C# inbrisk baseline benchmark — stdio JSON-RPC MCP driver.

NOTE on this machine's sandbox: `inbrisk.exe` (untrusted image) is denied
write access to %LOCALAPPDATA%\\inbrisk (telemetry sink ctor throws → every
tools/call fails). `dotnet.exe` is allowlisted, so the server runs as
    dotnet src\\Inbrisk.Cli\\bin\\Release\\net8.0-windows10.0.19041.0\\inbrisk.dll mcp
— the exact same managed assemblies as the single-file publish exe
(apphost shim only differs). serverInfo/version + installMode recorded.

Per tool: N>=15 warm iterations, wall-ms, response bytes, image bytes.
Read tools are measured twice: `cached` (identical args — exercises the
read dedup layer) and `uncached` (args varied per call → real UIA work).
Cold start = spawn → initialize result (in-process server; no named-pipe
daemon is running on this machine — verified via \\.\\pipe\\ listing, so the
proxy/daemon path is not exercised).

Output: benchmarks/baseline_2026-10-06.json  (machine-readable)
"""
import ctypes, datetime, json, os, platform, re, statistics, subprocess, sys, threading, time
from ctypes import wintypes

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
DLL = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.dll")
EXE = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.exe")
PUBLISH_EXE = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
    "net8.0-windows10.0.19041.0", "win-x64", "publish", "inbrisk.exe")
DATA = os.path.join(os.environ["LOCALAPPDATA"], "inbrisk")
PERF = os.path.join(DATA, "perf-trace.jsonl")
FIND = os.path.join(DATA, "find-perf.jsonl")
BENCH_DIR = os.path.join(REPO, "benchmarks")
ERRLOG = os.path.join(REPO, "artifacts", "bench-csharp-stderr.log")
ITERS = 15
CALL_TIMEOUT = 30
BENCH_TITLE_HINTS = ("inbrisk-bench", "bench-observe.txt", "bench-doc.txt")

# ------------------------------------------------------------- MCP driver

class Server:
    """Stdio MCP server: `dotnet <inbrisk.dll> mcp`."""
    def __init__(self, dll=DLL, extra_env=None):
        os.makedirs(os.path.dirname(ERRLOG), exist_ok=True)
        self._err = open(ERRLOG, "a", encoding="utf-8", errors="replace")
        env = dict(os.environ)
        if extra_env:
            env.update(extra_env)
        self.proc = subprocess.Popen(
            ["dotnet", dll, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self._err, text=True, bufsize=1, encoding="utf-8",
            errors="replace", env=env)
        self._id = 0
        self.spawn_ts = time.perf_counter()
        init = self.call("initialize", {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "bench-csharp-baseline", "version": "0.1"}})
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
            raise TimeoutError("no response within %ss" % timeout)
        line = box.get("m", "")
        if line == "":
            raise EOFError("server stdout closed")
        return line

    def call(self, method, params=None):
        rid = self.send(method, params)
        while True:
            line = self._recv()
            m = json.loads(line)
            if m.get("id") == rid:
                m["_lineBytes"] = len(line.encode("utf-8", "replace"))
                return m

    def tool(self, name, args=None):
        """(result_dict, wall_ms, text, resp_bytes, image_bytes, n_images, is_error)"""
        t0 = time.perf_counter()
        r = self.call("tools/call", {"name": name, "arguments": args or {}})
        wall = (time.perf_counter() - t0) * 1000
        res = r.get("result", {})
        texts, img_b, n_img = "", 0, 0
        for c in res.get("content", []):
            if c.get("type") == "text":
                texts += c.get("text", "")
            elif c.get("type") == "image":
                n_img += 1
                img_b += int(len(c.get("data", "")) * 3 / 4)
        return res, wall, texts, r.get("_lineBytes", 0), img_b, n_img, bool(res.get("isError"))

    def close(self):
        """Guarantee the spawned `dotnet inbrisk.dll mcp` process terminates:
        polite stdin-EOF first, then kill OUR spawned pid (allowed — we
        spawned it this run) and reap it so a wedged server cannot leak a
        live process or hold cross-process locks (physical_input.lock etc.)."""
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
        try:
            self.proc.stdout.close()
        except Exception:
            pass
        try:
            self._err.close()
        except Exception:
            pass

# ------------------------------------------------------------- off-primary parking

_user32 = ctypes.windll.user32

def _off_primary_point():
    vx = _user32.GetSystemMetrics(76); vy = _user32.GetSystemMetrics(77)
    vw = _user32.GetSystemMetrics(78); vh = _user32.GetSystemMetrics(79)
    pw = _user32.GetSystemMetrics(0);  ph = _user32.GetSystemMetrics(1)
    if vh > ph: return (vx + pw // 2, vy + ph + 60)
    if vw > pw: return (vx + pw + 60, vy + 120)
    return None

OFF_PRIMARY = _off_primary_point()

def _pids_for(procs):
    out = subprocess.run(["tasklist", "/fo", "csv", "/nh"],
                         capture_output=True, text=True)
    pids = set()
    for line in out.stdout.splitlines():
        m = re.match(r'"([^"]+)","(\d+)"', line)
        if m and m.group(1).lower().rpartition(".")[0] in procs:
            pids.add(int(m.group(2)))
    return pids

def _top_windows(pids):
    found = []
    WNDENUM = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    pid = wintypes.DWORD()
    def cb(hwnd, _):
        if not _user32.IsWindowVisible(hwnd): return True
        if _user32.GetWindow(hwnd, 4): return True
        if _user32.GetWindowLongW(hwnd, -20) & 0x80: return True
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids: found.append(hwnd)
        return True
    _user32.EnumWindows(WNDENUM(cb), 0)
    return found

def park_offscreen(*procs, extra_hwnds=()):
    if OFF_PRIMARY is None: return
    x, y = OFF_PRIMARY
    for h in set(extra_hwnds) | set(_top_windows(_pids_for(procs))):
        _user32.SetWindowPos(h, 0, x, y, 0, 0, 0x0001 | 0x0004)

def window_hwnds(sv):
    _, _, wins, *_ = sv.tool("computer_windows")
    return {int(m, 16) for m in re.findall(r'0x([0-9A-Fa-f]+)', wins)}

def hwnd_from_result(txt):
    m = re.search(r'"hwnd"\s*:\s*"(0x[0-9A-Fa-f]+)"', txt)
    return m.group(1) if m else None

def close_bench_notepads(sv):
    """Close any notepad window whose title matches a bench file."""
    _, _, wins, *_ = sv.tool("computer_windows")
    for line in wins.splitlines():
        m = re.match(r'\s*(0x[0-9A-Fa-f]+)\s+"([^"]*)"', line)
        if m and any(h.lower() in m.group(2).lower() for h in BENCH_TITLE_HINTS):
            # never force — force escalates to Process.Kill (incident: force
            # close of a diff-captured hwnd killed the IDE host window)
            sv.tool("computer_close_window", {"hwnd": m.group(1)})

# ------------------------------------------------------------- perf-trace slicing
# perf-trace.jsonl / find-perf.jsonl are shared with the user's live
# inbrisk instance — counts below may include its traffic. Best-effort.

def _offset(path):
    try: return os.path.getsize(path)
    except OSError: return 0

def _read_from(path, off):
    out = []
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            f.seek(off)
            for l in f:
                if not l.strip(): continue
                try: out.append(json.loads(l))
                except Exception: continue
    except OSError:
        pass
    return out

def uia_stats(find_events):
    u = {"finds": 0, "comCalls": 0, "elementsEnumerated": 0,
         "liveReads": 0, "cachedReads": 0, "inspectCalls": 0}
    for ev in find_events:
        k = ev.get("kind")
        if k == "uia.find":
            u["finds"] += 1
            u["comCalls"] += ev.get("comCalls") or 0
            u["elementsEnumerated"] += ev.get("candidatesEnumerated") or 0
            u["liveReads"] += ev.get("crossProcessPropertyReads") or 0
            u["cachedReads"] += ev.get("cachedReads") or 0
        elif k == "uia.inspect":
            u["inspectCalls"] += 1
            u["comCalls"] += ev.get("comCalls") or 0
            u["elementsEnumerated"] += ev.get("elements") or 0
            u["liveReads"] += ev.get("crossProcessPropertyReads") or 0
    return u

# ------------------------------------------------------------- stats

def pct(xs, p):
    if not xs: return 0.0
    xs = sorted(xs)
    k = max(0, min(len(xs) - 1, int(round(p / 100 * (len(xs) - 1)))))
    return xs[k]

def stat_block(xs):
    xs = [x for x in xs if x is not None]
    if not xs: return None
    return {"n": len(xs), "min": round(min(xs), 1), "p50": round(pct(xs, 50), 1),
            "p95": round(pct(xs, 95), 1), "max": round(max(xs), 1)}

# ------------------------------------------------------------- measurements

def measure_tool(sv, name, args=None, iters=ITERS, warmups=3, vary=None):
    """vary(i,args) mutates a copy of args per call → defeats read dedup."""
    recs = []
    p0, f0 = _offset(PERF), _offset(FIND)
    for i in range(iters + warmups):
        a = dict(args or {})
        if vary:
            vary(i, a)
        try:
            res, wall, txt, rb, ib, nimg, iserr = sv.tool(name, a)
            rec = {"wallMs": round(wall, 1), "respBytes": rb,
                   "imageBytes": ib, "images": nimg, "isError": iserr}
            if iserr:
                rec["error"] = txt[:200]
            if i >= warmups:
                recs.append(rec)
        except (TimeoutError, EOFError) as e:
            if i >= warmups:
                recs.append({"wallMs": None, "error": str(e)[:200]})
    time.sleep(0.2)
    find_evs = _read_from(FIND, f0)
    return {
        "iters": len(recs),
        "wall": stat_block([r["wallMs"] for r in recs]),
        "respBytes": stat_block([r["respBytes"] for r in recs if r.get("respBytes")]),
        "imageBytes": stat_block([r["imageBytes"] for r in recs if r.get("imageBytes")]),
        "imagesTotal": sum(r.get("images", 0) for r in recs),
        "errors": [r.get("error") for r in recs if r.get("error")],
        "uiaTotalsSharedLog": uia_stats(find_evs),
    }

def measure_rpc(sv, method="tools/list", iters=ITERS, warmups=2):
    recs = []
    for i in range(iters + warmups):
        t0 = time.perf_counter()
        r = sv.call(method)
        wall = (time.perf_counter() - t0) * 1000
        if i >= warmups:
            recs.append({"wallMs": round(wall, 1), "respBytes": r["_lineBytes"]})
    return {"iters": len(recs),
            "wall": stat_block([r["wallMs"] for r in recs]),
            "respBytes": stat_block([r["respBytes"] for r in recs])}

def working_set_bytes(pid):
    r = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"(Get-CimInstance Win32_Process -Filter 'ProcessId={pid}').WorkingSetSize"],
        capture_output=True, text=True, timeout=30)
    try:
        return int(r.stdout.strip())
    except ValueError:
        return None

def launch_fresh_notepad(sv, bench_file):
    """Close stale bench notepads, launch fresh, park off-primary.
    Returns (hwnd_str, launch_wall_ms, launch_text)."""
    close_bench_notepads(sv)
    time.sleep(0.8)
    before = window_hwnds(sv)
    _, lwall, ltxt, *_ = sv.tool("computer_launch",
        {"executable": "notepad.exe", "arguments": [bench_file],
         "waitFor": "window", "timeoutMs": 20000})
    after = window_hwnds(sv)
    new = after - before
    hwnd = hwnd_from_result(ltxt)
    if OFF_PRIMARY:
        extra = set(new)
        if hwnd:
            try: extra.add(int(hwnd, 16))
            except ValueError: pass
        park_offscreen(extra_hwnds=extra)
    return hwnd, lwall, ltxt

def main():
    os.makedirs(BENCH_DIR, exist_ok=True)
    report = {
        "label": "csharp-baseline",
        "date": "2026-10-06",
        "env": {
            "os": platform.platform(),
            "python": platform.python_version(),
            "machine": platform.node(),
            "driver": "python stdio JSON-RPC (newline-delimited)",
        },
        "binary": {
            "servingCommand": f"dotnet {DLL} mcp",
            "note": ("inbrisk.exe apphost denied LocalAppData writes under the "
                     "agent sandbox (untrusted image — verified for both direct "
                     "spawn and WMI-spawn); dotnet.exe is allowlisted so the "
                     "identical managed assemblies are hosted directly."),
            "exePath": EXE, "publishExePath": PUBLISH_EXE,
            "daemonReused": False,
            "daemonNote": ("No inbrisk-runtime-* named pipe existed at bench "
                           "time; `inbrisk.exe mcp` runs McpHost in-process "
                           "(Program.cs → McpHost.RunAsync). ThinStdioProxy is "
                           "only the inbrisk-mcp.exe entrypoint."),
        },
    }

    r = subprocess.run(["powershell", "-NoProfile", "-Command",
        "Get-CimInstance Win32_Process -Filter \"Name like 'inbrisk%'\" | "
        "Select-Object ProcessId,Name,ExecutablePath | ConvertTo-Json"],
        capture_output=True, text=True, timeout=30)
    report["env"]["preExistingInbriskProcesses"] = r.stdout.strip()

    # ---- cold start (spawn → initialize result), 5 reps
    cold = []
    for i in range(5):
        sv = Server()
        cold.append(round(sv.init_ms, 1))
        if i == 0:
            report["binary"]["serverInfo"] = sv.init_result.get("serverInfo")
            report["binary"]["protocolVersion"] = sv.init_result.get("protocolVersion")
            report["binary"]["instructionsBytes"] = len(
                (sv.init_result.get("instructions") or "").encode())
        sv.close()
        time.sleep(0.5)
    report["coldStart"] = {
        "note": "spawn → initialize result ms; in-process server "
                "(dotnet host + JIT + DI boot), no daemon reuse",
        "spawnToInitializeMs": stat_block(cold), "samples": cold}

    # ---- tools/list under BOTH profiles (settings.json ToolProfile=core
    #      → 16 tools; INBRISK_TOOL_PROFILE=full → all tools)
    sv_full = Server(extra_env={"INBRISK_TOOL_PROFILE": "full"})
    try:
        tl = sv_full.call("tools/list")
        tools = tl["result"]["tools"]
        report["toolsListFull"] = {"toolCount": len(tools),
                                   "responseBytes": tl["_lineBytes"],
                                   "names": sorted(t["name"] for t in tools)}
    finally:
        sv_full.close()

    # ---- warm session (ambient profile = whatever settings.json says)
    sv = Server()
    try:
        t0 = time.time()
        txt = ""
        while time.time() - t0 < 10:
            _, _, txt, *_ = sv.tool("computer_app_status")
            if '"running"' in txt:
                break
            time.sleep(0.5)
        report["appStatus"] = txt.strip()

        tl = sv.call("tools/list")
        report["toolsListAmbient"] = {
            "toolCount": len(tl["result"]["tools"]),
            "responseBytes": tl["_lineBytes"],
            "note": "ambient profile from %LOCALAPPDATA%\\inbrisk\\settings.json"}

        bench_txt_dir = os.path.join(os.environ["TEMP"], "inbrisk-bench")
        os.makedirs(bench_txt_dir, exist_ok=True)
        bench_file = os.path.join(bench_txt_dir, "bench-observe.txt")
        open(bench_file, "w").write("bench baseline document\n")

        # FRESH launch — real process start → window-ready
        note_hwnd, lwall, ltxt = launch_fresh_notepad(sv, bench_file)
        report["benchNotepad"] = {"hwnd": note_hwnd,
                                  "freshLaunchMs": round(lwall, 1),
                                  "launchResult": ltxt[:600]}

        # warm caches
        sv.tool("computer_windows"); sv.tool("computer_observe", {"mode": "semantic"})
        if note_hwnd:
            sv.tool("computer_observe", {"mode": "semantic", "hwnd": note_hwnd})

        T = report["tools"] = {}
        T["computer_app_status"] = measure_tool(sv, "computer_app_status")
        T["computer_windows.cached"] = measure_tool(sv, "computer_windows")
        T["computer_windows.uncached"] = measure_tool(
            sv, "computer_windows", {"detail": "full"},
            vary=lambda i, a: a.__setitem__("detail", "full" if i % 2 else "slim"))
        if note_hwnd:
            T["computer_observe.semantic.window.cached"] = measure_tool(
                sv, "computer_observe", {"mode": "semantic", "hwnd": note_hwnd})
            T["computer_observe.semantic.window.uncached"] = measure_tool(
                sv, "computer_observe", {"mode": "semantic", "hwnd": note_hwnd},
                vary=lambda i, a: a.__setitem__("maxElements", 60 + i))
            T["computer_find.button.window.cached"] = measure_tool(
                sv, "computer_find", {"role": "button", "hwnd": note_hwnd})
            T["computer_find.button.window.uncached"] = measure_tool(
                sv, "computer_find", {"role": "button", "hwnd": note_hwnd},
                vary=lambda i, a: a.__setitem__("limit", 20 + i))
        T["computer_observe.semantic.desktop.cached"] = measure_tool(
            sv, "computer_observe", {"mode": "semantic"})
        T["computer_observe.semantic.desktop.uncached"] = measure_tool(
            sv, "computer_observe", {"mode": "semantic"},
            vary=lambda i, a: a.__setitem__("maxElements", 60 + i))
        T["computer_screenshot.desktop"] = measure_tool(
            sv, "computer_screenshot", {"target": "desktop"}, iters=8, warmups=1)
        if note_hwnd:
            T["computer_screenshot.window"] = measure_tool(
                sv, "computer_screenshot",
                {"target": "window", "hwnd": note_hwnd}, iters=8, warmups=1)
        T["tools/list"] = measure_rpc(sv)
        T["computer_launch.notepad_existing"] = measure_tool(
            sv, "computer_launch",
            {"executable": "notepad.exe", "waitFor": "window", "timeoutMs": 15000},
            iters=5, warmups=0)

        report["memory"] = {
            "serverPid": sv.proc.pid,
            "serverWorkingSetBytes": working_set_bytes(sv.proc.pid),
            "note": "dotnet.exe hosting inbrisk.dll mcp, after warm run"}

        if note_hwnd:
            sv.tool("computer_close_window", {"hwnd": note_hwnd})
    finally:
        sv.close()

    out = os.path.join(BENCH_DIR, "baseline_2026-10-06.json")
    with open(out, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=1)
    print("[bench] wrote", out)
    for name, t in report["tools"].items():
        w = t["wall"] or {}
        print("%-52s p50=%s p95=%s max=%s" % (name, w.get("p50"), w.get("p95"), w.get("max")))

if __name__ == "__main__":
    main()
