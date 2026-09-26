"""Zero-knowledge wire audit — capture EVERYTHING a fresh MCP client sees."""
import json, subprocess, sys, os, threading

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0",
                   "inbrisk-mcp.dll")
proc = subprocess.Popen(
    ["dotnet", os.path.abspath(DLL)],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, bufsize=1)

_id = 0
def send(method, params=None, is_notif=False):
    global _id
    _id += 1
    msg = {"jsonrpc": "2.0", "method": method}
    if not is_notif: msg["id"] = _id
    if params is not None: msg["params"] = params
    proc.stdin.write(json.dumps(msg) + "\n")
    proc.stdin.flush()
    return _id

def recv(timeout=30):
    box = {}
    def rd():
        box["m"] = proc.stdout.readline()
    t = threading.Thread(target=rd, daemon=True); t.start(); t.join(timeout)
    if t.is_alive(): raise TimeoutError("no response")
    return json.loads(box["m"]) if box.get("m") else {}

def call(method, params=None):
    rid = send(method, params)
    while True:
        m = recv()
        if m.get("id") == rid: return m

out = {}
init = call("initialize", {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": {"name": "zk-audit", "version": "0.0"}})
out["initialize"] = init
send("notifications/initialized", is_notif=True)

for method in ["tools/list", "resources/list", "resources/templates/list",
               "prompts/list"]:
    try:
        out[method] = call(method)
    except Exception as e:
        out[method] = {"error": str(e)}

# ping (control)
try:
    out["ping"] = call("ping")
except Exception as e:
    out["ping"] = {"error": str(e)}

sys.stdout.reconfigure(encoding="utf-8")
print(json.dumps(out, indent=1, ensure_ascii=False))
proc.terminate()
