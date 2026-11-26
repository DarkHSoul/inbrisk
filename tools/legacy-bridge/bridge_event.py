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
EVENTS_FILE = os.path.join(BRIDGE_DIR, "events.jsonl")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--id", type=int, required=True, help="Message ID")
    parser.add_argument("--type", type=str, choices=["tool_call", "tool_output", "thought"], required=True)
    parser.add_argument("--name", type=str, default="", help="Tool name")
    parser.add_argument("--data", type=str, default="", help="Event text/data")
    args = parser.parse_args()

    record = {
        "id": args.id,
        "type": args.type,
        "name": args.name,
        "data": args.data
    }

    with open(EVENTS_FILE, "a", encoding="utf-8") as f:
        f.write(json.dumps(record, ensure_ascii=False) + "\n")

if __name__ == "__main__":
    main()
