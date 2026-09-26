using System.Diagnostics;
using System.Text.Json;

namespace Inbrisk.Core;

/// <summary>
/// Per-call latency tracing. One PerfTrace wraps a logical operation (an MCP
/// tool call, a computer_run plan); Stage() records nested named spans with
/// both inclusive and exclusive time (children never double-count into the
/// parent's exclusive ms). On Dispose the trace is appended as one JSONL
/// record to %LOCALAPPDATA%\inbrisk\perf-trace.jsonl — analysis data only,
/// never sent over the MCP channel.
///
/// AsyncLocal flows into Task.Run continuations, so spans opened by helpers
/// (Executor, WaitService, backend finds) land in the caller's trace.
/// </summary>
public sealed class PerfTrace : IDisposable
{
    private static readonly AsyncLocal<PerfTrace?> _current = new();

    private sealed class Rec
    {
        public required string Name;
        public int Parent = -1;
        public long StartTicks;
        public long ElapsedTicks;
    }

    private readonly List<Rec> _spans = new();
    private readonly List<int> _stack = new();
    private readonly Dictionary<string, long> _counters = new();
    private readonly Dictionary<string, object?> _extra = new();
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private bool _emitted;

    public string Kind { get; }
    public string Id { get; set; }

    private PerfTrace(string kind, string id) { Kind = kind; Id = id; }

    /// <summary>The ambient trace's id (runId / tool-call id) — lets the UIA
    /// layer correlate its find events with the enclosing operation.</summary>
    public static string? CurrentId => _current.Value?.Id;
    public static PerfTrace? TryCurrent => _current.Value;

    /// <summary>Open a trace. Nested opens replace the ambient trace —
    /// callers own disposal.</summary>
    public static PerfTrace Begin(string kind, string id)
    {
        var t = new PerfTrace(kind, id);
        _current.Value = t;
        return t;
    }

    /// <summary>Record a named stage. No-op outside a trace.</summary>
    public static IDisposable Stage(string name)
    {
        var t = _current.Value;
        if (t == null) return NullScope.Instance;
        var rec = new Rec
        {
            Name = name,
            Parent = t._stack.Count > 0 ? t._stack[^1] : -1,
            StartTicks = Stopwatch.GetTimestamp(),
        };
        var idx = t._spans.Count;
        t._spans.Add(rec);
        t._stack.Add(idx);
        return new SpanScope(t, idx);
    }

    /// <summary>Add to a named counter (e.g. poll ticks, event wakeups).</summary>
    public static void Count(string name, long n = 1)
    {
        var t = _current.Value;
        if (t == null) return;
        t._counters.TryGetValue(name, out var cur);
        t._counters[name] = cur + n;
    }

    /// <summary>Attach a scalar field to the emitted record.</summary>
    public void Set(string key, object? value) => _extra[key] = value;

    private sealed class SpanScope(PerfTrace owner, int idx) : IDisposable
    {
        public void Dispose()
        {
            owner._spans[idx].ElapsedTicks =
                Stopwatch.GetTimestamp() - owner._spans[idx].StartTicks;
            if (owner._stack.Count > 0 && owner._stack[^1] == idx)
                owner._stack.RemoveAt(owner._stack.Count - 1);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    public void Dispose() => Emit();

    private void Emit()
    {
        if (_emitted) return;
        _emitted = true;
        _current.Value = null;

        // aggregate per stage name; exclusive time subtracts direct children
        var excl = new long[_spans.Count];
        for (var i = 0; i < _spans.Count; i++)
        {
            long childTicks = 0;
            for (var j = 0; j < _spans.Count; j++)
                if (_spans[j].Parent == i) childTicks += _spans[j].ElapsedTicks;
            excl[i] = _spans[i].ElapsedTicks - childTicks;
        }
        var agg = new Dictionary<string, (int Count, double Total, double Excl, double Max)>();
        for (var i = 0; i < _spans.Count; i++)
        {
            var r = _spans[i];
            var ms = r.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
            var ems = Math.Max(0, excl[i] * 1000.0 / Stopwatch.Frequency);
            if (!agg.TryGetValue(r.Name, out var a)) a = (0, 0, 0, 0);
            agg[r.Name] = (a.Count + 1, a.Total + ms, a.Excl + ems,
                Math.Max(a.Max, ms));
        }
        PerfLog.Write(new
        {
            kind = Kind,
            id = Id,
            at = DateTimeOffset.Now,
            totalMs = _total.ElapsedMilliseconds,
            stages = agg.Select(kv => new
            {
                name = kv.Key, count = kv.Value.Count,
                totalMs = Math.Round(kv.Value.Total, 2),
                exclMs = Math.Round(kv.Value.Excl, 2),
                maxMs = Math.Round(kv.Value.Max, 2),
            }).OrderByDescending(s => s.totalMs),
            counters = _counters.Count > 0 ? _counters : null,
            extra = _extra.Count > 0 ? _extra : null,
        });
    }
}

/// <summary>Shared JSONL sink for performance events — one file the bench
/// harness tails. Appends are locked + non-fatal.</summary>
public static class PerfLog
{
    private static readonly object Gate = new();
    public static string FilePath =>
        Path.Combine(UserSettings.DataDir, "perf-trace.jsonl");

    public static void Write(object payload)
    {
        try
        {
            var line = JsonSerializer.Serialize(payload);
            lock (Gate)
            {
                Directory.CreateDirectory(UserSettings.DataDir);
                File.AppendAllText(FilePath, line + "\n");
            }
        }
        catch { /* telemetry must never break the measured operation */ }
    }
}
