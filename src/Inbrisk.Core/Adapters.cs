namespace Inbrisk.Core;

/// <summary>
/// Result of an action executed directly through an application adapter.
/// </summary>
public sealed record AdapterResult(
    bool Success,
    string Method,
    string? Detail = null,
    IReadOnlyDictionary<string, object?>? Data = null,
    ErrorCode? Error = null);

/// <summary>
/// Advertised capability of an application adapter.
/// </summary>
public sealed record AdapterCapability(
    string AdapterId,
    string DisplayName,
    IReadOnlyList<string> SupportedActions,
    bool IsActive);

/// <summary>
/// Specialist application adapter providing direct semantic interaction
/// (REST, WebSocket, CLI, COM, Media, IPC) with known applications when available.
/// Unifies desktop automation across UIA, Win32, and specialist app APIs.
/// </summary>
public interface IApplicationAdapter
{
    /// <summary>Unique identifier of the adapter, e.g. "media", "testapp", "process_cli".</summary>
    string AdapterId { get; }

    /// <summary>Human-readable display name, e.g. "Windows Media / Spotify Controller".</summary>
    string DisplayName { get; }

    /// <summary>List of canonical actions supported by this adapter.</summary>
    IReadOnlyList<string> SupportedActions { get; }

    /// <summary>Tests whether this adapter applies to a given application process or window.</summary>
    bool IsApplicable(string? processName, long? hwnd);

    /// <summary>Checks whether this adapter can execute a specific action with the given target/args.</summary>
    bool CanHandle(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null);

    /// <summary>Executes the action directly through the adapter's specialist channel.</summary>
    Task<AdapterResult> ExecuteAsync(string action, TargetRef? target = null, IReadOnlyDictionary<string, object?>? args = null, CancellationToken ct = default);
}
