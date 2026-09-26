import json, subprocess, threading, queue, time

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
    txt = res.get("content", [{}])[0].get("text", "")
    print(f"== {tool} {args} =>\n{txt[:900]}\n")
    try:
        return json.loads(txt)
    except Exception:
        return None

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "drive", "version": "0.1"}})
wait_for(1, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

tabs = call("browser_tabs", {"port": PORT})
wiki = next((t for t in tabs["data"]["tabs"]
             if t["Type"] == "page" and "wikipedia" in t["Url"]), None)
tid = wiki["Id"] if wiki else None
print(f"wiki tab: {tid}")

stops = [
    "https://tr.wikipedia.org/wiki/%C4%B0stanbul",          # İstanbul
    "https://tr.wikipedia.org/wiki/Yapay_zek%C3%A2",        # Yapay zekâ
    "https://tr.wikipedia.org/wiki/Anadolu_pars%C4%B1",     # Anadolu parsı
]
for url in stops:
    call("browser_browse", {"url": url, "port": PORT, "tabId": tid})
    time.sleep(2)

call("browser_tabs", {"port": PORT})
proc.terminate()
