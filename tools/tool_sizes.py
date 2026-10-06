import json, subprocess, os

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     text=True, bufsize=1)
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                         "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                    "clientInfo": {"name": "test", "version": "1"}}}) + "\n")
p.stdin.flush()
p.stdout.readline()
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list"}) + "\n")
p.stdin.flush()
res = json.loads(p.stdout.readline())
tools = res["result"]["tools"]
print(f"Total tools: {len(tools)}")
total_bytes = len(json.dumps(res["result"]).encode("utf-8"))
print(f"Total catalog bytes: {total_bytes}")
for t in sorted(tools, key=lambda x: len(json.dumps(x)), reverse=True):
    print(f"  {t['name']}: {len(json.dumps(t))} bytes")
p.terminate()
