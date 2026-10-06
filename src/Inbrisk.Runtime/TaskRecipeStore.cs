using System.Text.Json;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Persists semantic UI automation task recipes (macros with semantic preconditions,
/// parameterized inputs, and postcondition verification).
/// Stored workflows can be replayed deterministically without requiring redundant
/// LLM reasoning cycles unless a verification or UI layout change occurs.
/// </summary>
public sealed class TaskRecipeStore : ITaskRecipeStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public TaskRecipeStore(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetEnvironmentVariable("INBRISK_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "inbrisk"),
            "task-recipes");
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    public void Save(TaskRecipeDefinition recipe)
    {
        var safeName = Sanitize(recipe.Name);
        var path = Path.Combine(_dir, $"{safeName}.json");
        var json = JsonSerializer.Serialize(recipe, JsonOpts);
        File.WriteAllText(path, json);
    }

    public TaskRecipeDefinition? Get(string name)
    {
        var safeName = Sanitize(name);
        var path = Path.Combine(_dir, $"{safeName}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TaskRecipeDefinition>(json, JsonOpts);
        }
        catch { return null; }
    }

    public IReadOnlyList<TaskRecipeDefinition> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<TaskRecipeDefinition>();
        var list = new List<TaskRecipeDefinition>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                if (JsonSerializer.Deserialize<TaskRecipeDefinition>(json, JsonOpts) is { } r)
                    list.Add(r);
            }
            catch { }
        }
        return list;
    }

    public void RecordRun(string name, bool success)
    {
        var existing = Get(name);
        if (existing == null) return;
        var updated = existing with
        {
            LastRunAt = DateTimeOffset.UtcNow,
            SuccessCount = success ? existing.SuccessCount + 1 : existing.SuccessCount,
            FailureCount = success ? existing.FailureCount : existing.FailureCount + 1,
        };
        Save(updated);
    }

    private static string Sanitize(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalids.Contains(c) ? '_' : c)).ToLowerInvariant();
    }
}
