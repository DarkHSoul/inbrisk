"""Wave-2 (Hardening/OCR/Silent/Recipes) integration verification.
Spawns isolated MCP server + self-owned notepad only. Never touches foreign windows."""
import json, os, re, subprocess, sys, tempfile, time
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "bench"))
import bench_csharp_baseline as B

REPO = B.REPO
DLL = os.path.join(REPO, "src", "Inbrisk.Cli", "bin", "Release",
                   "net8.0-windows10.0.19041.0", "inbrisk.dll")
DATADIR = tempfile.mkdtemp(prefix="inbrisk-wave2-")
ENV = {
    "INBRISK_PANIC_HOTKEY": "ctrl+alt+f12",
    "INBRISK_DATA_DIR": DATADIR,
    "INBRISK_TOOL_PROFILE": "full",
}
RESULTS = []
def check(name, ok, detail=""):
    RESULTS.append((name, ok, detail))
    print(("PASS" if ok else "FAIL"), name, ("— " + str(detail)[:200] if detail else ""))

def text(res):
    return res[2] if isinstance(res, tuple) else res

s = B.Server(dll=DLL, extra_env=ENV)
print(f"init {s.init_ms:.0f}ms")
# wait for server active
for _ in range(10):
    _,_,t,_,_,_,_ = s.tool("computer_app_status")
    if "EmergencyStopped" not in t:
        break
    time.sleep(3)

own_hwnd = None
notepads = []
try:
    # --- spawn own notepad via computer_launch (tracked by SessionProcessTracker) ---
    res = s.tool("computer_launch", {"app": "notepad"})
    txt = text(res)
    m = re.search(r'"hwnd":\s*"?(\d+|[0-9a-fA-Fx]+)"?', txt) or re.search(r"hwnd[=: ]+0x?([0-9a-fA-F]+)", txt)
    own_hwnd = m.group(1) if m else None
    check("launch notepad", not res[6], txt[:160])
    time.sleep(1.5)

    # --- 1. wait_for map: ---
    res = s.tool("computer_wait_for", {"query": "map:notepad.document", "timeoutMs": 5000})
    t = text(res)
    check("wait_for map:notepad.document", not res[6] and "unknown ui-map key" not in t and "invalid map" not in t.lower(), t[:200])

    res = s.tool("computer_wait_for", {"query": "map:bogus.x", "timeoutMs": 3000})
    t = text(res)
    check("wait_for map:bogus.x fast-fail", res[6] and ("unknown ui-map key" in t or "TargetNotFound" in t or "Malformed" in t), t[:160])

    # --- 2. observe ocr:true ---
    res = s.tool("computer_observe", {"hwnd": own_hwnd, "ocr": True}) if own_hwnd else s.tool("computer_observe", {"ocr": True})
    t = text(res)
    check("observe ocr:true section", "ocr words" in t, t[t.find("ocr words"):t.find("ocr words")+200] if "ocr words" in t else t[:200])

    # --- 3. find ocrText ---
    res = s.tool("computer_find", {"process": "notepad", "ocr": True, "name": "düzenleyici"})
    t = text(res)
    has_ocr = "ocr:" in t
    check("find ocr hit (or clean miss)", "ocr:" in t or res[6], t[:200])

    # --- 4. silent type ---
    # find the document element
    res = s.tool("computer_find", {"role": "document", "process": "notepad"})
    t = text(res)
    m = re.search(r"\[(uia_\d+_\d+)\]", t)
    eid = m.group(1) if m else None
    check("find notepad document", eid is not None, t[:150])
    if eid:
        fg_before = text(s.tool("computer_app_status"))
        res = s.tool("computer_type", {"elementId": eid, "text": "WAVE2SILENT", "silent": True, "mode": "replace"})
        t = text(res)
        check("silent type", not res[6], t[:200])
        check("silent method annotated", '"wm_' in t or '"silent": true' in t or "wm_settext" in t or "em_replacesel" in t, t[:200])
        # verify text landed via read
        res = s.tool("computer_find", {"role": "document", "process": "notepad"})
        check("silent text verified", "WAVE2SILENT" in text(res), text(res)[:200])
        # focus assertion: our notepad must NOT be foreground
        res = s.tool("computer_windows", {})
        t = text(res)
        mfg = re.search(r"\[foreground\].*?(notepad|Not Defteri)", t, re.I)
        check("no focus theft (notepad not foreground)", mfg is None, t[:300])

    # --- 5. silent unsupported (hotkey) ---
    res = s.tool("computer_hotkey", {"keys": "ctrl+s", "silent": True})
    t = text(res)
    check("silent hotkey -> NotSupported", res[6] and ("NotSupported" in t or "Unsupported" in t or "not supported" in t.lower()), t[:200])

    # --- 6. second notepad + cleanup ---
    res = s.tool("computer_launch", {"app": "notepad"})
    check("launch notepad #2", not res[6], text(res)[:120])
    time.sleep(1.5)
    res = s.tool("computer_cleanup", {})
    t = text(res)
    mj = re.search(r'"reaped":\s*(\d+)', t)
    check("computer_cleanup reaps tracked", mj is not None and int(mj.group(1)) >= 2, t[:250])

    # --- 7. browser_evaluate gate ---
    res = s.tool("browser_evaluate", {"expression": "1+1"})
    t = text(res)
    check("browser_evaluate gated", res[6] and ("ConfirmationDenied" in t or "PolicyDenied" in t or "not allowed" in t.lower() or "denied" in t.lower()), t[:200])

    # --- 8. script-launch gate ---
    res = s.tool("computer_launch", {"path": "C:\\Temp\\test.bat"})
    t = text(res)
    check("script launch gated", res[6] and ("ConfirmationRequired" in t or "PolicyDenied" in t or "consent" in t.lower() or "script" in t.lower()), t[:250])

    # --- 9. save_as_recipe (scoped to our notepad's hwnd to avoid ambiguity) ---
    run_target = {"role": "document"}
    if own_hwnd:
        run_target["hwnd"] = own_hwnd
    res = s.tool("computer_run", {"steps": [
        {"action": "type", "target": run_target, "text": "recipe-test-abc"},
    ], "save_as_recipe": "w2-test"}, )
    t = text(res)
    check("save_as_recipe saved", "recipe" in t.lower(), t[:300])
    rfile = os.path.join(DATADIR, "task-recipes", "w2-test.json")
    if os.path.exists(rfile):
        content = open(rfile, encoding="utf-8").read()
        check("recipe parameterized", "{{text}}" in content or '"text"' in content, content[:200])
    else:
        check("recipe file exists", False, f"missing {rfile}; dir={os.listdir(os.path.join(DATADIR,'task-recipes')) if os.path.isdir(os.path.join(DATADIR,'task-recipes')) else 'none'}")

finally:
    # close our notepads
    for pid in []:
        pass
    s.close()
    # reap any leftover test notepads we spawned (by our titles only if needed)
    try:
        out = subprocess.run(["powershell", "-c",
            "Get-Process notepad -ErrorAction SilentlyContinue | Where-Object {$_.MainWindowTitle -match 'WAVE2SILENT|recipe-test'} | Select-Object -Expand Id"],
            capture_output=True, text=True, timeout=10).stdout.strip()
        for line in out.splitlines():
            line = line.strip()
            if line.isdigit():
                subprocess.run(["powershell", "-c", f"(Get-Process -Id {line}).CloseMainWindow()"], capture_output=True, timeout=8)
                print("closed leftover notepad pid", line)
    except Exception as e:
        print("cleanup warn:", e)

print("\n=== SUMMARY ===")
passed = sum(1 for _, ok, _ in RESULTS if ok)
for n, ok, d in RESULTS:
    print(("PASS" if ok else "FAIL"), n)
print(f"{passed}/{len(RESULTS)} passed")
json.dump([{"name": n, "ok": ok, "detail": str(d)[:400]} for n, ok, d in RESULTS],
          open(os.path.join(REPO, "benchmarks", "wave2_verification.json"), "w"), indent=2)
