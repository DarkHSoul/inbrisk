import json, subprocess, os, time

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     text=True, bufsize=1)
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                         "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                    "clientInfo": {"name": "test-inspect", "version": "1"}}}) + "\n")
p.stdin.flush()
p.stdout.readline()
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()

# 1. Get a window HWND
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/call",
                         "params": {"name": "computer_windows", "arguments": {}}}) + "\n")
p.stdin.flush()
win_res = json.loads(p.stdout.readline())

# 2. Inspect call 1
t0 = time.perf_counter()
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
                         "params": {"name": "computer_inspect", "arguments": {}}}) + "\n")
p.stdin.flush()
ins1 = json.loads(p.stdout.readline())
t1 = (time.perf_counter() - t0) * 1000

# 3. Inspect call 2 immediately
t0 = time.perf_counter()
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 4, "method": "tools/call",
                         "params": {"name": "computer_inspect", "arguments": {}}}) + "\n")
p.stdin.flush()
ins2 = json.loads(p.stdout.readline())
t2 = (time.perf_counter() - t0) * 1000

print(f"Inspect 1: {t1:.2f} ms")
print(f"Inspect 2: {t2:.2f} ms")
print(f"Speedup: {t1/max(0.01, t2):.2f}x")

p.terminate()
