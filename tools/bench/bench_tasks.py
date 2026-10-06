"""Task-corpus benchmark for the C# inbrisk build — programmatic verifiers
only (no LLM judging). Each task runs on a FRESH MCP server session (a hung
call poisons the session's serialized executor — observed with computer_type
on this build, see notes) and records success/fail, wall time, tool-call
count, screenshot count. Everything launched is closed afterwards;
pre-existing user windows and inbrisk processes are never touched.

Run AFTER bench_csharp_baseline.py — merges a "tasks" section into
benchmarks/baseline_2026-10-06.json.
"""
import ctypes, datetime, json, os, re, subprocess, sys, tempfile, time
from ctypes import wintypes

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench_csharp_baseline import (
    Server, REPO, OFF_PRIMARY, park_offscreen, window_hwnds,
    hwnd_from_result, CALL_TIMEOUT, _top_windows)

OUT = os.path.join(REPO, "benchmarks", "baseline_2026-10-06.json")
_user32 = ctypes.windll.user32
EDGE_EXE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
VSCODE_EXE = os.path.join(os.environ["LOCALAPPDATA"],
                          "Programs", "Microsoft VS Code", "Code.exe")
TESTAPP_EXE = os.path.join(REPO, "tests", "Inbrisk.TestApp", "bin",
                           "Release", "net8.0-windows", "Inbrisk.TestApp.exe")
BENCH_TEMP = os.path.join(tempfile.gettempdir(), "inbrisk-bench-tasks")
WM_CLOSE = 0x0010

def wm_close(hwnd):
    _user32.PostMessageW(int(hwnd), WM_CLOSE, 0, 0)

POLICY_RE = re.compile(
    r"policydenied|protected|refusing to launch|denied", re.I)

def is_policy_denied(txt):
    """Launch result text that is a policy Deny/Protected outcome rather
    than a real failure — ProtectedProcesses in user settings (Deny mode)
    or the shell-host guard refuse the spawn."""
    return bool(POLICY_RE.search(txt or ""))

def pid_from_result(txt):
    m = re.search(r'"pid"\s*:\s*(\d+)', txt or "")
    return int(m.group(1)) if m else None

def find_windows(ctx, proc_re=None, title_re=None):
    _, _, wins, _iserr = ctx.tool("computer_windows")
    out = []
    for line in wins.splitlines():
        m = re.match(r'\s*(0x[0-9A-Fa-f]+)\s+"([^"]*)"\s+app=([^\s]+)', line)
        if not m:
            continue
        hwnd, title, proc = m.group(1), m.group(2), m.group(3)
        if proc_re and not re.search(proc_re, proc, re.I):
            continue
        if title_re and not re.search(title_re, title, re.I):
            continue
        out.append((hwnd, title, proc))
    return out

class Ctx:
    """Per-task accounting + cleanup registry.

    Cleanup targets come ONLY from our own launch results in this run —
    never from a whole-desktop diff (run 1 diff-closed + force-killed the
    IDE host window). hwnds = launch-reported hwnds plus NEW windows owned
    by the launched pid; pids = pids our launches reported."""
    def __init__(self, sv):
        self.sv = sv
        self.calls = 0
        self.screenshots = 0
        self.hwnds = []
        self.pids = []
        self.edge_pids = []   # msedge pids WE spawned (unique bench profile)
        self.transport_ok = True
        self.leftovers = []
    def tool(self, name, args=None):
        self.calls += 1
        try:
            res, wall, txt, rb, ib, nimg, iserr = self.sv.tool(name, args)
        except (TimeoutError, EOFError, OSError, ValueError):
            self.transport_ok = False
            raise
        self.screenshots += nimg
        return res, wall, txt, iserr
    def run(self, steps):
        self.calls += 1
        try:
            res, wall, txt, rb, ib, nimg, iserr = self.sv.tool(
                "computer_run", {"steps": steps})
        except (TimeoutError, EOFError, OSError, ValueError):
            self.transport_ok = False
            raise
        self.screenshots += nimg
        return res, wall, txt, iserr
    def park(self):
        if OFF_PRIMARY and self.hwnds:
            park_offscreen(extra_hwnds=self.hwnds)
    def cleanup(self):
        """Close ONLY launch-captured hwnds: WM_CLOSE first, then a single
        NON-force computer_close_window fallback (force escalates to
        Process.Kill — banned after run 1). If the transport is dead we do
        not poke the wedged server — wm_close only. Whatever survives is
        reported as a leftover, not hunted."""
        for h in self.hwnds:
            try:
                wm_close(h)
            except Exception:
                pass
        if self.hwnds:
            time.sleep(0.7)
        for h in self.hwnds:
            try:
                alive = bool(_user32.IsWindow(h))
            except Exception:
                alive = False
            if not alive:
                continue
            if self.transport_ok:
                try:
                    self.tool("computer_close_window",
                              {"hwnd": "0x%X" % h})
                except Exception:
                    self.transport_ok = False
            try:
                if _user32.IsWindow(h):
                    self.leftovers.append(h)
            except Exception:
                pass
        for pid in self.edge_pids:
            # spawned by THIS run — the WMI query filtered on the bench-only
            # --user-data-dir path and the pre/post diff kept only new pids
            subprocess.run(["taskkill", "/PID", str(pid), "/T", "/F"],
                           capture_output=True)

def new_hwnds(ctx, before, launch_txt):
    """Register cleanup targets from OUR launch result only: the launch-
    reported hwnd, plus any NEW desktop window (before→after diff) owned
    by the launched pid. The pid filter is required — a bare diff picks up
    foreign windows (the run-1 incident), and a bare pid would adopt every
    pre-existing window of shared processes (explorer.exe shell, single-
    instance code.exe/notepad). Returns the launch-reported hwnd string."""
    try:
        after = window_hwnds(ctx.sv)
    except Exception:
        after = set()
    new = set(after) - set(before)
    h = hwnd_from_result(launch_txt)
    if h:
        try:
            ctx.hwnds.append(int(h, 16))
        except ValueError:
            pass
    pid = pid_from_result(launch_txt)
    if pid:
        ctx.pids.append(pid)
        try:
            own = set(_top_windows({pid}))
            ctx.hwnds.extend(sorted(new & own))
        except Exception:
            pass
    return h

def wait_ready(sv, timeout=12.0):
    """Wait for app_status running AND not EmergencyStopped — a fresh server
    may boot stopped while it reacquires the panic chord from a killed peer
    (EmergencyControl.TryReacquireAuthority ticks every ~3s)."""
    t0 = time.time()
    while time.time() - t0 < timeout:
        _, _, txt, *_ = sv.tool("computer_app_status")
        if '"running"' in txt and "EmergencyStopped" not in txt:
            return True
        time.sleep(0.5)
    return False

# ------------------------------------------------------------- tasks

def task_notepad(ctx):
    """launch → type text → ctrl+s → verify file contents (run-plan path —
    computer_type hangs on this build; run 'type' uses UIA.ValuePattern)."""
    os.makedirs(BENCH_TEMP, exist_ok=True)
    path = os.path.join(BENCH_TEMP, "bench-task.txt")
    open(path, "w").write("")
    marker = "inbrisk-csharp-baseline-%d" % int(time.time())
    before = window_hwnds(ctx.sv)
    res, wms, txt, iserr = ctx.run([
        {"action": "launch", "executable": "notepad.exe",
         "arguments": [path], "as": "win"},
        {"action": "type",
         "target": {"process": "notepad", "role": "document", "within": "$win"},
         "text": marker, "mode": "replace"},
        {"action": "key", "keys": "ctrl+s"},
        {"action": "wait", "ms": 400},
    ])
    hwnd = new_hwnds(ctx, before, txt)
    ctx.park()
    time.sleep(0.5)
    try:
        ok = marker in open(path, encoding="utf-8", errors="replace").read()
    except OSError:
        ok = False
    return ok, ("run status=" + ("ERR" if iserr else "ok") +
                " file=" + ("contains marker" if ok else "missing marker") +
                " :: " + txt[:180]), None

def task_calculator(ctx):
    """UWP launch via AUMID → calculator window appears in computer_windows."""
    before = window_hwnds(ctx.sv)
    _, lms, ltxt, lerr = ctx.tool("computer_launch", {
        "aumid": "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
        "waitFor": "window", "timeoutMs": 25000})
    hwnd = new_hwnds(ctx, before, ltxt)
    ctx.park()
    wins = find_windows(ctx, proc_re=r"calculatorapp|applicationframehost")
    ok = bool(hwnd) or bool(wins)
    return ok, ("hwnd=%s calcWins=%s" % (hwnd, [w[1] for w in wins][:3])) \
        if ok else ("launch=" + ltxt[:200]), lms

def task_edge_cdp(ctx):
    """Edge + --remote-debugging-port=9222 (temp profile) → browser_browse
    example.com → verify via browser_evaluate document.title."""
    prof = os.path.join(BENCH_TEMP, "edge-cdp-profile")
    os.makedirs(prof, exist_ok=True)
    pre = subprocess.run(["powershell", "-NoProfile", "-Command",
        "(Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | "
        "Where-Object {$_.CommandLine -like '*inbrisk-bench-tasks*'}).ProcessId"],
        capture_output=True, text=True).stdout or ""
    before = window_hwnds(ctx.sv)
    _, lms, ltxt, lerr = ctx.tool("computer_launch", {
        "executable": EDGE_EXE,
        "arguments": ["--remote-debugging-port=9222",
                      "--user-data-dir=" + prof, "about:blank"],
        "waitFor": "window", "timeoutMs": 30000})
    hwnd = new_hwnds(ctx, before, ltxt)
    ctx.park()
    post = subprocess.run(["powershell", "-NoProfile", "-Command",
        "(Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | "
        "Where-Object {$_.CommandLine -like '*inbrisk-bench-tasks*'}).ProcessId"],
        capture_output=True, text=True).stdout or ""
    for tok in set(re.findall(r"\d+", post)) - set(re.findall(r"\d+", pre)):
        ctx.edge_pids.append(int(tok))
    if lerr:
        return False, "edge launch error: " + ltxt[:200], lms
    time.sleep(1.0)
    res, bms, btxt, berr = ctx.tool("browser_browse",
                                  {"url": "example.com", "port": 9222})
    if berr:
        return False, "browse error: " + btxt[:200], lms
    res, ems, etxt, eerr = ctx.tool("browser_evaluate",
        {"expression": "document.title", "port": 9222})
    ok = (not eerr) and "example" in etxt.lower()
    return ok, ("title: " + etxt[:120]) if ok else ("eval: " + etxt[:200]), lms

def task_explorer(ctx):
    """explorer on a temp dir → window present (explorer.exe is a
    ProtectedProcess in user settings — launch may be policy-denied)."""
    d = os.path.join(BENCH_TEMP, "expldir")
    os.makedirs(d, exist_ok=True)
    before = window_hwnds(ctx.sv)
    _, lms, ltxt, lerr = ctx.tool("computer_launch",
        {"executable": "explorer.exe", "arguments": [d],
         "waitFor": "window", "timeoutMs": 15000})
    hwnd = new_hwnds(ctx, before, ltxt)
    ctx.park()
    if is_policy_denied(ltxt) and not hwnd:
        return None, "policy-protected: " + ltxt[:160], lms
    wins = find_windows(ctx, proc_re=r"explorer")
    ok = bool(hwnd) or any("expldir" in w[1].lower() for w in wins)
    if lerr and not ok:
        return False, "launch error: " + ltxt[:200], lms
    return ok, "hwnd=%s wins=%s" % (hwnd, [w[1] for w in wins][:4]), lms

def task_vscode(ctx):
    if not os.path.exists(VSCODE_EXE):
        return None, "VS Code not installed", None
    d = os.path.join(BENCH_TEMP, "vscdir")
    os.makedirs(d, exist_ok=True)
    before = window_hwnds(ctx.sv)
    _, lms, ltxt, lerr = ctx.tool("computer_launch",
        {"executable": VSCODE_EXE, "arguments": ["--new-window", d],
         "waitFor": "window", "timeoutMs": 30000})
    hwnd = new_hwnds(ctx, before, ltxt)
    ctx.park()
    if is_policy_denied(ltxt) and not hwnd:
        return None, "policy-protected: " + ltxt[:160], lms
    time.sleep(2.0)
    wins = find_windows(ctx, proc_re=r"code\.exe")
    ok = bool(hwnd) or bool(wins)
    if lerr and not ok:
        return False, "launch error: " + ltxt[:200], lms
    return ok, "hwnd=%s codeWins=%d" % (hwnd, len(wins)), lms

def task_custom_app(ctx):
    """Inbrisk.TestApp (WPF) → window + find resolves its controls."""
    if not os.path.exists(TESTAPP_EXE):
        return None, "TestApp not built: " + TESTAPP_EXE, None
    before = window_hwnds(ctx.sv)
    _, lms, ltxt, lerr = ctx.tool("computer_launch",
        {"executable": TESTAPP_EXE, "waitFor": "window", "timeoutMs": 15000})
    hwnd = new_hwnds(ctx, before, ltxt)
    ctx.park()
    if lerr:
        return False, "launch error: " + ltxt[:200], lms
    args = {"role": "button", "hwnd": hwnd} if hwnd else \
           {"role": "button", "process": "Inbrisk.TestApp"}
    res, fms, ftxt, ferr = ctx.tool("computer_find", args)
    ok = (not ferr) and ("button" in ftxt.lower())
    return ok, "find→" + ftxt[:160], lms

TASKS = [
    ("notepad", task_notepad),
    ("calculator_uwp", task_calculator),
    ("edge_cdp", task_edge_cdp),
    ("explorer", task_explorer),
    ("vscode", task_vscode),
    ("custom_app", task_custom_app),
]

# Per-task server env overrides. The ambient ToolProfile is "core" (16
# tools — no browser_*), so edge_cdp spawns its server with the full
# profile; every other task uses ambient like the baseline run.
TASK_ENV = {
    "edge_cdp": {"INBRISK_TOOL_PROFILE": "full"},
}

def main():
    results = {}
    for name, fn in TASKS:
        t0 = time.perf_counter()
        status, note, launch_ms = "FAIL", "", None
        sv = ctx = None
        try:
            sv = Server(extra_env=TASK_ENV.get(name))  # fresh session per
            ctx = Ctx(sv)                            # task — a hung call
            if not wait_ready(sv):                   # poisons the session's
                raise TimeoutError("server never became ready")  # executor
            ok, note, launch_ms = fn(ctx)
            status = "SKIPPED" if ok is None else ("PASS" if ok else "FAIL")
        except (TimeoutError, EOFError) as e:
            note = "transport: " + str(e)[:160]
        except Exception as e:
            note = "exception: " + str(e)[:160]
        wall = (time.perf_counter() - t0) * 1000
        if ctx is not None:
            try:
                ctx.cleanup()
            except Exception:
                pass
        if sv is not None:
            sv.close()   # reaps the spawned dotnet — kills it if stdin-EOF
                         # didn't exit it (wedged mid-call)
        calls = ctx.calls if ctx is not None else 0
        shots = ctx.screenshots if ctx is not None else 0
        if ctx is not None and ctx.leftovers:
            note += " | leftover hwnds: " + ",".join(
                "0x%X" % h for h in ctx.leftovers)
        results[name] = {
            "status": status, "wallMs": round(wall, 1),
            "launchMs": round(launch_ms, 1) if launch_ms else None,
            "toolCalls": calls, "screenshots": shots,
            "notes": note[:400]}
        print("%-16s %-7s %7dms calls=%d shots=%d %s" %
              (name, status, round(wall), calls, shots,
               note[:120]), flush=True)
        time.sleep(1.0)

    if os.path.exists(OUT):
        rep = json.load(open(OUT))
    else:
        rep = {"label": "csharp-baseline", "date": "2026-10-06"}
    rep["tasks"] = results
    rep["tasksNote"] = (
        "custom_app uses tests/Inbrisk.TestApp (WPF) as the "
        "'custom-rendered-ish' target — a real game was unavailable. "
        "explorer.exe and code.exe are ProtectedProcesses in user settings "
        "(launch denial → SKIPPED 'policy-protected', not FAIL). "
        "computer_type hangs >30s on this build (observed twice); the "
        "computer_run 'type' step (UIA.ValuePattern) works fine — tasks use "
        "the run-plan path for typing. edge_cdp runs its server with "
        "INBRISK_TOOL_PROFILE=full (ambient 'core' profile has no browser_* "
        "tools). Cleanup is launch-scoped: only hwnds our own computer_launch "
        "results report (plus new windows owned by the launched pid) get "
        "WM_CLOSE, then a single non-force computer_close_window — never "
        "force, never a whole-desktop diff; a dead transport short-circuits "
        "to wm_close only. Server.close() now guarantees the spawned dotnet "
        "process is killed and reaped.")
    json.dump(rep, open(OUT, "w"), indent=1)
    print("[bench] merged tasks ->", OUT)

if __name__ == "__main__":
    main()
