using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Appends one JSON line per action to a log file — the audit trail.
/// Each line is hash-chained (F32: "prev" links to the previous record's
/// "h"), so truncation/rewrites are detectable via AuditLog.VerifyChain.
/// An optional mirrorPath receives a byte-identical copy (agent-readable
/// mirror under the data dir); the primary file is the source of truth.
/// </summary>
public sealed class JsonlTelemetrySink : ITelemetrySink, IDisposable
{
    private StreamWriter _writer;
    private readonly StreamWriter? _mirror;
    private readonly string _path;
    private readonly object _gate = new();
    private long _bytesWritten;
    private string _prevHash;
    private const long MaxFileBytes = 5 * 1024 * 1024; // 5 MB
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonlTelemetrySink(string path, string? mirrorPath = null)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckRotation();
        _writer = OpenWriter();
        _prevHash = AuditLog.LastChainHash(path);
        // rotated just now? keep the chain anchored on the .old tail hash
        if (_prevHash == AuditLog.GenesisHash && File.Exists(path + ".old"))
            _prevHash = AuditLog.LastChainHash(path + ".old");
        if (mirrorPath != null &&
            !mirrorPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath)!);
                _mirror = new StreamWriter(new FileStream(mirrorPath,
                    FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                { AutoFlush = true };
            }
            catch { /* mirror is a convenience copy — never required */ }
        }
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
                    // chain continues across rotation — the new file's first
                    // "prev" still anchors on the rotated tail hash
                }
            }
        }
        catch { }
    }

    private void WriteRecord<T>(T record)
    {
        lock (_gate)
        {
            var obj = JsonSerializer.SerializeToNode(record, Opts)!.AsObject();
            var line = AuditLog.ChainLine(obj, _prevHash);
            _prevHash = obj["h"]!.GetValue<string>();
            _bytesWritten += line.Length + 1;
            if (_bytesWritten > 100_000)
            {
                _bytesWritten = 0;
                CheckRotation();
            }
            _writer.WriteLine(line);
            try { _mirror?.WriteLine(line); } catch { /* best-effort mirror */ }
        }
    }

    public void Emit(ActionTelemetry record) => WriteRecord(record);

    public void EmitPipeline(PipelineTelemetry record) => WriteRecord(record);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
            try { _mirror?.Dispose(); } catch { }
        }
    }
}
