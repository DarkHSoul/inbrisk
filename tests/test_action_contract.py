import asyncio
import os
import sys

# Test the deployed copy in LocalAppData
installed_bin_dir = os.path.expandvars(r"%LOCALAPPDATA%\inbrisk\bin")
sys.path.insert(0, installed_bin_dir)

import inbrisk_pc_agent

async def run_tests():
    print("=== TESTING HARDENED ACTION CONTRACT ===")
    
    # 1. Initialize session
    bridge_exe = inbrisk_pc_agent.resolve_bridge_path()
    inbrisk_pc_agent.SESSION = inbrisk_pc_agent.InbriskPCSession(bridge_path=bridge_exe, debug=True)
    await inbrisk_pc_agent.SESSION.open()
    
    try:
        # Test 1: Unknown operation rejection
        res = await inbrisk_pc_agent.inbrisk_pc(op="fake_op")
        print(f"Unknown op test: ok={res.get('ok')}, code={res.get('error', {}).get('code')}")
        assert res.get("ok") is False
        assert res.get("error", {}).get("code") == "UnknownOperation"

        # Test 2: Unknown action rejection
        res = await inbrisk_pc_agent.inbrisk_pc(op="act", action="invalid_test_action")
        print(f"Unknown action test: ok={res.get('ok')}, code={res.get('error', {}).get('code')}")
        assert res.get("ok") is False
        assert res.get("error", {}).get("code") == "InvalidAction"

        # Test 3: Protected target rejection
        res = await inbrisk_pc_agent.inbrisk_pc(op="act", action="focus", target={"window": {"name": "Administrator: PowerShell"}})
        print(f"Protected target test: ok={res.get('ok')}, code={res.get('error', {}).get('code')}")
        assert res.get("ok") is False
        assert res.get("error", {}).get("code") == "ProtectedTarget"

        # Test 4: Loop detection
        # Attempt 1
        res1 = await inbrisk_pc_agent.inbrisk_pc(op="act", action="focus", target={"window": {"name": "NonExistentWindow_9999"}})
        # Attempt 2
        res2 = await inbrisk_pc_agent.inbrisk_pc(op="act", action="focus", target={"window": {"name": "NonExistentWindow_9999"}})
        # Attempt 3 (Should be caught by loop detector)
        res3 = await inbrisk_pc_agent.inbrisk_pc(op="act", action="focus", target={"window": {"name": "NonExistentWindow_9999"}})
        print(f"Loop detection test: ok={res3.get('ok')}, code={res3.get('error', {}).get('code')}")
        assert res3.get("ok") is False
        assert res3.get("error", {}).get("code") == "RepeatedActionLoop"

        # Reset loop detector via observation
        await inbrisk_pc_agent.inbrisk_pc(op="observe")

        # Test 5: Clipboard write & read
        await inbrisk_pc_agent.inbrisk_pc(op="clipboard_write", text="Inbrisk Contract Test 42")
        clip_res = await inbrisk_pc_agent.inbrisk_pc(op="clipboard_read")
        print(f"Clipboard test: text={clip_res.get('clipboard_text')}")
        assert clip_res.get("clipboard_text") == "Inbrisk Contract Test 42"

        # Test 6: High-level launch_app (idempotent, reuses Chrome)
        launch_res = await inbrisk_pc_agent.inbrisk_pc(op="launch_app", app="chrome")
        print(f"Launch app test: ok={launch_res.get('ok')}, reused={launch_res.get('reused')}, verified={launch_res.get('verified')}")
        assert launch_res.get("ok") is True

        # Test 7: High-level navigate_url
        nav_res = await inbrisk_pc_agent.inbrisk_pc(op="navigate_url", url="https://example.com")
        print(f"Navigate URL test: ok={nav_res.get('ok')}, verified={nav_res.get('verified')}")
        assert nav_res.get("ok") is True

        # Test 8: High-level scroll
        scroll_res = await inbrisk_pc_agent.inbrisk_pc(op="scroll", direction="down", amount=400)
        print(f"Scroll test: ok={scroll_res.get('ok')}, verified={scroll_res.get('verified')}")
        assert scroll_res.get("ok") is True

        # Test 9: High-level select_text and clipboard
        select_res = await inbrisk_pc_agent.inbrisk_pc(op="select_text", text="Example")
        print(f"Select text test: ok={select_res.get('ok')}, text={select_res.get('selected_text')}")
        assert select_res.get("ok") is True
        assert "example" in select_res.get("selected_text", "").lower()

        print("=== ALL UNIT CONTRACT TESTS PASSED ===")
    finally:
        await inbrisk_pc_agent.SESSION.close()

if __name__ == "__main__":
    if sys.platform == "win32":
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())
    asyncio.run(run_tests())
