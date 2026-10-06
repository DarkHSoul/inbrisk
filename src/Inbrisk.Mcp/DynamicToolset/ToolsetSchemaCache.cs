using System.Collections.Concurrent;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp.DynamicToolset;

/// <summary>
/// Cache key for toolset-aware schema caching.
/// Composed of base profile, canonical active toolsets, schema generation, and compatibility mode flag.
/// Ensures complete cache partitioning and zero cross-talk between different toolset configurations.
/// </summary>
public readonly record struct ToolsetCacheKey(
    string BaseProfile,
    string ActiveToolsetsCanonical,
    long SchemaGeneration,
    bool CompatibilityMode)
{
    /// <summary>
    /// Creates a normalized, canonical cache key.
    /// Sorts active toolsets to guarantee order-independent cache hits.
    /// </summary>
    public static ToolsetCacheKey Create(
        string baseProfile,
        IEnumerable<string>? activeToolsets,
        long schemaGeneration,
        bool compatibilityMode)
    {
        var normalizedProfile = (baseProfile ?? "core").Trim().ToLowerInvariant();
        var sortedSets = string.Join(",", (activeToolsets ?? Enumerable.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToLowerInvariant())
            .OrderBy(s => s, StringComparer.Ordinal));

        return new ToolsetCacheKey(
            normalizedProfile,
            sortedSets,
            schemaGeneration,
            compatibilityMode);
    }
}

/// <summary>
/// Toolset-aware schema cache.
/// Caches filtered and annotated MCP Tool schema collections by ToolsetCacheKey.
/// Supports atomic invalidations via schema generation counters and defensive cloning.
/// </summary>
public sealed class ToolsetSchemaCache
{
    private readonly ConcurrentDictionary<ToolsetCacheKey, IList<Tool>> _cache = new();
    private long _generation = 1;

    /// <summary>
    /// Current schema generation counter. Incremented on Invalidate().
    /// </summary>
    public long CurrentGeneration => Interlocked.Read(ref _generation);

    /// <summary>
    /// Total entries currently stored in the cache.
    /// </summary>
    public int EntryCount => _cache.Count;

    /// <summary>
    /// Invalidates all cached schemas and advances the generation counter.
    /// </summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _cache.Clear();
    }

    /// <summary>
    /// Attempts to retrieve cached tools for the given key.
    /// Clones returned tools to protect internal cache state against caller mutation.
    /// </summary>
    public bool TryGet(ToolsetCacheKey key, out IList<Tool> tools)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            tools = CloneTools(cached);
            return true;
        }

        tools = Array.Empty<Tool>();
        return false;
    }

    /// <summary>
    /// Caches tools for the given key with defensive cloning.
    /// </summary>
    public void Set(ToolsetCacheKey key, IList<Tool> tools)
    {
        _cache[key] = CloneTools(tools);
    }

    /// <summary>
    /// Retrieves or computes tools for the given key.
    /// </summary>
    public IList<Tool> GetOrAdd(ToolsetCacheKey key, Func<ToolsetCacheKey, IList<Tool>> factory)
    {
        var cached = _cache.GetOrAdd(key, k => CloneTools(factory(k)));
        return CloneTools(cached);
    }

    /// <summary>
    /// Creates a deep-enough clone of each Tool object (cloning Annotations and metadata).
    /// </summary>
    public static IList<Tool> CloneTools(IEnumerable<Tool> tools)
    {
        return Array.AsReadOnly(tools.Select(CloneTool).ToArray());
    }

    /// <summary>
    /// Deep clones an individual Tool instance.
    /// </summary>
    public static Tool CloneTool(Tool t)
    {
        return new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema,
            Annotations = t.Annotations != null ? new ToolAnnotations
            {
                ReadOnlyHint = t.Annotations.ReadOnlyHint,
                IdempotentHint = t.Annotations.IdempotentHint,
                DestructiveHint = t.Annotations.DestructiveHint,
            } : null
        };
    }
}
