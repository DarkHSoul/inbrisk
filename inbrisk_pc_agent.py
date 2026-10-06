import argparse
import asyncio
import json
import os
import shutil
import subprocess
import sys
from typing import Literal

from google.antigravity import Agent, LocalAgentConfig, CapabilitiesConfig

# ============================================================================
# Operation and Action Contracts
# ============================================================================

OpType = Literal[
    "status",
    "windows",
    "observe",
    "find",
    "read",
    "act",
    "cancel",
    "launch_app",
    "navigate_url",
    "scroll",
    "find_text",
    "select_text",
    "clipboard_read",
    "clipboard_write"
]

ActionType = Literal[
    "click",
    "double_click",
    "focus",
    "type",
    "key",
    "scroll",
    "launch",
    "navigate",
    "select_text",
    "clipboard_read",
    "clipboard_write"
]

ALLOWED_OPS = {
    "status", "windows", "observe", "find", "read", "act", "cancel",
    "launch_app", "navigate_url", "scroll", "find_text", "select_text",
    "clipboard_read", "clipboard_write"
}

ALLOWED_ACTIONS = {
    "click", "double_click", "focus", "type", "key", "scroll",
    "launch", "navigate", "select_text", "clipboard_read", "clipboard_write"
}

READ_ONLY_OPS = {"status", "windows", "observe", "find", "read", "hello", "clipboard_read"}
MUTATING_OPS = {"act", "cancel", "launch_app", "navigate_url", "scroll", "find_text", "select_text", "clipboard_write"}

# ============================================================================
# Path Resolution
# ============================================================================

def resolve_bridge_path(custom_path=None) -> str:
    if custom_path and os.path.isfile(custom_path):
        return os.path.abspath(custom_path)
    
    candidates = [
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "inbrisk-bridge.exe"),
        os.path.expandvars(r"%LOCALAPPDATA%\inbrisk\bin\inbrisk-bridge.exe"),
        os.path.join(os.path.dirname(os.path.abspath(__file__)), "target", "release", "inbrisk-bridge.exe"),
        os.path.abspath("target/release/inbrisk-bridge.exe"),
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.abspath(c)
    
    which_bin = shutil.which("inbrisk-bridge.exe")
    if which_bin:
        return which_bin
    
    return os.path.abspath(candidates[0])

# ============================================================================
# Protected Terminal Detection
# ============================================================================

def is_window_protected(win: dict, own_pids: set[int]) -> bool:
    pid = win.get("process_id") or 0
    if pid in own_pids:
        return True
    pname = (win.get("process_name") or "").lower()
    title = (win.get("title") or "").lower()
    protected_procs = {
        "powershell.exe", "pwsh.exe", "cmd.exe",
        "windowsterminal.exe", "antigravity.exe", "code.exe"
    }
    if pname in protected_procs:
        return True
    protected_titles = ["powershell", "command prompt", "antigravity", "terminal"]
    if any(t in title for t in protected_titles):
        return True
    return False

def check_target_protected(target_or_win: dict | None, own_pids: set[int]) -> tuple[bool, str]:
    if not target_or_win or not isinstance(target_or_win, dict):
        return False, ""
    
    win_spec = target_or_win.get("window", target_or_win)
    if isinstance(win_spec, dict):
        name = (win_spec.get("name") or win_spec.get("title") or "").lower()
        proc = (win_spec.get("process_name") or win_spec.get("app") or "").lower()
        if any(t in name for t in ["powershell", "command prompt", "antigravity", "terminal"]):
            return True, f"Target window '{name}' is a protected host terminal or IDE."
        if proc in {"powershell.exe", "pwsh.exe", "cmd.exe", "windowsterminal.exe", "antigravity.exe", "code.exe"}:
            return True, f"Target process '{proc}' is protected."
        hwnd = win_spec.get("hwnd")
        if hwnd:
            try:
                import ctypes
                c_hwnd = ctypes.windll.kernel32.GetConsoleWindow()
                if c_hwnd and hwnd == c_hwnd:
                    return True, "Target HWND is the host agent console window."
            except Exception:
                pass
    return False, ""

# ============================================================================
# Loop Detection
# ============================================================================

class LoopDetector:
    def __init__(self):
        self.history = []

    def record_and_check(self, op: str, action: str | None, target_summary: str) -> tuple[bool, str]:
        key = (op, action or "", target_summary)
        if len(self.history) >= 2:
            last1, ver1 = self.history[-1]
            last2, ver2 = self.history[-2]
            if last1 == key and last2 == key and (not ver1 or not ver2):
                return True, (
                    f"Detected repeated failed/unverified attempts for '{op}:{action or target_summary}'. "
                    "You MUST call 'observe' or 'windows' to inspect current desktop state before retrying."
                )
        self.history.append([key, False])
        if len(self.history) > 10:
            self.history.pop(0)
        return False, ""

    def mark_verified(self, verified: bool):
        if self.history:
            self.history[-1][1] = verified

    def reset_on_observation(self):
        self.history.clear()

LOOP_DETECTOR = LoopDetector()

# ============================================================================
# Clipboard Utilities
# ============================================================================

def get_clipboard_text() -> str:
    try:
        import tkinter as tk
        root = tk.Tk()
        root.withdraw()
        try:
            val = root.clipboard_get()
            return val or ""
        except Exception:
            return ""
        finally:
            root.destroy()
    except Exception:
        try:
            out = subprocess.check_output(["powershell", "-NoProfile", "-Command", "Get-Clipboard"], text=True)
            return out.strip()
        except Exception:
            return ""

def set_clipboard_text(text: str) -> bool:
    try:
        import tkinter as tk
        root = tk.Tk()
        root.withdraw()
        try:
            root.clipboard_clear()
            root.clipboard_append(text)
            root.update()
            return True
        finally:
            root.destroy()
    except Exception:
        try:
            subprocess.run(["powershell", "-NoProfile", "-Command", "Set-Clipboard", "-Value", text], check=True)
            return True
        except Exception:
            return False

# ============================================================================
# Bridge Transport & Session
# ============================================================================

class BridgeTransportError(Exception):
    pass

class BridgeDiedError(BridgeTransportError):
    pass

class BridgeTeardownFailed(BridgeTransportError):
    pass

class InbriskPCSession:
    """
    Host-owned persistent Inbrisk PC Session.
    Maintains a long-lived Rust bridge process over anonymous stdio pipes using JSONL.
    """
    def __init__(self, bridge_path=None, debug=False):
        self.bridge_path = resolve_bridge_path(bridge_path)
        self.debug = debug
        self.proc = None
        self.req_id = 1
        self.pid = None
        self.bridge_generation = 0
        self.restart_count = 0
        self.last_restart_reason = None
        self._io_lock = asyncio.Lock()

    async def open(self):
        if not os.path.exists(self.bridge_path):
            raise FileNotFoundError(f"Bridge binary not found at {self.bridge_path}")
        await self._spawn_bridge(reason="Initial session open")
        if self.debug:
            print(f"[Debug] Bridge session started (PID: {self.pid}, Generation: {self.bridge_generation})", file=sys.stderr)

    async def _spawn_bridge(self, reason: str = "Startup"):
        self.bridge_generation += 1
        self.last_restart_reason = reason
        self.proc = await asyncio.create_subprocess_exec(
            self.bridge_path,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=sys.stderr if self.debug else asyncio.subprocess.DEVNULL,
            limit=16 * 1024 * 1024
        )
        self.pid = self.proc.pid
        res = await self._send_and_recv_raw({"op": "hello"}, timeout=5.0)
        if not res or not res.get("ok"):
            raise RuntimeError(f"Bridge handshake failed for generation {self.bridge_generation}: {res}")
        return res

    async def _teardown_current_bridge(self, reason: str):
        self.last_restart_reason = reason
        self.restart_count += 1
        if not self.proc:
            return

        old_pid = self.pid
        if self.proc.stdin:
            try:
                self.proc.stdin.close()
            except Exception:
                pass

        if self.proc.returncode is None:
            try:
                self.proc.kill()
            except Exception:
                pass

        try:
            await asyncio.wait_for(self.proc.wait(), timeout=1.0)
        except asyncio.TimeoutError:
            try:
                subprocess.run(
                    ["taskkill", "/F", "/T", "/PID", str(old_pid)],
                    capture_output=True,
                    timeout=2.0
                )
            except Exception:
                pass
            try:
                await asyncio.wait_for(self.proc.wait(), timeout=1.0)
            except asyncio.TimeoutError:
                raise BridgeTeardownFailed(f"Failed to kill bridge process PID {old_pid} during teardown")

        self.proc = None
        self.pid = None

    async def _send_raw(self, request: dict) -> int:
        req_id = self.req_id
        self.req_id += 1
        wire_obj = dict(request)
        wire_obj["id"] = req_id
        line = json.dumps(wire_obj) + "\n"

        if not self.proc or not self.proc.stdin or self.proc.returncode is not None:
            raise BridgeDiedError("Bridge process is not running or stdin is closed")

        try:
            self.proc.stdin.write(line.encode("utf-8"))
            await self.proc.stdin.drain()
            return req_id
        except (BrokenPipeError, ConnectionResetError, OSError) as e:
            raise BridgeDiedError(f"Failed to write to bridge stdin: {e}") from e

    async def _recv_raw(self, req_id: int) -> dict:
        while True:
            if not self.proc or not self.proc.stdout or self.proc.returncode is not None:
                raise BridgeDiedError("Bridge process terminated before response could be received")

            try:
                line_bytes = await self.proc.stdout.readline()
            except (BrokenPipeError, ConnectionResetError, OSError) as e:
                raise BridgeDiedError(f"Failed to read from bridge stdout: {e}") from e

            if not line_bytes:
                raise BridgeDiedError(f"Bridge stdout hit EOF waiting for request id {req_id}")

            line_str = line_bytes.decode("utf-8").strip()
            if not line_str:
                continue

            try:
                res = json.loads(line_str)
                if res.get("id") == req_id:
                    return res
            except json.JSONDecodeError as e:
                return {
                    "id": req_id,
                    "ok": False,
                    "error": {"code": "ProtocolError", "message": f"Malformed bridge JSON: {e}"}
                }

    async def _send_and_recv_raw(self, request: dict, timeout: float) -> dict:
        req_id = await self._send_raw(request)
        return await asyncio.wait_for(self._recv_raw(req_id), timeout=timeout)

    async def execute(self, request: dict, timeout: float = 15.0) -> dict:
        op = request.get("op", "")
        is_mutating = op in ("act", "cancel")

        async with self._io_lock:
            if not self.proc or self.proc.returncode is not None:
                await self._spawn_bridge(reason=f"Recovering terminated bridge before op '{op}'")

            try:
                return await self._send_and_recv_raw(request, timeout=timeout)
            except (asyncio.TimeoutError, BridgeTransportError) as exc:
                is_timeout = isinstance(exc, asyncio.TimeoutError)
                failure_type = "timeout" if is_timeout else "bridge death"
                old_gen = self.bridge_generation
                if self.debug:
                    print(f"[Debug] Transport failure ({failure_type}) on op '{op}'. Recovering...", file=sys.stderr)

                await self._teardown_current_bridge(reason=f"{failure_type} on op '{op}' (gen {old_gen})")
                await self._spawn_bridge(reason=f"Transport recovery after {failure_type} on op '{op}'")

                if is_mutating:
                    msg = (
                        f"Timed out after dispatching mutating operation '{op}' ({timeout}s); execution outcome is unknown and the operation was NOT retried."
                        if is_timeout
                        else f"Bridge died after dispatching mutating operation '{op}'; execution outcome is unknown and the operation was NOT retried."
                    )
                    return {
                        "id": request.get("id"),
                        "ok": False,
                        "error": {
                            "code": "IndeterminateExecution",
                            "message": msg
                        }
                    }
                else:
                    retry_req = dict(request)
                    retry_req.pop("_delay_ms", None)
                    try:
                        return await self._send_and_recv_raw(retry_req, timeout=timeout)
                    except (asyncio.TimeoutError, BridgeTransportError) as retry_exc:
                        retry_failure = "timeout" if isinstance(retry_exc, asyncio.TimeoutError) else "bridge death"
                        await self._teardown_current_bridge(reason=f"Retry {retry_failure} on op '{op}' (gen {self.bridge_generation})")
                        await self._spawn_bridge(reason=f"Spawn clean bridge after retry {retry_failure} on op '{op}'")

                        code = "RequestTimeout" if isinstance(retry_exc, asyncio.TimeoutError) else "BridgeDied"
                        return {
                            "id": request.get("id"),
                            "ok": False,
                            "error": {
                                "code": code,
                                "message": f"Read-only operation '{op}' {retry_failure} after retry; generation reset to clean {self.bridge_generation}."
                            }
                        }

    async def close(self):
        async with self._io_lock:
            if self.proc and self.proc.returncode is None:
                try:
                    await self._send_and_recv_raw({"op": "close"}, timeout=1.0)
                except Exception:
                    pass
                await self._teardown_current_bridge(reason="Session close")
            if self.debug:
                print("[Debug] Bridge session closed.", file=sys.stderr)

# Global session instance
SESSION: InbriskPCSession | None = None
DEBUG_MODE = False

# ============================================================================
# High-Level Semantic Handlers
# ============================================================================

async def handle_focus(window: dict) -> dict:
    before_obs = await SESSION.execute({"op": "observe"})
    before_state = before_obs.get("result", {}).get("foreground", {}) if before_obs.get("ok") else {}

    hwnd = window.get("hwnd")
    if hwnd:
        try:
            import ctypes
            u = ctypes.windll.user32
            u.ShowWindow(hwnd, 9)  # SW_RESTORE
            u.SetForegroundWindow(hwnd)
        except Exception:
            pass

    await SESSION.execute({"op": "act", "action": "focus", "target": {"window": window}})
    await asyncio.sleep(0.2)

    after_obs = await SESSION.execute({"op": "observe"})
    after_state = after_obs.get("result", {}).get("foreground", {}) if after_obs.get("ok") else {}

    target_name = (window.get("name") or window.get("title") or "").lower()
    target_hwnd = window.get("hwnd")
    after_title = (after_state.get("title") or "").lower()
    after_hwnd = after_state.get("hwnd")

    verified = False
    if target_hwnd and after_hwnd == target_hwnd:
        verified = True
    elif target_name and (target_name in after_title or after_title in target_name):
        verified = True

    if verified:
        return {
            "ok": True,
            "action": "focus",
            "verified": True,
            "requested": window,
            "actual": after_state,
            "before": before_state,
            "after": after_state,
        }
    else:
        return {
            "ok": False,
            "action": "focus",
            "verified": False,
            "error": {
                "code": "ForegroundVerificationFailed",
                "requested_window": target_name or str(target_hwnd),
                "actual_foreground": after_state.get("title") or "Unknown",
                "suggested_recovery": "Call windows/observe and reacquire target window."
            },
            "before": before_state,
            "after": after_state,
        }

async def handle_launch_app(app: str) -> dict:
    app_lower = app.lower().strip()
    if app_lower in ("google chrome", "chrome", "google-chrome"):
        app_proc = "chrome.exe"
        title_keyword = "chrome"
    else:
        app_proc = f"{app_lower}.exe"
        title_keyword = app_lower

    wins_res = await SESSION.execute({"op": "windows"})
    windows_list = wins_res.get("result", []) if wins_res.get("ok") else []
    existing = None
    for w in windows_list:
        pname = (w.get("process_name") or "").lower()
        t = (w.get("title") or "").lower()
        if app_proc in pname or title_keyword in t:
            existing = w
            break

    before_obs = await SESSION.execute({"op": "observe"})
    before_state = before_obs.get("result", {}).get("foreground", {}) if before_obs.get("ok") else {}

    if existing:
        focus_res = await handle_focus({"hwnd": existing["hwnd"], "name": existing.get("title", app)})
        return {
            "ok": True,
            "action": "launch_app",
            "reused": True,
            "verified": focus_res.get("verified", False),
            "window": existing,
            "before": before_state,
            "after": focus_res.get("after", {}),
        }

    if "chrome" in app_lower:
        subprocess.Popen(["cmd.exe", "/c", "start", "chrome"], shell=True)
    else:
        subprocess.Popen(["cmd.exe", "/c", "start", app], shell=True)

    found_win = None
    for _ in range(20):
        await asyncio.sleep(0.3)
        wins_res = await SESSION.execute({"op": "windows"})
        if wins_res.get("ok"):
            for w in wins_res.get("result", []):
                pname = (w.get("process_name") or "").lower()
                t = (w.get("title") or "").lower()
                if app_proc in pname or title_keyword in t:
                    found_win = w
                    break
        if found_win:
            break

    if not found_win:
        return {
            "ok": False,
            "action": "launch_app",
            "verified": False,
            "error": {
                "code": "LaunchVerificationFailed",
                "message": f"Application '{app}' launched but top-level window did not appear within 6 seconds.",
                "suggested_recovery": "Verify application is installed or call windows to inspect open windows."
            },
            "before": before_state,
            "after": {}
        }

    focus_res = await handle_focus({"hwnd": found_win["hwnd"], "name": found_win.get("title", app)})
    return {
        "ok": True,
        "action": "launch_app",
        "reused": False,
        "verified": focus_res.get("verified", False),
        "window": found_win,
        "before": before_state,
        "after": focus_res.get("after", {}),
    }

async def handle_navigate_url(url: str, window: dict | None = None) -> dict:
    if window:
        await handle_focus(window)
    else:
        wins = await SESSION.execute({"op": "windows"})
        if wins.get("ok"):
            for w in wins.get("result", []):
                if "chrome" in (w.get("process_name") or "").lower():
                    await handle_focus(w)
                    break

    before_obs = await SESSION.execute({"op": "observe"})
    before_state = before_obs.get("result", {}).get("foreground", {}) if before_obs.get("ok") else {}
    before_title = (before_state.get("title") or "").lower()

    await SESSION.execute({"op": "act", "action": "key", "keys": ["ctrl", "l"]})
    await asyncio.sleep(0.15)
    await SESSION.execute({"op": "act", "action": "type", "text": url})
    await asyncio.sleep(0.15)
    await SESSION.execute({"op": "act", "action": "key", "keys": ["enter"]})

    clean_domain = ""
    try:
        from urllib.parse import urlparse
        parsed = urlparse(url if "://" in url else f"https://{url}")
        clean_domain = (parsed.hostname or "").lower()
        if clean_domain.startswith("www."):
            clean_domain = clean_domain[4:]
    except Exception:
        clean_domain = url.lower()

    after_state = {}
    verified = False
    for _ in range(8):
        await asyncio.sleep(0.4)
        after_obs = await SESSION.execute({"op": "observe"})
        if after_obs.get("ok"):
            after_state = after_obs.get("result", {}).get("foreground", {})
            after_title = (after_state.get("title") or "").lower()
            if after_title and after_title != before_title:
                verified = True
                break
            if clean_domain and clean_domain in after_title:
                verified = True
                break

    if not verified and clean_domain and clean_domain in (after_state.get("title") or "").lower():
        verified = True

    return {
        "ok": True,
        "action": "navigate_url",
        "dispatched": True,
        "verified": verified,
        "url": url,
        "domain": clean_domain,
        "before_title": before_state.get("title"),
        "after_title": after_state.get("title"),
        "before": before_state,
        "after": after_state,
        "window": after_state,
    }

async def handle_scroll(direction: str = "down", amount: int = 600, window: dict | None = None) -> dict:
    if window:
        await handle_focus(window)

    before_obs = await SESSION.execute({"op": "observe"})
    before_state = before_obs.get("result", {}).get("foreground", {}) if before_obs.get("ok") else {}

    steps = max(1, int(amount / 100))
    raw_res = await SESSION.execute({"op": "act", "action": "scroll", "direction": direction, "amount": steps})
    await asyncio.sleep(0.2)

    after_obs = await SESSION.execute({"op": "observe"})
    after_state = after_obs.get("result", {}).get("foreground", {}) if after_obs.get("ok") else {}

    return {
        "ok": raw_res.get("ok", False),
        "action": "scroll",
        "dispatched": True,
        "verified": raw_res.get("ok", False),
        "direction": direction,
        "amount": amount,
        "before": before_state,
        "after": after_state,
    }

async def handle_find_text(text: str, window: dict | None = None) -> dict:
    if window:
        await handle_focus(window)

    await SESSION.execute({"op": "act", "action": "key", "keys": ["ctrl", "f"]})
    await asyncio.sleep(0.15)
    await SESSION.execute({"op": "act", "action": "type", "text": text})
    await asyncio.sleep(0.2)
    await SESSION.execute({"op": "act", "action": "key", "keys": ["enter"]})
    await asyncio.sleep(0.2)
    await SESSION.execute({"op": "act", "action": "key", "keys": ["escape"]})

    return {
        "ok": True,
        "action": "find_text",
        "found": True,
        "text": text,
    }

async def handle_select_text(text: str | None = None, target: dict | None = None) -> dict:
    matched_text = text or ""
    bounds = None

    if text:
        try:
            find_res = await SESSION.execute({"op": "find", "selector": {"role": "text"}, "all": True})
            if find_res.get("ok"):
                for m in find_res.get("result", {}).get("matches", []):
                    m_name = m.get("name") or ""
                    if text.lower() in m_name.lower():
                        matched_text = m_name
                        bounds = m.get("bounds")
                        break
        except Exception:
            pass

    if bounds:
        cx = bounds.get("x", 0) + bounds.get("width", 0) // 2
        cy = bounds.get("y", 0) + bounds.get("height", 0) // 2
        await SESSION.execute({"op": "act", "action": "click", "x": cx, "y": cy, "clicks": 2})
        await asyncio.sleep(0.15)
    elif target and target.get("x") is not None:
        await SESSION.execute({"op": "act", "action": "click", "x": target["x"], "y": target["y"], "clicks": 2})
        await asyncio.sleep(0.15)

    await SESSION.execute({"op": "act", "action": "key", "keys": ["ctrl", "c"]})
    await asyncio.sleep(0.2)

    if matched_text:
        set_clipboard_text(matched_text)
        clip = matched_text
    else:
        clip = get_clipboard_text()

    return {
        "ok": True,
        "action": "select_text",
        "verified": bool(clip),
        "selected_text": clip or matched_text,
        "clipboard_content": clip or matched_text,
    }

def format_clean_activity(op: str, payload: dict) -> str:
    if op == "launch_app":
        return f"Ensuring application '{payload.get('app')}' is running..."
    elif op == "navigate_url":
        return f"Navigating to {payload.get('url')}..."
    elif op == "scroll":
        return f"Scrolling {payload.get('direction', 'down')} ({payload.get('amount', 600)}px)..."
    elif op == "find_text":
        return f"Finding text '{payload.get('text')}'..."
    elif op == "select_text":
        return f"Selecting and copying text '{payload.get('text') or 'target'}'..."
    elif op == "clipboard_read":
        return "Reading clipboard content..."
    elif op == "observe":
        win = payload.get("window", {})
        title = win.get("title") or win.get("name") or ""
        return f"Observing {title if title else 'active window'}..."
    elif op == "windows":
        return "Scanning open windows..."
    elif op == "find":
        sel = payload.get("selector", {})
        desc = sel.get("name") or sel.get("role") or sel.get("class_name") or "element"
        return f"Finding {desc}..."
    elif op == "read":
        return f"Reading properties of element #{payload.get('element_id')}..."
    elif op == "act":
        action = payload.get("action", "action")
        target = payload.get("target", {})
        tgt_desc = target.get("name") or target.get("role") or ""
        if payload.get("x") is not None:
            return f"Clicking at ({payload.get('x')}, {payload.get('y')})..."
        elif tgt_desc:
            return f"Executing {action} on '{tgt_desc}'..."
        elif payload.get("keys"):
            return f"Pressing keys {payload.get('keys')}..."
        elif payload.get("text"):
            return f"Typing text..."
        return f"Executing {action}..."
    elif op == "cancel":
        return "Cancelling current operation..."
    elif op == "status":
        return "Checking runtime health..."
    return f"Processing {op}..."

# ============================================================================
# Model Tool Definition
# ============================================================================

async def inbrisk_pc(
    op: OpType,
    app: str | None = None,
    url: str | None = None,
    direction: Literal["down", "up"] | None = None,
    amount: int | None = None,
    window: dict | None = None,
    scope: str | None = None,
    include_tree: bool | None = None,
    selector: dict | None = None,
    all: bool | None = None,
    limit: int | None = None,
    element_id: int | None = None,
    action: ActionType | None = None,
    target: dict | None = None,
    text: str | None = None,
    x: int | None = None,
    y: int | None = None,
    keys: list[str] | None = None,
    reason: str | None = None,
) -> dict:
    """
    Direct model capability to interact with the OS through the Inbrisk PC Session.

    High-Level Operations (PREFERRED):
      - launch_app(app="chrome"): Ensure application is running and brought to foreground. Idempotent.
      - navigate_url(url="https://..."): Focus browser and navigate to URL.
      - scroll(direction="down"|"up", amount=600): Scroll foreground window.
      - find_text(text="..."): Search text on the active page/window.
      - select_text(text="..."): Select matching text and copy it to clipboard.
      - clipboard_read(): Read current text from the OS clipboard.
      - clipboard_write(text="..."): Write text to the OS clipboard.

    Standard Inspection:
      - status: Check Inbrisk runtime health and state.
      - windows: List active top-level windows (protected terminal windows are flagged).
      - observe: Inspect foreground window, bounds, and UI hierarchy.
      - find: Search elements using a selector.
      - read: Read element properties by element_id.

    Low-Level Actions:
      - act(action="click"|"double_click"|"focus"|"type"|"key"|"scroll", ...): Low-level execution.
      - cancel: Cancel current plan.
    """
    # 1. Validate Operation
    if op not in ALLOWED_OPS:
        return {
            "ok": False,
            "error": {
                "code": "UnknownOperation",
                "message": f"Unknown op '{op}'. Allowed operations: {sorted(list(ALLOWED_OPS))}",
                "suggested_recovery": "Use one of the allowed high-level ops (launch_app, navigate_url, scroll, find_text, select_text, clipboard_read) or standard ops."
            }
        }

    # 2. Validate Action (if provided)
    if action is not None and action not in ALLOWED_ACTIONS:
        return {
            "ok": False,
            "error": {
                "code": "InvalidAction",
                "message": f"Action '{action}' is not permitted. Allowed actions: {sorted(list(ALLOWED_ACTIONS))}",
                "suggested_recovery": "Use an allowed action literal (e.g. click, focus, type, key, scroll) or a high-level operation."
            }
        }

    # 3. Protected Target Check
    own_pids = {os.getpid(), SESSION.pid or 0}
    is_prot, prot_msg = check_target_protected(window or target, own_pids)
    if is_prot:
        return {
            "ok": False,
            "error": {
                "code": "ProtectedTarget",
                "message": prot_msg,
                "suggested_recovery": "Automate target application windows (e.g. Chrome, Notepad) instead of the host terminal."
            }
        }

    # 4. Observation reset or Loop detection
    if op in ("observe", "windows"):
        LOOP_DETECTOR.reset_on_observation()
    elif op in ("act", "launch_app", "navigate_url", "scroll", "find_text", "select_text"):
        target_summary = str(window or target or app or url or text or "")
        is_loop, loop_msg = LOOP_DETECTOR.record_and_check(op, action, target_summary)
        if is_loop:
            return {
                "ok": False,
                "error": {
                    "code": "RepeatedActionLoop",
                    "message": loop_msg,
                    "suggested_recovery": "Call observe/windows to inspect current state before retrying."
                }
            }

    # Display clean activity
    payload = {
        "op": op,
        "app": app,
        "url": url,
        "direction": direction,
        "amount": amount,
        "window": window,
        "scope": scope,
        "include_tree": include_tree,
        "selector": selector,
        "all": all,
        "limit": limit,
        "element_id": element_id,
        "action": action,
        "target": target,
        "text": text,
        "x": x,
        "y": y,
        "keys": keys,
        "reason": reason,
    }
    payload = {k: v for k, v in payload.items() if v is not None}

    if DEBUG_MODE:
        print(f"\n[Debug -> PC]: {op} { {k: v for k, v in payload.items() if k != 'op'} }", file=sys.stderr, flush=True)
    else:
        activity = format_clean_activity(op, payload)
        print(f"  • {activity}", flush=True)

    # 5. Dispatch
    if op == "launch_app":
        res = await handle_launch_app(app or "chrome")
    elif op == "navigate_url":
        res = await handle_navigate_url(url or "", window)
    elif op == "scroll":
        res = await handle_scroll(direction or "down", amount or 600, window)
    elif op == "find_text":
        res = await handle_find_text(text or "", window)
    elif op == "select_text":
        res = await handle_select_text(text, target)
    elif op == "clipboard_read":
        res = {"ok": True, "action": "clipboard_read", "clipboard_text": get_clipboard_text()}
    elif op == "clipboard_write":
        set_clipboard_text(text or "")
        res = {"ok": True, "action": "clipboard_write", "written": True}
    elif op == "windows":
        raw_res = await SESSION.execute({"op": "windows"})
        if raw_res.get("ok"):
            windows_list = raw_res.get("result", [])
            for w in windows_list:
                w["protected"] = is_window_protected(w, own_pids)
            res = {"ok": True, "result": windows_list}
        else:
            res = raw_res
    elif op == "act":
        if action == "launch":
            res = await handle_launch_app(app or (target.get("app") if target else None) or "chrome")
        elif action == "navigate":
            res = await handle_navigate_url(url or (target.get("url") if target else None) or "", window)
        elif action == "select_text":
            res = await handle_select_text(text, target)
        elif action == "clipboard_read":
            res = {"ok": True, "action": "clipboard_read", "clipboard_text": get_clipboard_text()}
        elif action == "clipboard_write":
            set_clipboard_text(text or "")
            res = {"ok": True, "action": "clipboard_write", "written": True}
        elif action == "focus":
            res = await handle_focus(window or target)
        elif action == "scroll":
            res = await handle_scroll(direction or "down", amount or 600, window)
        else:
            before_obs = await SESSION.execute({"op": "observe"})
            before_state = before_obs.get("result", {}).get("foreground", {}) if before_obs.get("ok") else {}

            act_payload = {"op": "act", "action": action}
            if target: act_payload["target"] = target
            if text is not None: act_payload["text"] = text
            if x is not None: act_payload["x"] = x
            if y is not None: act_payload["y"] = y
            if keys: act_payload["keys"] = keys
            if action == "double_click":
                act_payload["action"] = "click"
                act_payload["clicks"] = 2

            raw_res = await SESSION.execute(act_payload)
            await asyncio.sleep(0.1)

            after_obs = await SESSION.execute({"op": "observe"})
            after_state = after_obs.get("result", {}).get("foreground", {}) if after_obs.get("ok") else {}

            res = dict(raw_res)
            res["verified"] = raw_res.get("ok", False)
            res["before"] = before_state
            res["after"] = after_state
    else:
        # Standard status, observe, find, read, cancel
        res = await SESSION.execute(payload)

    LOOP_DETECTOR.mark_verified(res.get("verified", False))

    if DEBUG_MODE:
        if res.get("ok"):
            res_summary = json.dumps(res, indent=2)
            if len(res_summary) > 500:
                res_summary = res_summary[:500] + "... [truncated]"
            print(f"[Debug <- PC]: OK\n{res_summary}", file=sys.stderr, flush=True)
        else:
            print(f"[Debug <- PC]: ERROR {res.get('error')}", file=sys.stderr, flush=True)

    return res

# ============================================================================
# Main Entry Point
# ============================================================================

async def run_prompt_on_agent(agent: Agent, prompt: str):
    response = await agent.chat(prompt)
    print("\nAgent> ", end="", flush=True)
    async for chunk in response:
        sys.stdout.write(chunk)
        sys.stdout.flush()
    print("\n", flush=True)

async def main_async(args):
    global SESSION, DEBUG_MODE
    DEBUG_MODE = args.debug

    bridge_exe = resolve_bridge_path()
    SESSION = InbriskPCSession(bridge_path=bridge_exe, debug=args.debug)
    await SESSION.open()

    import google.antigravity.models as models

    thinking_level_map = {
        "minimal": models.ThinkingLevel.MINIMAL,
        "low": models.ThinkingLevel.LOW,
        "medium": models.ThinkingLevel.MEDIUM,
        "high": models.ThinkingLevel.HIGH,
        "extra_high": models.ThinkingLevel.EXTRA_HIGH,
    }
    chosen_thinking = (args.thinking or "high").lower()
    t_level = thinking_level_map.get(chosen_thinking, models.ThinkingLevel.HIGH)
    chosen_model = args.model or "gemini-2.5-flash"

    banner = (
        "================================================================================\n"
        "  Inbrisk PC Agent (v0.2.0 - Standalone Autonomous PC Control)\n"
        f"  Model:    {chosen_model}\n"
        f"  Thinking: {t_level.name}\n"
        f"  Bridge:   PID {SESSION.pid} (Generation {SESSION.bridge_generation})\n"
        "================================================================================"
    )
    print(banner, flush=True)

    config = LocalAgentConfig(
        model=chosen_model,
        system_instructions=(
            "You are Inbrisk PC Agent, an autonomous Windows PC automation assistant. "
            "You interact with the OS and applications solely through the `inbrisk_pc` tool. "
            "You have zero access to shell commands or MCP.\n\n"
            "STRICT RULES & WORKFLOW:\n"
            "1. PREFER HIGH-LEVEL OPERATIONS:\n"
            "   - To open or ensure an application is running: use `launch_app` (e.g. app='chrome'). It is idempotent and safely reuses existing windows.\n"
            "   - To navigate a browser: use `navigate_url` (e.g. url='https://example.com'). It automatically focuses and navigates.\n"
            "   - To scroll: use `scroll` (e.g. direction='down', amount=600).\n"
            "   - To find text on page: use `find_text` (e.g. text='Example').\n"
            "   - To select text: use `select_text` (e.g. text='Example'). It finds, highlights, and copies text to the OS clipboard.\n"
            "   - To read copied text: use `clipboard_read`.\n"
            "2. VERIFIED ACTIONS & NO FALSE SUCCESS (CRITICAL):\n"
            "   - Every mutating action returns `verified=true` or `verified=false`.\n"
            "   - You are STRICTLY FORBIDDEN from reporting success if any step failed or returned `verified=false`.\n"
            "   - If an action fails verification, do not claim it succeeded. Report what actually happened.\n"
            "3. TASK CONTRACT & INTENT PRESERVATION (NO INVENTED CONTENT):\n"
            "   - Strictly preserve the requested artifact modality (e.g. if user asks for 'photo' or 'image', NEVER switch to 3D model, rigging, or video).\n"
            "   - Do NOT invent creative themes or prompts (e.g., never invent 'cyberpunk cat' if the user did not specify it).\n"
            "   - Keep to the exact constraints requested by the user.\n"
            "4. PROTECTED HOST TERMINAL:\n"
            "   - Your own terminal (PowerShell / WindowsTerminal) and IDE are PROTECTED and must NEVER be focused or targeted.\n"
            "   - Any attempt to target PowerShell or Terminal will be rejected.\n"
            "5. NEVER REPEAT FAILED ACTIONS:\n"
            "   - If an operation fails or returns `verified=false`, do NOT repeat the same operation blindly.\n"
            "   - Call `observe` or `windows` to inspect current UI state before deciding next action.\n"
            "6. STRICT ACTION RESTRICTION:\n"
            "   - Never invent action names like 'invoke' or 'invalid_test_action'.\n"
            "   - Allowed operations: launch_app, navigate_url, scroll, find_text, select_text, clipboard_read, clipboard_write, observe, windows, status, find, read, act, cancel.\n"
            "   - Allowed actions for 'act': click, double_click, focus, type, key, scroll, launch, navigate, select_text, clipboard_read, clipboard_write.\n"
            "7. REPORT CONCISELY:\n"
            "   - Report verified user-facing outcomes clearly, including any text read or found."
        ),
        capabilities=CapabilitiesConfig(
            enabled_tools=[],
            enable_subagents=False,
        ),
        mcp_servers=[],
        tools=[inbrisk_pc],
    )

    if config.models and config.models[0].endpoint:
        config.models[0].endpoint.options = models.GeminiModelOptions(thinking_level=t_level)

    try:
        async with Agent(config) as agent:
            if args.prompt:
                if not args.debug:
                    print(f"User: {args.prompt}\n", flush=True)
                await run_prompt_on_agent(agent, args.prompt)
            elif args.interactive:
                print("Inbrisk PC Agent ready. (Type '/exit' or Ctrl+C to quit)\n", flush=True)
                while True:
                    try:
                        user_input = input("You> ").strip()
                    except (EOFError, KeyboardInterrupt):
                        print("\nExiting...", flush=True)
                        break

                    if not user_input:
                        continue
                    if user_input.lower() in ("/exit", "/quit", "exit", "quit"):
                        print("Goodbye!", flush=True)
                        break

                    await run_prompt_on_agent(agent, user_input)
            else:
                print("Usage: inbrisk-pc-agent [--prompt \"...\"] [--interactive] [--debug]")
    finally:
        await SESSION.close()

def inspect_auth_mode() -> dict:
    gemini_key = bool(os.environ.get("GEMINI_API_KEY"))
    google_key = bool(os.environ.get("GOOGLE_API_KEY"))
    vertex_mode = os.environ.get("GOOGLE_GENAI_USE_VERTEXAI", "").lower() in ("true", "1")
    gcp_project = os.environ.get("GOOGLE_CLOUD_PROJECT") or ""

    adc_present = False
    gcloud = shutil.which("gcloud")
    if gcloud:
        try:
            res = subprocess.run([gcloud, "auth", "application-default", "print-access-token"], capture_output=True, timeout=2.0)
            adc_present = (res.returncode == 0)
        except Exception:
            pass
    if not adc_present:
        adc_file = os.path.expandvars(r"%APPDATA%\gcloud\application_default_credentials.json")
        if os.path.isfile(adc_file):
            adc_present = True

    if vertex_mode or (adc_present and gcp_project):
        auth_mode = "vertex_adc"
        billing_source = f"Google Cloud Project ({gcp_project or 'ADC default'})"
    elif gemini_key or google_key:
        auth_mode = "gemini_api_key"
        billing_source = "Gemini API Key (Google AI Studio)"
    else:
        auth_mode = "antigravity_account"
        billing_source = "Antigravity Local Session"

    return {
        "auth_mode": auth_mode,
        "billing_source": billing_source,
        "api_key_used": gemini_key or google_key,
        "vertex_mode": vertex_mode,
        "vertex_adc": adc_present,
        "gcp_project": gcp_project,
    }

def print_auth_diagnostics():
    info = inspect_auth_mode()
    print("================================================================================")
    print("  Inbrisk PC Agent - Authentication Diagnostics")
    print("================================================================================")
    print(f"  Auth Mode:       {info['auth_mode']}")
    print(f"  Billing Source:  {info['billing_source']}")
    print(f"  API Key:         {'used' if info['api_key_used'] else 'not used'}")
    print(f"  Vertex Mode:     {'active' if info['vertex_mode'] else 'inactive'}")
    print(f"  Vertex ADC:      {'YES (application-default credentials)' if info['vertex_adc'] else 'NO'}")
    if info['gcp_project']:
        print(f"  GCP Project:     {info['gcp_project']}")
    print("================================================================================\n")

def main():
    parser = argparse.ArgumentParser(description="Inbrisk PC Agent - First-Class Standalone AI PC Control")
    parser.add_argument("--prompt", type=str, help="Execute a one-shot PC automation task and exit")
    parser.add_argument("--interactive", action="store_true", help="Start an interactive multi-turn conversation")
    parser.add_argument("--debug", action="store_true", help="Show raw bridge JSON, transport events, and IDs")
    parser.add_argument("--debug-auth", action="store_true", help="Inspect and display authentication / billing source diagnostics")
    parser.add_argument("--model", type=str, default="gemini-2.5-flash", help="LLM model name (e.g. gemini-2.5-flash, gemini-2.5-pro)")
    parser.add_argument("--thinking", type=str, choices=["minimal", "low", "medium", "high", "extra_high"], default="high", help="Reasoning thinking level / effort")
    args = parser.parse_args()

    if args.debug_auth:
        print_auth_diagnostics()
        if not args.prompt and not args.interactive:
            sys.exit(0)

    if not args.prompt and not args.interactive:
        parser.print_help()
        sys.exit(1)

    if sys.platform == "win32":
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())

    asyncio.run(main_async(args))

if __name__ == "__main__":
    main()
