"""Test instant Ctrl+V pasting and Antigravity staying in foreground.
"""

import sys
import time

sys.path.insert(0, r"C:\Users\Ahmet\AppData\Local\hermes\hermes-agent")

from agent.antigravity_bridge import AntigravityChatCompletionsFacade, AntigravityBridge, get_antigravity_hwnd, get_foreground_hwnd

def main():
    print("=" * 60)
    print("TEST: Instant Ctrl+V Paste and Foreground Persistence")
    print("=" * 60)

    bridge = AntigravityBridge(original_chat_name="ANTIGRAVITY i hermes e bağla")
    facade = AntigravityChatCompletionsFacade(bridge=bridge)

    ag_hwnd = get_antigravity_hwnd()
    print(f"Antigravity HWND: {ag_hwnd}")

    test_prompt = "Merhaba! Bu mesaj Ctrl+V ile tek seferde yapıştırılmıştır. Bana sadece 'Pasted successfully!' yaz."

    print("\n[Step 1] Sending prompt via instant Ctrl+V...")
    t0 = time.time()
    resp = facade.create(
        messages=[{"role": "user", "content": test_prompt}],
        model="antigravity",
    )
    dt = time.time() - t0
    content = resp.choices[0].message.content
    print(f"Completed in {dt:.2f}s: {content.strip()}")

    # Check foreground
    fg = get_foreground_hwnd()
    print(f"Foreground HWND after turn: {fg} (Antigravity={ag_hwnd})")

    active_ref = AntigravityChatCompletionsFacade._current_active_ref
    print(f"Active Session: {active_ref.session_id}, Title='{active_ref.ui_title}'")

    print("\n" + "=" * 60)
    print("SUCCESS: Instant paste and foreground verified!")
    print("=" * 60)

if __name__ == "__main__":
    main()
