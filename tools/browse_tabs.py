import json, subprocess, sys, threading, queue

EXE = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"
PORT = 9222

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
    return _id

def wait_for(want_id, timeout=90):
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
    r = wait_for(rid)
    if r is None:
        print(f"!! timeout waiting for {tool}")
        return
    for c in r.get("result", {}).get("content", []):
        print(f"== {tool} {args} =>")
        print(c.get("text", "")[:1500])
        print()

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "drive", "version": "0.1"}})
wait_for(1, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

sites = [
    "https://www.google.com",
    "https://tr.wikipedia.org",
    "https://github.com",
    "https://example.com",
]
for i, url in enumerate(sites):
    call("browser_browse", {"url": url, "port": PORT, "newTab": i > 0})

call("browser_tabs", {"port": PORT})

proc.terminate()
