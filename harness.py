import asyncio
import json
import os
import subprocess
import sys

from google.antigravity import Agent, LocalAgentConfig, CapabilitiesConfig

READ_ONLY_OPS = {"status", "windows", "observe", "find", "read", "hello"}
MUTATING_OPS = {"act", "cancel"}

class BridgeTransportError(Exception):
    """Base transport error for bridge communication."""
    pass

class BridgeDiedError(BridgeTransportError):
    """Raised when bridge process exits unexpectedly or stdout hits EOF."""
    pass

class BridgeTeardownFailed(BridgeTransportError):
    """Raised when teardown cannot confirm termination of bridge process."""
    pass

class InbriskPCSession:
    """
    Host-owned persistent Inbrisk PC Session.
    Maintains a long-lived Rust bridge process over anonymous stdio pipes using JSONL.
    Features:
      - Strict asyncio.Lock serialization (zero concurrent reader/pipe corruption)
      - Typed transport error propagation (BridgeDiedError, BridgeTeardownFailed)
      - Deterministic transport recovery on timeouts and bridge deaths
      - IndeterminateExecution safety for mutating timeouts and bridge deaths (never duplicated)
      - Transparent single retry for read-only timeouts and bridge deaths
      - Automatic teardown of wedged retry bridges (clean Gen N+2 ready on double timeout)
      - Guaranteed zero-zombie teardown escalation
      - Single-request ID handshake
      - Generation and PID tracking
    """
    def __init__(self, bridge_path="target/release/inbrisk-bridge.exe"):
        self.bridge_path = bridge_path
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
        print(f"=== INBRISK PC SESSION STARTED (Bridge PID: {self.pid}, Generation: {self.bridge_generation}) ===", flush=True)

    async def _spawn_bridge(self, reason: str = "Startup"):
        self.bridge_generation += 1
        self.last_restart_reason = reason
        self.proc = await asyncio.create_subprocess_exec(
            self.bridge_path,
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=sys.stderr,
            limit=16 * 1024 * 1024 # 16MB line buffer for large accessibility trees
        )
        self.pid = self.proc.pid
        # Handshake consumes exactly 1 request ID
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
        # 1. Close stdin
        if self.proc.stdin:
            try:
                self.proc.stdin.close()
            except Exception:
                pass

        # 2. Terminate / kill process
        if self.proc.returncode is None:
            try:
                self.proc.kill()
            except ProcessLookupError:
                pass
            except Exception as e:
                print(f"Warning killing bridge process {old_pid}: {e}", file=sys.stderr)

        # 3. First bounded wait
        try:
            await asyncio.wait_for(self.proc.wait(), timeout=1.5)
        except asyncio.TimeoutError:
            # Escalation path
            print(f"Teardown: bridge process {old_pid} did not exit after 1.5s, escalating...", file=sys.stderr)
            try:
                subprocess.run(["taskkill", "/F", "/PID", str(old_pid)], capture_output=True)
            except Exception:
                pass
            try:
                await asyncio.wait_for(self.proc.wait(), timeout=1.5)
            except asyncio.TimeoutError:
                pass

        # 4. Confirmation guarantee: do NOT set self.proc = None if termination not confirmed
        if self.proc.returncode is None:
            raise BridgeTeardownFailed(f"Failed to confirm termination of bridge process PID {old_pid}")

        self.proc = None

    async def _send_raw(self, request: dict) -> int:
        if not self.proc or self.proc.returncode is not None:
            raise BridgeDiedError(f"Bridge process {self.pid} is not running")
        cur_id = self.req_id
        self.req_id += 1
        request["id"] = cur_id
        data = json.dumps(request) + "\n"
        try:
            self.proc.stdin.write(data.encode("utf-8"))
            await self.proc.stdin.drain()
        except (BrokenPipeError, ConnectionResetError, OSError) as e:
            raise BridgeDiedError(f"Failed to write to bridge stdin: {e}") from e
        return cur_id

    async def _recv_raw(self, req_id: int | None = None) -> dict:
        while True:
            try:
                line = await self.proc.stdout.readline()
            except (BrokenPipeError, ConnectionResetError, OSError) as e:
                raise BridgeDiedError(f"Failed to read from bridge stdout: {e}") from e
            if not line:
                raise BridgeDiedError(f"Bridge process {self.pid} exited with code {self.proc.returncode}")
            try:
                res = json.loads(line.decode("utf-8"))
                if req_id is None or res.get("id") == req_id or res.get("id") is None:
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
        if op not in READ_ONLY_OPS and op not in MUTATING_OPS and op != "close":
            return {
                "id": request.get("id"),
                "ok": False,
                "error": {"code": "UnknownOperation", "message": f"unknown op: '{op}'"}
            }
        is_mutating = op in MUTATING_OPS

        async with self._io_lock:
            if not self.proc or self.proc.returncode is not None:
                await self._spawn_bridge(reason=f"Recovering terminated bridge before op '{op}'")

            try:
                return await self._send_and_recv_raw(request, timeout=timeout)
            except (asyncio.TimeoutError, BridgeTransportError) as exc:
                is_timeout = isinstance(exc, asyncio.TimeoutError)
                failure_type = "timeout" if is_timeout else "bridge death"
                old_gen = self.bridge_generation

                # Teardown Generation N and spawn clean Generation N+1
                await self._teardown_current_bridge(reason=f"{failure_type} on op '{op}' (gen {old_gen})")
                await self._spawn_bridge(reason=f"Transport recovery after {failure_type} on op '{op}'")

                if is_mutating:
                    # NEVER retry mutating operations on timeout or bridge death!
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
                    # Read-only operation: Retry ONCE on clean Generation N+1
                    retry_req = dict(request)
                    retry_req.pop("_delay_ms", None)

                    try:
                        return await self._send_and_recv_raw(retry_req, timeout=timeout)
                    except (asyncio.TimeoutError, BridgeTransportError) as retry_exc:
                        # RETRY ALSO FAILED: Teardown Generation N+1 and spawn clean Generation N+2
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
            print("=== INBRISK PC SESSION CLOSED ===", flush=True)

# Global Host Session
SESSION = InbriskPCSession()

async def inbrisk_pc(
    op: str,
    window: dict | None = None,
    scope: str | None = None,
    include_tree: bool | None = None,
    selector: dict | None = None,
    all: bool | None = None,
    limit: int | None = None,
    element_id: int | None = None,
    action: str | None = None,
    target: dict | None = None,
    text: str | None = None,
    x: int | None = None,
    y: int | None = None,
    keys: list[str] | None = None,
    reason: str | None = None,
) -> dict:
    """
    Direct model capability to interact with the OS through the Inbrisk PC Session.
    
    Supported operations (op):
      - 'status': Retrieve overall Inbrisk runtime health and state.
      - 'windows': Enumerate active top-level windows.
      - 'observe': Inspect foreground window and UI hierarchy.
      - 'find': Search UI elements matching selector. Requires 'selector'. Optional 'all', 'limit'.
      - 'read': Read detailed properties of an element. Requires 'element_id'.
      - 'act': Perform an input action. Requires 'action' ('click', 'type', 'key', 'focus').
      - 'cancel': Cancel running actions/plans.
    """
    payload = {
        "op": op,
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

    print(f"\n[Agent -> PC]: {op} { {k: v for k, v in payload.items() if k != 'op'} }", flush=True)
    res = await SESSION.execute(payload)
    if res.get("ok"):
        res_summary = json.dumps(res.get("result", {}), indent=2)
        if len(res_summary) > 600:
            res_summary = res_summary[:600] + "... [truncated]"
        print(f"[PC -> Agent]: OK\n{res_summary}", flush=True)
    else:
        print(f"[PC -> Agent]: ERROR {res.get('error')}", flush=True)
    return res

async def main():
    await SESSION.open()
    
    # Official google-antigravity config:
    # enabled_tools=[] disables all builtin tools (run_command, view_file, etc.)
    # enable_subagents=False ensures no subagents
    # mcp_servers=[] ensures zero MCP tools
    config = LocalAgentConfig(
        system_instructions=(
            "You are an autonomous PC automation assistant. "
            "For all computer control and inspection, use `inbrisk_pc`. "
            "You do not have access to run_command, shell execution, or MCP tools. "
            "The host maintains a single persistent Inbrisk PC session for this entire task."
        ),
        capabilities=CapabilitiesConfig(
            enabled_tools=[],
            enable_subagents=False,
        ),
        mcp_servers=[],
        tools=[inbrisk_pc],
    )

    async with Agent(config) as agent:
        prompt = (
            "Use inbrisk_pc to observe the 'Chrome' window, determine what page is currently loaded, "
            "find a safe UI element (like a button or tab), click it, observe the resulting state, "
            "and provide a concise report of what changed."
        )
        print(f"\n--- USER PROMPT: {prompt} ---\n", flush=True)
        response = await agent.chat(prompt)

        print("\n--- FINAL AGENT SUMMARY ---", flush=True)
        async for token in response:
            sys.stdout.write(token)
            sys.stdout.flush()
        print("\n---------------------------", flush=True)

    await SESSION.close()

if __name__ == "__main__":
    if sys.platform == "win32":
        asyncio.set_event_loop_policy(asyncio.WindowsProactorEventLoopPolicy())
    asyncio.run(main())
