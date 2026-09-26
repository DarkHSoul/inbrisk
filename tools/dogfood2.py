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

def call(tool, args, show=1500):
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

# schemas of interest
rid = send("tools/list"); r = wait_for(rid, 30)
tools = {t["name"]: t for t in r["result"]["tools"]}
for n in ["computer_hotkey","computer_key","computer_click","computer_run",
          "computer_type","computer_launch","computer_app_shutdown",
          "computer_set_value","computer_wait_for","computer_scroll"]:
    print("\n###", n, json.dumps(tools[n].get("inputSchema",{}).get("properties",{}))[:1200])

proc.terminate()
