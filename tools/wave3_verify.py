"""Wave-3 verify: reflex engine — owned-modal auto-dismiss mid-run,
unknown-modal fail-safe abort, enableReflex:false regression."""
import json, os, re, subprocess, sys, tempfile, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "bench"))
import bench_csharp_baseline as B

REPO = B.REPO
DLL = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.dll")
DATADIR = tempfile.mkdtemp(prefix="inbrisk-wave3-")
POP = os.path.join(REPO, "tools", "pop_modal.py")
ENV = {"INBRISK_PANIC_HOTKEY": "ctrl+alt+f12",
       "INBRISK_DATA_DIR": DATADIR,
       "INBRISK_TOOL_PROFILE": "full"}
R = []
def check(n, ok, d=""):
    R.append((n, ok, d)); print(("PASS" if ok else "FAIL"), n, ("- " + str(d)[:240] if d else ""))
def text(r): return r[2]

helpers = []
def pop(owner_hwnd_dec, txt, title, buttons=1, delay=1500):
    p = subprocess.Popen([sys.executable, POP,
                          str(owner_hwnd_dec), txt, title,
                          str(buttons), str(delay)],
                         creationflags=subprocess.CREATE_NO_WINDOW)
    helpers.append(p)
    return p

def alive(p, grace_ms=0):
    if grace_ms: time.sleep(grace_ms / 1000)
    return p.poll() is None

def kill_helpers():
    for p in helpers:
        if p.poll() is None:
            try: p.kill()
            except Exception: pass

s = B.Server(dll=DLL, extra_env=ENV)
print(f"init {s.init_ms:.0f}ms")
for _ in range(10):
    _,_,t,_,_,_,_ = s.tool("computer_app_status")
    if "EmergencyStopped" not in t: break
    time.sleep(3)

try:
    # ---- setup: fresh notepad, capture hwnd ----
    res = s.tool("computer_launch", {"executable": "notepad.exe",
                 "waitFor": "window", "timeoutMs": 15000, "newInstance": True})
    t = text(res)
    check("launch notepad", not res[6], t[:160])
    m = re.search(r'"hwnd":\s*"(0x[0-9A-Fa-f]+|\d+)"', t)
    if not m:
        m = re.search(r'"hwnd":\s*(\d+)', t)
    hwnd = int(m.group(1), 16 if str(m.group(1)).lower().startswith("0x") else 10) if m else 0
    print("  notepad hwnd:", hex(hwnd) if hwnd else "?", "(dec", hwnd, ")")
    check("hwnd captured", hwnd > 0, t[:160])

    hwnd_hex = f"0x{hwnd:X}"
    batch = lambda reflex, dismiss=None: {
        "steps": [
            {"do":"type","target":{"hwnd":hwnd_hex,"role":"document"},"v":"w3-reflex "},
            {"do":"wait","ms":3500},
            {"do":"type","target":{"hwnd":hwnd_hex,"role":"document"},"v":"after-modal"}],
        "failFast": True,
        "enableReflex": reflex,
        **({"autoDismissModals": dismiss} if dismiss else {})}

    # ---- S1: update-shaped modal mid-run -> reflex dismisses, run completes ----
    p1 = pop(hwnd, "An update is available for Notepad. Restart to update.",
             "Update available", buttons=1, delay=1500)  # OKCancel -> Cancel is a defer button
    res = s.tool("computer_batch", batch(True))
    t = text(res)
    ok_run = not res[6] and ('"status":"Completed"' in t or '"success":true' in t)
    check("S1 run completed over dismissed modal", ok_run, t[-260:])
    check("S1 reflex.modals non-empty", '"modals"' in t and re.search(r'"modals":\s*\[\s*\{', t) is not None,
          t[t.find('"modals"'):t.find('"modals"')+300] if '"modals"' in t else "no reflex block")
    check("S1 reflex.aborted != true", '"aborted":true' not in t, "")
    check("S1 modal was clicked (helper exited)", not alive(p1, 500),
          "helper still alive -> modal never clicked")

    # ---- S3: unknown modal -> fail-safe abort, modal left alone ----
    p3 = pop(hwnd, "Completely foreign prompt body", "Weird prompt", buttons=4, delay=1500)  # YesNo
    res = s.tool("computer_batch", batch(True))
    t = text(res)
    check("S3 abort -> InterruptedByDialog", "InterruptedByDialog" in t, t[-260:])
    check("S3 reflex.aborted true", '"aborted":true' in t, "")
    time.sleep(1)
    still = alive(p3)
    check("S3 unknown modal NOT clicked", still, "helper exited -> something clicked it")
    if not still: print("  !! unknown modal was dismissed - FAIL SAFE VIOLATION")
    kill_helpers(); time.sleep(1)

    # ---- S4: enableReflex:false -> modal blocks, no dismiss ----
    p4 = pop(hwnd, "An update is available for Notepad. Restart to update.",
             "Update available", buttons=1, delay=1500)
    res = s.tool("computer_batch", batch(False))
    t = text(res)
    blocked = "UnexpectedModalOpened" in t or "InterruptedByDialog" in t or "Paused" in t
    check("S4 enableReflex:false -> run blocked/paused", blocked or res[6], t[-260:])
    check("S4 modal NOT dismissed by reflex", alive(p4, 500), "helper exited")
    kill_helpers(); time.sleep(1)

    # ---- S8: telemetry ----
    pt = os.path.join(DATADIR, "perf-trace.jsonl")
    if os.path.exists(pt):
        lines = open(pt, encoding="utf-8", errors="replace").read()
        check("perf reflex.intercept present", '"reflex.intercept"' in lines or '"kind":"reflex.intercept"' in lines, "")
        check("perf reflex.dismiss present", '"reflex.dismiss"' in lines, "")
        mm = re.search(r'"kind":\s*"reflex.dismiss"[^}]*"ms":\s*(\d+)', lines)
        if mm: print("  intercept->dismiss ms:", mm.group(1))
    else:
        check("perf-trace.jsonl exists", False, pt)

    # ---- cleanup ----
    res = s.tool("computer_cleanup", {})
    check("cleanup", not res[6], text(res)[:160])
finally:
    kill_helpers()
    s.close()

print("\n=== SUMMARY ===")
p_ = sum(1 for _,ok,_ in R if ok)
for n,ok,_ in R: print(("PASS" if ok else "FAIL"), n)
print(f"{p_}/{len(R)} passed")
json.dump([{"name":n,"ok":ok,"detail":str(d)[:400]} for n,ok,d in R],
          open(os.path.join(REPO,"benchmarks","wave3_verification.json"),"w"), indent=2)
