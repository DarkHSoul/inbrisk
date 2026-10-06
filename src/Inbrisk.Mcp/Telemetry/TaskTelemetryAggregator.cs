using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Inbrisk.Mcp.Telemetry;

/// <summary>
/// Call-level metrics captured for an individual MCP tool execution.
/// </summary>
public sealed class CallTelemetryMetrics
{
    public string CallId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string TaskId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public DateTimeOffset ArrivedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.UtcNow;

    public double QueueMs { get; init; }
    public double ExecutionMs { get; init; }
    public long ResponseBytes { get; init; }
    public long EstimatedTokens { get; init; }
    public bool CacheHit { get; init; }
    public int RetryCount { get; init; }
    public double ReadLaneWaitMs { get; init; }
    public double MutationLaneWaitMs { get; init; }

    public Dictionary<string, object?> Arguments { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Server-estimated model turn containing one or more tool calls.
/// </summary>
public sealed class ModelTurnInfo
{
    public int TurnIndex { get; init; }
    public DateTimeOffset TurnStart { get; init; }
    public DateTimeOffset TurnEnd { get; init; }
    public double ThinkingMs { get; init; }
    public bool IsParallel => Calls.Count > 1;
    public List<CallTelemetryMetrics> Calls { get; init; } = new();
}

/// <summary>
/// Task-level aggregated metrics across all tool calls in a task.
/// </summary>
public sealed class TaskMetricsSummary
{
    public string TaskId { get; init; } = string.Empty;
    public int TotalToolCalls { get; init; }
    public int ParallelCallCount { get; init; }
    public int SequentialCallCount { get; init; }
    public int EstimatedModelTurns { get; init; }

    public double TotalQueueWaitMs { get; init; }
    public double TotalReadLaneWaitMs { get; init; }
    public double TotalMutationLaneWaitMs { get; init; }
    public double TotalExecutionMs { get; init; }
    public double TotalThinkingMs { get; init; }

    public IReadOnlyList<CallTelemetryMetrics> Calls { get; init; } = Array.Empty<CallTelemetryMetrics>();
    public IReadOnlyList<ModelTurnInfo> Turns { get; init; } = Array.Empty<ModelTurnInfo>();
}

/// <summary>
/// Aggregates call-level and task-level telemetry, estimates model turns via arrival heuristics,
/// and enforces privacy filtering on sensitive data.
/// </summary>
public sealed class TaskTelemetryAggregator
{
    private readonly ConcurrentDictionary<string, List<CallTelemetryMetrics>> _taskCalls = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public const double DefaultArrivalThresholdMs = 50.0;

    // --- Privacy Filter Regexes ---
    private static readonly Regex PasswordPattern = new(
        @"(?i)(password|pwd|passphrase)\s*[:=]\s*['""]?([^\s,'""}]+)['""]?",
        RegexOptions.Compiled);

    private static readonly Regex BearerTokenPattern = new(
        @"(?i)bearer\s+[a-zA-Z0-9_\-\.~]+",
        RegexOptions.Compiled);

    private static readonly Regex KeyTokenPattern = new(
        @"(?i)(?:sk|ghp|gho|ghu|ghs|ghr)[_-][a-zA-Z0-9_\-]{16,}",
        RegexOptions.Compiled);

    private static readonly Regex JwtPattern = new(
        @"eyJ[a-zA-Z0-9_\-]{10,}\.eyJ[a-zA-Z0-9_\-]{10,}\.[a-zA-Z0-9_\-]{10,}",
        RegexOptions.Compiled);

    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)(api[_-]?key|secret|token|auth)\s*[:=]\s*['""]?([a-zA-Z0-9_\-\.]{16,})['""]?",
        RegexOptions.Compiled);

    /// <summary>
    /// Records an individual tool call metrics item. Sanitizes any arguments before storing.
    /// </summary>
    public void RecordCall(CallTelemetryMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        var sanitizedArgs = SanitizeArguments(metrics.Arguments);
        var effectiveTokens = metrics.EstimatedTokens > 0
            ? metrics.EstimatedTokens
            : EstimateTokensFromBytes(metrics.ResponseBytes);

        var sanitizedMetrics = new CallTelemetryMetrics
        {
            CallId = metrics.CallId,
            TaskId = metrics.TaskId,
            ToolName = metrics.ToolName,
            ArrivedAt = metrics.ArrivedAt,
            CompletedAt = metrics.CompletedAt,
            QueueMs = metrics.QueueMs,
            ExecutionMs = metrics.ExecutionMs,
            ResponseBytes = metrics.ResponseBytes,
            EstimatedTokens = effectiveTokens,
            CacheHit = metrics.CacheHit,
            RetryCount = metrics.RetryCount,
            ReadLaneWaitMs = metrics.ReadLaneWaitMs,
            MutationLaneWaitMs = metrics.MutationLaneWaitMs,
            Arguments = sanitizedArgs
        };

        var key = string.IsNullOrEmpty(metrics.TaskId) ? "default" : metrics.TaskId;
        lock (_lock)
        {
            if (!_taskCalls.TryGetValue(key, out var list))
            {
                list = new List<CallTelemetryMetrics>();
                _taskCalls[key] = list;
            }
            list.Add(sanitizedMetrics);
        }
    }

    /// <summary>
    /// Retrieves all recorded calls for a specific task.
    /// </summary>
    public IReadOnlyList<CallTelemetryMetrics> GetCallsForTask(string taskId)
    {
        var key = string.IsNullOrEmpty(taskId) ? "default" : taskId;
        lock (_lock)
        {
            return _taskCalls.TryGetValue(key, out var list)
                ? list.ToList().AsReadOnly()
                : Array.Empty<CallTelemetryMetrics>();
        }
    }

    /// <summary>
    /// Computes task-level metrics summary by aggregating calls and grouping them into estimated model turns.
    /// </summary>
    public TaskMetricsSummary GetTaskSummary(string taskId, double arrivalThresholdMs = DefaultArrivalThresholdMs)
    {
        var calls = GetCallsForTask(taskId);
        if (calls.Count == 0)
        {
            return new TaskMetricsSummary
            {
                TaskId = taskId,
                Calls = Array.Empty<CallTelemetryMetrics>(),
                Turns = Array.Empty<ModelTurnInfo>()
            };
        }

        var turns = EstimateModelTurns(calls, arrivalThresholdMs);

        int parallelCount = 0;
        int sequentialCount = 0;
        double totalThinkingMs = 0;

        foreach (var turn in turns)
        {
            if (turn.IsParallel)
                parallelCount += turn.Calls.Count;
            else
                sequentialCount += turn.Calls.Count;

            totalThinkingMs += turn.ThinkingMs;
        }

        return new TaskMetricsSummary
        {
            TaskId = taskId,
            TotalToolCalls = calls.Count,
            ParallelCallCount = parallelCount,
            SequentialCallCount = sequentialCount,
            EstimatedModelTurns = turns.Count,
            TotalQueueWaitMs = calls.Sum(c => c.QueueMs),
            TotalReadLaneWaitMs = calls.Sum(c => c.ReadLaneWaitMs),
            TotalMutationLaneWaitMs = calls.Sum(c => c.MutationLaneWaitMs),
            TotalExecutionMs = calls.Sum(c => c.ExecutionMs),
            TotalThinkingMs = totalThinkingMs,
            Calls = calls,
            Turns = turns
        };
    }

    /// <summary>
    /// Server-estimated model turn heuristic:
    /// Requests arriving within ~50ms of each other belong to the same parallel turn group;
    /// gap between turns represents thinking time.
    /// </summary>
    public static IReadOnlyList<ModelTurnInfo> EstimateModelTurns(
        IEnumerable<CallTelemetryMetrics> calls,
        double arrivalThresholdMs = DefaultArrivalThresholdMs)
    {
        var sortedCalls = calls.OrderBy(c => c.ArrivedAt).ToList();
        if (sortedCalls.Count == 0)
            return Array.Empty<ModelTurnInfo>();

        var turns = new List<ModelTurnInfo>();
        ModelTurnInfo? currentTurn = null;
        DateTimeOffset? previousTurnEnd = null;
        DateTimeOffset? lastArrivalInTurn = null;

        int turnIndex = 1;

        foreach (var call in sortedCalls)
        {
            if (currentTurn == null)
            {
                // First turn
                currentTurn = new ModelTurnInfo
                {
                    TurnIndex = turnIndex++,
                    TurnStart = call.ArrivedAt,
                    TurnEnd = call.CompletedAt,
                    ThinkingMs = 0,
                    Calls = { call }
                };
                lastArrivalInTurn = call.ArrivedAt;
                continue;
            }

            // Check if this call arrived within the threshold of the last arrival in the turn
            var arrivalDelta = (call.ArrivedAt - lastArrivalInTurn!.Value).TotalMilliseconds;

            if (arrivalDelta <= arrivalThresholdMs)
            {
                // Parallel call in the same model turn
                currentTurn.Calls.Add(call);
                lastArrivalInTurn = call.ArrivedAt;
                if (call.CompletedAt > currentTurn.TurnEnd)
                {
                    // Update turn end to the latest completion
                    currentTurn = new ModelTurnInfo
                    {
                        TurnIndex = currentTurn.TurnIndex,
                        TurnStart = currentTurn.TurnStart,
                        TurnEnd = call.CompletedAt,
                        ThinkingMs = currentTurn.ThinkingMs,
                        Calls = currentTurn.Calls
                    };
                }
            }
            else
            {
                // Turn boundary: complete current turn and start new one
                turns.Add(currentTurn);
                previousTurnEnd = currentTurn.TurnEnd;

                var gapMs = (call.ArrivedAt - previousTurnEnd.Value).TotalMilliseconds;
                var thinkingMs = gapMs > 0 ? gapMs : 0;

                currentTurn = new ModelTurnInfo
                {
                    TurnIndex = turnIndex++,
                    TurnStart = call.ArrivedAt,
                    TurnEnd = call.CompletedAt,
                    ThinkingMs = thinkingMs,
                    Calls = { call }
                };
                lastArrivalInTurn = call.ArrivedAt;
            }
        }

        if (currentTurn != null)
        {
            turns.Add(currentTurn);
        }

        return turns.AsReadOnly();
    }

    /// <summary>
    /// Estimates token consumption from payload byte size (~4 chars per token).
    /// </summary>
    public static long EstimateTokensFromBytes(long byteCount) =>
        byteCount <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(byteCount / 4.0));

    // =========================================================================
    // PRIVACY FILTER: Strictly sanitizes passwords, clipboard secrets,
    // raw document content, and auth tokens.
    // =========================================================================

    public static Dictionary<string, object?> SanitizeArguments(IDictionary<string, object?>? arguments)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (arguments == null) return result;

        foreach (var (key, value) in arguments)
        {
            result[key] = SanitizeValue(key, value);
        }

        return result;
    }

    public static object? SanitizeValue(string key, object? value)
    {
        if (value == null) return null;

        var lowerKey = key.ToLowerInvariant();

        // 1. Password detection by key
        if (lowerKey.Contains("password") || lowerKey == "pwd" || lowerKey == "passphrase" || lowerKey == "pass")
        {
            return "[REDACTED_PASSWORD]";
        }

        // 2. Auth tokens / Secrets by key
        if (lowerKey.Contains("secret") || lowerKey.Contains("token") || lowerKey.Contains("apikey")
            || lowerKey.Contains("api_key") || lowerKey.Contains("auth") || lowerKey.Contains("bearer") || lowerKey == "credential")
        {
            return "[REDACTED_AUTH_TOKEN]";
        }

        // 3. Clipboard secrets by key
        if (lowerKey.Contains("clipboard"))
        {
            return "[REDACTED_CLIPBOARD]";
        }

        // 4. Raw document content by key
        if (lowerKey.Contains("document") || lowerKey.Contains("doccontent") || lowerKey.Contains("rawcontent")
            || lowerKey.Contains("filecontent") || lowerKey == "body")
        {
            return "[REDACTED_DOCUMENT]";
        }

        // 5. String value inspection (regex redaction)
        if (value is string strValue)
        {
            return SanitizeText(strValue);
        }

        // 6. Nested dictionary
        if (value is IDictionary<string, object?> nestedDict)
        {
            return SanitizeArguments(nestedDict);
        }

        return value;
    }

    /// <summary>
    /// Sanitizes plain text containing embedded secrets, passwords, tokens, or clipboard/document dumps.
    /// </summary>
    public static string SanitizeText(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var text = input;

        // Passwords
        text = PasswordPattern.Replace(text, "$1=[REDACTED_PASSWORD]");

        // Bearer tokens
        text = BearerTokenPattern.Replace(text, "Bearer [REDACTED_AUTH_TOKEN]");

        // API keys (sk-, ghp-, etc.)
        text = KeyTokenPattern.Replace(text, "[REDACTED_AUTH_TOKEN]");

        // JWT tokens
        text = JwtPattern.Replace(text, "[REDACTED_AUTH_TOKEN]");

        // Secret assignments
        text = SecretAssignmentPattern.Replace(text, "$1=[REDACTED_AUTH_TOKEN]");

        // Clipboard mentions containing secrets
        if (text.Contains("clipboard:", StringComparison.OrdinalIgnoreCase) &&
            (text.Contains("token", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("key", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
             text.Contains("password", StringComparison.OrdinalIgnoreCase)))
        {
            text = Regex.Replace(text, @"(?i)clipboard:\s*['""]?[^'""]+['""]?", "clipboard:[REDACTED_CLIPBOARD]");
        }

        return text;
    }

    /// <summary>
    /// Clears recorded telemetry for all tasks or a specific task.
    /// </summary>
    public void Clear(string? taskId = null)
    {
        lock (_lock)
        {
            if (string.IsNullOrEmpty(taskId))
            {
                _taskCalls.Clear();
            }
            else
            {
                _taskCalls.TryRemove(taskId, out _);
            }
        }
    }
}
