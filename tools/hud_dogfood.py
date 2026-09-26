"""Physical HUD panic/resume dogfood.

Drives a real MCP session and verifies the on-screen HUD state machine via
computer_ui_status (currentHudState + rendered currentActivity text).

Flow:
  1. ConnectedIdle  -> expected non-emergency HUD (Hidden/Standby)
  2. harmless run   -> Working HUD while the run executes
  3. user presses Ctrl+Alt+Pause      -> Emergency + "Inbrisk durduruldu"
  4. user presses Ctrl+Alt+Shift+Pause -> emergency text gone instantly,
     state returns to idle policy (Hidden/Standby) or Working
  5. another harmless run -> Working again

No reset_input, no restart, no reconnect. Only the Inbrisk HUD may render
on the primary screen — the perimeter indicator is disabled for the
duration and restored afterwards.
"""
import json, os, subprocess, sys, threading, time

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench_latency import DEFAULT_EXE

EXE = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_EXE
log = []


class Server:
    """MCP stdio client with a single reader thread — responses are
    dispatched by id so a long-running computer_run never blocks status
    polls (the naive send+recv races on shared stdout)."""

    def __init__(self, exe):
        # stderr is captured — EmergencyControl.Log writes panic/resume
        # transitions there; it is the ground truth for whether the chord
        # physically fired
        self._err = open(os.path.join(os.path.dirname(
            os.path.abspath(__file__)), "hud-dogfood-stderr.log"),
            "w", encoding="utf-8", errors="replace")
        self.proc = subprocess.Popen(
            [exe, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=self._err, text=True, bufsize=1,
            encoding="utf-8", errors="replace")
        self._id = 0
        self._pending = {}
        self._dead = False
        threading.Thread(target=self._reader, daemon=True).start()
        init = self.call("initialize", {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "hud-dogfood", "version": "0.0"}})
        assert "result" in init, f"initialize failed: {init}"
        self.send("notifications/initialized", is_notif=True)
        self._wait_control_ready()

    def _reader(self):
        for line in self.proc.stdout:
            try:
                m = json.loads(line)
            except Exception:
                continue
            rid = m.get("id")
            p = self._pending.get(rid)
            if p is not None:
                p["msg"] = m
                p["ev"].set()
        self._dead = True
        for p in self._pending.values():
            p["ev"].set()

    def _wait_control_ready(self, timeout=8.0):
        t0 = time.time()
        while time.time() - t0 < timeout:
            try:
                _, _, txt = self.run([{"action": "wait", "ms": 1}])
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

    def call(self, method, params=None, timeout=60):
        rid = self.send(method, params)
        p = {"ev": threading.Event(), "msg": None}
        self._pending[rid] = p
        try:
            if not p["ev"].wait(timeout) or p["msg"] is None:
                raise TimeoutError(f"no response for id {rid}")
            return p["msg"]
        finally:
            self._pending.pop(rid, None)

    def tool(self, name, args=None, timeout=60):
        t0 = time.perf_counter()
        r = self.call("tools/call", {"name": name, "arguments": args or {}},
                      timeout=timeout)
        wall = (time.perf_counter() - t0) * 1000
        res = r.get("result", {})
        texts = "\n".join(c.get("text", "") for c in res.get("content", [])
                          if c.get("type") == "text")
        if res.get("isError"):
            raise RuntimeError(f"{name} isError: {texts[:200]}")
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


def note(msg):
    line = f"[{time.strftime('%H:%M:%S')}.{int(time.time()*1000)%1000:03d}] {msg}"
    log.append(line)
    print(line, flush=True)


def ui_status(sv):
    res, _, txt = sv.tool("computer_ui_status", timeout=5)
    try:
        return json.loads(txt)
    except Exception:
        return {"raw": txt}


def app_status(sv):
    res, _, txt = sv.tool("computer_app_status", timeout=5)
    try:
        return json.loads(txt)
    except Exception:
        return {"raw": txt}


def panic_lines(sv):
    """Count EmergencyControl panic/resume transitions in the stderr log —
    the ground truth for whether the chord physically fired."""
    try:
        sv._err.flush()
        with open(sv._err.name, encoding="utf-8", errors="replace") as f:
            lines = f.read()
        # covers direct hotkey and peer-marker transitions alike
        return (lines.count("Inbrisk emergency control: EmergencyStopped"),
                lines.count("Inbrisk emergency control: Active ("))
    except Exception:
        return (0, 0)


def poll_until(sv, pred, timeout, interval=0.15):
    """Poll ui_status until pred(status) or timeout; returns (status, dt)."""
    t0 = time.time()
    last = None
    while time.time() - t0 < timeout:
        try:
            st = ui_status(sv)
        except Exception as e:
            st = {"error": str(e)}
        if st != last:
            note(f"  hudState={st.get('currentHudState')} "
                 f"visible={st.get('hudVisible')} "
                 f"text={st.get('currentActivity')!r}")
            last = st
        if pred(st):
            return st, time.time() - t0
        time.sleep(interval)
    return last, time.time() - t0


def main():
    sv = Server(EXE)
    verdicts = []
    try:
        # -- setup: HUD on, perimeter OFF (screen-1 = HUD only) ------------
        st = ui_status(sv)
        note(f"initial: {st}")
        orig_hud, orig_per = st.get("hudEnabled"), st.get("perimeterEnabled")
        if not orig_hud or orig_per:
            sv.tool("computer_ui_set", {"hud": True, "perimeter": False})
            st = ui_status(sv)
            note(f"after ui_set: {st}")

        # -- phase 1: idle ------------------------------------------------
        note("PHASE 1 — ConnectedIdle check")
        st, _ = poll_until(sv, lambda s: True, 0.2)
        idle_ok = st.get("currentHudState") in ("Hidden", "Standby") and \
            "durduruldu" not in (st.get("currentActivity") or "")
        verdicts.append(("idle state is Hidden/Standby, no emergency text",
                         idle_ok))
        note(f"  idle observed: state={st.get('currentHudState')} "
             f"text={st.get('currentActivity')!r} -> {'OK' if idle_ok else 'FAIL'}")

        # -- phase 2: Working during a harmless run ------------------------
        # a bare "wait" step never touches Perform/BeginActivity — the HUD
        # only shows Working for real activity. A burst of read-only finds
        # is the harmless-but-active workload.
        note("PHASE 2 — harmless run (10 scoped finds) should show Working")
        done = {}
        def run_finds(n=10):
            done["r"] = sv.run([
                {"action": "find", "as": f"e{i}",
                 "target": {"process": "notepad", "role": "document"}}
                for i in range(n)])
        t = threading.Thread(target=run_finds, daemon=True)
        t.start()
        st, dt = poll_until(
            sv, lambda s: s.get("currentHudState") == "Working", 8)
        verdicts.append(("run in progress -> HUD Working", st.get(
            "currentHudState") == "Working"))
        note(f"  reached Working in {dt:.2f}s")
        # -- phase 3: physical panic during a real in-flight run -----------
        # a 100-find run keeps the HUD in Working for several seconds —
        # panic must interrupt it and take visual precedence
        t = threading.Thread(target=run_finds, args=(100,), daemon=True)
        t.start()
        poll_until(sv, lambda s: s.get("currentHudState") == "Working", 10)
        note("=" * 60)
        note(">>> SIMDI Ctrl+Alt+Pause BAS <<<  (panic — run hâlâ Working)")
        note("=" * 60)
        # watch BOTH layers: control state proves the chord fired; hud state
        # proves the visual transition followed
        t0 = time.time()
        st, ctl = {}, {}
        while time.time() - t0 < 600:
            try:
                ctl = app_status(sv)
                st = ui_status(sv)
            except Exception as e:
                note(f"  poll error: {e}")
            note(f"  ctl={ctl.get('emergencyState')} "
                 f"hud={st.get('currentHudState')} "
                 f"text={st.get('currentActivity')!r}")
            if st.get("currentHudState") == "Emergency" or \
                    ctl.get("emergencyState") == "EmergencyStopped":
                break
            # stderr ground truth covers a wedged MCP transport
            if panic_lines(sv)[0] > 0:
                note("  panic detected via stderr (MCP wedged?)")
                time.sleep(0.3)  # give the HUD a beat to transition
                try:
                    st = ui_status(sv)
                except Exception:
                    pass
                break
            time.sleep(0.15)
        dt = time.time() - t0
        # ground truth: EmergencyControl.Log writes panic/resume transitions
        # directly to stderr (unbuffered) — authoritative even when the MCP
        # transport wedges and status polls can't observe the state
        panics, resumes = panic_lines(sv)
        note(f"  stderr: {panics} panic / {resumes} resume transitions")
        if ctl.get("emergencyState") == "EmergencyStopped" and \
                st.get("currentHudState") != "Emergency":
            note("  !! control stopped but HUD never showed Emergency "
                 "— propagation bug")
        elif ctl.get("emergencyState") != "EmergencyStopped" and panics == 0:
            note("  !! control never stopped — chord did not fire?")
        panic_seen = st.get("currentHudState") == "Emergency"
        txt = st.get("currentActivity") or ""
        verdicts.append(("panic -> HUD Emergency", panic_seen))
        verdicts.append(("emergency text 'Inbrisk durduruldu' rendered",
                         "durduruldu" in txt))
        note(f"  Emergency after {dt:.2f}s wait; text={txt!r}")

        # -- phase 4: physical resume --------------------------------------
        if panics == 0:
            note("panic never fired — resume phase meaningless, skipping")
        else:
            note("=" * 60)
            note(">>> SIMDI Ctrl+Alt+Shift+Pause BAS <<<  (resume)")
            note("=" * 60)
        # measure control-state vs HUD-state gap: "instant" means the HUD
        # leaves Emergency in the same poll tick as the control state, or
        # the immediately following one (~150ms granularity)
        t0 = time.time()
        ctl_at = hud_at = None
        st = {}
        while panics > 0 and time.time() - t0 < 600:
            try:
                ast = app_status(sv)
                st = ui_status(sv)
            except Exception as e:
                note(f"  poll error: {e}")
                ast, st = {}, st or {}
            if panic_lines(sv)[1] > 0:
                note("  resume detected via stderr")
                ast = {"emergencyState": "Active"}
            if ctl_at is None and \
                    ast.get("emergencyState") != "EmergencyStopped":
                ctl_at = time.time() - t0
                note(f"  control left EmergencyStopped at t={ctl_at:.2f}s "
                     f"-> {ast.get('emergencyState')}")
            if hud_at is None and \
                    st.get("currentHudState") != "Emergency":
                hud_at = time.time() - t0
                note(f"  HUD left Emergency at t={hud_at:.2f}s "
                     f"-> {st.get('currentHudState')}")
            if ctl_at is not None and hud_at is not None:
                break
            time.sleep(0.1)
        resumed = hud_at is not None
        txt = st.get("currentActivity") or ""
        gap = (hud_at - ctl_at) if (hud_at is not None and
                                  ctl_at is not None) else None
        verdicts.append(("resume -> HUD leaves Emergency", resumed))
        verdicts.append(("emergency text cleared immediately",
                         resumed and "durduruldu" not in txt))
        if gap is not None:
            verdicts.append((
                f"HUD transition within 500ms of control resume "
                f"(gap={gap*1000:.0f}ms)", gap < 0.5))
        note(f"  state={st.get('currentHudState')} text={txt!r} "
             f"control->HUD gap={gap}")

        # linger check: text must not reappear for 2s
        time.sleep(2.0)
        st2 = ui_status(sv)
        txt2 = st2.get("currentActivity") or ""
        verdicts.append(("no stale 'durduruldu' 2s after resume",
                         "durduruldu" not in txt2 and
                         st2.get("currentHudState") != "Emergency"))
        note(f"  2s later: state={st2.get('currentHudState')} text={txt2!r}")

        # -- phase 5: harmless action shows Working again ------------------
        note("PHASE 5 — harmless run (30 scoped finds) should show Working")
        t = threading.Thread(target=run_finds, args=(30,), daemon=True)
        t.start()
        st, dt = poll_until(
            sv, lambda s: s.get("currentHudState") == "Working", 5)
        verdicts.append(("post-resume run -> HUD Working again",
                         st.get("currentHudState") == "Working"))
        note(f"  Working again in {dt:.2f}s")
        t.join(timeout=4)

        # -- restore --------------------------------------------------------
        if orig_hud is not None or orig_per is not None:
            sv.tool("computer_ui_set",
                    {"hud": bool(orig_hud), "perimeter": bool(orig_per)})
            note(f"restored hud={orig_hud} perimeter={orig_per}")
    finally:
        sv.close()

    print("\n================ VERDICT ================")
    ok = True
    for name, passed in verdicts:
        ok &= bool(passed)
        print(("PASS " if passed else "FAIL ") + name)
    print("OVERALL:", "PASS" if ok else "FAIL")


if __name__ == "__main__":
    main()
