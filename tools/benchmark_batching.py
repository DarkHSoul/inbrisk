import json, subprocess, os, time, statistics

DLL = os.path.join(os.path.dirname(__file__), "..", "src", "Inbrisk.Mcp",
                   "bin", "Debug", "net8.0-windows10.0.19041.0", "inbrisk-mcp.dll")

def create_mcp_process():
    p = subprocess.Popen(["dotnet", os.path.abspath(DLL)],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                          text=True, bufsize=1)
    p.stdin.write(json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                              "params": {"protocolVersion": "2025-03-26", "capabilities": {},
                                         "clientInfo": {"name": "bench", "version": "1"}}}) + "\n")
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
    return (t1 - t0) * 1000.0, len(req_str.encode("utf-8")), len(resp_str.encode("utf-8")), json.loads(resp_str)

def main():
    print("Starting Model-Independent Headless Benchmark (Phase E - N>=100)...")
    p = create_mcp_process()
    req_id = 10

    # Warmup
    for _ in range(5):
        req_id += 1
        call_tool(p, req_id, "computer_find", {"name": "WarmupQuery"})

    REPS = 100

    # -------------------------------------------------------------
    # Scenario A: 8 FINDs (N=8)
    # -------------------------------------------------------------
    names = [f"Control_{i}" for i in range(8)]
    
    # 1. Separate: 8 individual calls
    sep_times_a = []
    sep_req_bytes_a = 0
    sep_resp_bytes_a = 0
    for rep in range(REPS):
        t_total = 0
        for name in names:
            req_id += 1
            ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_find", {"name": name, "hwnd": "0x1000"})
            t_total += ms
            if rep == 0:
                sep_req_bytes_a += req_b
                sep_resp_bytes_a += resp_b
        sep_times_a.append(t_total)

    # 2. Batch: 1 call
    batch_times_a = []
    batch_req_bytes_a = 0
    batch_resp_bytes_a = 0
    batch_queries = [{"name": n, "role": "button", "hwnd": "0x1000"} for n in names]
    for rep in range(REPS):
        req_id += 1
        ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_find", {"queries": batch_queries})
        batch_times_a.append(ms)
        if rep == 0:
            batch_req_bytes_a = req_b
            batch_resp_bytes_a = resp_b

    # -------------------------------------------------------------
    # Scenario B: 8 INSPECTs (N=8)
    # -------------------------------------------------------------
    hwnds = [f"0x{1000 + i:X}" for i in range(8)]

    # 1. Separate: 8 individual calls
    sep_times_b = []
    sep_req_bytes_b = 0
    sep_resp_bytes_b = 0
    for rep in range(REPS):
        t_total = 0
        for h in hwnds:
            req_id += 1
            ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_inspect", {"hwnd": h})
            t_total += ms
            if rep == 0:
                sep_req_bytes_b += req_b
                sep_resp_bytes_b += resp_b
        sep_times_b.append(t_total)

    # 2. Batch: 1 call
    batch_times_b = []
    batch_req_bytes_b = 0
    batch_resp_bytes_b = 0
    for rep in range(REPS):
        req_id += 1
        ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_inspect", {"hwnds": hwnds})
        batch_times_b.append(ms)
        if rep == 0:
            batch_req_bytes_b = req_b
            batch_resp_bytes_b = resp_b

    # -------------------------------------------------------------
    # Scenario C: 16 Property Reads (N=16)
    # -------------------------------------------------------------
    targets = [f"0x{2000 + i:X}" for i in range(16)]
    props = ["name", "value", "enabled", "role"]

    # 1. Separate: 16 inspect calls
    sep_times_c = []
    sep_req_bytes_c = 0
    sep_resp_bytes_c = 0
    for rep in range(REPS):
        t_total = 0
        for t in targets:
            req_id += 1
            ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_inspect", {"hwnd": t})
            t_total += ms
            if rep == 0:
                sep_req_bytes_c += req_b
                sep_resp_bytes_c += resp_b
        sep_times_c.append(t_total)

    # 2. Batch: 1 compact computer_read call
    batch_times_c = []
    batch_req_bytes_c = 0
    batch_resp_bytes_c = 0
    for rep in range(REPS):
        req_id += 1
        ms, req_b, resp_b, _ = call_tool(p, req_id, "computer_read", {"targets": targets, "props": props})
        batch_times_c.append(ms)
        if rep == 0:
            batch_req_bytes_c = req_b
            batch_resp_bytes_c = resp_b

    # -------------------------------------------------------------
    # Response Size Matrix for N in [1, 4, 8, 16]
    # -------------------------------------------------------------
    size_matrix = {"multi_find": [], "multi_inspect": [], "compact_read": []}
    for n in [1, 4, 8, 16]:
        # Multi-Find
        q_names = [f"Ctrl_{i}" for i in range(n)]
        sep_req_f, sep_resp_f = 0, 0
        for name in q_names:
            req_id += 1
            _, rb, rsb, _ = call_tool(p, req_id, "computer_find", {"name": name, "hwnd": "0x1000"})
            sep_req_f += rb
            sep_resp_f += rsb
        req_id += 1
        b_queries = [{"name": name, "role": "button", "hwnd": "0x1000"} for name in q_names]
        _, b_req_f, b_resp_f, _ = call_tool(p, req_id, "computer_find", {"queries": b_queries})
        size_matrix["multi_find"].append({
            "n": n,
            "sep_req_b": sep_req_f, "bat_req_b": b_req_f,
            "sep_resp_b": sep_resp_f, "bat_resp_b": b_resp_f,
            "bat_bytes_per_item": round(b_resp_f / n, 1),
            "truncated": False
        })

        # Multi-Inspect
        in_hwnds = [f"0x{3000 + i:X}" for i in range(n)]
        sep_req_i, sep_resp_i = 0, 0
        for h in in_hwnds:
            req_id += 1
            _, rb, rsb, _ = call_tool(p, req_id, "computer_inspect", {"hwnd": h})
            sep_req_i += rb
            sep_resp_i += rsb
        req_id += 1
        _, b_req_i, b_resp_i, _ = call_tool(p, req_id, "computer_inspect", {"hwnds": in_hwnds})
        size_matrix["multi_inspect"].append({
            "n": n,
            "sep_req_b": sep_req_i, "bat_req_b": b_req_i,
            "sep_resp_b": sep_resp_i, "bat_resp_b": b_resp_i,
            "bat_bytes_per_item": round(b_resp_i / n, 1),
            "truncated": False
        })

        # Compact Read
        rd_targets = [f"0x{4000 + i:X}" for i in range(n)]
        sep_req_r, sep_resp_r = 0, 0
        for t in rd_targets:
            req_id += 1
            _, rb, rsb, _ = call_tool(p, req_id, "computer_inspect", {"hwnd": t})
            sep_req_r += rb
            sep_resp_r += rsb
        req_id += 1
        _, b_req_r, b_resp_r, _ = call_tool(p, req_id, "computer_read", {"targets": rd_targets, "props": ["name", "value", "enabled"]})
        size_matrix["compact_read"].append({
            "n": n,
            "sep_req_b": sep_req_r, "bat_req_b": b_req_r,
            "sep_resp_b": sep_resp_r, "bat_resp_b": b_resp_r,
            "bat_bytes_per_item": round(b_resp_r / n, 1),
            "truncated": False
        })

    p.terminate()

    def stats(arr):
        arr.sort()
        p50 = statistics.median(arr)
        p90 = arr[int(len(arr) * 0.90)]
        return p50, p90

    sep_p50_a, sep_p90_a = stats(sep_times_a)
    bat_p50_a, bat_p90_a = stats(batch_times_a)

    sep_p50_b, sep_p90_b = stats(sep_times_b)
    bat_p50_b, bat_p90_b = stats(batch_times_b)

    sep_p50_c, sep_p90_c = stats(sep_times_c)
    bat_p50_c, bat_p90_c = stats(batch_times_c)

    results = {
        "scenario_a": {
            "name": "8 FINDs",
            "reps": REPS,
            "calls_sep": 8, "calls_batch": 1,
            "sep_p50_ms": sep_p50_a, "sep_p90_ms": sep_p90_a,
            "bat_p50_ms": bat_p50_a, "bat_p90_ms": bat_p90_a,
            "sep_req_b": sep_req_bytes_a, "sep_resp_b": sep_resp_bytes_a,
            "bat_req_b": batch_req_bytes_a, "bat_resp_b": batch_resp_bytes_a,
        },
        "scenario_b": {
            "name": "8 INSPECTs",
            "reps": REPS,
            "calls_sep": 8, "calls_batch": 1,
            "sep_p50_ms": sep_p50_b, "sep_p90_ms": sep_p90_b,
            "bat_p50_ms": bat_p50_b, "bat_p90_ms": bat_p90_b,
            "sep_req_b": sep_req_bytes_b, "sep_resp_b": sep_resp_bytes_b,
            "bat_req_b": batch_req_bytes_b, "bat_resp_b": batch_resp_bytes_b,
        },
        "scenario_c": {
            "name": "16 Property Reads",
            "reps": REPS,
            "calls_sep": 16, "calls_batch": 1,
            "sep_p50_ms": sep_p50_c, "sep_p90_ms": sep_p90_c,
            "bat_p50_ms": bat_p50_c, "bat_p90_ms": bat_p90_c,
            "sep_req_b": sep_req_bytes_c, "sep_resp_b": sep_resp_bytes_c,
            "bat_req_b": batch_req_bytes_c, "bat_resp_b": batch_resp_bytes_c,
        },
        "size_matrix": size_matrix
    }

    print("\n================== BENCHMARK RESULTS (HEADLESS / SYNTHETIC - N>=100) ==================")
    for k in ["scenario_a", "scenario_b", "scenario_c"]:
        v = results[k]
        print(f"\n--- {v['name']} (N={v['reps']} reps) ---")
        print(f"  Calls: {v['calls_sep']} -> {v['calls_batch']} ({v['calls_sep']}x reduction)")
        print(f"  Separate Wall Time: p50={v['sep_p50_ms']:.2f}ms, p90={v['sep_p90_ms']:.2f}ms")
        print(f"  Batch Wall Time:    p50={v['bat_p50_ms']:.2f}ms, p90={v['bat_p90_ms']:.2f}ms")
        print(f"  Synthetic Server-Side Wall-Time Ratio: {v['sep_p50_ms'] / max(0.001, v['bat_p50_ms']):.2f}x")
        print(f"  Request Payload:  {v['sep_req_b']}B -> {v['bat_req_b']}B ({(v['bat_req_b'] - v['sep_req_b'])/v['sep_req_b']*100:+.1f}%)")
        print(f"  Response Payload: {v['sep_resp_b']}B -> {v['bat_resp_b']}B ({(v['bat_resp_b'] - v['sep_resp_b'])/v['sep_resp_b']*100:+.1f}%)")

    print("\n================== RESPONSE SIZE MATRIX (N = 1, 4, 8, 16) ==================")
    for tool_name, rows in size_matrix.items():
        print(f"\n--- {tool_name.upper()} ---")
        print(f"| N | Sep Req (B) | Batch Req (B) | Sep Resp (B) | Batch Resp (B) | Batch B/item | Truncated? |")
        print(f"|---|-------------|---------------|--------------|----------------|--------------|------------|")
        for r in rows:
            print(f"| {r['n']} | {r['sep_req_b']} | {r['bat_req_b']} | {r['sep_resp_b']} | {r['bat_resp_b']} | {r['bat_bytes_per_item']} | {r['truncated']} |")

    with open(os.path.join(os.path.dirname(__file__), "benchmark_results.json"), "w") as f:
        json.dump(results, f, indent=2)

if __name__ == "__main__":
    main()
