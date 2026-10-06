import json, subprocess, os, time

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")

p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     text=True, bufsize=1)
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 1, "method": "initialize",
    "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}
}) + "\n")
p.stdin.flush()
p.stdout.readline()
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()

# Test 1: computer_click with elementId
t0 = time.time()
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 2, "method": "tools/call",
    "params": {"name": "computer_click", "arguments": {"elementId": "uia_btn_1"}}
}) + "\n")
p.stdin.flush()
resp1 = p.stdout.readline()
t1 = time.time()
print(f"Click elementId time: {(t1-t0)*1000:.2f}ms")

# Test 2: computer_set_value with elementId
t0 = time.time()
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 3, "method": "tools/call",
    "params": {"name": "computer_set_value", "arguments": {"elementId": "uia_field_1", "value": "val"}}
}) + "\n")
p.stdin.flush()
resp2 = p.stdout.readline()
t1 = time.time()
print(f"SetValue elementId time: {(t1-t0)*1000:.2f}ms")

# Test 3: computer_batch with elementId steps
t0 = time.time()
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 4, "method": "tools/call",
    "params": {"name": "computer_batch", "arguments": {
        "steps": [{"do": "click", "t": "uia_btn_1"}, {"do": "click", "t": "uia_btn_2"}]
    }}
}) + "\n")
p.stdin.flush()
resp3 = p.stdout.readline()
t1 = time.time()
print(f"Batch elementId time: {(t1-t0)*1000:.2f}ms")

# Test 4: computer_batch with set (target with elementId)
t0 = time.time()
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 5, "method": "tools/call",
    "params": {"name": "computer_batch", "arguments": {
        "set": [{"target": "uia_field_1", "value": "val1"}]
    }}
}) + "\n")
p.stdin.flush()
resp4 = p.stdout.readline()
t1 = time.time()
print(f"Batch set elementId time: {(t1-t0)*1000:.2f}ms")

# Test 5: computer_launch with nonexistent executable and waitFor: none
t0 = time.time()
p.stdin.write(json.dumps({
    "jsonrpc": "2.0", "id": 6, "method": "tools/call",
    "params": {"name": "computer_launch", "arguments": {
        "executable": "nonexistent.exe",
        "waitFor": "none",
        "timeoutMs": 1
    }}
}) + "\n")
p.stdin.flush()
resp5 = p.stdout.readline()
t1 = time.time()
print(f"Launch executable time: {(t1-t0)*1000:.2f}ms")

p.terminate()
