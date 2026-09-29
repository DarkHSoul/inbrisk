#!/usr/bin/env python3
"""
delegate_to_antigravity.py
===========================
Bridge between Hermes Lead Architect (Astra) and Antigravity SWE Worker.
Dispatches task briefs to the active Antigravity GUI session and streams
real-time reasoning, tool calls, and final responses back to Hermes.
"""

import os
import sys
import time
import json
import glob
import re
import subprocess
import argparse
from pathlib import Path

INBRISK_EXE = r"C:\Users\Ahmet\AppData\Local\Programs\Inbrisk\inbrisk.exe"
BRAIN_DIR = os.path.expanduser(r"~\.gemini\antigravity\brain")


def get_latest_transcript_path():
    """Finds the transcript.jsonl of the currently active Antigravity session."""
    pattern = os.path.join(BRAIN_DIR, "*", ".system_generated", "logs", "transcript.jsonl")
    candidates = glob.glob(pattern)
    if not candidates:
        raise FileNotFoundError(f"No active Antigravity transcript found in {BRAIN_DIR}")
    return max(candidates, key=os.path.getmtime)


def get_antigravity_window_info():
    """Returns (hwnd, click_x, click_y) for Antigravity's chat input area."""
    hwnd = None
    click_x = 960
    click_y = 960

    if os.path.exists(INBRISK_EXE):
        try:
            res = subprocess.run([INBRISK_EXE, "windows"], capture_output=True, text=True, timeout=5)
            for line in res.stdout.splitlines():
                if "Antigravity.exe" in line:
                    # Extract hwnd
                    m_hwnd = re.search(r"hwnd=(0x[0-9a-fA-F]+|\d+)", line)
                    if m_hwnd:
                        hwnd = m_hwnd.group(1)
                    
                    # Extract rect: [x,y WxH] or (x,y WxH)
                    m_rect = re.search(r"[\[\(](-?\d+),\s*(-?\d+)\s+(\d+)x(\d+)[\]\)]", line)
                    if m_rect:
                        rx = int(m_rect.group(1))
                        ry = int(m_rect.group(2))
                        rw = int(m_rect.group(3))
                        rh = int(m_rect.group(4))
                        # Input box is at bottom center of the window
                        click_x = rx + (rw // 2)
                        click_y = ry + rh - 75
                    break
        except Exception:
            pass

    return hwnd, click_x, click_y


def send_task_to_antigravity(task_text, hwnd=None, click_x=960, click_y=960):
    """Brings Antigravity to focus, clicks the chat box, pastes text, and presses Enter."""
    if not os.path.exists(INBRISK_EXE):
        raise RuntimeError("Inbrisk executable not found at " + INBRISK_EXE)

    # 1. Focus the window
    if hwnd:
        subprocess.run([INBRISK_EXE, "focus", hwnd], capture_output=True, text=True, timeout=5)
    else:
        subprocess.run([INBRISK_EXE, "focus", "--process", "Antigravity.exe"], capture_output=True, text=True, timeout=5)
    
    time.sleep(0.4)

    # 2. Click explicitly in the chat input area to give keyboard focus
    subprocess.run([INBRISK_EXE, "clickat", str(click_x), str(click_y)], capture_output=True, text=True, timeout=5)
    time.sleep(0.2)

    # 3. Write task to clipboard
    subprocess.run([INBRISK_EXE, "clipboard", "write", task_text], capture_output=True, text=True, timeout=5)
    time.sleep(0.15)

    # 4. Paste and Submit
    subprocess.run([INBRISK_EXE, "hotkey", "ctrl+v"], capture_output=True, text=True, timeout=5)
    time.sleep(0.2)
    subprocess.run([INBRISK_EXE, "key", "enter"], capture_output=True, text=True, timeout=5)
    return True


def stream_antigravity_response(transcript_path, start_pos, timeout_sec=600):
    """
    Streams Antigravity execution steps from transcript.jsonl.
    Returns the final response text when Antigravity completes the turn.
    """
    start_time = time.time()
    last_response_text = ""
    got_user_input = False

    print(f"\n[Antigravity Bridge] Monitoring session transcript...", flush=True)

    with open(transcript_path, "r", encoding="utf-8") as f:
        f.seek(start_pos)

        while True:
            if time.time() - start_time > timeout_sec:
                print("\n[Antigravity Bridge] Timeout waiting for Antigravity completion.", file=sys.stderr)
                return last_response_text or "[Timeout]: Antigravity did not respond within timeout."

            line = f.readline()
            if not line:
                time.sleep(0.3)
                continue

            line = line.strip()
            if not line:
                continue

            try:
                data = json.loads(line)
            except json.JSONDecodeError:
                continue

            step_type = data.get("type")
            source = data.get("source")
            status = data.get("status")

            if step_type == "USER_INPUT":
                got_user_input = True

            # 1. Stream Thinking / Internal reasoning
            if "thinking" in data and data["thinking"]:
                print(f"\033[90m[Antigravity Thinking]\n{data['thinking']}\033[0m\n", flush=True)

            # 2. Stream Tool Calls
            if "tool_calls" in data and data["tool_calls"]:
                for tc in data["tool_calls"]:
                    name = tc.get("name", "unknown_tool")
                    args = tc.get("args", {})
                    print(f"\033[33m[Antigravity Tool Call] {name} -> {json.dumps(args, ensure_ascii=False)[:300]}\033[0m", flush=True)

            # 3. Stream Tool Output
            if step_type == "GENERIC" and data.get("content"):
                preview = data["content"].strip().split("\n")[0]
                if len(preview) > 120:
                    preview = preview[:117] + "..."
                print(f"\033[36m[Tool Output] {preview}\033[0m", flush=True)

            # 4. Model response message
            if source == "MODEL" and step_type == "PLANNER_RESPONSE":
                content = data.get("content", "")
                tool_calls = data.get("tool_calls", [])

                if content:
                    last_response_text = content
                    print(f"\033[32m\n[Antigravity Response]:\n{content}\033[0m\n", flush=True)

                # Check if this marks turn completion
                if status == "DONE" and not tool_calls and content and got_user_input:
                    print("[Antigravity Bridge] Task finished successfully.", flush=True)
                    return last_response_text


def run_delegation_sync(task_text, timeout_sec=600):
    """Callable from Python code (e.g. from Hermes delegate_tool)."""
    transcript_path = get_latest_transcript_path()
    start_pos = os.path.getsize(transcript_path)

    hwnd, cx, cy = get_antigravity_window_info()
    send_task_to_antigravity(task_text, hwnd=hwnd, click_x=cx, click_y=cy)
    return stream_antigravity_response(transcript_path, start_pos, timeout_sec=timeout_sec)


def main():
    parser = argparse.ArgumentParser(description="Delegate task from Hermes to Antigravity session")
    parser.add_argument("--task", "-t", type=str, help="Task brief text")
    parser.add_argument("--file", "-f", type=str, help="Path to file containing task brief")
    parser.add_argument("--timeout", type=int, default=600, help="Timeout in seconds")
    args = parser.parse_args()

    task_text = ""
    if args.file and os.path.exists(args.file):
        with open(args.file, "r", encoding="utf-8") as f:
            task_text = f.read().strip()
    elif args.task:
        task_text = args.task.strip()
    elif not sys.stdin.isatty():
        task_text = sys.stdin.read().strip()

    if not task_text:
        print("Error: No task brief provided. Use --task '...' or pass via stdin.", file=sys.stderr)
        sys.exit(1)

    result = run_delegation_sync(task_text, timeout_sec=args.timeout)
    print("\n=================== FINAL OUTCOME ===================")
    print(result)
    print("=====================================================")


if __name__ == "__main__":
    main()
