using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Persists element re-resolution recipes to a per-process file so an
/// element id minted by one process (e.g. `inbrisk find`) can still be
/// resolved by a later one-shot process (e.g. `inbrisk invoke uia_…`).
/// Element ids embed the minting pid, so a lookup reads exactly one file —
/// no cross-process locking. Purely advisory: a corrupt, missing or expired
/// entry simply behaves like an unknown id.
/// </summary>
public sealed class ElementRecipeStore
{
    private readonly string _dir;
    private readonly string _ownPath;
    private readonly ConcurrentDictionary<string, ElementHandle> _items = new();
    /// <summary>In-memory cache of handles loaded from OTHER processes'
    /// files — kept out of _items so our own file stays ours.</summary>
    private readonly ConcurrentDictionary<string, ElementHandle> _looked = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);
    private const int MaxItems = 500;
    private const long MinFlushMs = 300;
    private long _lastFlush;
    private bool _dirty;

    private long _lastPruneMs;
    private const long PruneIntervalMs = 60_000;

    public ElementRecipeStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "inbrisk"),
            "element-recipes");
        _ownPath = Path.Combine(_dir, $"elements-{Environment.ProcessId}.json");

        PruneOldFiles();
    }

    public void PruneNow()
    {
        DoPrune();
    }

    public void PruneOldFiles()
    {
        var now = Environment.TickCount64;
        if (now - _lastPruneMs < PruneIntervalMs) return;
        _lastPruneMs = now;

        ThreadPool.QueueUserWorkItem(_ => DoPrune());
    }

    private void DoPrune()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var dirInfo = new DirectoryInfo(_dir);
            var files = dirInfo.GetFiles("elements-*.json");
            if (files.Length == 0) return;

            var livePids = new HashSet<int>(System.Diagnostics.Process.GetProcesses().Select(p => p.Id));
            var cutoff = DateTime.UtcNow - Ttl; // 30 minutes

            foreach (var f in files)
            {
                try
                {
                    if (f.FullName.Equals(_ownPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var m = Regex.Match(f.Name, @"^elements-(\d+)\.json$");
                    int pid = 0;
                    bool hasPid = m.Success && int.TryParse(m.Groups[1].Value, out pid);
                    bool isProcessAlive = hasPid && livePids.Contains(pid);

                    // Running processes must never have their recipe files purged
                    if (isProcessAlive)
                        continue;

                    // Dead process files remain accessible for subsequent CLI commands until TTL expires
                    if (f.LastWriteTimeUtc < cutoff)
                    {
                        f.Delete();
                    }
                }
                catch { }
            }

            // If directory still has excessive files (> 200), prune oldest dead files
            var remaining = new DirectoryInfo(_dir).GetFiles("elements-*.json");
            if (remaining.Length > 200)
            {
                foreach (var old in remaining.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (old.FullName.Equals(_ownPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var m = Regex.Match(old.Name, @"^elements-(\d+)\.json$");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var pid) && livePids.Contains(pid))
                        continue;

                    try { old.Delete(); } catch { }
                    if (new DirectoryInfo(_dir).GetFiles("elements-*.json").Length <= 200)
                        break;
                }
            }
        }
        catch { }
    }

    private sealed record PersistedElement(
        BackendId Backend, ReResolveRecipe Recipe, DateTimeOffset At);

    /// <summary>uia_{pid}_{n} → the minting pid, or null for other id shapes.</summary>
    public static int? OwnerPid(string id)
    {
        var m = Regex.Match(id, @"^uia_(\d+)_\d+$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var pid) ? pid : null;
    }

    public void Put(UiElement e)
    {
        _items[e.Id] = new ElementHandle(e.Handle.Backend,
            e.Handle.BackendRef, e.Handle.Recipe);
        _dirty = true;
        FlushThrottled();
    }

    /// <summary>Write-through is throttled — Register fires per find/inspect
    /// and the file is small; 300ms between flushes keeps it cheap.</summary>
    private void FlushThrottled()
    {
        var now = Environment.TickCount64;
        if (now - _lastFlush < MinFlushMs) return;
        _lastFlush = now;
        Flush();
    }

    public void Flush()
    {
        if (!_dirty) return;
        _dirty = false;
        try
        {
            Directory.CreateDirectory(_dir);
            var cutoff = DateTimeOffset.UtcNow - Ttl;
            var snapshot = _items.ToArray();
            var dict = new Dictionary<string, object?>();
            // we only know our own write times — re-read to preserve them
            var prev = ReadFile(_ownPath);
            var merged = new Dictionary<string, PersistedElement>();
            if (prev != null)
                foreach (var kv in prev)
                    if (kv.Value.At > cutoff) merged[kv.Key] = kv.Value;
            foreach (var kv in snapshot)
                merged[kv.Key] = new PersistedElement(kv.Value.Backend,
                    kv.Value.Recipe, DateTimeOffset.UtcNow);
            if (merged.Count > MaxItems)
                foreach (var old in merged.OrderBy(kv => kv.Value.At)
                             .Take(merged.Count - MaxItems).Select(kv => kv.Key).ToList())
                    merged.Remove(old);
            var tmp = _ownPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(merged));
            File.Move(tmp, _ownPath, overwrite: true);
        }
        catch { /* advisory cache — never break the caller */ }
    }

    /// <summary>Look up a persisted re-resolution handle for an id unknown
    /// to this process's registry. Checks our own cache first, then the
    /// file belonging to the minting pid (ids carry it).</summary>
    public ElementHandle? Lookup(string id)
    {
        if (_items.TryGetValue(id, out var own)) return own;
        if (_looked.TryGetValue(id, out var cached)) return cached;
        var pid = OwnerPid(id);
        if (pid == null) return null;
        var file = pid == Environment.ProcessId ? _ownPath
            : Path.Combine(_dir, $"elements-{pid}.json");
        var map = ReadFile(file);
        if (map == null || !map.TryGetValue(id, out var p)) return null;
        if (p.At < DateTimeOffset.UtcNow - Ttl) return null;
        var handle = new ElementHandle(p.Backend, "", p.Recipe);
        _looked[id] = handle;
        return handle;
    }

    private static Dictionary<string, PersistedElement>? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Dictionary<string, PersistedElement>>(
                File.ReadAllText(path));
        }
        catch { return null; }
    }
}
