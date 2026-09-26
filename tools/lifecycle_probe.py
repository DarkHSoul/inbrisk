"""Disconnect dogfood: second client process attaches -> smoke doubles up,
stdin EOF -> process exits -> its smoke is gone. Staged sleeps so the user
can watch the physical perimeter between phases."""
import json, subprocess, sys, os, time

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0",
                   "inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     stderr=subprocess.DEVNULL, text=True, bufsize=1)

def call(method, params=None):
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 99, "method": method,
                              "params": params or {}}) + "\n")
    p.stdin.flush()
    while True:
        m = json.loads(p.stdout.readline())
        if m.get("id") == 99: return m

print("PHASE1 process-alive-no-session (no tool call yet) — watch perimeter: still single lime", flush=True)
call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                    "clientInfo": {"name": "probe2", "version": "0"}})
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
p.stdin.flush()
time.sleep(5)

print("PHASE2 session created (capabilities call) — second smoke layer should now overlap the perimeter (visibly stronger)", flush=True)
r = call("tools/call", {"name": "computer_capabilities", "arguments": {}})
txt = r["result"]["content"][0]["text"]
i = txt.find('"indicator"')
print("probe's own indicator:", txt[i:i+90], flush=True)
time.sleep(6)

print("PHASE3 stdin EOF -> process exit -> its smoke layer gone", flush=True)
p.stdin.close()
try:
    p.wait(timeout=6)
    print("probe exited:", p.returncode, flush=True)
except subprocess.TimeoutExpired:
    print("probe DID NOT exit on EOF", flush=True); p.kill()
