import json, subprocess, threading, queue

EXE = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"
PORT = 9222

proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT, bufsize=0)

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
        return None
    res = r.get("result", {})
    for c in res.get("content", []):
        print(f"== {tool} {args} =>")
        print(c.get("text", "")[:1200])
        print()
    try:
        return json.loads(res["content"][0]["text"])
    except Exception:
        return None

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "drive", "version": "0.1"}})
wait_for(1, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

# current tabs
tabs = call("browser_tabs", {"port": PORT})
pages = {t["Url"]: t["Id"] for t in tabs["data"]["tabs"] if t["Type"] == "page"}

def tab_for(host):
    for url, tid in pages.items():
        if host in url:
            return tid
    return None

# deeper navigation per tab — same tabId pinned
nav = [
    ("google.com",   "https://www.google.com/search?q=inbrisk+cdp"),
    ("wikipedia.org","https://tr.wikipedia.org/wiki/T%C3%BCrkiye"),
    ("github.com",   "https://github.com/explore"),
    ("example.com",  "https://www.iana.org/domains/example"),
]
for host, url in nav:
    tid = tab_for(host)
    call("browser_browse", {"url": url, "port": PORT, "tabId": tid})

call("browser_tabs", {"port": PORT})
proc.terminate()
