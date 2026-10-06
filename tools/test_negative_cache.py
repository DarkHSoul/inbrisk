import json, subprocess, os, time

DLL = os.path.abspath(r"src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", DLL], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)

_id = 0
def req(m, params=None):
    global _id
    _id += 1
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": _id, "method": m, "params": params or {}}) + "\n")
    p.stdin.flush()
    while True:
        line = p.stdout.readline()
        if not line: return None
        try:
            msg = json.loads(line)
            if msg.get("id") == _id:
                return msg
        except Exception:
            pass

req("initialize", {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}})
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()

t0 = time.perf_counter()
r1 = req("tools/call", {"name": "computer_find", "arguments": {"name": "NonExistentDummyTarget"}})
ms1 = (time.perf_counter() - t0) * 1000

t0 = time.perf_counter()
r2 = req("tools/call", {"name": "computer_find", "arguments": {"name": "NonExistentDummyTarget"}})
ms2 = (time.perf_counter() - t0) * 1000

print(f"Negative Cold: {ms1:.2f} ms")
print(f"Negative Warm: {ms2:.2f} ms")
print(f"Speedup: {ms1/max(0.01, ms2):.1f}x")
print("Response text:", r2.get("result", {}).get("content", [{}])[0].get("text", "")[:100])
p.terminate()
