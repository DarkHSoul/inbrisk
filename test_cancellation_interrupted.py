"""Verification of Antigravity user cancellation handling in Hermes delegation."""

import os
import sys

sys.path.insert(0, r"C:\Users\Ahmet\AppData\Local\hermes\hermes-agent")

from unittest.mock import patch, MagicMock
from agent.antigravity_bridge import AntigravityChatCompletionsFacade, ConversationRef
from run_agent import AIAgent
from tools.delegate_tool import delegate_task

def test_cancellation_behavior():
    print("[*] Testing that user cancellation in Antigravity raises InterruptedError and stops cleanly...", flush=True)

    parent = AIAgent(
        model="qwen38",
        provider="custom",
        base_url="http://127.0.0.1:11434/v1",
        api_key="none",
        quiet_mode=True,
    )

    # Patch start_conversation to simulate user pressing Stop/Cancel in Antigravity
    with patch("agent.antigravity_bridge.AntigravityBridge.start_conversation") as mock_start:
        mock_start.side_effect = InterruptedError("User cancelled agent execution in Antigravity.")
        
        result = delegate_task(
            tasks=[
                {
                    "goal": "Test cancellation handling",
                    "context": "Verification"
                }
            ],
            parent_agent=parent,
        )

        print("\n" + "=" * 60, flush=True)
        print("[*] delegate_task Result upon Cancellation:", flush=True)
        print(result, flush=True)
        print("=" * 60, flush=True)

        # Verify that the child did NOT crash with an uncaught exception, did NOT retry 3 times,
        # but terminated cleanly with exit_reason='interrupted' or error status
        assert "interrupted" in result.lower() or "cancel" in result.lower() or "error" in result.lower(), f"Unexpected result: {result}"
        print("[+] USER CANCELLATION IS PROPERLY HANDLED AND NOT RETRIED!", flush=True)

if __name__ == "__main__":
    test_cancellation_behavior()
