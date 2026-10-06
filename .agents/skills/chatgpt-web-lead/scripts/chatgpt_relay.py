#!/usr/bin/env python3
"""
chatgpt_relay.py
================
On-demand courier between Antigravity (sidekick / hands) and ChatGPT Web
(lead / brain — no tool calls).

Direction: Antigravity -> ChatGPT Web -> Antigravity.
A companion Antigravity skill (chatgpt-web-lead) calls this script when the
user explicitly invokes the lead bridge; otherwise Antigravity answers
normally — there is no persistent on/off state.

Usage:
  python chatgpt_relay.py --status           diagnostics (window, composer, generating)
  python chatgpt_relay.py --send "message"   send text to ChatGPT Web, print its reply on stdout
  flags: --timeout N (default 300)   --new (open a fresh ChatGPT chat first)   --pid N (pin a browser)

Contract:
  stdout carries ONLY the ChatGPT reply for --send (machine-parseable).
  All diagnostics go to stderr.
"""

import argparse
import ctypes
import os
import re
import subprocess
import sys
import time

try:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

INBRISK = "inbrisk"
BASE_DIR = os.path.dirname(os.path.abspath(__file__))

BROWSER_PROCS = ("chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe")

# ChatGPT composer (contenteditable Edit) localized names — substring match, lowercase
COMPOSER_KEYS = ("chatgpt'ye sor", "ask anything", "message chatgpt", "chatgpt'ye mesaj", "chatgpt")
COMPOSER_EXCLUDE = ("address", "search", "arama", "yazmaya", "yeni sohbet", "new chat")
COPY_BTN_KEYS = ("kopyala", "copy")          # ChatGPT "copy response" button
STOP_BTN_KEYS = ("durdur", "stop", "pause")  # streaming indicator buttons
NEW_CHAT_KEYS = ("yeni sohbet", "new chat", "new conversation")
SCROLL_BOTTOM_KEYS = ("en alta", "alta kayd", "scroll to bottom", "to bottom")

# ChatGPT mode control: in-conversation sidebar button 'Modu degistir, gecerli
# mod: X' (EN: 'Change mode, current mode: X'), plus a 'Sohbet | Calisma'
# segment on the home page. Bridge always enforces chat mode (GPT-5.6,
# near-unlimited on Plus+ plans).
CHAT_MODE_KEYS = ("chatgpt", "sohbet", "chat")
WORK_MODE_KEYS = ("codex", "çalışma", "agent", "work")
MODE_BTN_HINTS = ("geçerli mod", "current mode")
LOGIN_BTN_KEYS = ("oturum aç", "giriş", "log in", "sign in", "sign up", "kaydol", "üye ol")

CONTENT_REF_RE = re.compile(r"\s*:chatgpt-content-reference\{[^}]*\}")


def log(msg: str) -> None:
    print(f"[relay] {msg}", file=sys.stderr, flush=True)


def run_inbrisk(args, timeout=30):
    cmd = [INBRISK] + [str(a) for a in args]
    res = subprocess.run(cmd, capture_output=True, timeout=timeout)
    out = res.stdout.decode("utf-8", errors="replace").strip()
    err = res.stderr.decode("utf-8", errors="replace").strip()
    return res.returncode, out, err


ELEM_RE = re.compile(r"^(\S+)\s+(\S+)\s+'(.*)'\s+\[(-?\d+),\s*(-?\d+)\s+(\d+)x(\d+)\]")


def find_elements(pid, role=None, name=None, limit=100):
    """Return [{id, role, name, x, y, w, h}] for a pid-scoped UIA find."""
    args = ["find", "--pid", str(pid), "--limit", str(limit)]
    if role:
        args += ["--role", role]
    if name:
        args += ["--name", name]
    code, out, _ = run_inbrisk(args)
    if code != 0 or not out:
        return []
    elems = []
    for line in out.splitlines():
        m = ELEM_RE.match(line.strip())
        if m:
            elems.append({
                "id": m.group(1), "role": m.group(2), "name": m.group(3),
                "x": int(m.group(4)), "y": int(m.group(5)),
                "w": int(m.group(6)), "h": int(m.group(7)),
            })
    return elems


def iter_browser_windows():
    """Yield (pid, hwnd, proc, title) for browser top-level windows."""
    code, out, _ = run_inbrisk(["windows"])
    for line in out.splitlines():
        m = re.search(r"\[([A-Za-z0-9_.\-]+)\s+pid=(\d+)\s+hwnd=(0x[0-9a-fA-F]+|\d+)\]", line)
        if not m:
            continue
        proc, pid, hwnd = m.group(1).lower(), m.group(2), m.group(3)
        if proc in BROWSER_PROCS:
            yield int(pid), hwnd, proc, line


def pick_composer(pid):
    """Locate the ChatGPT composer Edit element in a browser window."""
    for e in find_elements(pid, role="Edit", limit=40):
        low = e["name"].lower()
        if any(k in low for k in COMPOSER_KEYS) and not any(x in low for x in COMPOSER_EXCLUDE):
            return e
    return None


def find_chatgpt_window(prefer_pid=None):
    """Return dict(pid, hwnd, composer) for the first browser window hosting ChatGPT."""
    candidates = []
    for pid, hwnd, proc, raw in iter_browser_windows():
        if prefer_pid and pid != prefer_pid:
            continue
        comp = pick_composer(pid)
        if comp:
            candidates.append({"pid": pid, "hwnd": hwnd, "composer": comp["name"]})
    return candidates[0] if candidates else None


def find_chatgpt_login_wall():
    """Detect a chatgpt.com tab showing the login wall (Document present, no composer)."""
    for pid, hwnd, proc, raw in iter_browser_windows():
        docs = find_elements(pid, role="Document", limit=5)
        if not any("chatgpt" in d["name"].lower() for d in docs):
            continue
        for key in LOGIN_BTN_KEYS:
            for role in ("Button", "Hyperlink", "Text"):
                if find_elements(pid, role=role, name=key, limit=5):
                    return pid
    return None


def ensure_chatgpt_window(prefer_pid=None, allow_launch=True, launch_wait=30):
    """Find the ChatGPT window; if absent, launch the default browser at chatgpt.com
    and wait. Returns {"login_wall": True, "pid": pid} if a login screen is detected."""
    win = find_chatgpt_window(prefer_pid)
    if win or prefer_pid:
        return win
    wall = find_chatgpt_login_wall()
    if wall:
        return {"login_wall": True, "pid": wall, "hwnd": None, "composer": None}
    if allow_launch:
        log("no ChatGPT tab found — launching default browser -> chatgpt.com")
        try:
            os.startfile("https://chatgpt.com/")
        except (OSError, AttributeError):
            subprocess.Popen('cmd /c start "" "https://chatgpt.com/"', shell=False)
    t0 = time.time()
    while time.time() - t0 < launch_wait:
        time.sleep(2.0)
        win = find_chatgpt_window(prefer_pid)
        if win:
            time.sleep(1.0)
            log(f"ChatGPT window ready (pid={win['pid']})")
            return win
        wall = find_chatgpt_login_wall()
        if wall:
            return {"login_wall": True, "pid": wall, "hwnd": None, "composer": None}
    return None


def ensure_sohbet_mode(pid):
    """Enforce ChatGPT 'Sohbet' (chat) mode before sending.

    Two INDEPENDENT controls, both checked:
    Surface 1 — sidebar button 'Modu degistir, gecerli mod: X' (ChatGPT app vs
    Codex app). X=Codex/Calisma/Agent -> open picker, select ChatGPT MenuItem.
    Surface 2 — home-page 'Olusturucu modu' segment 'Sohbet | Calisma'
    (ToggleButtons). If a work segment is ON, toggle Sohbet on.
    The bridge uses chat mode (GPT-5.6, near-unlimited on Plus+)."""
    # Surface 1 — sidebar 'Modu degistir, gecerli mod: X' (ChatGPT app vs Codex app)
    for e in find_elements(pid, role="Button", limit=80):
        n = e["name"].lower()
        if not any(h in n for h in MODE_BTN_HINTS) or e["y"] >= 300:
            continue
        if any(k in n for k in WORK_MODE_KEYS):
            # clickat is more reliable than invoke for opening this picker
            cx, cy = e["x"] + e["w"] // 2, e["y"] + e["h"] // 2
            run_inbrisk(["clickat", str(cx), str(cy)])
            items = []
            for _ in range(4):
                time.sleep(0.8)
                items = find_elements(pid, role="MenuItem", limit=20)
                if items:
                    break
            for it in items:
                it_n = it["name"].strip().lower()
                if it_n.startswith(CHAT_MODE_KEYS) and not any(k in it_n for k in WORK_MODE_KEYS):
                    run_inbrisk(["invoke", it["id"]])
                    time.sleep(1.2)
                    log("switched ChatGPT to Sohbet (chat) mode")
                    # verify the mode actually changed
                    for e2 in find_elements(pid, role="Button", limit=80):
                        n2 = e2["name"].lower()
                        if any(h in n2 for h in MODE_BTN_HINTS) and e2["y"] < 300 \
                                and any(k in n2 for k in WORK_MODE_KEYS):
                            log("warning: still in work mode after switch attempt")
                    break
        break  # surface-1 checked (switched or already chat) -> fall through

    # Home-page segmented toggle ('Olusturucu modu'): 'Sohbet' | 'Calisma' are
    # ToggleButtons; inspect exposes toggle=ToggleState_On/Off. Only flip when
    # a work segment is ON.
    code, out, _ = run_inbrisk(["inspect", "--pid", str(pid), "--depth", "25"], timeout=60)
    segs = {}
    for line in out.splitlines():
        m = re.search(r"^(\S+)\s+Button\s+(.*?)\s+\[(-?\d+),\s*(-?\d+)\s+\d+x\d+\].*toggle=(\w+)", line.strip())
        if not m or int(m.group(4)) >= 300:
            continue
        segs[m.group(2).strip().lower()] = {"id": m.group(1), "state": m.group(5)}
    chat_seg = next((segs[k] for k in ("sohbet", "chat") if k in segs), None)
    work_on = any(segs.get(k, {}).get("state") == "ToggleState_On" for k in WORK_MODE_KEYS)
    if work_on and chat_seg and chat_seg["state"] != "ToggleState_On":
        run_inbrisk(["toggle", chat_seg["id"]])
        time.sleep(1.2)
        log("switched ChatGPT home segment to Sohbet (chat) mode")


def copy_buttons(pid):
    """All 'Kopyala'/'Copy' buttons, sorted top->bottom (last = newest message footer)."""
    btns = []
    for key in COPY_BTN_KEYS:
        btns.extend(find_elements(pid, role="Button", name=key, limit=60))
    btns.sort(key=lambda b: (b["y"], b["x"]))
    return btns


def is_generating(pid):
    """True while ChatGPT is still streaming (Stop/Durdur button visible)."""
    for key in STOP_BTN_KEYS:
        if find_elements(pid, role="Button", name=key, limit=10):
            return True
    return False


# --- clipboard (native Win32, quoting-safe for long/multiline text) -----------

def set_clipboard_text(text):
    kernel32 = ctypes.windll.kernel32
    user32 = ctypes.windll.user32
    kernel32.GlobalAlloc.restype = ctypes.c_void_p
    kernel32.GlobalLock.restype = ctypes.c_void_p
    kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
    kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    user32.SetClipboardData.restype = ctypes.c_void_p
    user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
    for _ in range(10):
        if user32.OpenClipboard(None):
            try:
                user32.EmptyClipboard()
                data = text.encode("utf-16le") + b"\x00\x00"
                h = kernel32.GlobalAlloc(0x0002, len(data))  # GMEM_MOVEABLE
                if h:
                    p = kernel32.GlobalLock(h)
                    if p:
                        ctypes.memmove(p, data, len(data))
                        kernel32.GlobalUnlock(h)
                        user32.SetClipboardData(13, h)  # CF_UNICODETEXT
                        return True
            finally:
                user32.CloseClipboard()
        time.sleep(0.05)
    return False


def get_clipboard_text():
    _, out, _ = run_inbrisk(["clipboard", "read"])
    return out


def send_ctrl_v():
    user32 = ctypes.windll.user32
    VK_CONTROL, VK_V, KEYUP = 0x11, 0x56, 0x0002
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    user32.keybd_event(VK_V, 0, 0, 0)
    time.sleep(0.02)
    user32.keybd_event(VK_V, 0, KEYUP, 0)
    user32.keybd_event(VK_CONTROL, 0, KEYUP, 0)


def send_ctrl_a():
    user32 = ctypes.windll.user32
    VK_CONTROL, VK_A, KEYUP = 0x11, 0x41, 0x0002
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    user32.keybd_event(VK_A, 0, 0, 0)
    time.sleep(0.02)
    user32.keybd_event(VK_A, 0, KEYUP, 0)
    user32.keybd_event(VK_CONTROL, 0, KEYUP, 0)


def send_enter():
    user32 = ctypes.windll.user32
    user32.keybd_event(0x0D, 0, 0, 0)
    time.sleep(0.02)
    user32.keybd_event(0x0D, 0, 0x0002, 0)


def sanitize_reply(text):
    text = CONTENT_REF_RE.sub("", text)
    return text.strip()


def wait_until_idle(pid, timeout=90):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if not is_generating(pid):
            return True
        time.sleep(1.5)
    return False


def scroll_to_bottom(pid, chat_x=None, chat_y=None):
    """
    Ensure the chat view is scrolled all the way to the bottom so the newest
    message footer and its 'Kopyala' button are materialized and visible.
    """
    run_inbrisk(["focus", "--pid", str(pid)])
    time.sleep(0.15)

    if chat_x is None or chat_y is None:
        comp = pick_composer(pid)
        if comp:
            chat_x = comp["x"] + comp["w"] // 2
            chat_y = max(150, comp["y"] - 250)
        else:
            chat_x, chat_y = 500, 500

    # 1. Click ChatGPT's floating 'Scroll to bottom' / 'En alta kaydır' button if present
    for _ in range(5):
        btns = find_elements(pid, role="Button", limit=100)
        scroll_btns = [
            b for b in btns
            if any(k in b["name"].lower() for k in SCROLL_BOTTOM_KEYS)
        ]
        if not scroll_btns:
            break
        for sb in scroll_btns:
            run_inbrisk(["invoke", sb["id"]])
            cx = sb["x"] + sb["w"] // 2
            cy = sb["y"] + sb["h"] // 2
            run_inbrisk(["clickat", str(cx), str(cy)])
        time.sleep(0.3)

    # 2. Wheel scroll down aggressively at the chat container
    for _ in range(4):
        run_inbrisk(["scroll", str(chat_x), str(chat_y), "--delta", "-4000"])
        time.sleep(0.15)


def wait_for_reply(pid, baseline_copy_count, baseline_clip, timeout, prompt_text="", prev_reply_text=""):
    """Wait for the assistant reply to complete, then copy it via its Kopyala button."""
    t0 = time.time()
    settle_since = None
    sentinel = f"__WAIT_CLIP_{time.time()}__"

    # Wait up to 10s for generation to start (Durdur/Stop button appearing or generating state)
    t_start = time.time()
    while time.time() - t_start < 10:
        if is_generating(pid):
            break
        time.sleep(0.5)

    while time.time() - t0 < timeout:
        if is_generating(pid):
            settle_since = None
            time.sleep(1.5)
            continue

        # Assistant stopped generating — scroll all the way down so bottom action bar is rendered
        scroll_to_bottom(pid)

        btns = copy_buttons(pid)
        # Settle check: give the DOM a second to stabilize after generation stops
        if settle_since is None:
            settle_since = time.time()
            time.sleep(1.0)
            continue

        if btns:
            # Try from the newest (bottom-most) copy button
            for target in reversed(btns):
                set_clipboard_text(sentinel)
                time.sleep(0.1)

                # Tier 1: UIA Invoke
                run_inbrisk(["invoke", target["id"]])
                time.sleep(0.5)
                text = sanitize_reply(get_clipboard_text())

                # Tier 2: Semantic Click
                if not text or text == sentinel or text == prompt_text or text == baseline_clip or (prev_reply_text and text == prev_reply_text):
                    run_inbrisk(["click", target["id"]])
                    time.sleep(0.5)
                    text = sanitize_reply(get_clipboard_text())

                # Tier 3: Coordinate Clickat (center of target)
                if not text or text == sentinel or text == prompt_text or text == baseline_clip or (prev_reply_text and text == prev_reply_text):
                    cx = target["x"] + target["w"] // 2
                    cy = target["y"] + target["h"] // 2
                    run_inbrisk(["clickat", str(cx), str(cy)])
                    time.sleep(0.5)
                    text = sanitize_reply(get_clipboard_text())

                if text and text != sentinel and text != prompt_text and text != baseline_clip and (not prev_reply_text or text != prev_reply_text):
                    return text

        time.sleep(1.5)
    raise TimeoutError(f"ChatGPT did not finish replying within {timeout}s")


def send_to_chatgpt(win, text, timeout, open_new_chat=False):
    pid = win["pid"]
    prev_clip = get_clipboard_text()

    if open_new_chat:
        for key in NEW_CHAT_KEYS:
            btn = find_elements(pid, role="Button", name=key, limit=5)
            if btn:
                run_inbrisk(["invoke", btn[0]["id"]])
                time.sleep(1.5)
                break

    if not wait_until_idle(pid, timeout=90):
        raise TimeoutError("ChatGPT is still generating after 90s; message not sent.")

    ensure_sohbet_mode(pid)
    time.sleep(0.5)

    # Record current reply text from bottom-most button to avoid stale copy
    prev_reply_text = ""
    try:
        cur_btns = copy_buttons(pid)
        if cur_btns:
            sentinel = f"__PREV_CLIP_{time.time()}__"
            set_clipboard_text(sentinel)
            run_inbrisk(["invoke", cur_btns[-1]["id"]])
            time.sleep(0.4)
            c = sanitize_reply(get_clipboard_text())
            if c and c != sentinel:
                prev_reply_text = c
    except Exception:
        pass

    baseline_btns = len(copy_buttons(pid))

    # Focus + click composer + paste via inbrisk hotkey
    run_inbrisk(["focus", "--pid", str(pid)])
    time.sleep(0.4)
    run_inbrisk(["click", "--pid", str(pid), "--role", "Edit", "--name", win["composer"]])
    time.sleep(0.3)
    if not set_clipboard_text(text):
        run_inbrisk(["clipboard", "write", text])
    time.sleep(0.1)
    run_inbrisk(["hotkey", "ctrl+v", "--yes"])
    time.sleep(0.5)

    # Click the Send ("Gönder") button if visible, else send Enter key
    send_btn = find_elements(pid, role="Button", name="Gönder", limit=5)
    if not send_btn:
        send_btn = find_elements(pid, role="Button", name="Send", limit=5)
    if send_btn:
        sb = send_btn[0]
        run_inbrisk(["invoke", sb["id"]])
        time.sleep(0.3)
        if not is_generating(pid):
            cx = sb["x"] + sb["w"] // 2
            cy = sb["y"] + sb["h"] // 2
            run_inbrisk(["clickat", str(cx), str(cy)])
    else:
        run_inbrisk(["key", "enter", "--yes"])

    log(f"sent {len(text)} chars to ChatGPT (pid={pid})")

    try:
        reply = wait_for_reply(pid, baseline_btns, prev_clip, timeout, prompt_text=text, prev_reply_text=prev_reply_text)
    finally:
        if prev_clip:
            set_clipboard_text(prev_clip)
    return reply



def main():
    ap = argparse.ArgumentParser(description="Antigravity <-> ChatGPT Web relay courier")
    ap.add_argument("--status", action="store_true", help="print diagnostics")
    ap.add_argument("--send", nargs="?", const="", default=None, help="message text (or stdin if empty)")
    ap.add_argument("--file", type=str, default=None, help="read message text from a UTF-8 file")
    ap.add_argument("--timeout", type=int, default=300)
    ap.add_argument("--new", action="store_true", help="open a fresh ChatGPT conversation first")
    ap.add_argument("--pid", type=int, default=None, help="pin a specific browser pid")
    ap.add_argument("--no-launch", action="store_true", help="never auto-open chatgpt.com")
    args = ap.parse_args()

    if args.status:
        win = find_chatgpt_window(args.pid)
        if win:
            gen = is_generating(win["pid"])
            print(f"chatgpt window: pid={win['pid']} hwnd={win['hwnd']} composer='{win['composer']}' generating={gen}")
        else:
            wall = find_chatgpt_login_wall()
            if wall:
                print(f"chatgpt window: LOGIN WALL detected (pid={wall}) — log in to chatgpt.com first")
            else:
                print("chatgpt window: NOT FOUND (will auto-launch chatgpt.com on --send)")
        return

    if args.send is None and args.file is None:
        ap.print_help(sys.stderr)
        sys.exit(2)

    if args.file:
        with open(args.file, "r", encoding="utf-8") as f:
            text = f.read().strip()
    else:
        text = args.send or sys.stdin.read().strip()

    if not text:
        log("empty message, nothing sent")
        sys.exit(2)

    win = ensure_chatgpt_window(args.pid, allow_launch=not args.no_launch)
    if not win:
        log("no ChatGPT tab found in any browser window")
        sys.exit(4)
    if win.get("login_wall"):
        log("ChatGPT login wall detected — please log in to chatgpt.com, then retry")
        sys.exit(7)

    try:
        reply = send_to_chatgpt(win, text, args.timeout, open_new_chat=args.new)
    except TimeoutError as e:
        log(str(e))
        sys.exit(5)
    except Exception as e:
        log(f"relay error: {e}")
        sys.exit(6)

    try:
        reply_path = os.path.join(BASE_DIR, "last_chatgpt_reply.md")
        with open(reply_path, "w", encoding="utf-8") as f:
            f.write(reply)
    except Exception as e:
        log(f"warning: failed to save last_chatgpt_reply.md: {e}")

    try:
        print(reply)
    except Exception:
        sys.stdout.buffer.write(reply.encode("utf-8", errors="replace") + b"\n")


if __name__ == "__main__":
    main()
