using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inbrisk.Setup;

/// <summary>
/// Safe mutation of another app's MCP config file. Never overwrites:
/// parse → modify only the Inbrisk entry → validate → timestamped backup →
/// atomic same-volume replace → re-parse verification. A malformed existing
/// config is refused, not "fixed".
/// </summary>
public sealed class JsonConfigFile
{
    private static readonly JsonNodeOptions NodeOpts = new();
    private static readonly JsonDocumentOptions ParseOpts = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
    };

    public string Path { get; }
    public string? LastBackup { get; private set; }
    private readonly string _backupDir;

    public JsonConfigFile(string path, string? backupDir = null)
    {
        Path = path;
        _backupDir = backupDir ?? InstallLayout.BackupDir;
    }

    public bool Exists => File.Exists(Path);

    /// <summary>Parse the file. Returns null if missing. Throws
    /// ConfigMalformedException if it exists but isn't valid JSON.</summary>
    public JsonObject Read()
    {
        if (!File.Exists(Path)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(Path),
                nodeOptions: NodeOpts, documentOptions: ParseOpts);
            if (node is not JsonObject obj)
                throw new ConfigMalformedException(Path,
                    "top-level value is not a JSON object");
            return obj;
        }
        catch (JsonException e)
        {
            throw new ConfigMalformedException(Path, e.Message);
        }
    }

    /// <summary>The Inbrisk server entry under the given root key, if any.</summary>
    public JsonObject? GetEntry(string rootKey, string name = "inbrisk")
    {
        var root = Read();
        return root[rootKey]?[name] as JsonObject;
    }

    /// <summary>Entry's command+args normalized for comparison.</summary>
    public static (string? Command, string Args) Describe(JsonObject? entry)
    {
        if (entry == null) return (null, "");
        var args = entry["args"] is JsonArray a
            ? string.Join(" ", a.Select(x => x?.GetValue<string>()))
            : "";
        return (entry["command"]?.GetValue<string>(), args);
    }

    public sealed record WriteResult(bool Changed, string? BackupPath);

    /// <summary>Insert or replace the Inbrisk entry. Idempotent: if the
    /// existing entry already matches, nothing is written.</summary>
    public WriteResult UpsertEntry(string rootKey, JsonObject entry,
        string name = "inbrisk")
    {
        var root = Read(); // throws on malformed — by design
        var servers = root[rootKey] as JsonObject;
        var existing = servers?[name] as JsonObject;
        if (existing != null && JsonNode.DeepEquals(existing, entry))
            return new WriteResult(false, null);

        Write(root, servers =>
        {
            servers[name] = entry;
        }, rootKey);
        return new WriteResult(true, LastBackup);
    }

    /// <summary>Remove only the Inbrisk entry. Other servers untouched.</summary>
    public WriteResult RemoveEntry(string rootKey, string name = "inbrisk")
    {
        if (!File.Exists(Path)) return new WriteResult(false, null);
        var root = Read();
        if (root[rootKey] is not JsonObject servers || !servers.ContainsKey(name))
            return new WriteResult(false, null);
        Write(root, s => s.Remove(name), rootKey);
        return new WriteResult(true, LastBackup);
    }

    private void Write(JsonObject root, Action<JsonObject> mutate, string rootKey)
    {
        var servers = root[rootKey] as JsonObject;
        if (servers == null)
        {
            servers = new JsonObject();
            root[rootKey] = servers;
        }
        mutate(servers);

        var content = root.ToJsonString(WriteOpts) + "\n";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        // backup before mutation (only if the file already exists and changed)
        if (File.Exists(Path))
            LastBackup = Backup();

        // atomic same-volume replace: temp → validate temp → Move(overwrite)
        var tmp = Path + ".inbrisk-tmp";
        File.WriteAllText(tmp, content);
        try
        {
            using var _ = JsonDocument.Parse(File.ReadAllText(tmp), ParseOpts);
            File.Move(tmp, Path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>Timestamped backup under %LOCALAPPDATA%\inbrisk\host-backups,
    /// retaining only the newest 10 per config file name.</summary>
    private string Backup()
    {
        var dir = System.IO.Path.Combine(_backupDir,
            Sanitize(System.IO.Path.GetFileName(Path)));
        Directory.CreateDirectory(dir);
        var dest = System.IO.Path.Combine(dir,
            $"{System.IO.Path.GetFileNameWithoutExtension(Path)}-" +
            $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.bak");
        File.Copy(Path, dest, overwrite: true);
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.bak")
            .OrderByDescending(f => f.LastWriteTimeUtc).Skip(10))
            try { old.Delete(); } catch { }
        return dest;
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}

public sealed class ConfigMalformedException : Exception
{
    public string ConfigPath { get; }
    public ConfigMalformedException(string path, string detail)
        : base($"config file is malformed and will NOT be modified: {path} ({detail})")
        => ConfigPath = path;
}
