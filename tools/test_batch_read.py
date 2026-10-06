import json, subprocess, os

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                     stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                     text=True, bufsize=1)
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                         "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                    "clientInfo": {"name": "batch-read-test", "version": "1"}}}) + "\n")
p.stdin.flush()
p.stdout.readline()
p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()

req = {
    "jsonrpc": "2.0", "id": 2, "method": "tools/call",
    "params": {
        "name": "computer_batch",
        "arguments": {
            "steps": [
                {"do": "wait", "ms": 20}
            ],
            "read": [
                {"target": "NonExistentTargetToTestNotFound", "props": ["name", "value"]}
            ],
            "detail": "slim"
        }
    }
}
p.stdin.write(json.dumps(req) + "\n")
p.stdin.flush()

res = json.loads(p.stdout.readline())
print("Batch With Read Result:", json.dumps(res, indent=2))

p.terminate()
