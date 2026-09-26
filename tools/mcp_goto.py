import json, subprocess, sys, threading, queue

EXE = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"
URL = sys.argv[1] if len(sys.argv) > 1 else "https://www.google.com"
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 9222

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

def send(method, params=None):
    msg = {"jsonrpc": "2.0", "id": send.id, "method": method}
    send.id += 1
    if params is not None:
        msg["params"] = params
    proc.stdin.write((json.dumps(msg) + "\n").encode())
    proc.stdin.flush()
    return msg["id"]
send.id = 1

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

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "drive", "version": "0.1"}})
wait_for(1, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

# Single call: browser_browse self-heals — spawns a debug-enabled
# browser on PORT if none is reachable, then navigates.
rid = send("tools/call", {"name": "browser_browse", "arguments": {
    "url": URL, "port": PORT}})
r = wait_for(rid)
for c in (r or {}).get("result", {}).get("content", []):
    print(c.get("text", "")[:2500])

proc.terminate()
