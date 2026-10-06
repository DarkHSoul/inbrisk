import os
import sys
import json
import argparse

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

BRIDGE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".chat_bridge")
OUTBOX_FILE = os.path.join(BRIDGE_DIR, "outbox.json")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", type=int, required=True, help="Message ID to reply to")
    parser.add_argument("--text", type=str, required=True, help="Response text")
    args = parser.parse_args()

    payload = {
        "id": args.id,
        "reply": args.text
    }

    tmp_out = OUTBOX_FILE + ".tmp"
    with open(tmp_out, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    os.replace(tmp_out, OUTBOX_FILE)
    print(f"Reply for ID {args.id} sent successfully.")

if __name__ == "__main__":
    main()
