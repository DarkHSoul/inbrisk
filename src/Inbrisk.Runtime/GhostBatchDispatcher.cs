using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

#region Exceptions

/// <summary>
/// Thrown when a batch execution is requested but the Ghost Session 2 worker is not connected,
/// not running, or the cross-session IPC channel is closed.
/// </summary>
public class GhostSessionNotReadyException : InvalidOperationException
{
    public GhostSessionNotReadyException(string message) : base(message) { }
    public GhostSessionNotReadyException(string message, Exception innerException) : base(message, innerException) { }
}

#endregion

#region Models & Contracts

/// <summary>
/// Result of an individual action step inside an executed batch.
/// </summary>
public sealed record GhostBatchStepResult
{
    [JsonPropertyName("step")]
    public int Step { get; init; }

    [JsonPropertyName("do")]
    public string? Do { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("t")]
    public string? Target { get; init; }

    [JsonPropertyName("target")]
    public string? TargetFull { get; init; }

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("ms")]
    public long Ms { get; init; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    public string ActionName => !string.IsNullOrEmpty(Do) ? Do : (Action ?? string.Empty);
    public string TargetName => !string.IsNullOrEmpty(Target) ? Target : (TargetFull ?? string.Empty);
    public long Duration => Ms > 0 ? Ms : (DurationMs ?? 0);
}

/// <summary>
/// Aggregated result of a batch execution dispatched to the Ghost Session 2 worker.
/// Matches { success, stepsExecuted, totalDurationMs, results[] } specification.
/// </summary>
public sealed record GhostBatchResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("stepsExecuted")]
    public int StepsExecuted { get; init; }

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<GhostBatchStepResult> Results { get; init; } = Array.Empty<GhostBatchStepResult>();

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("rawJson")]
    public string? RawJson { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; init; }

    public static GhostBatchResult Succeeded(
        int stepsExecuted,
        long totalDurationMs,
        IReadOnlyList<GhostBatchStepResult>? results = null,
        string? rawJson = null) =>
        new()
        {
            Success = true,
            StepsExecuted = stepsExecuted,
            TotalDurationMs = totalDurationMs,
            Results = results ?? Array.Empty<GhostBatchStepResult>(),
            RawJson = rawJson
        };

    public static GhostBatchResult Failed(
        string error,
        int stepsExecuted = 0,
        long totalDurationMs = 0,
        IReadOnlyList<GhostBatchStepResult>? results = null,
        string? rawJson = null) =>
        new()
        {
            Success = false,
            StepsExecuted = stepsExecuted,
            TotalDurationMs = totalDurationMs,
            Results = results ?? Array.Empty<GhostBatchStepResult>(),
            Error = error,
            RawJson = rawJson
        };

    /// <summary>
    /// Parses raw JSON string returned by the Session 2 worker into a structured GhostBatchResult.
    /// Supports standard batch envelope, MCP call-tool envelopes, and snake_case property aliases.
    /// </summary>
    public static GhostBatchResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Failed("Empty or whitespace response received from Ghost worker.", rawJson: json);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Handle nested MCP CallToolResult envelope if wrapped in { content: [ { text: "..." } ] }
            if (root.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in contentArray.EnumerateArray())
                {
                    if (element.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    {
                        var innerJson = textProp.GetString();
                        if (!string.IsNullOrWhiteSpace(innerJson) && innerJson.TrimStart().StartsWith('{'))
                        {
                            return Parse(innerJson);
                        }
                    }
                }
            }

            // Success property
            bool success = false;
            if (root.TryGetProperty("success", out var sProp) ||
                root.TryGetProperty("Success", out sProp) ||
                root.TryGetProperty("ok", out sProp))
            {
                success = sProp.ValueKind == JsonValueKind.True;
            }

            // Steps executed
            int stepsExecuted = 0;
            if (root.TryGetProperty("stepsExecuted", out var seProp) ||
                root.TryGetProperty("steps_executed", out seProp) ||
                root.TryGetProperty("StepsExecuted", out seProp) ||
                root.TryGetProperty("executedSteps", out seProp))
            {
                seProp.TryGetInt32(out stepsExecuted);
            }

            // Total duration
            long totalDurationMs = 0;
            if (root.TryGetProperty("totalDurationMs", out var tdProp) ||
                root.TryGetProperty("total_duration_ms", out tdProp) ||
                root.TryGetProperty("TotalDurationMs", out tdProp) ||
                root.TryGetProperty("durationMs", out tdProp) ||
                root.TryGetProperty("duration_ms", out tdProp) ||
                root.TryGetProperty("elapsedMs", out tdProp))
            {
                tdProp.TryGetInt64(out totalDurationMs);
            }

            // Error message
            string? error = null;
            if (root.TryGetProperty("error", out var errProp) ||
                root.TryGetProperty("Error", out errProp) ||
                root.TryGetProperty("errorMessage", out errProp))
            {
                error = errProp.GetString();
            }

            // Step results array
            var stepResults = new List<GhostBatchStepResult>();
            if (root.TryGetProperty("results", out var resArray) && resArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resArray.EnumerateArray())
                {
                    var step = JsonSerializer.Deserialize<GhostBatchStepResult>(item.GetRawText(), JsonOptions);
                    if (step != null)
                    {
                        stepResults.Add(step);
                    }
                }
            }

            if (stepsExecuted == 0 && stepResults.Count > 0)
            {
                stepsExecuted = stepResults.Count;
            }

            return new GhostBatchResult
            {
                Success = success,
                StepsExecuted = stepsExecuted,
                TotalDurationMs = totalDurationMs,
                Results = stepResults,
                Error = error,
                RawJson = json
            };
        }
        catch (JsonException ex)
        {
            return Failed($"Failed to parse worker response JSON: {ex.Message}", rawJson: json);
        }
    }
}

/// <summary>
/// Interface for dispatching batch operations from Session 1 host to Session 2 worker.
/// </summary>
public interface IGhostBatchDispatcher
{
    /// <summary>Whether the Session 2 worker is currently connected to the IPC bus.</summary>
    bool IsConnected { get; }

    /// <summary>Default timeout applied to batch dispatch RPC calls.</summary>
    TimeSpan DefaultTimeout { get; }

    /// <summary>Dispatches a batch JSON payload to the worker and returns the execution outcome.</summary>
    Task<GhostBatchResult> DispatchBatchAsync(string batchPayloadJson, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Serializes and dispatches an arbitrary batch payload object to the worker.</summary>
    Task<GhostBatchResult> DispatchBatchAsync(object batchPayload, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Executes batch steps (alias of DispatchBatchAsync).</summary>
    Task<GhostBatchResult> ExecuteBatchAsync(string batchPayloadJson, TimeSpan? timeout = null, CancellationToken ct = default);

    /// <summary>Executes batch steps (alias of DispatchBatchAsync).</summary>
    Task<GhostBatchResult> ExecuteBatchAsync(object batchPayload, TimeSpan? timeout = null, CancellationToken ct = default);
}

#endregion

#region Implementation

/// <summary>
/// Host-side bridge service (Session 1) that dispatches computer_batch actions and automation
/// requests over the named pipe IPC channel to the InbriskAgent worker process running in Session 2.
/// </summary>
public sealed class GhostBatchDispatcher : IGhostBatchDispatcher
{
    /// <summary>RPC action name expected by the Session 2 Ghost worker.</summary>
    public const string ExecuteBatchAction = "execute_batch";

    /// <summary>Default timeout for batch execution (60 seconds).</summary>
    public static readonly TimeSpan DefaultBatchTimeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly IGhostIpcBus _ipcBus;
    private readonly TimeSpan _defaultTimeout;

    /// <inheritdoc/>
    public bool IsConnected => _ipcBus.IsConnected;

    /// <inheritdoc/>
    public TimeSpan DefaultTimeout => _defaultTimeout;

    /// <summary>
    /// Initializes a new instance of GhostBatchDispatcher backed by a GhostIpcServer.
    /// </summary>
    public GhostBatchDispatcher(GhostIpcServer server, TimeSpan? defaultTimeout = null)
        : this((IGhostIpcBus)server, defaultTimeout)
    {
    }

    /// <summary>
    /// Initializes a new instance of GhostBatchDispatcher backed by an IGhostIpcBus channel.
    /// </summary>
    public GhostBatchDispatcher(IGhostIpcBus ipcBus, TimeSpan? defaultTimeout = null)
    {
        _ipcBus = ipcBus ?? throw new ArgumentNullException(nameof(ipcBus));
        _defaultTimeout = defaultTimeout ?? DefaultBatchTimeout;
    }

    /// <inheritdoc/>
    public async Task<GhostBatchResult> DispatchBatchAsync(
        string batchPayloadJson,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchPayloadJson);

        if (!_ipcBus.IsConnected)
        {
            throw new GhostSessionNotReadyException(
                "Ghost Session 2 worker is not connected. " +
                "Ensure the Ghost desktop session is active and the InbriskAgent worker process has connected to the IPC pipe.");
        }

        var effectiveTimeout = timeout ?? _defaultTimeout;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            string responseJson = await _ipcBus.SendRequestAsync(
                action: ExecuteBatchAction,
                payloadJson: batchPayloadJson,
                timeout: effectiveTimeout,
                ct: ct).ConfigureAwait(false);

            stopwatch.Stop();
            var result = GhostBatchResult.Parse(responseJson);

            // If the worker did not supply total duration, supplement with elapsed dispatcher time
            if (result.TotalDurationMs == 0 && stopwatch.ElapsedMilliseconds > 0)
            {
                result = result with { TotalDurationMs = stopwatch.ElapsedMilliseconds };
            }

            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            throw new TimeoutException(
                $"Ghost batch execution timed out after {effectiveTimeout.TotalSeconds:F1}s while awaiting Session 2 worker response.", ex);
        }
        catch (GhostIpcException ex)
        {
            stopwatch.Stop();

            // Detect if the failure was caused by client disconnection or missing worker
            if (!_ipcBus.IsConnected ||
                ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("No active", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            {
                throw new GhostSessionNotReadyException(
                    $"Ghost Session 2 worker connection was lost during batch execution: {ex.Message}", ex);
            }

            return GhostBatchResult.Failed(
                error: $"Ghost IPC execution error: {ex.Message}",
                totalDurationMs: stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not GhostSessionNotReadyException)
        {
            stopwatch.Stop();
            throw new GhostSessionNotReadyException(
                $"Unexpected error communicating with Ghost Session 2 worker: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public Task<GhostBatchResult> DispatchBatchAsync(
        object batchPayload,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batchPayload);

        string json = batchPayload is string str
            ? str
            : JsonSerializer.Serialize(batchPayload, DefaultJsonOptions);

        return DispatchBatchAsync(json, timeout, ct);
    }

    /// <inheritdoc/>
    public Task<GhostBatchResult> ExecuteBatchAsync(
        string batchPayloadJson,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        DispatchBatchAsync(batchPayloadJson, timeout, ct);

    /// <inheritdoc/>
    public Task<GhostBatchResult> ExecuteBatchAsync(
        object batchPayload,
        TimeSpan? timeout = null,
        CancellationToken ct = default) =>
        DispatchBatchAsync(batchPayload, timeout, ct);

    /// <summary>
    /// Sends a low-level ping to verify the Session 2 worker is responsive.
    /// </summary>
    public Task<bool> PingWorkerAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!_ipcBus.IsConnected) return Task.FromResult(false);
        return _ipcBus.PingAsync(timeout ?? TimeSpan.FromSeconds(5), ct);
    }

    /// <summary>
    /// Waits asynchronously until the Session 2 worker is connected and responsive, or until timeout.
    /// </summary>
    public async Task<bool> WaitForWorkerReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (_ipcBus.IsConnected) return true;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            while (!linkedCts.Token.IsCancellationRequested)
            {
                if (_ipcBus.IsConnected)
                {
                    bool pingOk = await _ipcBus.PingAsync(TimeSpan.FromSeconds(2), linkedCts.Token).ConfigureAwait(false);
                    if (pingOk) return true;
                }

                await Task.Delay(100, linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return false;
        }

        return _ipcBus.IsConnected;
    }
}

#endregion
