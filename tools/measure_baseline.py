import json, subprocess, threading, queue, time, sys, os

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0",
                   "inbrisk-mcp.dll")
proc = subprocess.Popen(
    ["dotnet", os.path.abspath(DLL)],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL, text=True, bufsize=1)

q = queue.Queue()
def reader():
    for line in proc.stdout:
        try: q.put(json.loads(line))
        except Exception: pass
threading.Thread(target=reader, daemon=True).start()

_id = 0
def send(m, p=None):
    global _id; _id += 1
    msg = {"jsonrpc": "2.0", "id": _id, "method": m}
    if p is not None: msg["params"] = p
    proc.stdin.write(json.dumps(msg) + "\n"); proc.stdin.flush()
    return msg["id"]

def call(m, p=None, timeout=60):
    rid = send(m, p)
    end = time.time() + timeout
    while time.time() < end:
        try:
            msg = q.get(timeout=max(0.1, end - time.time()))
            if msg.get("id") == rid: return msg
        except queue.Empty: break
    return None

def tool_call(name, args=None):
    t0 = time.perf_counter()
    res = call("tools/call", {"name": name, "arguments": args or {}})
    dur_ms = (time.perf_counter() - t0) * 1000
    content = res.get("result", {}).get("content", []) if res else []
    text = content[0].get("text", "") if content else ""
    return res, text, dur_ms

call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                    "clientInfo": {"name": "baseline-probe", "version": "1.0"}})
proc.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
proc.stdin.flush()

report = {}

# 1. Capabilities
_, cap_text, cap_ms = tool_call("computer_capabilities")
report["capabilities"] = {
    "chars": len(cap_text),
    "bytes": len(cap_text.encode("utf-8")),
    "duration_ms": round(cap_ms, 2)
}

# 2. Apps catalog latency (cold)
_, apps_text, apps_ms = tool_call("computer_apps", {"limit": 20})
report["apps_cold"] = {
    "chars": len(apps_text),
    "duration_ms": round(apps_ms, 2)
}

# 2b. Apps catalog latency (warm)
_, apps_warm_text, apps_warm_ms = tool_call("computer_apps", {"limit": 20})
report["apps_warm"] = {
    "chars": len(apps_warm_text),
    "duration_ms": round(apps_warm_ms, 2)
}

# 3. Find twice (same query in same session) to test find cache
_, f1_text, f1_ms = tool_call("computer_find", {"name": "Inbrisk"})
_, f2_text, f2_ms = tool_call("computer_find", {"name": "Inbrisk"})
report["find_repeat"] = {
    "find1_ms": round(f1_ms, 2),
    "find2_ms": round(f2_ms, 2),
    "f1_chars": len(f1_text),
    "f2_chars": len(f2_text)
}

# 4. Windows list
_, win_text, win_ms = tool_call("computer_windows")
report["windows"] = {
    "chars": len(win_text),
    "duration_ms": round(win_ms, 2)
}

# 5. Tools list catalog
t_list = call("tools/list")
tools = t_list.get("result", {}).get("tools", []) if t_list else []
tools_raw = json.dumps(t_list.get("result", {}), ensure_ascii=False) if t_list else ""
report["tools_catalog"] = {
    "tool_count": len(tools),
    "bytes": len(tools_raw.encode("utf-8")),
    "chars": len(tools_raw)
}

proc.terminate()
print(json.dumps(report, indent=2))
