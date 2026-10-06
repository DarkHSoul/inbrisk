"""Probe: capture UIA COM-call counts from find-perf.jsonl for a few tool
calls. The find-perf writer flushes asynchronously — this waits for the
log to settle so counters actually land. Merges a "uiaProbe" section into
benchmarks/baseline_2026-10-06.json."""
import json, os, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bench_csharp_baseline import Server, FIND, _offset, _read_from, uia_stats, REPO

OUT = os.path.join(REPO, "benchmarks", "baseline_2026-10-06.json")

def settle_read(off, quiet_ms=1500, timeout=8000):
    """Read appended records; keep waiting while new ones keep arriving."""
    t0 = time.time(); last_n = -1; last_t = time.time()
    while time.time() - t0 < timeout / 1000:
        evs = _read_from(FIND, off)
        if len(evs) != last_n:
            last_n = len(evs); last_t = time.time()
        elif (time.time() - last_t) * 1000 > quiet_ms:
            return evs
        time.sleep(0.2)
    return _read_from(FIND, off)

def main():
    sv = Server()
    try:
        t0 = time.time()
        while time.time() - t0 < 10:
            _, _, txt, *_ = sv.tool("computer_app_status")
            if '"running"' in txt: break
            time.sleep(0.5)
        probes = {}
        for label, name, args, n in [
            ("observe.desktop.x3", "computer_observe", {"mode": "semantic"}, 3),
            ("find.button.desktop.x3", "computer_find", {"role": "button", "limit": 5}, 3),
            ("windows.x3", "computer_windows", {}, 3),
        ]:
            off = _offset(FIND)
            for i in range(n):
                a = dict(args)
                a["maxElements"] = 60 + i  # defeat dedup
                sv.tool(name, a)
            evs = settle_read(off)
            probes[label] = {"calls": n, "uia": uia_stats(evs),
                             "events": [ {k: ev.get(k) for k in ("kind","method","roots","comCalls","cachedReads","crossProcessPropertyReads","elements","candidatesEnumerated","totalMs")} for ev in evs ]}
        # also grab raw kinds histogram
        r = json.load(open(OUT))
        r["uiaProbe"] = {
            "note": "comCalls/cachedReads from find-perf.jsonl (shared log; "
                    "offset-sliced, settled). Per-call counts are per uia.find/"
                    "uia.inspect event.",
            "probes": probes}
        json.dump(r, open(OUT, "w"), indent=1)
        print(json.dumps(probes, indent=1)[:3000])
    finally:
        sv.close()

if __name__ == "__main__":
    main()
