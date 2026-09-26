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
    short = {k: v for k, v in args.items() if k != "text"}
    print(f"== {tool} {short} =>\n{txt[:900]}\n")
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

def new_tab(url):
    r = call("browser_browse", {"url": url, "port": PORT, "newTab": True})
    return r["data"]["tabId"] if r and r.get("success") else None

time.sleep(1)

# 1) DuckDuckGo — type + submit a real search
ddg = new_tab("https://duckduckgo.com")
if ddg:
    time.sleep(2)
    call("browser_type", {"selector": "input[name='q']", "text": "inbrisk mcp cdp",
                          "port": PORT, "tabId": ddg})
    call("browser_evaluate", {"expression": "document.querySelector('form').submit(); 'submitted'",
                              "port": PORT, "tabId": ddg})
    time.sleep(3)
    call("browser_content", {"selector": "h1,h2,h3", "port": PORT, "tabId": ddg})

# 2) Wikipedia — fill its own search box and go
wiki = new_tab("https://tr.wikipedia.org/wiki/Anasayfa")
if wiki:
    time.sleep(2)
    call("browser_type", {"selector": "#searchInput", "text": "Galata Kulesi",
                          "port": PORT, "tabId": wiki})
    call("browser_evaluate", {"expression": "document.querySelector('#searchform').submit(); 'submitted'",
                              "port": PORT, "tabId": wiki})
    time.sleep(2)
    call("browser_content", {"selector": "h1", "port": PORT, "tabId": wiki})

# 3) example.com — click the "More information" link
ex = new_tab("https://example.com")
if ex:
    time.sleep(1)
    call("browser_click", {"selector": "a", "port": PORT, "tabId": ex})
    time.sleep(2)

# 4) GitHub — JS scroll + read trending titles via evaluate
gh = new_tab("https://github.com/trending")
if gh:
    time.sleep(3)
    call("browser_evaluate", {"expression": "window.scrollTo(0, 1500); 'scrolled'",
                              "port": PORT, "tabId": gh})
    time.sleep(1)
    call("browser_evaluate", {
        "expression": "[...document.querySelectorAll('article h2')].slice(0,5).map(h=>h.innerText.trim())",
        "port": PORT, "tabId": gh})

call("browser_tabs", {"port": PORT})
proc.terminate()
