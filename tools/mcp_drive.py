import json, subprocess, sys, threading, queue

EXE = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"

proc = subprocess.Popen(
    [EXE],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
    bufsize=0,
)

q = queue.Queue()
def reader():
    for line in proc.stdout:
        try:
            q.put(json.loads(line.decode("utf-8", "replace")))
        except Exception:
            pass
threading.Thread(target=reader, daemon=True).start()

_id = 0
def send(method, params=None):
    global _id
    _id += 1
    msg = {"jsonrpc": "2.0", "id": _id, "method": method}
    if params is not None:
        msg["params"] = params
    proc.stdin.write((json.dumps(msg) + "\n").encode())
    proc.stdin.flush()
    return msg["id"]

def wait_for(want_id, timeout=60):
    import time
    end = time.time() + timeout
    while time.time() < end:
        try:
            m = q.get(timeout=max(0.1, end - time.time()))
        except queue.Empty:
            break
        if m.get("id") == want_id:
            return m
    return None

def call(tool, args):
    rid = send("tools/call", {"name": tool, "arguments": args})
    r = wait_for(rid, 90)
    if r is None:
        print(f"!! timeout waiting for {tool}")
        return
    res = r.get("result", {})
    for c in res.get("content", []):
        print(f"== {tool} =>")
        print(c.get("text", "")[:2500])
        print()

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "drive", "version": "0.1"}})
wait_for(_id, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

# 1) launch chrome with debugPort while a debug chrome is already up on 9222
#    -> should REUSE (AlreadyRunning), not spawn a second instance
call("computer_launch", {"app": "chrome", "debugPort": 9222})

# 2) adapter on a FRESH port with no debug chrome -> should self-heal
call("computer_adapter", {"action": "status", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"},
                          "args": {"port": 9333}})

# 3) open + navigate on the healed instance
call("computer_adapter", {"action": "new_tab", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"},
                          "args": {"port": 9333, "url": "https://example.com"}})
call("computer_adapter", {"action": "navigate", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"},
                          "args": {"port": 9333, "url": "https://www.google.com"}})
call("computer_adapter", {"action": "list_tabs", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"},
                          "args": {"port": 9333}})

proc.terminate()
