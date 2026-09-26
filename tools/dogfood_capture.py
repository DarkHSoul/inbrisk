import json, subprocess, threading, queue, time, sys
sys.stdout.reconfigure(encoding='utf-8')

EXE = r"src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"
proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT, bufsize=0)
q = queue.Queue()
def reader():
    for line in proc.stdout:
        try: q.put(json.loads(line.decode("utf-8", "replace")))
        except Exception: pass
threading.Thread(target=reader, daemon=True).start()

_id = 0
def send(method, params=None):
    global _id; _id += 1
    m = {"jsonrpc": "2.0", "id": _id, "method": method}
    if params is not None: m["params"] = params
    proc.stdin.write((json.dumps(m) + "\n").encode()); proc.stdin.flush()
    return _id
def wait(i, t=60):
    end = time.time() + t
    while time.time() < end:
        try: m = q.get(timeout=max(0.1, end - time.time()))
        except queue.Empty: break
        if m.get("id") == i: return m

def call(name, args, t=60):
    r = wait(send("tools/call", {"name": name, "arguments": args}), t)
    try: return json.loads(r["result"]["content"][0]["text"])
    except Exception: return r

wait(send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
          "clientInfo": {"name": "t", "version": "1"}}))

# 1) browse github repo
r = call("browser_browse", {"url": "github.com/ChromeDevTools/chrome-devtools-mcp"})
tab = r.get("data", {}).get("tabId")
print("BROWSE", r.get("success"), r.get("detail", "")[:80])

# 2) snapshot -> find Issues link uid, click it by uid
r = call("browser_snapshot", {"tabId": tab})
nodes = r.get("data", {}).get("nodes", [])
print("SNAPSHOT nodes:", r.get("data", {}).get("nodeCount"))
issues = [n for n in nodes if n.get("role") == "link" and "Issues" in (n.get("name") or "")]
print("issues link:", issues[:2])
if issues:
    r = call("browser_click", {"tabId": tab, "uid": issues[0]["uid"]})
    print("CLICK-UID", r.get("success"), r.get("detail", "")[:100])

# 3) capture: reload repo page, collect network+console+vitals
r = wait(send("tools/call", {"name": "browser_capture", "arguments": {"tabId": tab, "durationMs": 8000}}), 90)
print("RAW:", json.dumps(r)[:800] if r else "TIMEOUT")
try: r = json.loads(r["result"]["content"][0]["text"])
except Exception: pass
d = (r or {}).get("data", {})
print("CAPTURE detail:", r.get("detail", "")[:120])
print("NET summary:", json.dumps(d.get("network", {}).get("summary")))
print("NET byType:", json.dumps(d.get("network", {}).get("byType")))
print("FAILED:", json.dumps(d.get("network", {}).get("failed"))[:400])
print("CONSOLE err/warn:", d.get("console", {}).get("errors"), d.get("console", {}).get("warnings"))
print("EXCEPTIONS:", json.dumps(d.get("runtime", {}).get("exceptions"))[:200])
print("TIMING:", json.dumps(d.get("timing")))
print("VITALS:", json.dumps(d.get("vitals")))
print("RENDERBLOCK count:", len(d.get("renderBlocking") or []))
proc.kill()
