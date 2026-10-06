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
DISPATCH_FILE = os.path.join(BRIDGE_DIR, "dispatch.json")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", type=int, required=True, help="Message ID")
    parser.add_argument("--command", type=str, required=True, help="Command to execute in terminal")
    args = parser.parse_args()

    payload = {
        "id": args.id,
        "action": "run_cmd",
        "command": args.command
    }

    tmp = DISPATCH_FILE + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    os.replace(tmp, DISPATCH_FILE)
    print(f"Command dispatched to terminal: {args.command}")

if __name__ == "__main__":
    main()
