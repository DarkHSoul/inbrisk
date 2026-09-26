using System.Text.Json;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Appends one JSON line per action to a log file — the audit trail.</summary>
public sealed class JsonlTelemetrySink : ITelemetrySink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonlTelemetrySink(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(new FileStream(path,
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public void Emit(ActionTelemetry record)
    {
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(record, Opts));
    }

    public void EmitPipeline(PipelineTelemetry record)
    {
        lock (_gate) _writer.WriteLine(JsonSerializer.Serialize(record, Opts));
    }

    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
