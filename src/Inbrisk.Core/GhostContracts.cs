namespace Inbrisk.Core;

/// <summary>
/// Configuration parameters for starting a dedicated Ghost desktop session.
/// </summary>
public record GhostSessionConfig(
    int Width = 1920,
    int Height = 1080,
    string Username = "InbriskAgent",
    bool AutoProvision = true,
    int TimeoutSeconds = 20);

/// <summary>
/// Represents the live status and diagnostic information of a Ghost desktop session.
/// </summary>
public record GhostSessionStatus(
    bool IsActive,
    int? SessionId,
    string? Username,
    TimeSpan Uptime,
    string State,
    int? ProcessId)
{
    /// <summary>
    /// Represents an inactive or stopped session status.
    /// </summary>
    public static GhostSessionStatus Inactive => new(
        IsActive: false,
        SessionId: null,
        Username: null,
        Uptime: TimeSpan.Zero,
        State: "Inactive",
        ProcessId: null);
}

/// <summary>
/// Result of provisioning a dedicated local Ghost user account.
/// </summary>
public record GhostUserProvisionResult(
    bool Success,
    string Username,
    bool CreatedNew = false,
    string? Error = null)
{
    public static GhostUserProvisionResult Ok(string username, bool createdNew = false) =>
        new(Success: true, Username: username, CreatedNew: createdNew, Error: null);

    public static GhostUserProvisionResult Fail(string username, string error) =>
        new(Success: false, Username: username, CreatedNew: false, Error: error);
}

/// <summary>
/// Cross-session IPC envelope for coordinating tasks between host and ghost sessions.
/// </summary>
public record GhostIpcMessage(
    string Id,
    string Action,
    string Payload,
    long TimestampTicks)
{
    public static GhostIpcMessage Create(string action, string payload) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            Action: action,
            Payload: payload,
            TimestampTicks: DateTime.UtcNow.Ticks);
}

/// <summary>
/// Core contract for managing isolated Ghost desktop sessions and user provisioning.
/// </summary>
public interface IGhostSessionManager
{
    /// <summary>
    /// Ensures the dedicated ghost user account exists and has proper permissions.
    /// </summary>
    Task<GhostUserProvisionResult> EnsureProvisionedAsync(CancellationToken ct = default);

    /// <summary>
    /// Launches or connects to a ghost desktop session with the specified configuration.
    /// </summary>
    Task<GhostSessionStatus> StartSessionAsync(GhostSessionConfig? config = null, CancellationToken ct = default);

    /// <summary>
    /// Stops the target ghost session or the current active session if sessionId is omitted.
    /// </summary>
    Task<bool> StopSessionAsync(int? sessionId = null, CancellationToken ct = default);

    /// <summary>
    /// Queries the current status of the ghost session.
    /// </summary>
    Task<GhostSessionStatus> GetStatusAsync(CancellationToken ct = default);
}

/// <summary>
/// Telemetry counters and performance tracing hooks for Ghost session lifecycle.
/// </summary>
public static class GhostTelemetry
{
    public const string MetricSessionStart = "ghostSessionStart";
    public const string MetricSessionStop = "ghostSessionStop";
    public const string MetricSessionFail = "ghostSessionFail";
    public const string MetricUserProvisioned = "ghostUserProvisioned";
    public const string MetricIpcSent = "ghostIpcSent";
    public const string MetricIpcReceived = "ghostIpcReceived";

    public static void RecordSessionStart() => PerfTrace.Count(MetricSessionStart);
    public static void RecordSessionStop() => PerfTrace.Count(MetricSessionStop);
    public static void RecordSessionFail() => PerfTrace.Count(MetricSessionFail);
    public static void RecordUserProvisioned() => PerfTrace.Count(MetricUserProvisioned);
    public static void RecordIpcSent() => PerfTrace.Count(MetricIpcSent);
    public static void RecordIpcReceived() => PerfTrace.Count(MetricIpcReceived);
}
