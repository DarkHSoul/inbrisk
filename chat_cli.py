import os
import sys
import json
import time
import subprocess

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

BRIDGE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), ".chat_bridge")
INBOX_FILE = os.path.join(BRIDGE_DIR, "inbox.json")
OUTBOX_FILE = os.path.join(BRIDGE_DIR, "outbox.json")
DISPATCH_FILE = os.path.join(BRIDGE_DIR, "dispatch.json")
STATE_FILE = os.path.join(BRIDGE_DIR, "state.json")

os.makedirs(BRIDGE_DIR, exist_ok=True)

def get_current_id():
    if os.path.exists(STATE_FILE):
        try:
            with open(STATE_FILE, "r", encoding="utf-8") as f:
                data = json.load(f)
                return int(data.get("last_id", 0))
        except Exception:
            pass
    return 0

def write_inbox(payload):
    tmp_inbox = INBOX_FILE + ".tmp"
    with open(tmp_inbox, "w", encoding="utf-8") as f:
        json.dump(payload, f, ensure_ascii=False, indent=2)
    os.replace(tmp_inbox, INBOX_FILE)

def main():
    print("=" * 70)
    print("  Antigravity Live Terminal Agent (Doğrudan Terminal İçi İcra)")
    print("  - Tüm tool ve CMD komutları BU terminal penceresinde canlı çalışır.")
    print("  - Çıkmak için: 'exit', 'quit' veya 'q' yazın.")
    print("=" * 70)

    msg_id = get_current_id()

    while True:
        try:
            user_msg = input("\nSen > ").strip()
        except (KeyboardInterrupt, EOFError):
            print("\nÇıkış yapılıyor...")
            break

        if not user_msg:
            continue

        if user_msg.lower() in ("exit", "quit", "q"):
            print("Görüşmek üzere!")
            break

        msg_id += 1

        # Kullanıcı mesajını gönder
        write_inbox({
            "id": msg_id,
            "type": "user_msg",
            "timestamp": time.time(),
            "text": user_msg
        })

        spinner = ["|", "/", "-", "\\"]
        spin_idx = 0
        start_time = time.time()
        print("\r[Antigravity düşünüyor...] ", end="", flush=True)

        handled_dispatch_ids = set()

        while True:
            # 1. Antigravity bu terminalde bir komut çalıştırmak istedi mi?
            if os.path.exists(DISPATCH_FILE):
                try:
                    with open(DISPATCH_FILE, "r", encoding="utf-8") as f:
                        disp = json.load(f)
                    
                    disp_id = disp.get("id")
                    disp_key = f"{disp_id}_{disp.get('action')}_{disp.get('command')}"

                    if disp_id == msg_id and disp_key not in handled_dispatch_ids:
                        handled_dispatch_ids.add(disp_key)
                        cmd = disp.get("command", "")

                        print("\r" + " " * 35 + "\r", end="")
                        print(f"\n" + "=" * 60)
                        print(f"⚡ [BU TERMİNALDE ÇALIŞTIRILIYOR] > {cmd}")
                        print("=" * 60)

                        # DOĞRUDAN BU TERMİNALDE ÇALIŞTIR (Canlı stdout/stderr)
                        res = subprocess.run(cmd, shell=True, text=True, capture_output=True)
                        
                        output = res.stdout
                        if res.stderr:
                            output += ("\n[STDERR]:\n" + res.stderr if output else res.stderr)
                        
                        if output:
                            print(output.strip())
                        print("-" * 60)

                        # Sonucu Antigravity'ye geri ilet
                        write_inbox({
                            "id": msg_id,
                            "type": "tool_result",
                            "timestamp": time.time(),
                            "command": cmd,
                            "exit_code": res.returncode,
                            "output": output
                        })

                        # Dispatch dosyasını temizle
                        try:
                            os.remove(DISPATCH_FILE)
                        except Exception:
                            pass

                        print("[Antigravity yanıtı derliyor...] ", end="", flush=True)
                except Exception:
                    pass

            # 2. Nihai yanıt geldi mi?
            if os.path.exists(OUTBOX_FILE):
                try:
                    with open(OUTBOX_FILE, "r", encoding="utf-8") as f:
                        out_data = json.load(f)
                    if out_data.get("id") == msg_id:
                        reply_found = out_data.get("reply", "")
                        print("\r" + " " * 35 + "\r", end="")
                        print(f"\nAntigravity > {reply_found}\n")
                        print("-" * 70)
                        break
                except Exception:
                    pass

            time.sleep(0.1)
            spin_idx = (spin_idx + 1) % len(spinner)
            if not handled_dispatch_ids:
                print(f"\r[Antigravity düşünüyor {spinner[spin_idx]}] ", end="", flush=True)

            if time.time() - start_time > 180:
                print("\n[Zaman aşımı: Yanıt 180 saniye içinde gelmedi]")
                break

if __name__ == "__main__":
    main()
