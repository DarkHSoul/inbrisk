import subprocess
import json
import time
import os
import glob
import re

def run_inbrisk(args):
    cmd = ["inbrisk"] + args
    res = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    return res.returncode, res.stdout.strip(), res.stderr.strip()

def get_antigravity_hwnd():
    code, stdout, stderr = run_inbrisk(["windows"])
    for line in stdout.splitlines():
        if "antigravity.exe" in line.lower():
            m = re.search(r'hwnd=(0x[0-9a-fA-F]+)', line)
            if m:
                return m.group(1)
    return None

def get_foreground_hwnd():
    code, stdout, stderr = run_inbrisk(["windows"])
    lines = stdout.splitlines()
    if lines:
        # First window in EnumWindows is usually top of z-order, but let's check
        for line in lines:
            m = re.search(r'\[([^\s]+)\s+pid=(\d+)\s+hwnd=(0x[0-9a-fA-F]+)\]', line)
            if m:
                return m.group(3)
    return None

def get_latest_brain_session():
    brain_dir = os.path.expanduser(r"~\.gemini\antigravity\brain")
    subdirs = [os.path.join(brain_dir, d) for d in os.listdir(brain_dir) if os.path.isdir(os.path.join(brain_dir, d))]
    if not subdirs:
        return None, 0
    latest = max(subdirs, key=os.path.getmtime)
    return latest, os.path.getmtime(latest)

def read_transcript_response(session_dir):
    log_file = os.path.join(session_dir, ".system_generated", "logs", "transcript.jsonl")
    if not os.path.exists(log_file):
        return None
    
    last_response = None
    with open(log_file, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                data = json.loads(line)
                if data.get("type") == "PLANNER_RESPONSE":
                    last_response = data
            except Exception:
                pass
    return last_response

def main():
    print("=== Antigravity <-> Inbrisk UI Bridge Test ===", flush=True)
    results = {"status": "starting", "steps": []}
    
    # Allow caller turn to complete so Antigravity is completely IDLE
    print("[0] Waiting 6 seconds for Antigravity caller turn to yield...", flush=True)
    time.sleep(6.0)
    
    ag_hwnd = get_antigravity_hwnd()
    if not ag_hwnd:
        print("[ERROR] Antigravity window not found!", flush=True)
        results["status"] = "failed"
        results["error"] = "Antigravity window not found"
        with open("bridge_test_results.json", "w", encoding="utf-8") as f:
            json.dump(results, f, indent=2)
        return False
    print(f"[1] Antigravity Window HWND: {ag_hwnd}", flush=True)
    results["steps"].append({"step": "find_hwnd", "hwnd": ag_hwnd})
    
    initial_fg = get_foreground_hwnd()
    print(f"[2] Initial Foreground HWND: {initial_fg}", flush=True)
    results["initial_fg"] = initial_fg
    
    initial_latest_session, initial_time = get_latest_brain_session()
    print(f"[3] Current Brain Latest Session: {os.path.basename(initial_latest_session)}", flush=True)
    
    # Step 1: Open New Conversation
    print("[4] Opening New Conversation in Antigravity via Inbrisk Invoke...", flush=True)
    code, stdout, stderr = run_inbrisk(["invoke", "--hwnd", ag_hwnd, "--name", "New Conversation"])
    print(f"    Result: code={code} out={stdout[:100]}", flush=True)
    results["steps"].append({"step": "invoke_new_conversation", "code": code, "output": stdout})
    time.sleep(1.5)
    
    # Step 2: Ensure Antigravity is focused for text entry
    print("    Ensuring Antigravity is active for typing...", flush=True)
    run_inbrisk(["focus", ag_hwnd])
    time.sleep(0.4)
    
    test_id = f"BRIDGE-TEST-{int(time.time())}"
    test_prompt = f"{test_id}: Please reply with exactly 'BRIDGE_PONG_CONFIRMED' and nothing else."
    print(f"[6] Sending test prompt: {test_prompt}", flush=True)
    results["prompt"] = test_prompt
    
    # Type text directly into Message input
    print("    Typing prompt via inbrisk type --text ... --yes...", flush=True)
    t_code, t_out, t_err = run_inbrisk(["type", "--hwnd", ag_hwnd, "--name", "Message input", "--text", test_prompt, "--yes"])
    print(f"    Type result: code={t_code} out={t_out} err={t_err}", flush=True)
    results["steps"].append({"step": "type_prompt", "code": t_code, "output": t_out, "error": t_err})
    time.sleep(0.5)
    
    # Send message via invoke or Enter key
    print("    Submitting message...", flush=True)
    s_code, s_out, _ = run_inbrisk(["invoke", "--hwnd", ag_hwnd, "--name", "Send message"])
    if s_code != 0:
        print(f"    Invoke Send message returned code={s_code}, trying key enter...", flush=True)
        s_code, s_out, _ = run_inbrisk(["key", "enter"])
    print(f"    Send message result: code={s_code} out={s_out[:100]}", flush=True)
    results["steps"].append({"step": "invoke_send", "code": s_code, "output": s_out})
    
    # Non-disruptive policy: restore user foreground immediately while Antigravity generates
    if initial_fg and initial_fg != ag_hwnd:
        print(f"    [POLICY] Restoring user foreground to {initial_fg} while model generates...", flush=True)
        run_inbrisk(["focus", initial_fg])
    
    # Step 3: Monitor completion
    print("[7] Monitoring Antigravity response generation...", flush=True)
    start_time = time.time()
    generating_seen = False
    completed = False
    
    while time.time() - start_time < 90:
        time.sleep(2.0)
        _, cancel_out, _ = run_inbrisk(["find", "--hwnd", ag_hwnd, "--name", "Cancel"])
        is_cancelling = "Cancel" in cancel_out
        
        if is_cancelling:
            if not generating_seen:
                generating_seen = True
                print("    [STATUS] Response generation started (Cancel button active).", flush=True)
        else:
            if generating_seen:
                print("    [STATUS] Cancel button disappeared -> Generation completed!", flush=True)
                completed = True
                break
            else:
                curr_session, curr_time = get_latest_brain_session()
                if curr_session != initial_latest_session and curr_time > initial_time:
                    resp = read_transcript_response(curr_session)
                    if resp and resp.get("status") == "DONE":
                        print("    [STATUS] New session transcript marked DONE!", flush=True)
                        completed = True
                        break
    
    results["completed"] = completed
    
    # Step 4: Read generated response
    time.sleep(1.0)
    curr_session, curr_time = get_latest_brain_session()
    print(f"[8] Newest session directory: {curr_session}", flush=True)
    results["target_session"] = curr_session
    resp_data = read_transcript_response(curr_session)
    if resp_data:
        content = resp_data.get("content", "")
        print(f"[9] Extracted Assistant Content:\n---\n{content}\n---", flush=True)
        results["response_content"] = content
        if "BRIDGE_PONG_CONFIRMED" in content:
            print("[SUCCESS] Bridge test verified! Model replied with expected token.", flush=True)
            results["status"] = "success"
        else:
            print(f"[INFO] Response received: {content[:200]}", flush=True)
            results["status"] = "partial_success"
    else:
        print("[ERROR] Could not read response from transcript.", flush=True)
        results["status"] = "failed"
        results["error"] = "No transcript response"
    
    # Step 5: Restore original conversation
    print("[10] Restoring original conversation ('ANTIGRAVITY i hermes e bağla')...", flush=True)
    code, stdout, stderr = run_inbrisk(["invoke", "--hwnd", ag_hwnd, "--name", "ANTIGRAVITY i hermes e bağla"])
    print(f"     Restore result: code={code} out={stdout[:100]}", flush=True)
    time.sleep(1.0)
    
    # Step 6: Check foreground state
    final_fg = get_foreground_hwnd()
    print(f"[11] Final Foreground HWND: {final_fg}", flush=True)
    results["final_fg"] = final_fg
    if initial_fg and final_fg and initial_fg != final_fg:
        print(f"     [NOTE] Restoring foreground to {initial_fg}...", flush=True)
        run_inbrisk(["focus", initial_fg])
    
    with open("bridge_test_results.json", "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print("=== Test finished. Results written to bridge_test_results.json ===", flush=True)

if __name__ == "__main__":
    main()
