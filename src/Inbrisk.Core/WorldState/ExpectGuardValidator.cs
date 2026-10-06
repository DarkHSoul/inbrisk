namespace Inbrisk.Core.WorldState;

/// <summary>
/// Target guard types supported for pre-mutation invariant validation.
/// </summary>
public enum GuardKind
{
    Exists,
    NotExists,
    Focused
}

/// <summary>
/// Pre-mutation state expectation asserting that world state has not drifted from a known version.
/// </summary>
public sealed record StateExpectation(long? StateVersion = null)
{
    public static StateExpectation Version(long stateVersion) => new(stateVersion);
}

/// <summary>
/// Structural or focus invariant that must hold immediately prior to mutation execution.
/// </summary>
public sealed record StateGuard(
    GuardKind Kind,
    string? ElementId = null,
    long? Hwnd = null,
    FindSpec? Selector = null,
    string? TargetName = null)
{
    public static StateGuard Exists(string elementId) => new(GuardKind.Exists, ElementId: elementId);
    public static StateGuard Exists(FindSpec selector) => new(GuardKind.Exists, Selector: selector);
    public static StateGuard ExistsWindow(long hwnd) => new(GuardKind.Exists, Hwnd: hwnd);

    public static StateGuard NotExists(string elementId) => new(GuardKind.NotExists, ElementId: elementId);
    public static StateGuard NotExists(FindSpec selector) => new(GuardKind.NotExists, Selector: selector);
    public static StateGuard NotExistsWindow(long hwnd) => new(GuardKind.NotExists, Hwnd: hwnd);

    public static StateGuard FocusedWindow(long hwnd) => new(GuardKind.Focused, Hwnd: hwnd);
    public static StateGuard FocusedElement(string elementId) => new(GuardKind.Focused, ElementId: elementId);

    public override string ToString() => Kind switch
    {
        GuardKind.Exists when ElementId != null => $"guard:exists(element:{ElementId})",
        GuardKind.Exists when Selector != null => $"guard:exists(selector:{Selector.Name ?? Selector.Role?.ToString()})",
        GuardKind.Exists when Hwnd != null => $"guard:exists(hwnd:0x{Hwnd.Value:X})",
        GuardKind.NotExists when ElementId != null => $"guard:notExists(element:{ElementId})",
        GuardKind.NotExists when Selector != null => $"guard:notExists(selector:{Selector.Name ?? Selector.Role?.ToString()})",
        GuardKind.NotExists when Hwnd != null => $"guard:notExists(hwnd:0x{Hwnd.Value:X})",
        GuardKind.Focused when Hwnd != null => $"guard:focused(hwnd:0x{Hwnd.Value:X})",
        GuardKind.Focused when ElementId != null => $"guard:focused(element:{ElementId})",
        _ => $"guard:{Kind}"
    };
}

/// <summary>
/// Read-only snapshot or live query provider for checking world state invariants.
/// </summary>
public interface IWorldStateReader
{
    long StateVersion { get; }
    bool WindowExists(long hwnd);
    bool ElementExists(string elementId);
    bool ElementExists(FindSpec selector);
    bool IsWindowFocused(long hwnd);
    bool IsElementFocused(string elementId);
}

/// <summary>
/// In-memory implementation of IWorldStateReader for testing and isolated sessions.
/// </summary>
public sealed class InMemoryWorldStateReader : IWorldStateReader
{
    private readonly Func<long>? _versionGetter;
    private long _version;

    public HashSet<long> Windows { get; } = new();
    public HashSet<string> Elements { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<FindSpec, bool>? SelectorMatcher { get; set; }
    public long? FocusedHwnd { get; set; }
    public string? FocusedElementId { get; set; }

    public long StateVersion
    {
        get => _versionGetter?.Invoke() ?? Volatile.Read(ref _version);
        set => Volatile.Write(ref _version, value);
    }

    public InMemoryWorldStateReader(long initialVersion = 1, Func<long>? versionGetter = null)
    {
        _version = initialVersion;
        _versionGetter = versionGetter;
    }

    public bool WindowExists(long hwnd) => Windows.Contains(hwnd);
    public bool ElementExists(string elementId) => Elements.Contains(elementId);
    public bool ElementExists(FindSpec selector) => SelectorMatcher?.Invoke(selector) ?? false;
    public bool IsWindowFocused(long hwnd) => FocusedHwnd == hwnd;
    public bool IsElementFocused(string elementId) =>
        string.Equals(FocusedElementId, elementId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pre-execution validation outcome.
/// </summary>
public sealed record ValidationResult(
    bool IsAllowed,
    long CurrentStateVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    StateGuard? FailedGuard = null)
{
    public static ValidationResult Allowed(long currentVersion) =>
        new(true, currentVersion);

    public static ValidationResult Conflict(long currentVersion, long expectedVersion) =>
        new(false, currentVersion, "Conflict", $"State version conflict: expected version {expectedVersion} but live version is {currentVersion}");

    public static ValidationResult GuardFailed(long currentVersion, StateGuard failedGuard, string reason) =>
        new(false, currentVersion, "GuardFailed", reason, failedGuard);
}

/// <summary>
/// Result of executing a single action inside the serialized mutation lane.
/// </summary>
public sealed record MutationExecutionResult<T>(
    bool Success,
    T? Result,
    long StateVersion,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    StateGuard? FailedGuard = null)
{
    public static MutationExecutionResult<T> Accepted(T result, long version) =>
        new(true, result, version);

    public static MutationExecutionResult<T> Rejected(long version, string code, string message, StateGuard? failedGuard = null) =>
        new(false, default, version, code, message, failedGuard);
}

/// <summary>
/// Result of executing a composite batch of steps inside the serialized mutation lane.
/// </summary>
public sealed record BatchExecutionResult(
    bool Success,
    int StepsExecuted,
    long StateVersion,
    IReadOnlyList<object?> StepResults,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    StateGuard? FailedGuard = null)
{
    public static BatchExecutionResult Accepted(int stepsExecuted, long version, IReadOnlyList<object?> results) =>
        new(true, stepsExecuted, version, results);

    public static BatchExecutionResult Rejected(long version, string code, string message, StateGuard? failedGuard = null) =>
        new(false, 0, version, Array.Empty<object?>(), code, message, failedGuard);
}

/// <summary>
/// Enforces TOCTOU-safe mutation boundaries with stateVersion expectations and structural guards.
/// TOCTOU HARD RULE: The expect check and guard evaluations MUST occur inside the serialized
/// mutation boundary immediately before executing mutating operations.
/// </summary>
public sealed class ExpectGuardValidator
{
    private readonly IWorldStateReader _stateReader;
    private readonly StateVersionTracker? _versionTracker;
    private readonly object _mutationLock = new();
    private readonly SemaphoreSlim _asyncMutationLock = new(1, 1);

    public ExpectGuardValidator(IWorldStateReader stateReader, StateVersionTracker? versionTracker = null)
    {
        _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        _versionTracker = versionTracker;
    }

    /// <summary>
    /// Evaluates expect and guards outside the lane (preflight check only).
    /// Note: Callers MUST still execute within ExecuteInMutationLane to prevent TOCTOU races.
    /// </summary>
    public ValidationResult ValidatePreflight(StateExpectation? expect, IEnumerable<StateGuard>? guards = null)
    {
        var currentVer = _stateReader.StateVersion;

        if (expect?.StateVersion is { } expectedVer && expectedVer != currentVer)
        {
            return ValidationResult.Conflict(currentVer, expectedVer);
        }

        if (guards != null)
        {
            foreach (var guard in guards)
            {
                if (!EvaluateGuard(guard, out var reason))
                {
                    return ValidationResult.GuardFailed(currentVer, guard, reason);
                }
            }
        }

        return ValidationResult.Allowed(currentVer);
    }

    /// <summary>
    /// Executes a mutating action inside the serialized mutation lane.
    /// Strictly re-checks expect and guards INSIDE the locked boundary before calling mutationAction.
    /// </summary>
    public MutationExecutionResult<T> ExecuteInMutationLane<T>(
        StateExpectation? expect,
        IEnumerable<StateGuard>? guards,
        Func<T> mutationAction,
        string? mutationKind = null)
    {
        lock (_mutationLock)
        {
            // TOCTOU HARD RULE: Re-verify stateVersion immediately inside the serialized boundary
            var liveVer = _stateReader.StateVersion;
            if (expect?.StateVersion is { } expectedVer && expectedVer != liveVer)
            {
                return MutationExecutionResult<T>.Rejected(
                    liveVer,
                    "Conflict",
                    $"State version conflict: expected version {expectedVer} but live version is {liveVer}");
            }

            // Re-evaluate guards inside the serialized boundary
            if (guards != null)
            {
                foreach (var guard in guards)
                {
                    if (!EvaluateGuard(guard, out var reason))
                    {
                        return MutationExecutionResult<T>.Rejected(
                            liveVer,
                            "GuardFailed",
                            reason,
                            failedGuard: guard);
                    }
                }
            }

            // Validated: Execute mutation action
            var result = mutationAction();

            // Bump version tracker if registered
            long finalVersion = liveVer;
            if (_versionTracker != null)
            {
                finalVersion = _versionTracker.RecordMutation(mutationKind ?? "lane_action");
            }

            return MutationExecutionResult<T>.Accepted(result, finalVersion);
        }
    }

    /// <summary>
    /// Asynchronous execution inside the serialized mutation lane with TOCTOU re-check.
    /// </summary>
    public async Task<MutationExecutionResult<T>> ExecuteInMutationLaneAsync<T>(
        StateExpectation? expect,
        IEnumerable<StateGuard>? guards,
        Func<Task<T>> mutationActionAsync,
        string? mutationKind = null,
        CancellationToken ct = default)
    {
        await _asyncMutationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // TOCTOU HARD RULE: Re-verify stateVersion inside the boundary
            var liveVer = _stateReader.StateVersion;
            if (expect?.StateVersion is { } expectedVer && expectedVer != liveVer)
            {
                return MutationExecutionResult<T>.Rejected(
                    liveVer,
                    "Conflict",
                    $"State version conflict: expected version {expectedVer} but live version is {liveVer}");
            }

            if (guards != null)
            {
                foreach (var guard in guards)
                {
                    if (!EvaluateGuard(guard, out var reason))
                    {
                        return MutationExecutionResult<T>.Rejected(
                            liveVer,
                            "GuardFailed",
                            reason,
                            failedGuard: guard);
                    }
                }
            }

            var result = await mutationActionAsync().ConfigureAwait(false);

            long finalVersion = liveVer;
            if (_versionTracker != null)
            {
                finalVersion = _versionTracker.RecordMutation(mutationKind ?? "lane_action_async");
            }

            return MutationExecutionResult<T>.Accepted(result, finalVersion);
        }
        finally
        {
            _asyncMutationLock.Release();
        }
    }

    /// <summary>
    /// Composes expect and guards with multi-step batch execution.
    /// If expect or any guard fails, blocks the batch without running any mutating steps.
    /// </summary>
    public BatchExecutionResult ExecuteBatchInMutationLane(
        StateExpectation? expect,
        IEnumerable<StateGuard>? guards,
        IReadOnlyList<Func<object?>> steps,
        string? batchDescription = null)
    {
        lock (_mutationLock)
        {
            var liveVer = _stateReader.StateVersion;
            if (expect?.StateVersion is { } expectedVer && expectedVer != liveVer)
            {
                return BatchExecutionResult.Rejected(
                    liveVer,
                    "Conflict",
                    $"State version conflict: expected version {expectedVer} but live version is {liveVer}");
            }

            if (guards != null)
            {
                foreach (var guard in guards)
                {
                    if (!EvaluateGuard(guard, out var reason))
                    {
                        return BatchExecutionResult.Rejected(
                            liveVer,
                            "GuardFailed",
                            reason,
                            failedGuard: guard);
                    }
                }
            }

            var results = new List<object?>();
            for (int i = 0; i < steps.Count; i++)
            {
                var stepOutput = steps[i]();
                results.Add(stepOutput);
            }

            long finalVersion = liveVer;
            if (_versionTracker != null)
            {
                finalVersion = _versionTracker.RecordMutation(batchDescription ?? "batch_execution");
            }

            return BatchExecutionResult.Accepted(steps.Count, finalVersion, results);
        }
    }

    private bool EvaluateGuard(StateGuard guard, out string failureReason)
    {
        failureReason = string.Empty;

        switch (guard.Kind)
        {
            case GuardKind.Exists:
                if (guard.ElementId != null)
                {
                    if (!_stateReader.ElementExists(guard.ElementId))
                    {
                        failureReason = $"Guard failed: Element '{guard.ElementId}' does not exist.";
                        return false;
                    }
                }
                else if (guard.Hwnd != null)
                {
                    if (!_stateReader.WindowExists(guard.Hwnd.Value))
                    {
                        failureReason = $"Guard failed: Window 0x{guard.Hwnd.Value:X} does not exist.";
                        return false;
                    }
                }
                else if (guard.Selector != null)
                {
                    if (!_stateReader.ElementExists(guard.Selector))
                    {
                        failureReason = $"Guard failed: Element matching selector does not exist.";
                        return false;
                    }
                }
                return true;

            case GuardKind.NotExists:
                if (guard.ElementId != null)
                {
                    if (_stateReader.ElementExists(guard.ElementId))
                    {
                        failureReason = $"Guard failed: Element '{guard.ElementId}' unexpectedly exists.";
                        return false;
                    }
                }
                else if (guard.Hwnd != null)
                {
                    if (_stateReader.WindowExists(guard.Hwnd.Value))
                    {
                        failureReason = $"Guard failed: Window 0x{guard.Hwnd.Value:X} unexpectedly exists.";
                        return false;
                    }
                }
                else if (guard.Selector != null)
                {
                    if (_stateReader.ElementExists(guard.Selector))
                    {
                        failureReason = $"Guard failed: Element matching selector unexpectedly exists.";
                        return false;
                    }
                }
                return true;

            case GuardKind.Focused:
                if (guard.Hwnd != null)
                {
                    if (!_stateReader.IsWindowFocused(guard.Hwnd.Value))
                    {
                        failureReason = $"Guard failed: Window 0x{guard.Hwnd.Value:X} is not the focused foreground window.";
                        return false;
                    }
                }
                else if (guard.ElementId != null)
                {
                    if (!_stateReader.IsElementFocused(guard.ElementId))
                    {
                        failureReason = $"Guard failed: Element '{guard.ElementId}' is not the focused element.";
                        return false;
                    }
                }
                return true;

            default:
                failureReason = $"Unknown guard kind: {guard.Kind}";
                return false;
        }
    }
}
