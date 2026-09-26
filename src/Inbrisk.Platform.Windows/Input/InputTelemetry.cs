using System.Text.Json;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>What a transaction is doing — drives watchdog budgets.</summary>
public enum InputTxnKind { Key, Hotkey, Type, Click, Drag, Scroll, Move, Hold, Sweep, Other }

/// <summary>One recorded input event. Written for every DOWN/UP attempt plus
/// anomalies (partial delivery, reconcile, watchdog kill, invariant recovery).</summary>
public sealed class InputEventRecord
{
    public long Seq { get; set; }
    public string TxnId { get; set; } = "";
    public InputTxnKind Kind { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>down | up | undelivered | partial | release | reconcile |
    /// reconcile-still-down | invariant | watchdog | skip-physical | blocked</summary>
    public string Phase { get; set; } = "";
    public ushort? Vk { get; set; }
    public ushort? Scan { get; set; }
    public uint? MouseFlag { get; set; }
    public uint Requested { get; set; }
    public uint Delivered { get; set; }
    public int LastError { get; set; }
    public bool Cancelled { get; set; }
    public bool Emergency { get; set; }
    public string? Note { get; set; }
}

/// <summary>
/// Central input telemetry: bounded in-memory ring (always on, cheap) plus an
/// opt-in JSONL file sink. Set INBRISK_INPUT_TRACE=1 to also append every event
/// to %LOCALAPPDATA%\inbrisk\input-events.jsonl — that's where a lockup's
/// unpaired DOWN becomes visible after the fact.
/// </summary>
public static class InputTelemetry
{
    private const int Capacity = 8192;
    private static readonly object _gate = new();
    private static readonly Queue<InputEventRecord> _ring = new();
    private static long _seq;
    private static StreamWriter? _file;
    private static int _fileInit;

    public static long TotalDown;
    public static long TotalUp;
    public static long PartialSends;
    public static long BlockedSends;
    public static long Reconciles;
    public static long InvariantRecoveries;
    public static long WatchdogKills;

    public static void Record(InputEventRecord r)
    {
        r.Seq = Interlocked.Increment(ref _seq);
        switch (r.Phase)
        {
            case "down": Interlocked.Increment(ref TotalDown); break;
            case "up": Interlocked.Increment(ref TotalUp); break;
            case "partial": Interlocked.Increment(ref PartialSends); break;
            case "blocked": Interlocked.Increment(ref BlockedSends); break;
            case "reconcile": Interlocked.Increment(ref Reconciles); break;
            case "invariant": Interlocked.Increment(ref InvariantRecoveries); break;
            case "watchdog": Interlocked.Increment(ref WatchdogKills); break;
        }
        lock (_gate)
        {
            _ring.Enqueue(r);
            while (_ring.Count > Capacity) _ring.Dequeue();
            try { EnsureFile()?.WriteLine(JsonSerializer.Serialize(r)); }
            catch { /* telemetry must never break input */ }
        }
    }

    /// <summary>Most recent N events (oldest→newest) for diagnostics.</summary>
    public static List<InputEventRecord> Recent(int n = 64)
    {
        lock (_gate) return _ring.Skip(Math.Max(0, _ring.Count - n)).ToList();
    }

    private static StreamWriter? EnsureFile()
    {
        if (_file != null) return _file;
        if (Interlocked.Exchange(ref _fileInit, 1) != 0) return null;
        var setting = Environment.GetEnvironmentVariable("INBRISK_INPUT_TRACE");
        if (string.IsNullOrEmpty(setting) || setting == "0") return null;
        try
        {
            var path = setting == "1"
                ? Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                    "inbrisk", "input-events.jsonl")
                : setting;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _file = new StreamWriter(path, append: true) { AutoFlush = true };
        }
        catch { /* diagnostics only */ }
        return _file;
    }
}
