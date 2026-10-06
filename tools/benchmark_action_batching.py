import json
import os
import statistics
import subprocess
import sys
import time

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")

def create_mcp_process():
    p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                         text=True, bufsize=1)
    p.stdin.write(json.dumps({
        "jsonrpc": "2.0",
        "id": 1,
        "method": "initialize",
        "params": {
            "protocolVersion": "2025-03-26",
            "capabilities": {},
            "clientInfo": {"name": "action_bench", "version": "1"}
        }
    }) + "\n")
    p.stdin.flush()
    p.stdout.readline()
    p.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
    p.stdin.flush()
    return p

def call_tool(p, req_id, name, args):
    req = {
        "jsonrpc": "2.0",
        "id": req_id,
        "method": "tools/call",
        "params": {
            "name": name,
            "arguments": args
        }
    }
    req_str = json.dumps(req)
    t0 = time.perf_counter()
    p.stdin.write(req_str + "\n")
    p.stdin.flush()
    resp_str = p.stdout.readline()
    t1 = time.perf_counter()
    resp_obj = json.loads(resp_str)
    return (t1 - t0) * 1000.0, len(req_str.encode("utf-8")), len(resp_str.encode("utf-8")), resp_obj

def run_scenario_benchmark(p, name, reps, run_sep, run_batch):
    sep_times = []
    sep_req_b = 0
    sep_resp_b = 0
    sep_calls = 0

    batch_times = []
    batch_req_b = 0
    batch_resp_b = 0
    batch_calls = 1

    # Measure separate sequential scenario (complete scenario wall time)
    for rep in range(reps):
        t_scenario, req_b, resp_b, calls = run_sep(rep == 0)
        sep_times.append(t_scenario)
        if rep == 0:
            sep_req_b = req_b
            sep_resp_b = resp_b
            sep_calls = calls

    # Measure batch scenario (complete scenario wall time)
    for rep in range(reps):
        t_scenario, req_b, resp_b = run_batch(rep == 0)
        batch_times.append(t_scenario)
        if rep == 0:
            batch_req_b = req_b
            batch_resp_b = resp_b

    def get_stats(arr):
        arr.sort()
        return {
            "p50_ms": round(statistics.median(arr), 2),
            "p90_ms": round(arr[int(len(arr) * 0.90)], 2),
            "mean_ms": round(statistics.mean(arr), 2)
        }

    sep_stats = get_stats(sep_times)
    batch_stats = get_stats(batch_times)

    # Synthetic roundtrip latency breakdown (0, 10, 50, 100 ms/call)
    latencies = [0, 10, 50, 100]
    transport_model = {}
    for lat in latencies:
        sep_with_lat = round(sep_stats["p50_ms"] + (sep_calls * lat), 2)
        batch_with_lat = round(batch_stats["p50_ms"] + (batch_calls * lat), 2)
        speedup = round(sep_with_lat / max(0.001, batch_with_lat), 2)
        transport_model[f"{lat}ms_per_call"] = {
            "simulated_call_latency_ms": lat,
            "sep_total_wall_ms": sep_with_lat,
            "batch_total_wall_ms": batch_with_lat,
            "speedup_ratio": speedup
        }

    # Break-even latency calculation:
    # sep_p50 + sep_calls * L = batch_p50 + batch_calls * L
    # L * (sep_calls - batch_calls) = batch_p50 - sep_p50
    diff_calls = sep_calls - batch_calls
    diff_time = batch_stats["p50_ms"] - sep_stats["p50_ms"]
    breakeven_lat = round(diff_time / diff_calls, 2) if diff_calls > 0 and diff_time > 0 else 0.0

    return {
        "name": name,
        "reps": reps,
        "calls_separate": sep_calls,
        "calls_batch": batch_calls,
        "calls_saved": sep_calls - batch_calls,
        "executor_wall_time_ms": {
            "separate": sep_stats,
            "batch": batch_stats,
            "in_process_executor_overhead_ms": round(batch_stats["p50_ms"] - sep_stats["p50_ms"], 2)
        },
        "payload_bytes": {
            "request_bytes": {"separate": sep_req_b, "batch": batch_req_b, "delta_pct": round((batch_req_b - sep_req_b) / sep_req_b * 100, 1)},
            "response_bytes": {"separate": sep_resp_b, "batch": batch_resp_b, "delta_pct": round((batch_resp_b - sep_resp_b) / sep_resp_b * 100, 1)},
            "total_wire_bytes": {"separate": sep_req_b + sep_resp_b, "batch": batch_req_b + batch_resp_b, "delta_pct": round(((batch_req_b + batch_resp_b) - (sep_req_b + sep_resp_b)) / (sep_req_b + sep_resp_b) * 100, 1)}
        },
        "breakeven_transport_latency_ms": breakeven_lat,
        "transport_latency_sensitivity": transport_model
    }

def main():
    print("Starting Methodologically Rigorous Action-Side Benchmark (Phase E Round 2 - N=100 reps)...")
    p = create_mcp_process()
    req_counter = [100]

    def next_id():
        req_counter[0] += 1
        return req_counter[0]

    # Warmup
    for _ in range(5):
        call_tool(p, next_id(), "computer_click", {"elementId": "uia_warmup"})
        call_tool(p, next_id(), "computer_batch", {"steps": [{"do": "wait", "ms": 1}]})

    REPS = 100

    # -------------------------------------------------------------
    # Scenario A: 8 Clicks / Actions
    # -------------------------------------------------------------
    print("Benchmarking Scenario A: 8 Clicks (N=100)...")
    def run_sep_a(record_bytes):
        t_tot, req_b, resp_b = 0.0, 0, 0
        for i in range(8):
            ms, qb, sb, _ = call_tool(p, next_id(), "computer_click", {"elementId": f"uia_btn_{i}"})
            t_tot += ms
            if record_bytes:
                req_b += qb
                resp_b += sb
        return t_tot, req_b, resp_b, 8

    steps_a = [{"do": "click", "t": f"uia_btn_{i}"} for i in range(8)]
    def run_batch_a(record_bytes):
        ms, qb, sb, _ = call_tool(p, next_id(), "computer_batch", {"steps": steps_a, "detail": "slim"})
        return ms, qb, sb

    res_a = run_scenario_benchmark(p, "Scenario A: 8 Clicks", REPS, run_sep_a, run_batch_a)

    # -------------------------------------------------------------
    # Scenario B: 5 Form Field Fills
    # -------------------------------------------------------------
    print("Benchmarking Scenario B: 5 Form Field Fills (N=100)...")
    def run_sep_b(record_bytes):
        t_tot, req_b, resp_b = 0.0, 0, 0
        for i in range(5):
            ms, qb, sb, _ = call_tool(p, next_id(), "computer_set_value", {"elementId": f"uia_field_{i}", "value": f"Val_{i}"})
            t_tot += ms
            if record_bytes:
                req_b += qb
                resp_b += sb
        return t_tot, req_b, resp_b, 5

    set_b = [{"target": f"uia_field_{i}", "value": f"Val_{i}", "role": "Edit"} for i in range(5)]
    def run_batch_b(record_bytes):
        ms, qb, sb, _ = call_tool(p, next_id(), "computer_batch", {"set": set_b, "detail": "slim"})
        return ms, qb, sb

    res_b = run_scenario_benchmark(p, "Scenario B: 5 Form Field Fills", REPS, run_sep_b, run_batch_b)

    # -------------------------------------------------------------
    # Scenario C: Launch + 3 Actions
    # -------------------------------------------------------------
    print("Benchmarking Scenario C: Launch + 3 Actions (N=100)...")
    def run_sep_c(record_bytes):
        t_tot, req_b, resp_b = 0.0, 0, 0
        ms, qb, sb, _ = call_tool(p, next_id(), "computer_launch", {"executable": "mock_app.exe", "waitFor": "none", "timeoutMs": 1})
        t_tot += ms
        if record_bytes:
            req_b += qb
            resp_b += sb
        for i in range(3):
            ms, qb, sb, _ = call_tool(p, next_id(), "computer_set_value", {"elementId": f"uia_act_{i}", "value": f"Val_{i}"})
            t_tot += ms
            if record_bytes:
                req_b += qb
                resp_b += sb
        return t_tot, req_b, resp_b, 4

    steps_c = [
        {"do": "launch", "t": "mock_app.exe"},
        {"do": "set_value", "t": "uia_act_0", "v": "Val_0"},
        {"do": "set_value", "t": "uia_act_1", "v": "Val_1"},
        {"do": "set_value", "t": "uia_act_2", "v": "Val_2"}
    ]
    def run_batch_c(record_bytes):
        ms, qb, sb, _ = call_tool(p, next_id(), "computer_batch", {"steps": steps_c, "detail": "slim"})
        return ms, qb, sb

    res_c = run_scenario_benchmark(p, "Scenario C: Launch + 3 Actions", REPS, run_sep_c, run_batch_c)

    p.terminate()

    results = {
        "metadata": {
            "methodology": "Same-unit whole-scenario wall time comparison over N=100 reps",
            "environment": "Headless stdio MCP process, .NET 8, Windows 10/11",
            "in_process_characteristic": "Batch includes RunPlanCore plan baseline snapshotting overhead; separate sequential calls do not initialize plan state",
            "reps": REPS
        },
        "scenarios": {
            "scenario_a": res_a,
            "scenario_b": res_b,
            "scenario_c": res_c
        }
    }

    out_path = os.path.join(os.path.dirname(__file__), "action_benchmark_results.json")
    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(results, f, indent=2)
    print(f"\nSaved benchmark results to {out_path}")

    # Print summary table
    print("\n" + "="*80)
    print("ACTION BENCHMARK SUMMARY (N=100 REPS, SAME-UNIT WHOLE-SCENARIO)")
    print("="*80)
    for key, s in results["scenarios"].items():
        print(f"\n--- {s['name']} ---")
        print(f"  Calls: {s['calls_separate']} -> {s['calls_batch']} ({s['calls_saved']} roundtrips saved)")
        print(f"  Executor Processing (p50): Separate={s['executor_wall_time_ms']['separate']['p50_ms']}ms vs Batch={s['executor_wall_time_ms']['batch']['p50_ms']}ms (overhead: {s['executor_wall_time_ms']['in_process_executor_overhead_ms']:+.2f}ms)")
        print(f"  Break-even Latency:       {s['breakeven_transport_latency_ms']} ms/call")
        p_bytes = s['payload_bytes']
        print(f"  Request Payload:          {p_bytes['request_bytes']['separate']}B -> {p_bytes['request_bytes']['batch']}B ({p_bytes['request_bytes']['delta_pct']:+.1f}%)")
        print(f"  Response Payload:         {p_bytes['response_bytes']['separate']}B -> {p_bytes['response_bytes']['batch']}B ({p_bytes['response_bytes']['delta_pct']:+.1f}%)")
        print(f"  Total Wire Bytes:         {p_bytes['total_wire_bytes']['separate']}B -> {p_bytes['total_wire_bytes']['batch']}B ({p_bytes['total_wire_bytes']['delta_pct']:+.1f}%)")
        print("  Controlled Synthetic Transport Sensitivity:")
        for lat_k, lat_v in s['transport_latency_sensitivity'].items():
            print(f"    - {lat_k:16}: Sep={lat_v['sep_total_wall_ms']:8.2f}ms | Batch={lat_v['batch_total_wall_ms']:8.2f}ms | Speedup={lat_v['speedup_ratio']:.2f}x")

if __name__ == "__main__":
    main()
