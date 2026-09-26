using System.Text.Json;

namespace Inbrisk.Platform.Windows.Uia;

/// <summary>
/// Internal UIA find-performance telemetry. Appends one JSONL event per
/// backend Find() to %LOCALAPPDATA%\inbrisk\find-perf.jsonl — debug data,
/// never serialized into MCP tool responses.
///
/// Counts live vs cached COM property reads via thread-static counters
/// started by a Find and incremented by PropRead()/CachedRead() at each
/// cross-process property access site.
/// </summary>
public static class UiaPerf
{
    private static readonly object Gate = new();
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "inbrisk");
    private static readonly string File = System.IO.Path.Combine(Dir, "find-perf.jsonl");

    [ThreadStatic] private static int _liveReads;
    [ThreadStatic] private static int _cachedReads;

    /// <summary>One live (cross-process) UIA property/pattern read.</summary>
    public static void LiveRead() => _liveReads++;

    /// <summary>One locally-cached property/pattern read (no COM round trip).</summary>
    public static void CachedRead() => _cachedReads++;

    /// <summary>Snapshot and reset the per-thread read counters.</summary>
    public static (int Live, int Cached) TakeReads()
    {
        var t = (_liveReads, _cachedReads);
        _liveReads = 0; _cachedReads = 0;
        return t;
    }

    /// <summary>Append an event. Payload is any anonymous record.</summary>
    public static void Write(object payload)
    {
        try
        {
            var line = JsonSerializer.Serialize(payload);
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Dir);
                System.IO.File.AppendAllText(File, line + "\n");
            }
        }
        catch { /* telemetry must never break a find */ }
    }
}
