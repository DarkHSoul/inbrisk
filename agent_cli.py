import os
import sys
import json
import time
import subprocess
import urllib.request
import urllib.error

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# F37: no hardcoded project id — the caller's own GCP project comes from env.
PROJECT_ID = os.environ.get("GOOGLE_CLOUD_PROJECT")
if not PROJECT_ID:
    sys.exit("GOOGLE_CLOUD_PROJECT env var is required (your GCP project id).")
# Vertex AI publisher models use regions like us-central1, not global
LOCATION = "us-central1"
MODEL = "gemini-2.5-flash"
API_URL = f"https://{LOCATION}-aiplatform.googleapis.com/v1/projects/{PROJECT_ID}/locations/{LOCATION}/publishers/google/models/{MODEL}:generateContent"

TOOLS_DECLARATION = [
    {
        "functionDeclarations": [
            {
                "name": "run_command",
                "description": "Execute a shell command or script in Windows Command Prompt and return stdout/stderr.",
                "parameters": {
                    "type": "OBJECT",
                    "properties": {
                        "command": {
                            "type": "STRING",
                            "description": "The exact shell command line to run."
                        }
                    },
                    "required": ["command"]
                }
            },
            {
                "name": "read_file",
                "description": "Read the text contents of a file from disk.",
                "parameters": {
                    "type": "OBJECT",
                    "properties": {
                        "path": {
                            "type": "STRING",
                            "description": "Relative or absolute file path to read."
                        }
                    },
                    "required": ["path"]
                }
            },
            {
                "name": "write_file",
                "description": "Write or overwrite text content to a file.",
                "parameters": {
                    "type": "OBJECT",
                    "properties": {
                        "path": {
                            "type": "STRING",
                            "description": "Relative or absolute file path."
                        },
                        "content": {
                            "type": "STRING",
                            "description": "Text content to write."
                        }
                    },
                    "required": ["path", "content"]
                }
            }
        ]
    }
]

def get_access_token():
    try:
        out = subprocess.check_output("gcloud.cmd auth print-access-token", shell=True, text=True).strip()
        if out:
            return out
    except Exception:
        pass
    try:
        out = subprocess.check_output("gcloud auth print-access-token", shell=True, text=True).strip()
        if out:
            return out
    except Exception as e:
        print(f"\n[Hata]: Google Cloud access token alinamadi. 'gcloud auth login' yapiniz.\nDetay: {e}")
        sys.exit(1)

def execute_tool(func_name, args):
    if func_name == "run_command":
        cmd = args.get("command", "")
        try:
            res = subprocess.run(cmd, shell=True, capture_output=True, text=True, timeout=60)
            output = res.stdout
            if res.stderr:
                output += ("\nSTDERR:\n" + res.stderr if output else res.stderr)
            return {"exit_code": res.returncode, "output": output or "(bos cikti)"}
        except Exception as e:
            return {"exit_code": -1, "error": str(e)}

    elif func_name == "read_file":
        path = args.get("path", "")
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as f:
                content = f.read()
            return {"content": content}
        except Exception as e:
            return {"error": str(e)}

    elif func_name == "write_file":
        path = args.get("path", "")
        content = args.get("content", "")
        try:
            parent = os.path.dirname(path)
            if parent:
                os.makedirs(parent, exist_ok=True)
            with open(path, "w", encoding="utf-8") as f:
                f.write(content)
            return {"status": "success", "bytes_written": len(content)}
        except Exception as e:
            return {"error": str(e)}

    return {"error": f"Bilinmeyen fonksiyon: {func_name}"}

def call_gemini(contents, token):
    payload = {
        "contents": contents,
        "tools": TOOLS_DECLARATION,
        "systemInstruction": {
            "parts": [
                {
                    "text": "Sen Windows terminalinde calisan bagimsiz bir AI gelistirici asistanisin. "
                            "Kullanici bir eylem istediginde uygun araci (run_command, read_file, write_file) cagir. "
                            "Yanitlarini Turkce, net ve ozet olarak ver."
                }
            ]
        }
    }

    req = urllib.request.Request(
        API_URL,
        data=json.dumps(payload).encode("utf-8"),
        headers={
            "Authorization": f"Bearer {token}",
            "Content-Type": "application/json"
        }
    )

    try:
        with urllib.request.urlopen(req) as resp:
            data = resp.read().decode("utf-8")
            return json.loads(data)
    except urllib.error.HTTPError as e:
        err_body = e.read().decode("utf-8", errors="replace")
        print(f"\n[API Hatasi {e.code}]: {err_body}")
        return None
    except Exception as e:
        print(f"\n[Baglanti Hatasi]: {e}")
        return None

def main():
    print("=" * 70)
    print("  Bagimsiz Terminal Agent (Sifir SDK - Dogrudan CMD Tool Calling)")
    print("  Arka planda hicbir watcher beklemez. Tool'lar dogrudan burada calisir!")
    print("  Cikmak icin: 'exit', 'quit' veya 'q' yazin.")
    print("=" * 70)
    print()

    token = get_access_token()
    token_time = time.time()
    history = []

    while True:
        try:
            user_input = input("\nSen > ").strip()
        except (KeyboardInterrupt, EOFError):
            print("\nCikis yapiliyor...")
            break

        if not user_input:
            continue

        if user_input.lower() in ("exit", "quit", "q"):
            print("Gorusmek uzere!")
            break

        # Token yenileme kontrolü (50 dakikada bir)
        if time.time() - token_time > 3000:
            token = get_access_token()
            token_time = time.time()

        history.append({
            "role": "user",
            "parts": [{"text": user_input}]
        })

        # Agent Tool Loop
        while True:
            spinner = ["|", "/", "-", "\\"]
            print("[Dusunuyor...] ", end="", flush=True)

            res = call_gemini(history, token)
            print("\r" + " " * 20 + "\r", end="")

            if not res or "candidates" not in res:
                print("Yanit alinamadi.")
                break

            candidate = res["candidates"][0]
            model_content = candidate.get("content", {})
            parts = model_content.get("parts", [])

            # Model yanitini gecmise ekle
            history.append(model_content)

            # Function call var mi kontrol et
            function_calls = [p["functionCall"] for p in parts if "functionCall" in p]

            if not function_calls:
                # Normal metin yaniti
                text_parts = [p.get("text", "") for p in parts if "text" in p]
                full_text = "\n".join(text_parts).strip()
                if full_text:
                    print(f"\nAgent > {full_text}\n")
                    print("-" * 70)
                break

            # Tool call calistirma
            response_parts = []
            for fc in function_calls:
                fn_name = fc["name"]
                fn_args = fc.get("args", {})
                
                print(f"\n[CANLI TOOL CALL] >>> {fn_name}")
                print(f"  Parametreler: {json.dumps(fn_args, ensure_ascii=False)}")

                # DOGRUDAN TERMINALDE CALISTIR
                result = execute_tool(fn_name, fn_args)

                print(f"[TOOL CIKTISI] <<<")
                res_str = json.dumps(result, ensure_ascii=False, indent=2)
                if len(res_str) > 1000:
                    print(res_str[:1000] + f"\n... [Kalan {len(res_str)-1000} karakter gizlendi]")
                else:
                    print(res_str)
                print("-" * 50)

                response_parts.append({
                    "functionResponse": {
                        "name": fn_name,
                        "response": result
                    }
                })

            # Tool sonuclarini modele gonder
            history.append({
                "role": "user",
                "parts": response_parts
            })

if __name__ == "__main__":
    main()
