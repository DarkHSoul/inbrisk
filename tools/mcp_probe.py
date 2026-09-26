"""Manual MCP stdio probe — newline-delimited JSON-RPC (MCP stdio spec)."""
import json, subprocess, sys, re, time, os, threading

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

def tool(name, args=None):
    r = call("tools/call", {"name": name, "arguments": args or {}})
    res = r.get("result", {})
    texts = [c.get("text","") for c in res.get("content",[])
             if c.get("type")=="text"]
    imgs = [c for c in res.get("content",[]) if c.get("type")=="image"]
    return res, "\n".join(texts), imgs

init = call("initialize", {
    "protocolVersion": "2025-03-26",
    "capabilities": {},
    "clientInfo": {"name": "probe", "version": "0.0"}})
print("INIT:", init.get("result", {}).get("serverInfo"))
send("notifications/initialized", is_notif=True)

tl = call("tools/list")
print("TOOLS:", [t["name"] for t in tl["result"]["tools"]])

_, wins, _ = tool("computer_windows")
m = re.search(r"0x([0-9A-Fa-f]+)\s+\"InbriskTestApp\"", wins)
if not m:
    print("TESTAPP NOT RUNNING"); print(wins); sys.exit(1)
hwnd = "0x" + m.group(1)
print("HWND:", hwnd)

_, obs, imgs = tool("computer_observe", {"hwnd": hwnd})
print("--- OBSERVE ---"); print(obs); print("images:", len(imgs))

_, f, _ = tool("computer_find", {"hwnd": hwnd, "role": "edit"})
print("--- FIND edit ---"); print(f)
tb = re.search(r"\[(uia_\d+)\]", f).group(1)

r, sv, _ = tool("computer_set_value", {"elementId": tb, "value": "hello-mcp"})
print("--- SETVALUE ---"); print(sv)

_, fc, _ = tool("computer_find", {"hwnd": hwnd, "role": "checkbox"})
print("--- FIND checkbox ---"); print(fc)
cb = re.search(r"\[(uia_\d+)\]", fc).group(1)
r, ck, _ = tool("computer_click", {"elementId": cb})
print("--- CLICK checkbox ---"); print(ck)

_, fs, _ = tool("computer_find", {"hwnd": hwnd, "role": "button", "name": "Save"})
saveid = re.search(r"\[(uia_\d+)\]", fs).group(1)
r, iv, _ = tool("computer_invoke", {"elementId": saveid})
print("--- INVOKE save ---"); print(iv)

time.sleep(0.5)
state = open(os.path.join(os.environ["TEMP"], "inbrisk_testapp", "state.txt")).read()
print("STATE:", state)

_, shot, simgs = tool("computer_screenshot", {"target": "window", "hwnd": hwnd})
print("--- SCREENSHOT ---"); print(shot); print("image blocks:", len(simgs))

proc.terminate()
