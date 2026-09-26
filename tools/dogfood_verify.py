import json, subprocess, threading, queue, time, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

EXE = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.exe"
proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE, bufsize=0)
q = queue.Queue()
def reader():
    for line in proc.stdout:
        try: q.put(json.loads(line.decode("utf-8","replace")))
        except Exception: pass
threading.Thread(target=reader, daemon=True).start()
_id=0
def send(m,p=None):
    global _id; _id+=1
    msg={"jsonrpc":"2.0","id":_id,"method":m}
    if p is not None: msg["params"]=p
    proc.stdin.write((json.dumps(msg)+"\n").encode()); proc.stdin.flush()
    return msg["id"]
def wait_for(wid,t=90):
    end=time.time()+t
    while time.time()<end:
        try: m=q.get(timeout=max(0.1,end-time.time()))
        except queue.Empty: break
        if m.get("id")==wid: return m
    return None
def call(tool,args,show=1200):
    rid=send("tools/call",{"name":tool,"arguments":args})
    r=wait_for(rid,120)
    print(f"\n===== {tool} {json.dumps(args)[:160]} =====")
    if r is None: print("!! TIMEOUT"); return None
    res=r.get("result",r)
    for c in res.get("content",[]):
        t=c.get("text",""); print(t[:show])
        if len(t)>show: print(f"...(+{len(t)-show} chars)")
    if res.get("isError"): print(">> isError=TRUE")
    return res

send("initialize",{"protocolVersion":"2024-11-05","capabilities":{},
                   "clientInfo":{"name":"verify","version":"0.1"}})
wait_for(_id,30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); proc.stdin.flush()

# FIX 1: hotkey keys shorthand (was: bare isError)
call("computer_hotkey", {"keys":"ctrl+a"})

# FIX 2: hotkey no args -> clear Malformed (was: bare isError)
call("computer_hotkey", {})

# FIX 3: process-scoped diagnosis — chrome is foreground, target notepad w/ bad name
# (notepad may not be running; that's fine — we check the message, not the result)
call("computer_click", {"target":{"process":"notepad","role":"button","name":"YokBoyle123"}})

# FIX 4: wait_for timeoutMs alias now honored (should time out ~2s, not 5s)
t0=time.time()
call("computer_wait_for", {"query":"AslaOlmaz999","timeoutMs":2000})
print(f"   elapsed={time.time()-t0:.1f}s (expect ~2s)")

# FIX 5: set_value on chrome-foreground + notepad-absent -> clean TargetNotFound naming process
call("computer_set_value", {"value":"x","target":{"process":"notepad","role":"document"}})

proc.terminate()
