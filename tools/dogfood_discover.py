import json, subprocess, threading, queue, time, sys, io
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

EXE = r"C:\Users\Ahmet\AppData\Local\Programs\Inbrisk\inbrisk.exe"
proc = subprocess.Popen([EXE,"mcp"], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
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
def wait_for(wid,t=60):
    end=time.time()+t
    while time.time()<end:
        try: m=q.get(timeout=max(0.1,end-time.time()))
        except queue.Empty: break
        if m.get("id")==wid: return m
    return None

rid=send("initialize",{"protocolVersion":"2024-11-05","capabilities":{},
          "clientInfo":{"name":"fresh-ai","version":"0.1"}})
r=wait_for(rid,30)
print("=== INITIALIZE RESULT ===")
print(json.dumps(r.get("result",{}), indent=1, ensure_ascii=False)[:4000])

proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); proc.stdin.flush()

rid=send("tools/list"); r=wait_for(rid,30)
print("\n=== TOOL DESCRIPTIONS ===")
for t in r["result"]["tools"]:
    print(f"- {t['name']}: {t.get('description','')[:220]}")

# does capabilities mention workflow?
rid=send("tools/call",{"name":"computer_capabilities","arguments":{}})
r=wait_for(rid,60)
txt = r["result"]["content"][0]["text"]
d = json.loads(txt)
print("\n=== CAPABILITIES KEYS ===")
print(list(d.keys()))
for k in ["workflow","hints","guidance","gettingStarted","recommendedOrder"]:
    if k in d: print(k, "=>", json.dumps(d[k], ensure_ascii=False)[:800])

proc.terminate()
