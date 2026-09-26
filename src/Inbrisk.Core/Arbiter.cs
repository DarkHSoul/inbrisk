namespace Inbrisk.Core;

/// <summary>
/// Type of desktop access lease requested by a task or AI client.
/// </summary>
public enum LeaseKind
{
    /// <summary>Non-exclusive read-only desktop inspection (observe, inspect, screenshot, find, read state). Multiple tasks can hold simultaneously.</summary>
    ReadOnly,

    /// <summary>Exclusive physical input simulation (click, type, drag, scroll, focus manipulation). Exactly one task holds this across the desktop.</summary>
    PhysicalInput,

    /// <summary>Targeted background window input (specialist app adapter, direct messaging) scoped to a specific HWND.</summary>
    WindowInput,
}

/// <summary>
/// Snapshot of an active or historical input lease.
/// </summary>
public sealed record InputLeaseInfo(
    string LeaseId,
    string OwnerId,
    LeaseKind Kind,
    string Description,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    long? TargetHwnd = null,
    bool IsActive = true);

/// <summary>
/// Represents a granted desktop lease. Releasing returns desktop control to other waiting tasks or the user.
/// </summary>
public interface IInputLease : IAsyncDisposable, IDisposable
{
    string LeaseId { get; }
    string OwnerId { get; }
    LeaseKind Kind { get; }
    string Description { get; }
    DateTimeOffset AcquiredAt { get; }
    DateTimeOffset ExpiresAt { get; }
    long? TargetHwnd { get; }
    CancellationToken CancellationToken { get; }
    bool IsActive { get; }
    void Release();
}

/// <summary>
/// Arbiter for desktop concurrency across multiple tasks, AI clients, and human users.
/// Enforces exclusive ownership of physical input (mouse, keyboard, focus)
/// while allowing concurrent read-only observations and fine-grained per-task cancellation.
/// </summary>
public interface IDesktopArbiter
{
    /// <summary>
    /// Asynchronously acquires a desktop lease. Waits up to <paramref name="timeout"/> if an exclusive physical lease is held by another task.
    /// </summary>
    Task<IInputLease> AcquireAsync(
        string ownerId,
        LeaseKind kind,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? leaseDuration = null,
        long? targetHwnd = null,
        CancellationToken ct = default);

    /// <summary>
    /// Synchronously attempts to acquire a lease without waiting.
    /// </summary>
    bool TryAcquire(
        string ownerId,
        LeaseKind kind,
        string description,
        out IInputLease? lease,
        TimeSpan? leaseDuration = null,
        long? targetHwnd = null);

    /// <summary>
    /// Cancels a specific task by owner ID, revoking its active leases and signaling its cancellation token,
    /// without interrupting other independent tasks or shutting down the runtime.
    /// </summary>
    bool CancelTask(string ownerId, string? reason = null);

    /// <summary>
    /// Cancels all currently active tasks and releases all held leases (emergency stop equivalent).
    /// </summary>
    void CancelAll(string? reason = null);

    /// <summary>
    /// Lists all currently active leases.
    /// </summary>
    IReadOnlyList<InputLeaseInfo> GetActiveLeases();

    /// <summary>
    /// Gets the current exclusive physical input owner, if any.
    /// </summary>
    InputLeaseInfo? GetExclusiveOwner();

    /// <summary>Raised when a lease is acquired.</summary>
    event Action<InputLeaseInfo>? LeaseAcquired;

    /// <summary>Raised when a lease is released or expires.</summary>
    event Action<InputLeaseInfo>? LeaseReleased;

    /// <summary>Raised when a task is cancelled.</summary>
    event Action<string, string?>? TaskCancelled;
}
