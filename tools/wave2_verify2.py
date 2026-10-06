"""Wave-2 focused re-verify: tracker reaping + save_as_recipe, using mspaint
(unique-instance process — notepad is single-instance on Win11 and reuses)."""
import json, os, re, subprocess, sys, tempfile, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "bench"))
import bench_csharp_baseline as B

REPO = B.REPO
DLL = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.dll")
DATADIR = tempfile.mkdtemp(prefix="inbrisk-wave2b-")
ENV = {"INBRISK_PANIC_HOTKEY": "ctrl+alt+f12",
       "INBRISK_DATA_DIR": DATADIR,
       "INBRISK_TOOL_PROFILE": "full"}
R = []
def check(n, ok, d=""):
    R.append((n, ok, d)); print(("PASS" if ok else "FAIL"), n, ("— " + str(d)[:220] if d else ""))
def text(r): return r[2]

s = B.Server(dll=DLL, extra_env=ENV)
print(f"init {s.init_ms:.0f}ms")
for _ in range(10):
    _,_,t,_,_,_,_ = s.tool("computer_app_status")
    if "EmergencyStopped" not in t: break
    time.sleep(3)

paint_pid = None
try:
    # 1. launch mspaint (guaranteed new instance)
    res = s.tool("computer_launch", {"app": "mspaint", "waitFor": "window", "timeoutMs": 15000})
    t = text(res)
    check("launch mspaint", not res[6], t[:200])
    m = re.search(r'"pid":\s*(\d+)', t)
    paint_pid = int(m.group(1)) if m else None
    print("  paint pid:", paint_pid)

    # 2. launch second mspaint
    res = s.tool("computer_launch", {"app": "mspaint", "waitFor": "window", "timeoutMs": 15000})
    t = text(res)
    check("launch mspaint #2", not res[6], t[:150])
    time.sleep(1)

    # 3. cleanup reaps tracked pids
    res = s.tool("computer_cleanup", {})
    t = text(res)
    mj = re.search(r'"reaped":\s*(\d+)', t)
    check("cleanup reaped>=1", mj is not None and int(mj.group(1)) >= 1, t[:300])
    check("cleanup shape", '"pending"' in t and '"skipped"' in t, t[:150])
    time.sleep(1)
    # verify mspaint actually closed
    alive = subprocess.run(["powershell","-c",
        f"(Get-Process -Id {paint_pid} -ErrorAction SilentlyContinue) -ne $null"],
        capture_output=True, text=True, timeout=10).stdout.strip() if paint_pid else "?"
    check("mspaint pid gone", "False" in alive or paint_pid is None, alive)

    # 4. save_as_recipe on a scoped run
    res = s.tool("computer_run", {"steps": [
        {"action": "launch", "app": "mspaint", "waitFor": "window", "timeout": 15000},
    ], "save_as_recipe": "w2-mspaint"})
    t = text(res)
    check("run + save_as_recipe", not res[6] and '"recipe"' in t.lower(), t[:300])
    rfile = os.path.join(DATADIR, "task-recipes", "w2-mspaint.json")
    if os.path.exists(rfile):
        content = open(rfile, encoding="utf-8").read()
        check("recipe file written", True, rfile)
        check("recipe parameterized flag", '"parameterized"' in t or "{{" in content, t[t.find('"recipe"'):t.find('"recipe"')+200] if '"recipe"' in t else content[:150])
    else:
        d = os.path.join(DATADIR, "task-recipes")
        check("recipe file exists", False, os.listdir(d) if os.path.isdir(d) else "no dir")

    # 5. session-end reap: launch mspaint again, close session, check it dies
    res = s.tool("computer_launch", {"app": "mspaint", "waitFor": "window", "timeoutMs": 15000})
    t = text(res)
    m2 = re.search(r'"pid":\s*(\d+)', t)
    pid2 = int(m2.group(1)) if m2 else None
    check("launch mspaint for session-reap", not res[6] and pid2, t[:150])
finally:
    s.close()
    time.sleep(3)
    if pid2:
        alive = subprocess.run(["powershell","-c",
            f"(Get-Process -Id {pid2} -ErrorAction SilentlyContinue) -ne $null"],
            capture_output=True, text=True, timeout=10).stdout.strip()
        check("session-end reaped own mspaint", "False" in alive, alive)
        if "True" in alive:
            subprocess.run(["powershell","-c", f"(Get-Process -Id {pid2}).CloseMainWindow()"],
                           capture_output=True, timeout=8)
            print("  manually closed leftover pid", pid2)

print("\n=== SUMMARY ===")
p = sum(1 for _,ok,_ in R if ok)
for n,ok,_ in R: print(("PASS" if ok else "FAIL"), n)
print(f"{p}/{len(R)} passed")
json.dump([{"name":n,"ok":ok,"detail":str(d)[:400]} for n,ok,d in R],
          open(os.path.join(REPO,"benchmarks","wave2_verification2.json"),"w"), indent=2)
