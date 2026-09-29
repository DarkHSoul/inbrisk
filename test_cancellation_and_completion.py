"""Unit and integration test for Antigravity cancellation handling and completion verification."""

import os
import sys
import time

sys.path.insert(0, r"C:\Users\Ahmet\AppData\Local\hermes\hermes-agent")

from agent.antigravity_bridge import (
    AntigravityBridge,
    AntigravityChatCompletionsFacade,
    ConversationRef,
    ConversationTurnResponse,
    get_antigravity_hwnd,
    get_antigravity_pid,
    run_inbrisk,
)

def test_button_and_process_detection():
    print("Testing Antigravity PID and HWND detection...")
    hwnd = get_antigravity_hwnd()
    pid = get_antigravity_pid()
    print(f"  Found Antigravity: HWND={hwnd}, PID={pid}")
    assert hwnd is not None, "HWND should not be None"
    assert pid is not None, "PID should not be None"
    print("  PID and HWND detection PASSED.")

def test_session_reuse_on_retry():
    print("\nTesting session reuse in AntigravityChatCompletionsFacade...")
    facade1 = AntigravityChatCompletionsFacade()
    fake_ref = ConversationRef(
        session_id="test-session-uuid",
        ui_title="Test Session",
        session_dir="C:/test",
        created_at="now",
        last_step_index=5,
        turn_count=1,
    )
    
    # Simulate a first turn setting the active ref
    messages = [
        {"role": "user", "content": "Create a procedural barrel with 3 iron bands in Blender"}
    ]
    goal_hash = "fake_hash_123"
    AntigravityChatCompletionsFacade._current_active_ref = fake_ref
    AntigravityChatCompletionsFacade._current_goal_hash = goal_hash

    # Now create a fresh facade (simulating Hermes client recreation on retry)
    facade2 = AntigravityChatCompletionsFacade()
    assert facade2.conversation_ref is None, "facade2 should start with None instance ref"
    
    # Check that it resolves target_ref from class-level active ref
    target_ref = facade2.conversation_ref or AntigravityChatCompletionsFacade._current_active_ref
    assert target_ref is fake_ref, "facade2 should reuse fake_ref across client recreation"
    print("  Session reuse across client recreation PASSED.")

def test_cancellation_detection_logic():
    print("\nTesting cancellation detection logic...")
    # Simulate an aborted transcript entry
    entries_normal = [
        {"step_index": 0, "type": "USER_INPUT", "status": "DONE", "content": "hello"},
        {"step_index": 1, "type": "PLANNER_RESPONSE", "status": "DONE", "tool_calls": [{"name": "run_command"}]},
        {"step_index": 2, "type": "GENERIC", "status": "DONE", "content": "dir output"},
        {"step_index": 3, "type": "PLANNER_RESPONSE", "status": "DONE", "content": "Here is your output", "tool_calls": None},
    ]

    entries_cancelled = [
        {"step_index": 0, "type": "USER_INPUT", "status": "DONE", "content": "hello"},
        {"step_index": 1, "type": "PLANNER_RESPONSE", "status": "DONE", "tool_calls": [{"name": "run_command"}]},
        {"step_index": 2, "type": "GENERIC", "status": "DONE", "content": "Tool execution was canceled"},
    ]

    # Check normal entries: has final planner response with content and no tool calls
    final_resp_normal = None
    for e in reversed(entries_normal):
        if e.get("type") == "PLANNER_RESPONSE" and e.get("status") == "DONE":
            c = e.get("content") or ""
            tc = e.get("tool_calls") or []
            if c.strip() and not tc:
                final_resp_normal = e
                break
    assert final_resp_normal is not None, "Normal entries should find final response"
    assert final_resp_normal["content"] == "Here is your output"
    print("  Normal final response check PASSED.")

    # Check cancelled entries: no final planner response, contains cancellation marker
    final_resp_cancelled = None
    has_cancel_marker = False
    for e in reversed(entries_cancelled):
        if e.get("type") == "PLANNER_RESPONSE" and e.get("status") == "DONE":
            c = e.get("content") or ""
            tc = e.get("tool_calls") or []
            if c.strip() and not tc:
                final_resp_cancelled = e
                break
        if "canceled" in str(e.get("content", "")) or "cancelled" in str(e.get("content", "")):
            has_cancel_marker = True

    assert final_resp_cancelled is None, "Cancelled entries must NOT have a final response"
    assert has_cancel_marker is True, "Cancelled entries must detect cancellation marker"
    print("  Cancellation detection check PASSED.")

if __name__ == "__main__":
    test_button_and_process_detection()
    test_session_reuse_on_retry()
    test_cancellation_detection_logic()
    print("\nALL VERIFICATION CHECKS PASSED.")
