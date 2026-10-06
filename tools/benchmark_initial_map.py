import json
import subprocess
import threading
import queue
import time
import os
import numpy as np

def run_initial_map_benchmark():
    dll_path = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Release\net8.0-windows10.0.19041.0\inbrisk-mcp.dll"
    if not os.path.exists(dll_path):
        dll_path = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.dll"

    print("=======================================================")
    print("Initial Map Cost & Budget Benchmark")
    print(f"Target: {dll_path}")
    print("=======================================================")

    proc = subprocess.Popen(
        ["dotnet", dll_path],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL, text=True, bufsize=1
    )

    q = queue.Queue()
    def reader():
        for line in proc.stdout:
            try: q.put(json.loads(line))
            except Exception: pass
    t = threading.Thread(target=reader, daemon=True)
    t.start()

    _id = 0
    def send(m, p=None):
        nonlocal _id
        _id += 1
        msg = {"jsonrpc": "2.0", "id": _id, "method": m}
        if p is not None: msg["params"] = p
        proc.stdin.write(json.dumps(msg) + "\n")
        proc.stdin.flush()
        return msg["id"]

    def call(m, p=None, timeout=30):
        rid = send(m, p)
        end = time.time() + timeout
        while time.time() < end:
            try:
                msg = q.get(timeout=max(0.1, end - time.time()))
                if msg.get("id") == rid: return msg
            except queue.Empty: break
        return None

    def tool_call(name, args=None):
        t0 = time.perf_counter()
        res = call("tools/call", {"name": name, "arguments": args or {}})
        dur_ms = (time.perf_counter() - t0) * 1000.0
        content = res.get("result", {}).get("content", []) if res else []
        text = content[0].get("text", "") if content else ""
        return res, text, dur_ms

    call("initialize", {"protocolVersion": "2025-03-26", "capabilities": {},
                        "clientInfo": {"name": "initial-map-benchmark", "version": "1.0"}})
    proc.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
    proc.stdin.flush()

    latencies = []
    payload_bytes = []
    actionable_counts = []
    landmark_counts = []
    has_modal_counts = 0

    print("Benchmarking computer_launch (reused instance with initialMap)...")
    for i in range(10):
        res, text, dur = tool_call("computer_launch", {"app": "notepad"})
        latencies.append(dur)
        payload_bytes.append(len(text.encode("utf-8")))
        try:
            data = json.loads(text)
            init_map = data.get("initialMap", {})
            actionables = init_map.get("firstActionables", [])
            landmarks = init_map.get("landmarks", [])
            actionable_counts.append(len(actionables))
            landmark_counts.append(len(landmarks))
            if init_map.get("modal") is not None:
                has_modal_counts += 1
        except Exception:
            pass

    proc.terminate()

    report = {
        "samples": len(latencies),
        "latency_min_ms": round(float(np.min(latencies)), 2),
        "latency_p50_ms": round(float(np.percentile(latencies, 50)), 2),
        "latency_p90_ms": round(float(np.percentile(latencies, 90)), 2),
        "latency_max_ms": round(float(np.max(latencies)), 2),
        "payload_bytes_avg": round(float(np.mean(payload_bytes)), 1),
        "payload_bytes_max": int(np.max(payload_bytes)),
        "actionables_returned_avg": round(float(np.mean(actionable_counts)), 1),
        "actionables_cap": 12,
        "landmarks_returned_avg": round(float(np.mean(landmark_counts)), 1),
        "landmarks_cap": 6,
        "modal_detected_count": has_modal_counts
    }

    out_path = r"C:\Users\Ahmet\Documents\inbrisk\artifacts\initial_map_benchmark.json"
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    print("\nBENCHMARK RESULTS:")
    print(json.dumps(report, indent=2))

if __name__ == "__main__":
    run_initial_map_benchmark()
