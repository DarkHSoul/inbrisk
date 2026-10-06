using System.Text.Json.Serialization;

namespace Inbrisk.Mcp.Daemon;

/// <summary>
/// Handshake request exchanged immediately upon Named Pipe IPC connection
/// before any MCP JSON-RPC frames are processed.
/// </summary>
public sealed class DaemonHandshakeRequest
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = InbriskRuntimeDaemon.CurrentProtocolVersion;

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("clientVersion")]
    public string? ClientVersion { get; set; }
}

/// <summary>
/// Handshake response returned by the daemon upon client connection.
/// Establishes the authoritative session ID, daemon epoch, and protocol agreement.
/// </summary>
public sealed class DaemonHandshakeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; } = InbriskRuntimeDaemon.CurrentProtocolVersion;

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("sessionEpoch")]
    public long SessionEpoch { get; set; }

    [JsonPropertyName("daemonPid")]
    public int DaemonPid { get; set; } = Environment.ProcessId;

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Represents an in-flight MCP request tracked by the thin proxy.
/// Enables crash/disconnect detection and ensures mutating requests are never silently replayed.
/// </summary>
public sealed class InFlightRequest
{
    public object Id { get; }
    public string Method { get; }
    public string? ToolName { get; }
    public bool IsMutating { get; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public TaskCompletionSource<string>? CompletionSource { get; }

    public InFlightRequest(object id, string method, string? toolName, bool isMutating, TaskCompletionSource<string>? tcs = null)
    {
        Id = id;
        Method = method;
        ToolName = toolName;
        IsMutating = isMutating;
        CompletionSource = tcs;
    }
}

/// <summary>
/// Tracks session-scoped handles (e.g. element handles, frame refs) bound to a specific session and epoch.
/// Handles minted in an earlier epoch/session are invalid after a crash/reconnect.
/// </summary>
public sealed record SessionHandle(string HandleId, long Epoch, string SessionId);

/// <summary>
/// Thrown when a client attempts to connect to the daemon with an incompatible protocol version.
/// </summary>
public sealed class ProtocolVersionMismatchException : Exception
{
    public int ClientVersion { get; }
    public int DaemonVersion { get; }

    public ProtocolVersionMismatchException(string message) : base(message) { }

    public ProtocolVersionMismatchException(int clientVersion, int daemonVersion, string? details = null)
        : base($"Protocol version mismatch: client version {clientVersion} is incompatible with daemon version {daemonVersion}.{(details != null ? $" Details: {details}" : "")}")
    {
        ClientVersion = clientVersion;
        DaemonVersion = daemonVersion;
    }
}

/// <summary>
/// Thrown when a mutating request was in-flight while the daemon connection broke.
/// Mutating requests must fail truthfully and must NOT be silently replayed.
/// </summary>
public sealed class InFlightMutationFailedException : Exception
{
    public object? RequestId { get; }
    public string? ToolName { get; }

    public InFlightMutationFailedException(string message, object? requestId = null, string? toolName = null)
        : base(message)
    {
        RequestId = requestId;
        ToolName = toolName;
    }
}
