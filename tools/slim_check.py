# Quick size check: full vs slim output for the heavy tools.
import json, os, sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench_latency import DEFAULT_EXE
from hud_dogfood import Server

sv = Server(DEFAULT_EXE)

def show(name, args):
    try:
        res, wall, txt = sv.tool(name, args)
        print(f"{name:24} {str(args.get('detail','(default)')):>9} "
              f"bytes={len(txt.encode('utf-8')):6} lines={txt.count(chr(10))}")
        return txt
    except Exception as e:
        print(f"{name:24} {str(args.get('detail','(default)')):>9} ERR {e}")

show("computer_capabilities", {})
show("computer_capabilities", {"detail": "slim"})
show("computer_capabilities", {"detail": "bogus"})
show("computer_find", {"process": "inbrisk"})
show("computer_find", {"process": "inbrisk", "detail": "slim"})
show("computer_find", {"role": "window", "detail": "slim", "limit": 3})
show("computer_observe", {})
show("computer_observe", {"detail": "slim"})
show("computer_run", {"steps": [{"action": "find",
     "target": {"process": "inbrisk"}}]})
show("computer_run", {"steps": [{"action": "find",
     "target": {"process": "inbrisk"}}], "detail": "slim"})

res, w, t = sv.tool("computer_find",
    {"role": "window", "detail": "slim", "limit": 4})
print("---- slim find ----"); print(t[:1200])
res, w, t = sv.tool("computer_observe", {"detail": "slim"})
print("---- slim observe head ----"); print(t[:900])
res, w, t = sv.tool("computer_capabilities", {"detail": "slim"})
print("---- slim capabilities ----"); print(t[:2200])
sv.close()
