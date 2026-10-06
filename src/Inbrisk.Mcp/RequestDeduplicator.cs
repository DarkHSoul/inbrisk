using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp;

public sealed record ReadCacheEntry(
    string Fingerprint,
    CallToolResult Result,
    long StateGeneration,
    DateTimeOffset Timestamp
);

public sealed record OperationRecord(
    string OperationId,
    string ToolName,
    string NormalizedArgsHash,
    CallToolResult Result,
    DateTimeOffset Timestamp
);

/// <summary>
/// Handles duplicate request detection per Section C2/C Round 2 invariants:
/// 1. READ-ONLY: Fingerprinted by (tool, normalizedArgs, scope, stateGeneration) with BOUNDED TTL
///    (windows: 2s, find/inspect: <= 5s, semantic observe: 2s). Visual/screenshot observes bypass deduplication.
/// 2. MUTATING: Explicit operationId records (operationId, toolName, argsHash, result, timestamp).
///    - Matching opId + same tool + same args -> return previous result.
///    - Matching opId + different tool/args -> explicit OperationIdConflict error.
///    - Mutations without opId are NEVER suppressed.
/// </summary>
public sealed class RequestDeduplicator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ReadCacheEntry> _readCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationRecord> _mutationsByOpId = new(StringComparer.Ordinal);

    private const int MaxCacheEntries = 128;
    private static readonly TimeSpan MutationTtl = TimeSpan.FromMinutes(5);

    public static TimeSpan GetReadTtl(string toolName) => toolName switch
    {
        "computer_windows" => TimeSpan.FromSeconds(2),
        "computer_find" => TimeSpan.FromSeconds(5),
        "computer_inspect" => TimeSpan.FromSeconds(5),
        "computer_observe" => TimeSpan.FromSeconds(2),
        _ => TimeSpan.FromSeconds(2)
    };

    public static string ComputeArgsHash(string? args)
    {
        if (string.IsNullOrEmpty(args)) return "empty";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(args));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Guard per Section C3: Visual/frame-derived content MUST NOT be deduped, regardless of mode string.
    /// Evaluates tool name, request mode, request flags (autoScreenshot, crop/region), frame counts,
    /// and output content blocks.
    /// </summary>
    public static bool IsSafeForReadDeduplication(
        string toolName,
        string? mode = null,
        bool? autoScreenshot = null,
        bool hasCropOrRegion = false,
        CallToolResult? result = null,
        int frameCount = 0)
    {
        if (toolName != "computer_observe" &&
            toolName != "computer_windows" &&
            toolName != "computer_find" &&
            toolName != "computer_inspect")
        {
            return false;
        }

        if (toolName == "computer_observe")
        {
            if (string.Equals(mode, "visual", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "both", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (autoScreenshot == true || hasCropOrRegion)
            {
                return false;
            }

            if (frameCount > 0)
            {
                return false;
            }

            if (result != null)
            {
                if (result.Content.Any(c => c is ImageContentBlock))
                    return false;

                foreach (var b in result.Content.OfType<TextContentBlock>())
                {
                    if (b.Text.Contains("frame(s) attached", StringComparison.OrdinalIgnoreCase) ||
                        b.Text.Contains("visual fallback", StringComparison.OrdinalIgnoreCase) ||
                        b.Text.Contains("autoScreenshot", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    public CallToolResult? TryDeduplicateRead(
        string toolName,
        string normalizedArgs,
        long scopeHwnd,
        long stateGeneration,
        SessionTelemetry? telemetry)
    {
        lock (_sync)
        {
            var fingerprint = $"{toolName}::hwnd=0x{scopeHwnd:X}::{normalizedArgs}";
            if (_readCache.TryGetValue(fingerprint, out var entry))
            {
                var ttl = GetReadTtl(toolName);
                if (DateTimeOffset.UtcNow - entry.Timestamp <= ttl &&
                    entry.StateGeneration == stateGeneration)
                {
                    telemetry?.IncDuplicateReadSuppressed();
                    Inbrisk.Core.PerfTrace.Count("duplicateReadSuppressed");

                    var cached = entry.Result;
                    var modifiedContent = new List<ContentBlock>();
                    foreach (var block in cached.Content)
                    {
                        if (block is TextContentBlock tb)
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(tb.Text);
                                var dict = new Dictionary<string, object?>();
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    dict[prop.Name] = prop.Value.Clone();
                                }
                                dict["unchanged"] = true;
                                dict["stateGeneration"] = stateGeneration;
                                var updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
                                modifiedContent.Add(new TextContentBlock { Text = updatedJson });
                                continue;
                            }
                            catch
                            {
                                modifiedContent.Add(new TextContentBlock { Text = $"{tb.Text.TrimEnd()}\n[unchanged: true, stateGeneration: {stateGeneration}]\n" });
                                continue;
                            }
                        }
                        modifiedContent.Add(block);
                    }

                    return new CallToolResult
                    {
                        IsError = cached.IsError,
                        Content = modifiedContent
                    };
                }
                else
                {
                    // Expired or stale state generation
                    _readCache.Remove(fingerprint);
                }
            }
            return null;
        }
    }

    public void RecordRead(
        string toolName,
        string normalizedArgs,
        long scopeHwnd,
        long stateGeneration,
        CallToolResult result)
    {
        lock (_sync)
        {
            PruneReadCacheIfFull();
            var fingerprint = $"{toolName}::hwnd=0x{scopeHwnd:X}::{normalizedArgs}";
            _readCache[fingerprint] = new ReadCacheEntry(fingerprint, result, stateGeneration, DateTimeOffset.UtcNow);
        }
    }

    public CallToolResult? TryDeduplicateMutation(
        string? operationId,
        string toolName,
        string normalizedArgs,
        SessionTelemetry? telemetry,
        out CallToolResult? conflictError)
    {
        conflictError = null;
        if (string.IsNullOrWhiteSpace(operationId))
            return null; // Mutations without explicit operationId are never suppressed

        lock (_sync)
        {
            PruneMutations();
            if (_mutationsByOpId.TryGetValue(operationId, out var existing))
            {
                var hash = ComputeArgsHash(normalizedArgs);
                if (string.Equals(existing.ToolName, toolName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.NormalizedArgsHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    telemetry?.IncDuplicateMutationRetrySuppressed();
                    Inbrisk.Core.PerfTrace.Count("duplicateMutationRetrySuppressed");
                    return existing.Result;
                }
                else
                {
                    // Conflict detected! Same operationId used for different tool or different args
                    conflictError = new CallToolResult
                    {
                        IsError = true,
                        Content = new List<ContentBlock>
                        {
                            new TextContentBlock
                            {
                                Text = JsonSerializer.Serialize(new
                                {
                                    error = "OperationIdConflict",
                                    detail = $"OperationId '{operationId}' was already used for tool '{existing.ToolName}' with different parameters. Reusing an operationId for different actions is forbidden.",
                                    operationId,
                                    previousTool = existing.ToolName,
                                    attemptedTool = toolName
                                })
                            }
                        }
                    };
                    return null;
                }
            }
            return null;
        }
    }

    public CallToolResult? TryDeduplicateMutation(
        string? operationId,
        SessionTelemetry? telemetry)
    {
        return TryDeduplicateMutation(operationId, "generic", "", telemetry, out _);
    }

    public void RecordMutation(
        string? operationId,
        string toolName,
        string normalizedArgs,
        CallToolResult result)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            return;

        lock (_sync)
        {
            PruneMutations();
            var hash = ComputeArgsHash(normalizedArgs);
            _mutationsByOpId[operationId] = new OperationRecord(operationId, toolName, hash, result, DateTimeOffset.UtcNow);
        }
    }

    public void RecordMutation(
        string? operationId,
        CallToolResult result)
    {
        RecordMutation(operationId, "generic", "", result);
    }

    public void InvalidateReadCache()
    {
        lock (_sync)
        {
            _readCache.Clear();
        }
    }

    private void PruneReadCacheIfFull()
    {
        if (_readCache.Count >= MaxCacheEntries)
        {
            var now = DateTimeOffset.UtcNow;
            var expired = _readCache.Where(kvp => now - kvp.Value.Timestamp > GetReadTtl(kvp.Value.Fingerprint.Split("::")[0])).Select(kvp => kvp.Key).ToList();
            foreach (var key in expired) _readCache.Remove(key);

            if (_readCache.Count >= MaxCacheEntries)
            {
                var oldest = _readCache.OrderBy(kvp => kvp.Value.Timestamp).Take(MaxCacheEntries / 4).Select(kvp => kvp.Key).ToList();
                foreach (var k in oldest) _readCache.Remove(k);
            }
        }
    }

    private void PruneMutations()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = _mutationsByOpId.Where(kvp => now - kvp.Value.Timestamp > MutationTtl).Select(kvp => kvp.Key).ToList();
        foreach (var k in expired) _mutationsByOpId.Remove(k);

        if (_mutationsByOpId.Count >= MaxCacheEntries)
        {
            var oldest = _mutationsByOpId.OrderBy(kvp => kvp.Value.Timestamp).Take(MaxCacheEntries / 4).Select(kvp => kvp.Key).ToList();
            foreach (var k in oldest) _mutationsByOpId.Remove(k);
        }
    }
}
