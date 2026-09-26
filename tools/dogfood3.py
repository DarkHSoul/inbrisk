import json, subprocess, sys, threading, queue, time

EXE = r"C:\Users\Ahmet\AppData\Local\Programs\Inbrisk\inbrisk.exe"
proc = subprocess.Popen([EXE, "mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.PIPE, bufsize=0)
q = queue.Queue()
def reader():
    for line in proc.stdout:
        try: q.put(json.loads(line.decode("utf-8","replace")))
        except Exception: pass
threading.Thread(target=reader, daemon=True).start()
_id = 0
def send(method, params=None):
    global _id; _id += 1
    msg = {"jsonrpc":"2.0","id":_id,"method":method}
    if params is not None: msg["params"]=params
    proc.stdin.write((json.dumps(msg)+"\n").encode()); proc.stdin.flush()
    return msg["id"]
def wait_for(want_id, timeout=90):
    end=time.time()+timeout
    while time.time()<end:
        try: m=q.get(timeout=max(0.1,end-time.time()))
        except queue.Empty: break
        if m.get("id")==want_id: return m
    return None
def call(tool,args,show=1500):
    rid=send("tools/call",{"name":tool,"arguments":args})
    r=wait_for(rid,120)
    print(f"\n===== {tool} {json.dumps(args)[:200]} =====")
    if r is None: print("!! TIMEOUT"); return None
    res=r.get("result",r)
    for c in res.get("content",[]):
        t=c.get("text",""); print(t[:show])
        if len(t)>show: print(f"...(+{len(t)-show} chars)")
    if res.get("isError"): print(">> isError=TRUE")
    return res

send("initialize",{"protocolVersion":"2024-11-05","capabilities":{},
                   "clientInfo":{"name":"dogfood","version":"0.1"}})
wait_for(_id,30)
proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); proc.stdin.flush()

# 1. Dismiss the stuck notepad dialog via semantic click
call("computer_click", {"target":{"process":"notepad","role":"button","name":"Tamam"}})

# 2. set_value into the document
call("computer_set_value", {"value":"set_value ile yazildi",
     "target":{"process":"notepad","role":"document"}})
call("computer_inspect", {"target":{"process":"notepad","role":"document"}}, show=800)

# 3. hotkey fixed
call("computer_hotkey", {"key":"a","modifiers":["ctrl"]})
call("computer_type", {"text":"hotkey ctrl+a sonrasi"})
call("computer_inspect", {"target":{"process":"notepad","role":"document"}}, show=800)

# 4. computer_run mini-plan: click document, type, wait
call("computer_run", {"steps":[
    {"action":"click","target":{"process":"notepad","role":"document"}},
    {"action":"key","args":{"key":"end"}},
    {"action":"type","args":{"text":" | run-plani"}},
    {"action":"wait","args":{"ms":300}},
    {"action":"assert","target":{"process":"notepad","role":"document","valueContains":"run-plani"}}
]}, show=2500)

# 5. Chrome with debug port
call("computer_launch", {"app":"chrome","debugPort":9222})
time.sleep(2)
call("computer_adapter", {"action":"status","adapter":"chrome_devtools",
     "target":{"process":"chrome"},"args":{"port":9222}})
call("computer_adapter", {"action":"new_tab","adapter":"chrome_devtools",
     "target":{"process":"chrome"},"args":{"port":9222,"url":"https://example.com"}})
time.sleep(1)
call("computer_adapter", {"action":"get_text","adapter":"chrome_devtools",
     "target":{"process":"chrome"},"args":{"port":9222,"selector":"h1"}})
call("computer_adapter", {"action":"list_tabs","adapter":"chrome_devtools",
     "target":{"process":"chrome"},"args":{"port":9222}})

# 6. media adapter read-only-ish
call("computer_adapter", {"action":"media","adapter":"media","target":{},"args":{}})

# 7. wait_for / error diagnosis path (expect a clean failure for nonsense target)
call("computer_click", {"target":{"process":"notepad","role":"button","name":"OlmayanButon123"}})
call("computer_wait_for", {"query":"OlmayanSey456","args":{"timeoutMs":3000}})

proc.terminate()
