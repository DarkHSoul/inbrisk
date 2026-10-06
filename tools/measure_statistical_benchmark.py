import json
import subprocess
import threading
import queue
import time
import sys
import os
import numpy as np

def run_mcp_benchmarks(dll_path, label):
    print(f"\n=======================================================")
    print(f"Starting Benchmark: {label}")
    print(f"DLL: {dll_path}")
    print(f"=======================================================")
    
    proc = subprocess.Popen(
        ["dotnet", os.path.abspath(dll_path)],
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

    def call(m, p=None, timeout=60):
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
                        "clientInfo": {"name": "benchmark-runner", "version": "1.0"}})
    proc.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
    proc.stdin.flush()

    results = {}

    # 1. tools/list (Catalog size & Latency)
    print("Measuring tools/list...")
    tools_latencies = []
    t_list = None
    for _ in range(10):
        t0 = time.perf_counter()
        res = call("tools/list")
        tools_latencies.append((time.perf_counter() - t0) * 1000.0)
        if t_list is None: t_list = res

    tools = t_list.get("result", {}).get("tools", []) if t_list else []
    tools_raw = json.dumps(t_list.get("result", {}), ensure_ascii=False) if t_list else ""
    results["tools_catalog"] = {
        "tool_count": len(tools),
        "bytes": len(tools_raw.encode("utf-8")),
        "chars": len(tools_raw),
        "samples": len(tools_latencies),
        "min_ms": round(float(np.min(tools_latencies)), 2),
        "p50_ms": round(float(np.percentile(tools_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(tools_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(tools_latencies, 99)), 2),
    }

    # 2. computer_capabilities
    print("Measuring computer_capabilities...")
    cap_latencies = []
    cap_text = ""
    for _ in range(10):
        _, text, dur = tool_call("computer_capabilities")
        cap_latencies.append(dur)
        if not cap_text: cap_text = text

    results["capabilities"] = {
        "bytes": len(cap_text.encode("utf-8")),
        "chars": len(cap_text),
        "samples": len(cap_latencies),
        "min_ms": round(float(np.min(cap_latencies)), 2),
        "p50_ms": round(float(np.percentile(cap_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(cap_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(cap_latencies, 99)), 2),
    }

    # 3. computer_apps (Cold vs Warm)
    print("Measuring computer_apps...")
    _, cold_text, cold_ms = tool_call("computer_apps", {"limit": 20})
    apps_warm_latencies = []
    for _ in range(10):
        _, _, dur = tool_call("computer_apps", {"limit": 20})
        apps_warm_latencies.append(dur)

    results["apps_cold"] = {
        "samples": 1,
        "duration_ms": round(cold_ms, 2),
        "chars": len(cold_text)
    }
    results["apps_warm"] = {
        "samples": len(apps_warm_latencies),
        "min_ms": round(float(np.min(apps_warm_latencies)), 2),
        "p50_ms": round(float(np.percentile(apps_warm_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(apps_warm_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(apps_warm_latencies, 99)), 2),
    }

    # 4. computer_find (Positive Hit: role=button)
    print("Measuring computer_find (role=button)...")
    _, find_btn_cold_text, find_btn_cold_ms = tool_call("computer_find", {"role": "button"})
    find_btn_warm_latencies = []
    for _ in range(10):
        _, _, dur = tool_call("computer_find", {"role": "button"})
        find_btn_warm_latencies.append(dur)

    results["find_button_cold"] = {
        "samples": 1,
        "duration_ms": round(find_btn_cold_ms, 2),
        "chars": len(find_btn_cold_text)
    }
    results["find_button_warm"] = {
        "samples": len(find_btn_warm_latencies),
        "min_ms": round(float(np.min(find_btn_warm_latencies)), 2),
        "p50_ms": round(float(np.percentile(find_btn_warm_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(find_btn_warm_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(find_btn_warm_latencies, 99)), 2),
    }

    # 5. computer_find (Negative Miss: NonExistentDummyTarget)
    print("Measuring computer_find (negative miss)...")
    _, find_cold_text, find_cold_ms = tool_call("computer_find", {"name": "NonExistentDummyTarget"})
    find_warm_latencies = []
    for _ in range(10):
        _, _, dur = tool_call("computer_find", {"name": "NonExistentDummyTarget"})
        find_warm_latencies.append(dur)

    results["find_miss_cold"] = {
        "samples": 1,
        "duration_ms": round(find_cold_ms, 2),
        "chars": len(find_cold_text)
    }
    results["find_miss_warm"] = {
        "samples": len(find_warm_latencies),
        "min_ms": round(float(np.min(find_warm_latencies)), 2),
        "p50_ms": round(float(np.percentile(find_warm_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(find_warm_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(find_warm_latencies, 99)), 2),
    }

    # 6. computer_windows
    print("Measuring computer_windows...")
    win_latencies = []
    win_text = ""
    for _ in range(10):
        _, text, dur = tool_call("computer_windows")
        win_latencies.append(dur)
        if not win_text: win_text = text

    results["windows"] = {
        "chars": len(win_text),
        "samples": len(win_latencies),
        "min_ms": round(float(np.min(win_latencies)), 2),
        "p50_ms": round(float(np.percentile(win_latencies, 50)), 2),
        "p90_ms": round(float(np.percentile(win_latencies, 90)), 2),
        "p99_ms": round(float(np.percentile(win_latencies, 99)), 2),
    }

    # 7. computer_batch (if available in tool catalog)
    tool_names = [t.get("name") for t in tools]
    if "computer_batch" in tool_names:
        print("Measuring computer_batch (3-action declarative plan)...")
        batch_latencies = []
        batch_text = ""
        batch_payload = {"steps": [{"do": "wait", "ms": 1}, {"do": "wait", "ms": 1}, {"do": "wait", "ms": 1}]}
        for _ in range(10):
            _, text, dur = tool_call("computer_batch", batch_payload)
            batch_latencies.append(dur)
            if not batch_text: batch_text = text
        results["batch_execution"] = {
            "chars": len(batch_text),
            "samples": len(batch_latencies),
            "min_ms": round(float(np.min(batch_latencies)), 2),
            "p50_ms": round(float(np.percentile(batch_latencies, 50)), 2),
            "p90_ms": round(float(np.percentile(batch_latencies, 90)), 2),
            "p99_ms": round(float(np.percentile(batch_latencies, 99)), 2),
        }

    proc.terminate()
    try: proc.wait(2)
    except: proc.kill()
    
    return results

if __name__ == "__main__":
    base_dll = r"C:\Users\Ahmet\Documents\inbrisk-baseline\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.dll"
    curr_dll = r"C:\Users\Ahmet\Documents\inbrisk\src\Inbrisk.Mcp\bin\Debug\net8.0-windows10.0.19041.0\inbrisk-mcp.dll"

    base_results = run_mcp_benchmarks(base_dll, "PRE-COMMIT LIVE MEASUREMENT (Baseline 4271b3f)")
    curr_results = run_mcp_benchmarks(curr_dll, "CURRENT MODIFIED LIVE MEASUREMENT")

    combined = {
        "baseline": base_results,
        "current": curr_results
    }

    with open(r"C:\Users\Ahmet\Documents\inbrisk\benchmark_comparison.json", "w", encoding="utf-8") as f:
        json.dump(combined, f, indent=2)

    print("\nBenchmark successfully finished and saved to benchmark_comparison.json!")
