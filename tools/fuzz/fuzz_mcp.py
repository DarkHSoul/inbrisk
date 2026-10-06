#!/usr/bin/env python3
"""
fuzz_mcp.py — malformed stdio MCP protocol fuzzer for the Inbrisk MCP server.

Phase-1 exception-resilience verification: every malformed request must
produce a structured JSON-RPC response (error or isError tool result) —
never a hang, never a crash. The server process must remain alive after
500 malformed calls and still answer a final tools/list.

Usage (from repo root):
    python tools/fuzz/fuzz_mcp.py
    python tools/fuzz/fuzz_mcp.py --calls 500 --timeout 10
    python tools/fuzz/fuzz_mcp.py --dll path\to\inbrisk.dll

Exit code: 0 = all assertions passed; 1 = resilience failure.
"""

import argparse
import json
import os
import queue
import subprocess
import sys
import threading

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TARGET_SUBDIR = os.path.join("src", "Inbrisk.Cli", "bin", "net8.0-placeholder")
DLL_CANDIDATES = [
    os.path.join(REPO_ROOT, "src", "Inbrisk.Cli", "bin", "Release",
                 "net8.0-windows10.0.19041.0", "inbrisk.dll"),
    os.path.join(REPO_ROOT, "src", "Inbrisk.Cli", "bin", "Debug",
                 "net8.0-windows10.0.19041.0", "inbrisk.dll"),
]

CALL_TIMEOUT_S = 10.0
DEFAULT_CALLS = 500


def find_dll(override=None):
    if override:
        if os.path.isfile(override):
            return override
        sys.exit(f"error: --dll path does not exist: {override}")
    for p in DLL_CANDIDATES:
        if os.path.isfile(p):
            return p
    sys.exit("error: no built inbrisk.dll found under "
             "src/Inbrisk.Cli/bin/{Release,Debug}/net8.0-windows10.0.19041.0 — "
             "run `dotnet build -c Release` first.")


class StdioServer:
    """Spawns `dotnet inbrisk.dll mcp` and reads stdout lines on a thread."""

    def __init__(self, dll):
        self.proc = subprocess.Popen(
            ["dotnet", dll, "mcp"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True, encoding="utf-8", errors="replace",
            bufsize=1)
        self._lines = queue.Queue()
        self._reader = threading.Thread(target=self._read_loop, daemon=True)
        self._reader.start()

    def _read_loop(self):
        try:
            for line in self.proc.stdout:
                self._lines.put(line)
        except Exception:
            pass
        self._lines.put(None)  # EOF sentinel

    def alive(self):
        return self.proc.poll() is None

    def send(self, payload):
        """Send one stdin line. `payload` is a raw string (possibly invalid JSON)."""
        self.proc.stdin.write(payload + "\n")
        self.proc.stdin.flush()

    def read_response(self, want_id, timeout):
        """Read stdout lines until a JSON-RPC response matching want_id arrives,
        or timeout. Notifications (no id / different id) are skipped."""
        deadline = threading.Event()
        timer = threading.Timer(timeout, deadline.set)
        timer.start()
        try:
            while not deadline.is_set():
                try:
                    line = self._lines.get(timeout=0.05)
                except queue.Empty:
                    if not self.alive():
                        return ("died", None)
                    continue
                if line is None:
                    return ("eof", None)
                line = line.strip()
                if not line:
                    continue
                try:
                    msg = json.loads(line)
                except json.JSONDecodeError:
                    continue  # non-protocol output on stdout — count as noise
                if not isinstance(msg, dict):
                    continue
                if msg.get("id") == want_id and ("result" in msg or "error" in msg):
                    return ("ok", msg)
                # else: a notification or a response to a different id — skip
            return ("timeout", None)
        finally:
            timer.cancel()

    def close(self):
        try:
            self.proc.kill()
        except Exception:
            pass


def build_malformed_requests(n):
    """Generate `n` deterministic malformed stdio payloads.

    Each entry is (raw_line_to_send, response_id_or_None). Requests with a
    well-formed id expect a matching response; intentionally malformed lines
    that cannot carry an id (invalid JSON) expect a response with id=None or
    a parse-error response — the server may also legitimately ignore them,
    so those are sent but not counted as timeouts when no reply arrives.
    """
    reqs = []
    kinds = [
        "invalid_json", "wrong_types", "missing_args", "huge_string",
        "invalid_hwnd", "unknown_tool", "bad_run_steps",
    ]
    next_id = 100
    for i in range(n):
        kind = kinds[i % len(kinds)]
        if kind == "invalid_json":
            # Not parseable — server should emit a JSON-RPC parse error or drop it.
            reqs.append((["{not json", '{"jsonrpc":"2.0",', "garbage!!!",
                          '{"method":'][i % 4], None))
        elif kind == "wrong_types":
            rid = next_id; next_id += 1
            variants = [
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": "a-string-not-object"},
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": 42},
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": {"name": 12345, "arguments": []}},
                {"jsonrpc": "2.0", "id": rid, "method": i},
            ]
            reqs.append((json.dumps(variants[i % len(variants)]), rid))
        elif kind == "missing_args":
            rid = next_id; next_id += 1
            variants = [
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": {"name": "computer_inspect"}},
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call"},
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": {"name": "computer_click"}},
                {"jsonrpc": "2.0", "id": rid, "method": "tools/call",
                 "params": {"name": "computer_type"}},
            ]
            reqs.append((json.dumps(variants[i % len(variants)]), rid))
        elif kind == "huge_string":
            rid = next_id; next_id += 1
            reqs.append((json.dumps({
                "jsonrpc": "2.0", "id": rid, "method": "tools/call",
                "params": {"name": "computer_type",
                           "arguments": {"text": "A" * 200_000}}}), rid))
        elif kind == "invalid_hwnd":
            rid = next_id; next_id += 1
            variants = [
                {"name": "computer_inspect", "arguments": {"hwnd": "not-a-hwnd"}},
                {"name": "computer_inspect", "arguments": {"hwnd": -1}},
                {"name": "computer_inspect", "arguments": {"hwnd": 99999999999999}},
                {"name": "computer_close_window", "arguments": {"hwnd": "0xZZZZ"}},
            ]
            reqs.append((json.dumps({
                "jsonrpc": "2.0", "id": rid, "method": "tools/call",
                "params": variants[i % len(variants)]}), rid))
        elif kind == "unknown_tool":
            rid = next_id; next_id += 1
            reqs.append((json.dumps({
                "jsonrpc": "2.0", "id": rid, "method": "tools/call",
                "params": {"name": f"computer_fuzz_{i}",
                           "arguments": {"x": 1}}}), rid))
        else:  # bad_run_steps
            rid = next_id; next_id += 1
            variants = [
                {"name": "computer_run", "arguments": {"steps": "not-a-list"}},
                {"name": "computer_run", "arguments": {"steps": [{"kind": 123}]}},
                {"name": "computer_run", "arguments": {"steps": [{"kind": "explode",
                                                                "hwnd": "junk"}]}},
                {"name": "computer_run", "arguments": {"steps": [None, 42, {}]}},
            ]
            reqs.append((json.dumps({
                "jsonrpc": "2.0", "id": rid, "method": "tools/call",
                "params": variants[i % len(variants)]}), rid))
    return reqs


def main():
    ap = argparse.ArgumentParser(description="Malformed-stdio MCP fuzzer for Inbrisk")
    ap.add_argument("--dll", help="explicit path to inbrisk.dll")
    ap.add_argument("--calls", type=int, default=DEFAULT_CALLS,
                    help="number of malformed calls (default 500)")
    ap.add_argument("--timeout", type=float, default=CALL_TIMEOUT_S,
                    help="per-call timeout in seconds (default 10)")
    args = ap.parse_args()

    dll = find_dll(args.dll)
    print(f"[fuzz] target: {dll}")
    server = StdioServer(dll)
    failures = []
    timeouts = 0
    structured = 0
    no_reply = 0

    try:
        # --- MCP initialize handshake ------------------------------------
        server.send(json.dumps({
            "jsonrpc": "2.0", "id": 0, "method": "initialize",
            "params": {"protocolVersion": "2024-11-05",
                       "capabilities": {},
                       "clientInfo": {"name": "fuzz-mcp", "version": "0.0.1"}}}))
        status, msg = server.read_response(0, args.timeout)
        if status != "ok":
            sys.exit(f"[fuzz] FAIL: initialize handshake got {status} (server "
                     f"{'dead' if not server.alive() else 'alive'})")
        server.send(json.dumps({
            "jsonrpc": "2.0", "method": "notifications/initialized"}))
        print("[fuzz] handshake ok — beginning malformed calls")

        # --- malformed call storm -----------------------------------------
        for i, (line, want_id) in enumerate(build_malformed_requests(args.calls)):
            if not server.alive():
                failures.append(f"server died before call #{i}")
                break
            try:
                server.send(line)
            except (BrokenPipeError, OSError) as e:
                failures.append(f"stdin write failed at call #{i}: {e}")
                break

            if want_id is None:
                # Unparseable line — drain briefly but don't demand a reply.
                status, _ = server.read_response(-1, timeout=0.3)
                if status in ("died", "eof") and not server.alive():
                    failures.append(f"server died on invalid-JSON call #{i}")
                    break
                no_reply += 1
                continue

            status, msg = server.read_response(want_id, args.timeout)
            if status == "ok":
                structured += 1
            elif status == "timeout":
                timeouts += 1
            else:  # died / eof
                failures.append(f"server died at call #{i} (status={status})")
                break
            if (i + 1) % 50 == 0:
                print(f"[fuzz] {i + 1}/{args.calls} — structured={structured} "
                      f"timeouts={timeouts} no-reply={no_reply}")

        # --- final liveness check ------------------------------------------
        if server.alive():
            server.send(json.dumps({
                "jsonrpc": "2.0", "id": 99999, "method": "tools/list"}))
            status, msg = server.read_response(99999, args.timeout)
            if status != "ok":
                failures.append(f"final tools/list failed: {status}")
            elif not (isinstance(msg.get("result"), dict)
                      and "tools" in msg["result"]):
                failures.append("final tools/list returned malformed result")
        else:
            failures.append("server not alive for final tools/list")
    finally:
        server.close()

    # --- verdict -----------------------------------------------------------
    print(f"[fuzz] done: calls={args.calls} structured={structured} "
          f"timeouts={timeouts} no-reply={no_reply} failures={len(failures)}")
    for f in failures:
        print(f"[fuzz]   FAIL: {f}")
    if timeouts:
        print(f"[fuzz]   FAIL: {timeouts} malformed calls timed out "
              f"(expected structured errors, not hangs)")
    if failures or timeouts:
        sys.exit(1)
    print("[fuzz] PASS — server stayed alive, all malformed calls answered")


if __name__ == "__main__":
    main()
