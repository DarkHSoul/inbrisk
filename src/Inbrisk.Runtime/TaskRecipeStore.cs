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
        // Reject names with no filename-usable character (empty, whitespace-only,
        // or every character mapped to the '_' placeholder). Saving one would
        // produce junk like ".json" or "___.json", and distinct placeholder-only
        // names ("//", "==", "***") would silently collide into one file.
        if (SanitizeName(recipe.Name) is null)
            throw new ArgumentException(
                $"Recipe name '{recipe.Name}' has no filename-usable characters.", nameof(recipe));

        var safeName = Sanitize(recipe.Name);
        var path = Path.Combine(_dir, $"{safeName}.json");
        var json = JsonSerializer.Serialize(recipe, JsonOpts);

        // Atomic-ish write: File.WriteAllText truncates in place, so a crash
        // or a concurrent List()/Get() could observe a half-written file.
        // Write a sibling temp file, then rename it over the target.
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, json);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    public TaskRecipeDefinition? Get(string name)
    {
        if (SanitizeName(name) is null) return null;
        var safeName = Sanitize(name);
        var path = Path.Combine(_dir, $"{safeName}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TaskRecipeDefinition>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            // A corrupted or unreadable recipe file must not vanish silently:
            // without this trace, Get() and List() would both hide it with no
            // way to diagnose why a saved recipe stopped being loadable.
            System.Diagnostics.Debug.WriteLine(
                $"TaskRecipeStore.Get('{name}') failed to read '{path}': {ex.Message}");
            return null;
        }
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
            catch (Exception ex)
            {
                // Same rationale as the Get() catch: surface the poisoned file
                // in the debug log instead of quietly dropping it from listings.
                System.Diagnostics.Debug.WriteLine(
                    $"TaskRecipeStore.List skipped unreadable recipe file '{file}': {ex.Message}");
            }
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

    /// <summary>Sanitize a caller-supplied recipe name with the same rule
    /// Save/Get apply; null when nothing filename-usable remains (empty or
    /// every character mapped to a placeholder).</summary>
    public static string? SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var safe = Sanitize(name.Trim());
        return safe.Length == 0 || safe.All(c => c is '_' or '.') ? null : safe;
    }

    /// <summary>On-disk path a recipe of this name occupies (after
    /// sanitization) — whether or not the file exists yet.</summary>
    public string PathFor(string name) =>
        Path.Combine(_dir, $"{Sanitize(name.Trim())}.json");

    /// <summary>
    /// Normalize a caller-supplied recipe name to its on-disk filename basis.
    /// Trims surrounding whitespace first — so a name saved padded (" test ")
    /// and later looked up trimmed ("test") resolve to the same file; Save,
    /// Get, SanitizeName and PathFor all funnel through this one rule.
    /// Trailing lower-casing is deliberate: it makes lookups deterministic and
    /// collapses case-only variants ("MyRecipe"/"myrecipe") to one file,
    /// consistent with case-insensitive Windows filesystems (two such names
    /// would collide on disk regardless). Save() rejects distinct names that
    /// would still map to junk/colliding files (placeholder-only names).
    /// </summary>
    private static string Sanitize(string name)
    {
        var invalids = Path.GetInvalidFileNameChars();
        return string.Concat(name.Trim().Select(c => invalids.Contains(c) ? '_' : c)).ToLowerInvariant();
    }
}
