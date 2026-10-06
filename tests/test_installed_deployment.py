import asyncio
import os
import sys

# Force importing from installed AppData location
installed_bin_dir = os.path.expandvars(r"%LOCALAPPDATA%\inbrisk\bin")
sys.path.insert(0, installed_bin_dir)

import inbrisk_pc_agent

async def run_deployment_verification():
    print(f"=== DEPLOYMENT ACCEPTANCE VERIFICATION ===")
    print(f"Imported module from: {inbrisk_pc_agent.__file__}")
    assert os.path.abspath(inbrisk_pc_agent.__file__) == os.path.abspath(os.path.join(installed_bin_dir, "inbrisk_pc_agent.py")), \
        "Module was not loaded from deployed AppData directory!"
    
    # 1. Verify bridge resolution points to deployed binary
    resolved_bridge = inbrisk_pc_agent.resolve_bridge_path()
    print(f"Resolved bridge binary: {resolved_bridge}")
    assert os.path.abspath(resolved_bridge) == os.path.abspath(os.path.join(installed_bin_dir, "inbrisk-bridge.exe")), \
        "Bridge binary did not resolve to installed AppData location!"

    # 2. Test InbriskPCSession lifecycle with deployed bridge
    session = inbrisk_pc_agent.InbriskPCSession(bridge_path=resolved_bridge, debug=True)
    await session.open()
    initial_pid = session.pid
    print(f"Bridge session started. PID: {initial_pid}, Generation: {session.bridge_generation}")

    # 3. Step 1: Status query
    status_res = await session.execute({"op": "status"})
    print(f"Status result: ok={status_res.get('ok')}")
    assert status_res.get("ok") is True, f"Status failed: {status_res}"
    assert session.pid == initial_pid, "PID changed during status query!"

    # 4. Step 2: Windows query
    windows_res = await session.execute({"op": "windows"})
    print(f"Windows result: ok={windows_res.get('ok')}, window_count={len(windows_res.get('data', {}).get('windows', []))}")
    assert windows_res.get("ok") is True, f"Windows failed: {windows_res}"
    assert session.pid == initial_pid, "PID changed during windows query (must be long-lived persistent bridge)!"

    # 5. Step 3: Observe query
    observe_res = await session.execute({"op": "observe"})
    print(f"Observe result: ok={observe_res.get('ok')}")
    assert observe_res.get("ok") is True, f"Observe failed: {observe_res}"
    assert session.pid == initial_pid, "PID changed during observe query!"

    # 6. Verify Agent tool schema has zero MCP and zero pc_exec
    print(f"Checking inbrisk_pc tool declaration...")
    tool_fn = inbrisk_pc_agent.inbrisk_pc
    doc = tool_fn.__doc__
    assert "pc_exec" not in doc, "pc_exec found in tool doc!"
    assert "pc_open" not in doc, "pc_open found in tool doc!"

    await session.close()
    print(f"Session closed successfully. Verified PID {initial_pid} stayed alive for all steps.")
    print("=== ACCEPTANCE VERIFICATION PASSED ===")

if __name__ == "__main__":
    if sys.platform == "win32":
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())
    asyncio.run(run_deployment_verification())
