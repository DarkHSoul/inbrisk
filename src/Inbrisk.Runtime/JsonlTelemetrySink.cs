using System.Text.Json;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Appends one JSON line per action to a log file — the audit trail.</summary>
public sealed class JsonlTelemetrySink : ITelemetrySink, IDisposable
{
    private StreamWriter _writer;
    private readonly string _path;
    private readonly object _gate = new();
    private long _bytesWritten;
    private const long MaxFileBytes = 5 * 1024 * 1024; // 5 MB
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonlTelemetrySink(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckRotation();
        _writer = OpenWriter();
    }

    private StreamWriter OpenWriter() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };

    private void CheckRotation()
    {
        try
        {
            if (File.Exists(_path))
            {
                var info = new FileInfo(_path);
                if (info.Length > MaxFileBytes)
                {
                    _writer?.Dispose();
                    var oldPath = _path + ".old";
                    try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
                    try { File.Move(_path, oldPath); } catch { }
                    _writer = OpenWriter();
                    _bytesWritten = 0;
                }
            }
        }
        catch { }
    }

    public void Emit(ActionTelemetry record)
    {
        lock (_gate)
        {
            var line = JsonSerializer.Serialize(record, Opts);
            _bytesWritten += line.Length + 1;
            if (_bytesWritten > 100_000)
            {
                _bytesWritten = 0;
                CheckRotation();
            }
            _writer.WriteLine(line);
        }
    }

    public void EmitPipeline(PipelineTelemetry record)
    {
        lock (_gate)
        {
            var line = JsonSerializer.Serialize(record, Opts);
            _bytesWritten += line.Length + 1;
            if (_bytesWritten > 100_000)
            {
                _bytesWritten = 0;
                CheckRotation();
            }
            _writer.WriteLine(line);
        }
    }

    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
