import subprocess, json, os

dll = os.path.abspath("src/Inbrisk.Mcp/bin/Debug/net8.0-windows10.0.19041.0/inbrisk-mcp.dll")
p = subprocess.Popen(['dotnet', dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)
p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': 1, 'method': 'initialize', 'params': {'protocolVersion': '2025-03-26', 'capabilities': {}, 'clientInfo': {'name': 'bench', 'version': '1'}}}) + '\n')
p.stdin.flush()
p.stdout.readline()
p.stdin.write(json.dumps({'jsonrpc': '2.0', 'method': 'notifications/initialized'}) + '\n')
p.stdin.flush()
p.stdin.write(json.dumps({'jsonrpc': '2.0', 'id': 2, 'method': 'tools/list', 'params': {}}) + '\n')
p.stdin.flush()
cur_str = p.stdout.readline()
p.terminate()

cur_obj = json.loads(cur_str)
cur_tools = {t['name']: t for t in cur_obj['result']['tools']}
print(f"Current tools: {len(cur_tools)}, Total wire bytes: {len(cur_str.encode('utf-8'))}")
for name, t in sorted(cur_tools.items()):
    b = len(json.dumps(t).encode('utf-8'))
    print(f"  {name}: {b} bytes")

with open("tools/current_tools.json", "w", encoding="utf-8") as f:
    json.dump(cur_obj, f, indent=2)
