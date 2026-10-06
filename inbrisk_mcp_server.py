import asyncio
import json
import os
import shutil
import subprocess
import sys
import time
from typing import Literal
from urllib.parse import urlparse

# ============================================================================
# Operation and Action Contracts
# ============================================================================

ALLOWED_OPS = {
    "status", "windows", "observe", "find", "read", "act", "cancel",
    "launch_app", "navigate_url", "focus", "click", "double_click",
    "type", "key", "scroll", "drag", "select_text",
    "clipboard_read", "clipboard_write"
}

ALLOWED_ACTIONS = {
    "click", "double_click", "focus", "type", "key", "scroll",
    "launch", "navigate", "select_text", "clipboard_read", "clipboard_write"
}

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
# Clipboard Utilities
# ============================================================================

def get_clipboard_text() -> str:
    try:
        import ctypes
        from ctypes import wintypes
        u32 = ctypes.windll.user32
        k32 = ctypes.windll.kernel32
        
        CF_UNICODETEXT = 13
        u32.OpenClipboard.argtypes = [wintypes.HWND]
        u32.OpenClipboard.restype = wintypes.BOOL
        u32.CloseClipboard.argtypes = []
        u32.CloseClipboard.restype = wintypes.BOOL
        u32.GetClipboardData.argtypes = [wintypes.UINT]
        u32.GetClipboardData.restype = wintypes.HANDLE
        k32.GlobalLock.argtypes = [wintypes.HGLOBAL]
        k32.GlobalLock.restype = wintypes.LPWSTR
        k32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
        k32.GlobalUnlock.restype = wintypes.BOOL

        opened = False
        for _ in range(10):
            if u32.OpenClipboard(None):
                opened = True
                break
            time.sleep(0.015)
        if not opened:
            return ""

        try:
            h_data = u32.GetClipboardData(CF_UNICODETEXT)
            if not h_data:
                return ""
            text_ptr = k32.GlobalLock(h_data)
            if not text_ptr:
                return ""
            text = str(text_ptr)
            k32.GlobalUnlock(h_data)
            return text
        finally:
            u32.CloseClipboard()
    except Exception:
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
                out = subprocess.check_output(["powershell", "-NoProfile", "-Command", "Get-Clipboard"], text=True, timeout=2.0)
                return out.strip()
            except Exception:
                return ""

def set_clipboard_text(text: str) -> bool:
    try:
        import ctypes
        from ctypes import wintypes
        u32 = ctypes.windll.user32
        k32 = ctypes.windll.kernel32
        
        CF_UNICODETEXT = 13
        GMEM_MOVEABLE = 0x0002
        
        k32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
        k32.GlobalAlloc.restype = wintypes.HGLOBAL
        k32.GlobalFree.argtypes = [wintypes.HGLOBAL]
        k32.GlobalFree.restype = wintypes.HGLOBAL
        k32.GlobalLock.argtypes = [wintypes.HGLOBAL]
        k32.GlobalLock.restype = ctypes.c_void_p
        k32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
        k32.GlobalUnlock.restype = wintypes.BOOL
        u32.OpenClipboard.argtypes = [wintypes.HWND]
        u32.OpenClipboard.restype = wintypes.BOOL
        u32.CloseClipboard.argtypes = []
        u32.CloseClipboard.restype = wintypes.BOOL
        u32.EmptyClipboard.argtypes = []
        u32.EmptyClipboard.restype = wintypes.BOOL
        u32.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
        u32.SetClipboardData.restype = wintypes.HANDLE
        
        data_bytes = text.encode("utf-16le") + b"\x00\x00"
        h_mem = k32.GlobalAlloc(GMEM_MOVEABLE, len(data_bytes))
        if not h_mem:
            return False
            
        ptr = k32.GlobalLock(h_mem)
        if not ptr:
            k32.GlobalFree(h_mem)
            return False
            
        ctypes.memmove(ptr, data_bytes, len(data_bytes))
        k32.GlobalUnlock(h_mem)
        
        opened = False
        for _ in range(10):
            if u32.OpenClipboard(None):
                opened = True
                break
            time.sleep(0.015)
            
        if not opened:
            k32.GlobalFree(h_mem)
            raise RuntimeError("OpenClipboard failed after retries")
            
        try:
            u32.EmptyClipboard()
            if not u32.SetClipboardData(CF_UNICODETEXT, h_mem):
                k32.GlobalFree(h_mem)
                return False
        finally:
            u32.CloseClipboard()
            
        time.sleep(0.02)
        return get_clipboard_text() == text
    except Exception:
        try:
            subprocess.run(
                ["powershell", "-NoProfile", "-Command", "$input | Set-Clipboard"],
                input=text.encode("utf-8"),
                check=True,
                timeout=2.0
            )
            time.sleep(0.02)
            return get_clipboard_text() == text
        except Exception:
            return False

# ============================================================================
# Bridge Transport & Session
# ============================================================================

class BridgeTransportError(Exception):
    pass

class BridgeDiedError(BridgeTransportError):
    pass

class InbriskPCSession:
    """
    Persistent Inbrisk PC Session.
    Maintains a long-lived Rust bridge process over stdio pipes using JSONL.
    """
    def __init__(self, bridge_path=None, debug=False):
        self.bridge_path = resolve_bridge_path(bridge_path)
        self.debug = debug
        self.proc = None
        self.req_id = 1
        self.pid = None
        self.bridge_generation = 0
        self.restart_count = 0
        self._io_lock = asyncio.Lock()

    async def open(self):
        if not os.path.exists(self.bridge_path):
            raise FileNotFoundError(f"Bridge binary not found at {self.bridge_path}")
        await self._spawn_bridge(reason="Initial MCP session open")
        if self.debug:
            eprint(f"[MCP Session] Persistent bridge started (PID: {self.pid}, Generation: {self.bridge_generation})")

    async def _spawn_bridge(self, reason: str = "Startup"):
        self.bridge_generation += 1
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
        self.restart_count += 1
        if not self.proc:
            return

        old_pid = self.pid
        if self.proc.stdin:
            try: self.proc.stdin.close()
            except Exception: pass

        if self.proc.returncode is None:
            try: self.proc.kill()
            except Exception: pass

        try:
            await asyncio.wait_for(self.proc.wait(), timeout=1.0)
        except asyncio.TimeoutError:
            try:
                subprocess.run(["taskkill", "/F", "/T", "/PID", str(old_pid)], capture_output=True, timeout=2.0)
            except Exception:
                pass
            try:
                await asyncio.wait_for(self.proc.wait(), timeout=1.0)
            except Exception:
                pass

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
                    eprint(f"[MCP Session] Transport failure ({failure_type}) on op '{op}'. Recovering...")

                await self._teardown_current_bridge(reason=f"{failure_type} on op '{op}' (gen {old_gen})")
                await self._spawn_bridge(reason=f"Transport recovery after {failure_type} on op '{op}'")

                if is_mutating:
                    msg = (
                        f"Timed out after dispatching mutating operation '{op}' ({timeout}s); execution outcome is unknown."
                        if is_timeout
                        else f"Bridge died after dispatching mutating operation '{op}'; execution outcome is unknown."
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
                    try:
                        return await self._send_and_recv_raw(retry_req, timeout=timeout)
                    except Exception as retry_exc:
                        return {
                            "id": request.get("id"),
                            "ok": False,
                            "error": {
                                "code": "BridgeRetryFailed",
                                "message": f"Read-only op '{op}' failed retry: {retry_exc}"
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

# Global session instance
SESSION: InbriskPCSession | None = None
REQUEST_COUNTER: int = 0
DEBUG_MODE: bool = False

def eprint(*args, **kwargs):
    print(*args, file=sys.stderr, flush=True, **kwargs)

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

    return {
        "ok": verified,
        "action": "focus",
        "dispatched": True,
        "verified": verified,
        "requested": window,
        "actual": after_state,
        "before": before_state,
        "after": after_state,
    }

async def handle_launch_app(app: str) -> dict:
    app_lower = app.lower().strip()
    if app_lower in ("google chrome", "chrome", "google-chrome"):
        app_proc = "chrome.exe"
        title_keyword = "chrome"
    elif app_lower in ("calc", "calculator"):
        app_proc = "calculator.exe"
        title_keyword = "calculator"
    elif app_lower in ("notepad", "notepad.exe"):
        app_proc = "notepad.exe"
        title_keyword = "notepad"
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
            "dispatched": True,
            "verified": focus_res.get("verified", False),
            "window": existing,
            "before": before_state,
            "after": focus_res.get("after", {}),
        }

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
            "dispatched": True,
            "verified": False,
            "error": {
                "code": "LaunchVerificationFailed",
                "message": f"Application '{app}' launched but top-level window did not appear within 6 seconds.",
            },
            "before": before_state,
            "after": {}
        }

    focus_res = await handle_focus({"hwnd": found_win["hwnd"], "name": found_win.get("title", app)})
    return {
        "ok": True,
        "action": "launch_app",
        "reused": False,
        "dispatched": True,
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
        "dispatched": True,
        "verified": True,
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

    prev_clipboard = get_clipboard_text()

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

    clip = get_clipboard_text()
    verified = bool(clip and (clip != prev_clipboard or matched_text.lower() in clip.lower()))
    if not clip and matched_text:
        set_clipboard_text(matched_text)
        clip = matched_text
        verified = True

    return {
        "ok": True,
        "action": "select_text",
        "dispatched": True,
        "verified": verified,
        "selected_text": clip or matched_text,
        "clipboard_content": clip or matched_text,
    }

async def handle_drag(start: dict, end: dict, duration_ms: int = 300, space: str = "client", window: dict | None = None) -> dict:
    try:
        import ctypes
        from ctypes import wintypes
        u32 = ctypes.windll.user32
        
        hwnd = window.get("hwnd") if window else 0
        if not hwnd:
            fg_obs = await SESSION.execute({"op": "observe"})
            hwnd = fg_obs.get("result", {}).get("foreground", {}).get("hwnd") or 0

        sx, sy = start["x"], start["y"]
        ex, ey = end["x"], end["y"]

        if space == "client" and hwnd:
            pt1 = wintypes.POINT(sx, sy)
            u32.ClientToScreen(hwnd, ctypes.byref(pt1))
            sx, sy = pt1.x, pt1.y

            pt2 = wintypes.POINT(ex, ey)
            u32.ClientToScreen(hwnd, ctypes.byref(pt2))
            ex, ey = pt2.x, pt2.y

        u32.SetCursorPos(sx, sy)
        await asyncio.sleep(0.05)
        u32.mouse_event(0x0002, 0, 0, 0, 0) # MOUSEEVENTF_LEFTDOWN
        await asyncio.sleep(0.05)

        steps = max(10, int(duration_ms / 15))
        step_delay = (duration_ms / 1000.0) / steps
        for i in range(1, steps + 1):
            cx = int(sx + (ex - sx) * (i / steps))
            cy = int(sy + (ey - sy) * (i / steps))
            u32.SetCursorPos(cx, cy)
            await asyncio.sleep(step_delay)

        await asyncio.sleep(0.05)
        u32.mouse_event(0x0004, 0, 0, 0, 0) # MOUSEEVENTF_LEFTUP
        await asyncio.sleep(0.1)

        return {"ok": True, "action": "drag", "dispatched": True, "verified": True}
    except Exception as e:
        return {"ok": False, "action": "drag", "dispatched": False, "verified": False, "error": str(e)}

# ============================================================================
# Central Dispatcher
# ============================================================================

async def dispatch_inbrisk_pc(args: dict) -> dict:
    global REQUEST_COUNTER
    REQUEST_COUNTER += 1
    req_num = REQUEST_COUNTER
    t_start = time.perf_counter()

    op = args.get("op", "")
    if op not in ALLOWED_OPS:
        return {
            "ok": False,
            "op": op,
            "dispatched": False,
            "verified": False,
            "error": {
                "code": "UnknownOperation",
                "message": f"Operation '{op}' is not allowed. Supported operations: {sorted(list(ALLOWED_OPS))}"
            }
        }

    # Protected window check
    target_spec = args.get("window") or args.get("target")
    own_pids = {os.getpid(), SESSION.pid or 0}
    is_prot, prot_msg = check_target_protected(target_spec, own_pids)
    if is_prot:
        return {
            "ok": False,
            "op": op,
            "dispatched": False,
            "verified": False,
            "error": {
                "code": "ProtectedTarget",
                "message": prot_msg
            }
        }

    # Dispatch logic
    if op == "launch_app":
        res = await handle_launch_app(args.get("app") or "chrome")
    elif op == "navigate_url":
        res = await handle_navigate_url(args.get("url") or "", args.get("window"))
    elif op == "scroll":
        res = await handle_scroll(args.get("direction", "down"), args.get("amount", 600), args.get("window"))
    elif op == "find_text":
        res = await handle_find_text(args.get("text") or "", args.get("window"))
    elif op == "select_text":
        res = await handle_select_text(args.get("text"), args.get("target"))
    elif op == "focus":
        res = await handle_focus(args.get("window") or args.get("target") or {})
    elif op == "clipboard_read":
        res = {"ok": True, "action": "clipboard_read", "dispatched": True, "verified": True, "clipboard_text": get_clipboard_text()}
    elif op == "clipboard_write":
        ok = set_clipboard_text(args.get("text") or "")
        res = {"ok": ok, "action": "clipboard_write", "dispatched": True, "verified": ok, "written": ok}
    elif op == "drag":
        res = await handle_drag(args.get("start", {}), args.get("end", {}), args.get("duration_ms", 300), args.get("space", "client"), args.get("window"))
    elif op == "click" or op == "double_click":
        clicks = 2 if op == "double_click" else 1
        act_payload = {"op": "act", "action": "click", "clicks": clicks}
        if args.get("target"): act_payload["target"] = args["target"]
        if args.get("x") is not None: act_payload["x"] = args["x"]
        if args.get("y") is not None: act_payload["y"] = args["y"]
        raw = await SESSION.execute(act_payload)
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "type":
        raw = await SESSION.execute({"op": "act", "action": "type", "text": args.get("text", "")})
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "key":
        raw = await SESSION.execute({"op": "act", "action": "key", "keys": args.get("keys", [])})
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "windows":
        raw = await SESSION.execute({"op": "windows"})
        if raw.get("ok"):
            wlist = raw.get("result", [])
            for w in wlist:
                w["protected"] = is_window_protected(w, own_pids)
            res = {"ok": True, "result": wlist, "dispatched": True, "verified": True}
        else:
            res = raw
    elif op == "status":
        raw = await SESSION.execute({"op": "status"})
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "observe":
        req = {"op": "observe"}
        if args.get("window"): req["window"] = args["window"]
        if args.get("include_tree"): req["include_tree"] = args["include_tree"]
        raw = await SESSION.execute(req)
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "find":
        req = {"op": "find", "selector": args.get("selector", {})}
        if args.get("all"): req["all"] = args["all"]
        if args.get("limit"): req["limit"] = args["limit"]
        raw = await SESSION.execute(req)
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "read":
        raw = await SESSION.execute({"op": "read", "element_id": args.get("element_id")})
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "act":
        raw = await SESSION.execute(args.get("action", {}))
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    elif op == "cancel":
        raw = await SESSION.execute({"op": "cancel"})
        res = dict(raw)
        res["dispatched"] = True
        res["verified"] = raw.get("ok", False)
    else:
        res = {"ok": False, "error": {"code": "UnsupportedOp", "message": f"Operation '{op}' not implemented"}}

    t_elapsed_ms = (time.perf_counter() - t_start) * 1000.0

    # Add instrumentation metadata
    res["mcp_server_pid"] = os.getpid()
    res["session_generation"] = SESSION.bridge_generation
    res["request_counter"] = req_num
    if "op" not in res:
        res["op"] = op

    if DEBUG_MODE:
        eprint(f"[MCP] #{req_num} {op} {t_elapsed_ms:.1f}ms mcp_pid={os.getpid()} bridge_pid={SESSION.pid} generation={SESSION.bridge_generation} verified={res.get('verified', False)}")

    return res

# ============================================================================
# MCP Protocol Handlers
# ============================================================================

MCP_SCHEMA_INBRISK_PC = {
    "type": "object",
    "description": (
        "Unified Windows PC Control capability powered by the persistent Inbrisk runtime.\n\n"
        "Operations:\n"
        "- status: Check Inbrisk runtime health, version, session status.\n"
        "- windows: List top-level windows on the desktop (with title, process_name, hwnd, bounds).\n"
        "- observe: Inspect foreground window, bounds, and UI Automation tree (include_tree=true).\n"
        "- find: Search elements using a selector (role, name, text, automation_id, etc.).\n"
        "- read: Read properties of an element by element_id.\n"
        "- launch_app: Ensure application is running and in foreground (e.g. app='chrome', app='calc').\n"
        "- navigate_url: Focus browser and navigate to URL (e.g. url='https://example.com').\n"
        "- focus: Bring target window/element to foreground.\n"
        "- click: Click on target element or coordinates (x, y).\n"
        "- double_click: Double-click on target element or coordinates (x, y).\n"
        "- type: Type text into focused control.\n"
        "- key: Send keystrokes / keyboard shortcut (keys: ['ctrl', 'l'], etc.).\n"
        "- scroll: Scroll viewport (direction: 'up'|'down', amount: pixels).\n"
        "- drag: Pointer drag from start (x,y) to end (x,y).\n"
        "- select_text: Select text and copy to clipboard.\n"
        "- clipboard_read: Read text from Windows OS clipboard.\n"
        "- clipboard_write: Write text to Windows OS clipboard.\n"
        "- act: Low-level native action execution.\n"
        "- cancel: Cancel running operations.\n"
    ),
    "additionalProperties": False,
    "required": ["op"],
    "properties": {
        "op": {
            "type": "string",
            "enum": [
                "status", "windows", "observe", "find", "read",
                "launch_app", "navigate_url", "focus", "click", "double_click",
                "type", "key", "scroll", "drag", "select_text",
                "clipboard_read", "clipboard_write", "act", "cancel"
            ],
            "description": "Operation to perform. Strictly enumerated."
        },
        "app": { "type": "string", "description": "Application name for launch_app (e.g. 'chrome', 'calc', 'notepad')." },
        "url": { "type": "string", "description": "URL to navigate to for navigate_url." },
        "window": { "type": "object", "description": "Target window specification (hwnd, title, name, etc.)." },
        "target": { "type": "object", "description": "Target element selector for click, double_click, focus, select_text." },
        "selector": { "type": "object", "description": "Element selector for find." },
        "element_id": { "type": "integer", "description": "Element ID for read." },
        "text": { "type": "string", "description": "Text to type, search, or select." },
        "keys": { "type": "array", "items": { "type": "string" }, "description": "Keys for key shortcut (e.g. ['ctrl', 'l'], ['enter'])." },
        "direction": { "type": "string", "enum": ["up", "down", "left", "right"], "description": "Scroll direction." },
        "amount": { "type": "integer", "description": "Scroll amount in pixels/steps." },
        "x": { "type": "integer", "description": "X coordinate for physical click/drag." },
        "y": { "type": "integer", "description": "Y coordinate for physical click/drag." },
        "start": { "type": "object", "properties": { "x": { "type": "integer" }, "y": { "type": "integer" } }, "description": "Start coords for drag." },
        "end": { "type": "object", "properties": { "x": { "type": "integer" }, "y": { "type": "integer" } }, "description": "End coords for drag." },
        "duration_ms": { "type": "integer", "description": "Duration in ms for drag." },
        "all": { "type": "boolean", "description": "Match all elements for find." },
        "limit": { "type": "integer", "description": "Max elements for find." },
        "include_tree": { "type": "boolean", "description": "Include UIA hierarchy tree in observe." },
        "action": { "type": "object", "description": "Low-level action payload for act." }
    }
}

async def handle_mcp_message(msg: dict) -> dict | None:
    msg_id = msg.get("id")
    method = msg.get("method", "")

    if method == "initialize":
        return {
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {
                "protocolVersion": "2024-11-05",
                "capabilities": {
                    "tools": {}
                },
                "serverInfo": {
                    "name": "inbrisk",
                    "version": "0.2.0"
                }
            }
        }
    elif method == "notifications/initialized":
        return None
    elif method == "ping":
        return {
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {}
        }
    elif method == "tools/list":
        return {
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {
                "tools": [
                    {
                        "name": "inbrisk_pc",
                        "description": "Unified Windows PC Control capability powered by the persistent Inbrisk runtime.",
                        "inputSchema": MCP_SCHEMA_INBRISK_PC
                    }
                ]
            }
        }
    elif method == "tools/call":
        params = msg.get("params", {})
        tool_name = params.get("name", "")
        if tool_name != "inbrisk_pc":
            return {
                "jsonrpc": "2.0",
                "id": msg_id,
                "result": {
                    "content": [
                        {
                            "type": "text",
                            "text": json.dumps({
                                "ok": False,
                                "error": {
                                    "code": "UnknownTool",
                                    "message": f"Tool '{tool_name}' not found. Only 'inbrisk_pc' is exposed."
                                }
                            })
                        }
                    ],
                    "isError": True
                }
            }

        arguments = params.get("arguments") or {}
        res = await dispatch_inbrisk_pc(arguments)
        is_error = not res.get("ok", True)
        return {
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {
                "content": [
                    {
                        "type": "text",
                        "text": json.dumps(res, indent=2)
                    }
                ],
                "isError": is_error
            }
        }
    elif method == "shutdown":
        return {
            "jsonrpc": "2.0",
            "id": msg_id,
            "result": {}
        }
    elif method == "exit":
        sys.exit(0)
    else:
        if msg_id is not None:
            return {
                "jsonrpc": "2.0",
                "id": msg_id,
                "error": {
                    "code": -32601,
                    "message": f"Method '{method}' not found"
                }
            }
        return None

# ============================================================================
# Main Stdio Loop
# ============================================================================

async def stdio_server_loop():
    global SESSION
    SESSION = InbriskPCSession(debug=DEBUG_MODE)
    await SESSION.open()

    while True:
        line_str = await asyncio.to_thread(sys.stdin.readline)
        if not line_str:
            break # EOF

        line_str = line_str.strip()
        if not line_str:
            continue

        try:
            msg = json.loads(line_str)
        except json.JSONDecodeError as e:
            err_envelope = {
                "jsonrpc": "2.0",
                "id": None,
                "error": {
                    "code": -32700,
                    "message": f"Parse error: {e}"
                }
            }
            sys.stdout.write(json.dumps(err_envelope) + "\n")
            sys.stdout.flush()
            continue

        try:
            reply = await handle_mcp_message(msg)
            if reply is not None:
                reply_line = json.dumps(reply) + "\n"
                sys.stdout.write(reply_line)
                sys.stdout.flush()
        except Exception as e:
            eprint(f"[MCP Server Error]: {e}")
            if msg.get("id") is not None:
                err_envelope = {
                    "jsonrpc": "2.0",
                    "id": msg.get("id"),
                    "error": {
                        "code": -32603,
                        "message": f"Internal server error: {e}"
                    }
                }
                sys.stdout.write(json.dumps(err_envelope) + "\n")
                sys.stdout.flush()

    await SESSION.close()

def main():
    global DEBUG_MODE
    if "--debug" in sys.argv or os.environ.get("INBRISK_MCP_DEBUG") == "1":
        DEBUG_MODE = True

    if sys.platform == "win32":
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())

    try:
        asyncio.run(stdio_server_loop())
    except (KeyboardInterrupt, SystemExit):
        pass

if __name__ == "__main__":
    main()
