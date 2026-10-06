import json, subprocess, os

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp", "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")
p = subprocess.Popen(["dotnet", os.path.abspath(DLL)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1)

p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-03-26", "capabilities": {}, "clientInfo": {"name": "bench", "version": "1"}}}) + "\n")
p.stdin.flush()
p.stdout.readline()

p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
p.stdin.flush()

p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}) + "\n")
p.stdin.flush()
tools_str = p.stdout.readline()
p.terminate()

tools_obj = json.loads(tools_str)
tools_list = tools_obj["result"]["tools"]
total_bytes = len(tools_str.encode("utf-8"))
result_bytes = len(json.dumps(tools_obj["result"]).encode("utf-8"))

baseline_raw = 56499
baseline_result = 58561

delta_raw = total_bytes - baseline_raw
pct_raw = (delta_raw / baseline_raw) * 100

delta_res = result_bytes - baseline_result
pct_res = (delta_res / baseline_result) * 100

print(f"Total tools: {len(tools_list)}")
print(f"Raw wire bytes: {total_bytes} (baseline: {baseline_raw}, delta: {delta_raw:+d} bytes, {pct_raw:+.2f}%)")
print(f"Result object bytes: {result_bytes} (baseline: {baseline_result}, delta: {delta_res:+d} bytes, {pct_res:+.2f}%)")

tools_list.sort(key=lambda t: len(json.dumps(t).encode("utf-8")), reverse=True)
print("\nTop 6 largest tools:")
for t in tools_list[:6]:
    b = len(json.dumps(t).encode("utf-8"))
    print(f"  {t['name']}: {b} bytes")
