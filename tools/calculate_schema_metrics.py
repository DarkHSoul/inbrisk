import json, subprocess, os

DLL = os.path.abspath("src/Inbrisk.Mcp/bin/Debug/net8.0-windows10.0.19041.0/inbrisk-mcp.dll")
env = os.environ.copy()
env["INBRISK_PANIC_HOTKEY"] = "Ctrl+Alt+F7"
env["INBRISK_RESUME_HOTKEY"] = "Ctrl+Alt+Shift+F7"
env["INBRISK_TOOL_PROFILE"] = "full"

p = subprocess.Popen(["dotnet", DLL, "--direct"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, bufsize=1, env=env)
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "bench", "version": "1.0"}}}) + "\n")
p.stdin.flush()
p.stdout.readline()
p.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}) + "\n")
p.stdin.flush()
p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}) + "\n")
p.stdin.flush()
res = json.loads(p.stdout.readline())
tools = res["result"]["tools"]
p.terminate()

core_names = {
    "computer_batch", "computer_do", "computer_run", "computer_launch",
    "computer_close_window", "computer_windows", "computer_observe",
    "computer_find", "computer_inspect", "computer_read", "computer_click",
    "computer_type", "computer_hotkey", "computer_screenshot",
    "computer_reset_input", "computer_capabilities"
}

browser_names = {
    "browser_browse", "browser_click", "browser_type", "browser_snapshot",
    "browser_tabs", "browser_content", "browser_screenshot"
}

core_tools = [t for t in tools if t["name"] in core_names]
browser_tools = [t for t in tools if t["name"] in browser_names]
core_plus_browser = [t for t in tools if t["name"] in core_names or t["name"] in browser_names]

def report(name, t_list):
    payload = {"jsonrpc": "2.0", "id": 2, "result": {"tools": t_list}}
    wire_bytes = len(json.dumps(payload).encode("utf-8"))
    result_bytes = len(json.dumps({"tools": t_list}).encode("utf-8"))
    print(f"{name}: count={len(t_list)}, wire_bytes={wire_bytes}, result_bytes={result_bytes}")

report("Default Core (16 tools)", core_tools)
report("Browser Contextual (7 tools)", browser_tools)
report("Core + Browser (23 tools)", core_plus_browser)
report("Full Union (54 tools)", tools)
