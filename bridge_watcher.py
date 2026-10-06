import os
import sys
import json
import time
import argparse

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

BRIDGE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".chat_bridge")
INBOX_FILE = os.path.join(BRIDGE_DIR, "inbox.json")
STATE_FILE = os.path.join(BRIDGE_DIR, "state.json")

def get_state():
    if os.path.exists(STATE_FILE):
        try:
            with open(STATE_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"last_id": 0, "last_type": ""}

def save_state(s):
    with open(STATE_FILE, "w", encoding="utf-8") as f:
        json.dump(s, f, indent=2)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--timeout", type=int, default=1200, help="Max wait seconds")
    args = parser.parse_args()

    state = get_state()
    last_id = state.get("last_id", 0)
    last_type = state.get("last_type", "")
    last_ts = state.get("last_ts", 0.0)

    start_time = time.time()

    while time.time() - start_time < args.timeout:
        if os.path.exists(INBOX_FILE):
            try:
                with open(INBOX_FILE, "r", encoding="utf-8") as f:
                    data = json.load(f)

                msg_id = int(data.get("id", 0))
                msg_type = data.get("type", "user_msg")
                msg_ts = float(data.get("timestamp", 0.0))

                # Yeni bir mesaj veya aynı id'ye ait yeni bir tool_result mı?
                is_new = (msg_id > last_id) or (msg_id == last_id and msg_type != last_type and msg_ts > last_ts)

                if is_new:
                    save_state({"last_id": msg_id, "last_type": msg_type, "last_ts": msg_ts})
                    
                    if msg_type == "user_msg":
                        text = data.get("text", "")
                        print(f"=== BRIDGE_USER_MSG ===\nID: {msg_id}\nTEXT: {text}\n=== END ===")
                    elif msg_type == "tool_result":
                        cmd = data.get("command", "")
                        out = data.get("output", "")
                        code = data.get("exit_code", 0)
                        print(f"=== BRIDGE_TOOL_RESULT ===\nID: {msg_id}\nCMD: {cmd}\nCODE: {code}\nOUTPUT:\n{out}\n=== END ===")
                    sys.exit(0)
            except Exception:
                pass
        time.sleep(0.12)

    print("=== BRIDGE_TIMEOUT ===")
    sys.exit(0)

if __name__ == "__main__":
    main()
