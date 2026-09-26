import json, subprocess, sys, threading, queue, time

EXE = r"C:\Users\Ahmet\AppData\Local\Programs\Inbrisk\inbrisk.exe"

proc = subprocess.Popen(
    [EXE, "mcp"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
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

def call(tool, args, show=1800):
    rid = send("tools/call", {"name": tool, "arguments": args})
    r = wait_for(rid, 120)
    print(f"\n===== {tool} {args} =====")
    if r is None:
        print("!! TIMEOUT")
        return None
    res = r.get("result", r)
    for c in res.get("content", []):
        t = c.get("text", "")
        print(t[:show])
        if len(t) > show: print(f"...(+{len(t)-show} chars)")
    if res.get("isError"):
        print(">> isError=TRUE")
    return res

send("initialize", {"protocolVersion": "2024-11-05",
                    "capabilities": {},
                    "clientInfo": {"name": "dogfood", "version": "0.1"}})
wait_for(_id, 30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

# --- list tools ---
rid = send("tools/list")
r = wait_for(rid, 30)
tools = r["result"]["tools"]
print(f"{len(tools)} tools:")
for t in tools:
    print(" -", t["name"])

def schema(name):
    for t in tools:
        if t["name"] == name:
            return t.get("inputSchema", {})
    return {}

import sys
if "--schema" in sys.argv:
    for t in tools:
        print("\n###", t["name"])
        print(json.dumps(t.get("inputSchema", {}), indent=1)[:1500])
    proc.terminate(); sys.exit()

# ============ DOGFOOD BATTERY ============

# 1. Read-only status tools
call("computer_capabilities", {})
call("computer_apps", {})
call("computer_windows", {})
call("computer_list_adapters", {})
call("computer_leases_status", {})
call("computer_ui_status", {})
call("computer_list_recipes", {})
call("computer_screen_memory", {})

# 2. Observe the real screen
call("computer_observe", {"scope": "screen"})

# 3. Launch notepad, type into it, read it back
call("computer_launch", {"app": "notepad"})
time.sleep(1.5)
call("computer_app_status", {"app": "notepad"})
call("computer_observe", {"scope": "foreground"})
call("computer_type", {"text": "Inbrisk dogfood testi - Merhaba Dunya! 123"})
call("computer_key", {"key": "enter"})
call("computer_type", {"text": "ikinci satir"})
call("computer_screenshot", {})
call("computer_inspect", {"scope": "foreground"})

# 4. Window ops: focus + find element
call("computer_windows", {})
call("computer_find", {"text": "Inbrisk"})

# 5. Hotkey / scroll sanity on notepad (ctrl+a select)
call("computer_hotkey", {"keys": "ctrl+a"})
call("computer_type", {"text": "secileni degistirdim"})

# 6. Chrome adapter path (real browser on user's machine)
call("computer_adapter", {"action": "status", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"}, "args": {"port": 9222}})
call("computer_adapter", {"action": "new_tab", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"},
                          "args": {"port": 9222, "url": "https://example.com"}})
call("computer_adapter", {"action": "list_tabs", "adapter": "chrome_devtools",
                          "target": {"process": "chrome"}, "args": {"port": 9222}})

# 7. Shutdown notepad cleanly
call("computer_app_shutdown", {"app": "notepad"})

proc.terminate()
