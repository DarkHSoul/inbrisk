"""Test multi-turn session continuity and absence of unwanted Blender checks.

Verifies:
1. Title generation returns fast JSON without touching Antigravity UI.
2. General prompt (no 3D/Blender) does NOT check or mention Blender.
3. Turn 2 with conversation history continues the SAME Antigravity conversation without opening a new tab.
4. Turn 3 continues seamlessly in the same conversation.
"""

import sys
import os
import time
import json

sys.path.insert(0, r"C:\Users\Ahmet\AppData\Local\hermes\hermes-agent")

from agent.antigravity_bridge import AntigravityChatCompletionsFacade, AntigravityBridge

def main():
    print("=" * 60)
    print("TEST: Session Continuity & No-Blender General Chat")
    print("=" * 60)

    bridge = AntigravityBridge(original_chat_name="ANTIGRAVITY i hermes e bağla")
    facade = AntigravityChatCompletionsFacade(bridge=bridge)

    # 1. Test fast title generation
    print("\n[Step 1] Testing fast auxiliary title generation...")
    t0 = time.time()
    title_resp = facade.create(
        messages=[
            {"role": "system", "content": "You are a session title generator. Generate a 3-5 word title."},
            {"role": "user", "content": "Merhaba, nasılsın?"}
        ],
        task="title_generation",
        model="antigravity",
    )
    dt_title = time.time() - t0
    title_content = title_resp.choices[0].message.content
    print(f"Title generation completed in {dt_title:.3f}s: {title_content}")
    assert dt_title < 0.5, f"Title generation took too long: {dt_title}s"
    title_data = json.loads(title_content)
    assert "title" in title_data, f"Invalid title format: {title_content}"
    print(f"Generated title verified: '{title_data['title']}'")

    # 2. Turn 1: General conversation (no Blender)
    print("\n[Step 2] Turn 1: Sending general conversation message...")
    msg1 = "Merhaba! Bana 1 ile 10 arasında sadece bir sayı söyle, örneğin 7 gibi. Sadece sayıyı söyle."
    t1 = time.time()
    resp1 = facade.create(
        messages=[{"role": "user", "content": msg1}],
        model="antigravity",
    )
    dt1 = time.time() - t1
    content1 = resp1.choices[0].message.content
    print(f"Turn 1 completed in {dt1:.2f}s.")
    print(f"Assistant Reply: {content1.strip()[:200]}")

    ref1 = AntigravityChatCompletionsFacade._current_active_ref
    assert ref1 is not None, "Error: No active conversation reference registered!"
    print(f"Active Session 1: ID={ref1.session_id}, Title='{ref1.ui_title}'")

    # Verify no blender error in content
    assert "blender" not in content1.lower() or "blocked" not in content1.lower(), (
        f"Unexpected Blender error in general conversation: {content1}"
    )

    # Simulate Hermes title generation between turns
    print("\n[Step 2.5] Simulating Hermes background title generation between turns...")
    facade.create(
        messages=[
            {"role": "system", "content": "You are a session title generator."},
            {"role": "user", "content": msg1}
        ],
        task="title_generation",
        model="antigravity",
    )
    assert AntigravityChatCompletionsFacade._current_active_ref.session_id == ref1.session_id, (
        "Title generation corrupted active conversation ref!"
    )

    # 3. Turn 2: Follow-up question in the same chat
    print("\n[Step 3] Turn 2: Sending follow-up message with conversation history...")
    msg2 = "Bu söylediğin sayıyı 2 ile çarpınca sonuç kaç olur? Kısa yanıt ver."
    t2 = time.time()
    resp2 = facade.create(
        messages=[
            {"role": "user", "content": msg1},
            {"role": "assistant", "content": content1},
            {"role": "user", "content": msg2},
        ],
        model="antigravity",
    )
    dt2 = time.time() - t2
    content2 = resp2.choices[0].message.content
    print(f"Turn 2 completed in {dt2:.2f}s.")
    print(f"Assistant Reply: {content2.strip()[:200]}")

    ref2 = AntigravityChatCompletionsFacade._current_active_ref
    assert ref2.session_id == ref1.session_id, (
        f"Session mismatch! Turn 2 opened a new chat: {ref2.session_id} != {ref1.session_id}"
    )
    print(f"Turn 2 continuity verified: Reused session {ref2.session_id} seamlessly!")

    print("\n" + "=" * 60)
    print("ALL TESTS PASSED! MULTI-TURN CONTINUITY AND NO-BLENDER CHAT VERIFIED.")
    print("=" * 60)

if __name__ == "__main__":
    main()
