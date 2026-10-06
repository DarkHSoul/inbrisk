namespace Inbrisk.Core;

// ============================================================================
// Inbrisk Computer Action Protocol — the canonical, provider-independent
// contract between an arbitrary AI model and the Windows runtime.
// A model says "click(elementId)"; Inbrisk decides UIA Invoke vs SendInput
// vs a future backend. Provider SDKs never leak into Core.
// ============================================================================

public enum AgentActionKind
{
    /// <summary>Request a fresh observation (e.g. after a settle wait).</summary>
    Observe,
    Click, RightClick, DoubleClick, Drag, Scroll, Hover,
    Invoke, SetValue, Toggle, Select, ScrollIntoView,
    Type, Key, Hotkey,
    FocusWindow, FocusElement,
    Wait, WaitFor, WaitForChange, WaitForStable,
    Finish,
}

/// <summary>A point in the image space of a specific captured frame.
/// FrameId + ObservationId bind the coordinate to the exact observation it
/// was derived from — the resolver rejects points from any other frame or
/// observation, and re-checks window identity/geometry/age at act time.</summary>
public sealed record ImagePoint(int X, int Y, long FrameId, long ObservationId = 0);

/// <summary>
/// One canonical action from the model. Exactly the fields relevant to Kind
/// are set; everything is plain data so it round-trips through JSON.
/// </summary>
public sealed record AgentAction(
    AgentActionKind Kind,
    string? ElementId = null,
    ImagePoint? Point = null,
    ImagePoint? To = null,
    long? Hwnd = null,
    int? Pid = null,
    string? Text = null,
    string? Key = null,
    IReadOnlyList<string>? Modifiers = null,
    int? Delta = null,
    int? Ms = null,
    int? Count = null,
    string? Query = null,
    string? Result = null,
    string? Note = null,
    /// <summary>Backend element id — scope a wait/find to that element's
    /// subtree (within:$element on a hwnd-less CEF/ Electron node).</summary>
    string? ScopeElementId = null,
    string? ExpectedState = null,
    string? ExpectedValue = null,
    bool Gone = false,
    bool StopOnUnexpectedDialog = true)
{
    public string Summary() => Kind switch
    {
        AgentActionKind.Click or AgentActionKind.RightClick or AgentActionKind.DoubleClick
            or AgentActionKind.Invoke or AgentActionKind.Toggle or AgentActionKind.Select
            or AgentActionKind.ScrollIntoView
            => $"{Kind.ToString().ToLower()}({ElementId ?? (Point != null ? $"img:{Point.X},{Point.Y}@{Point.FrameId}" : "?")})",
        AgentActionKind.SetValue or AgentActionKind.Type => $"{Kind.ToString().ToLower()}({ElementId ?? "focus"}, \"{Short(Text)}\")",
        AgentActionKind.Key => $"key({Key}{Mods()})",
        AgentActionKind.Hotkey => $"hotkey({Key}{Mods()})",
        AgentActionKind.Scroll => $"scroll({Delta ?? -120}@{ElementId ?? "pt"})",
        AgentActionKind.Drag => $"drag({ElementId ?? "pt"} -> {(To != null ? $"img:{To.X},{To.Y}" : "?")})",
        AgentActionKind.FocusWindow => $"focus_window(0x{(Hwnd ?? 0):X})",
        AgentActionKind.FocusElement => $"focus({ElementId ?? "?"})",
        AgentActionKind.Hover => $"hover({ElementId ?? (Point != null ? $"img:{Point.X},{Point.Y}" : "?")})",
        AgentActionKind.Wait => $"wait({Ms ?? 0}ms)",
        AgentActionKind.WaitFor => $"wait_for(\"{Query}\")",
        AgentActionKind.WaitForChange => $"wait_for_change({Ms ?? 0}ms)",
        AgentActionKind.WaitForStable => $"wait_for_stable({Ms ?? 0}ms)",
        AgentActionKind.Finish => $"finish(\"{Result}\")",
        AgentActionKind.Observe => "observe()",
        _ => Kind.ToString().ToLower(),
    };
    private string Mods() => Modifiers is { Count: > 0 } ? "+" + string.Join("+", Modifiers) : "";
    // summaries feed logs, loop keys and MCP responses — cap long payloads
    private static string? Short(string? s) => s != null && s.Length > 96
        ? s[..64] + "…" + s[^28..] : s;
}

// --------------------------- canonical observation ---------------------------

/// <summary>Which visual attachment the runtime put on the observation.</summary>
public enum VisualAttach
{
    /// <summary>Semantics are enough — no pixels sent to the model.</summary>
    None,
    /// <summary>Crops of the regions that changed since last observation.</summary>
    ChangedRegions,
    /// <summary>A crop around the element/region of interest.</summary>
    Crop,
    /// <summary>Full frame of the active window.</summary>
    WindowFrame,
    /// <summary>Full desktop frame.</summary>
    FullFrame,
}

/// <summary>Runtime-side policy for attaching pixels to observations.</summary>
public enum VisualAttachPolicy { Auto, Never, Always }

/// <summary>One condensed element as the model sees it.</summary>
public sealed record ObsElement(
    string Id,
    string Role,
    string? Name,
    string? Value,
    RectPx Bounds,
    IReadOnlyList<string> Actions,
    string Source,
    string? State,
    /// <summary>Cross-observation identity for delta matching (not actionable).</summary>
    string StableKey);

public sealed record ObsWindow(
    long Hwnd, string Title, string Process, RectPx Bounds, bool Active);

/// <summary>Metadata of a frame attached to an observation.</summary>
public sealed record ObsFrameRef(
    long FrameId, int Width, int Height, RectPx SourceRect,
    VisualAttach Attach, byte[]? Png,
    /// <summary>Window the frame was captured against (for geometry guards).</summary>
    long? Hwnd = null,
    /// <summary>Capture timestamp — frame age is a validity condition.</summary>
    DateTimeOffset At = default,
    RectPx? WindowBounds = null);

/// <summary>What changed between the previous observation and this one.</summary>
public sealed record ObsDelta(
    IReadOnlyList<ObsElement> Added,
    IReadOnlyList<ObsElement> Removed,
    IReadOnlyList<ObsElement> Changed,
    IReadOnlyList<RectPx> ChangedRegions,
    IReadOnlyList<string> Notes,
    IReadOnlyList<ObsElement>? Offscreen = null,
    IReadOnlyList<ObsElement>? PrunedByBudget = null)
{
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0
        && Changed.Count == 0 && ChangedRegions.Count == 0 && Notes.Count == 0
        && (Offscreen?.Count ?? 0) == 0 && (PrunedByBudget?.Count ?? 0) == 0;
}

/// <summary>Why an action counts as verified — the auditable evidence.</summary>
public sealed record VerifyEvidence(
    string Method,          // "ValueReadback" | "PropertyChanged" | "ForegroundWindow" | "SemanticEvent" | "ElementGone" | "WaitCondition"
    string? Expected = null,
    string? Actual = null,
    string? Detail = null)
{
    public override string ToString() =>
        $"{Method}" + (Expected != null ? $" expected={Expected}" : "")
        + (Actual != null ? $" actual={Actual}" : "")
        + (Detail != null ? $" ({Detail})" : "");
}

/// <summary>Condensed result of the previous action, embedded in the next
/// observation so the model sees consequences.</summary>
public sealed record StepOutcome(
    OutcomeKind Kind,
    bool Success,
    string? Method,
    string? Detail,
    int DurationMs,
    VerifyEvidence? Evidence = null,
    TargetDiagnosis? Diagnosis = null,
    /// <summary>What the action changed on the desktop (opened/closed
    /// windows, dialogs, focus, target-element state) — lets the model
    /// skip a mandatory re-observe.</summary>
    ActionDelta? Delta = null);

/// <summary>Size accounting for one observation — adapters use this for
/// provider-specific trimming/billing decisions.</summary>
public sealed record ContextStats(
    int EstimatedCharacters,
    long SerializedBytes,
    int ImageCount,
    long ImagePixels,
    long ImageBytes);

/// <summary>The canonical observation handed to every model adapter.</summary>
public sealed record AgentObservation(
    long ObservationId,
    DateTimeOffset At,
    ObsWindow? ActiveWindow,
    IReadOnlyList<ObsWindow> Windows,
    IReadOnlyList<ObsElement> Elements,
    IReadOnlyList<ObservedEvent> RecentEvents,
    /// <summary>Attached frames — one entry per visual attachment (window
    /// frame, or one per changed-region crop). Empty = semantic-only.</summary>
    IReadOnlyList<ObsFrameRef> Frames,
    IReadOnlyList<RectPx> ChangedRegions,
    string CaptureBackend,
    StepOutcome? PrevAction,
    ObsDelta? Delta,
    ContextStats Stats = default!,
    long? BaseObservationId = null);

// --------------------------- outcomes & history ------------------------------

/// <summary>Machine-readable outcome classification for recovery decisions.</summary>
public enum OutcomeKind
{
    Verified,
    /// <summary>A UI mutation or window event was observed after the action,
    /// but the target element's exact semantic outcome was not independently verified.</summary>
    ObservedChange,
    Unverified, Failed, Malformed,
    TargetNotFound, Stale, StaleFrame,
    TargetElevated, SecureDesktop, WindowLost,
    Timeout, InputRejected, CaptureUnavailable,
    PolicyDenied, ConfirmationDenied, EmergencyStopped,
    /// <summary>The action ran but produced no meaningful delta — loop signal.</summary>
    NoProgress,
    /// <summary>Frame still valid id-wise, but the window moved/resized
    /// since capture — coordinates would land wrong.</summary>
    WindowGeometryChanged,
    /// <summary>Run was cancelled mid-action.</summary>
    Cancelled,
    /// <summary>Multiple strong candidates matched a semantic target —
    /// the resolver refuses to guess; candidates are returned for refine.</summary>
    AmbiguousTarget,
    /// <summary>A friendly app name matched several distinct applications —
    /// launch refuses to pick; candidates are returned for refine.</summary>
    AmbiguousApplication,
    /// <summary>Wait or step interrupted because an unexpected modal or dialog window appeared.</summary>
    InterruptedByDialog,
    /// <summary>Target element not found on the active screen, but known on another screen in memory.</summary>
    TargetOnDifferentScreen,
    /// <summary>Target element exists in accessibility tree but is currently offscreen.</summary>
    TargetOffscreen,
    /// <summary>Desktop input or focus is currently leased exclusively by another task/client.</summary>
    ConcurrencyConflict,
    /// <summary>Execution was paused to yield control to human takeover.</summary>
    PausedForHuman,
}

/// <summary>Machine-readable root-cause diagnosis when target element resolution fails.</summary>
public sealed record TargetDiagnosis(
    string Reason,
    string Summary,
    string? SearchedScope = null,
    int CandidatesMatchedBase = 0,
    string? EliminatedBy = null,
    string? SuggestedAction = null,
    IReadOnlyList<string>? CloseMatches = null)
{
    public override string ToString() =>
        $"{Summary}" + (SuggestedAction != null ? $" — Actionable fix: {SuggestedAction}" : "");
}

/// <summary>One executed step in the agent run (debug history).</summary>
public sealed record StepRecord(
    int Step,
    long ObservationId,
    AgentAction Action,
    StepOutcome Outcome,
    BackendId? Backend,
    string? Method,
    string? DeltaSummary,
    /// <summary>Correlates this step back to its run for telemetry joins.</summary>
    string? RunId = null,
    string? ActionId = null);

/// <summary>What the model sees about past steps — deliberately condensed.</summary>
public sealed record CondensedStep(
    int Step, string Action, string Outcome, string? Detail);

// --------------------------- model adapter -----------------------------------

/// <summary>Everything an adapter gets for one decision. Provider SDKs live
/// only inside adapter implementations; this shape is vendor-neutral.</summary>
public sealed record ModelTurnInput(
    string Goal,
    AgentObservation Observation,
    IReadOnlyList<CondensedStep> History,
    int StepsRemaining,
    TimeSpan TimeRemaining,
    /// <summary>Deterministic summary of compacted older steps.</summary>
    string? HistorySummary = null,
    /// <summary>Run identity — adapters may log it for debugging.</summary>
    string? RunId = null);

/// <summary>One decision from the model: actions to run in order, optional
/// reasoning. A Finish action inside Actions ends the run.</summary>
public sealed record ModelTurn(
    IReadOnlyList<AgentAction> Actions,
    string? Reasoning = null);

/// <summary>The only interface a real provider must implement.</summary>
public interface IModelAdapter
{
    string Name { get; }
    Task<ModelTurn> DecideAsync(ModelTurnInput input, CancellationToken ct);
}

// --------------------------- agent run plumbing ------------------------------

/// <summary>How a failed/unusual outcome should steer the loop.</summary>
public enum RecoveryHint
{
    /// <summary>Continue — the fresh observation already reflects reality.</summary>
    ReObserve,
    /// <summary>Element stale: try re-resolution before the next model call.</summary>
    ReResolve,
    /// <summary>Window gone: surface "window lost" to the model.</summary>
    FindWindow,
    /// <summary>Keep going but tell the model what failed.</summary>
    ReportToModel,
    /// <summary>Non-recoverable — end the run.</summary>
    Abort,
}

public sealed record AgentOptions(
    int MaxSteps = 30,
    int TimeoutMs = 120_000,
    int MaxConsecutiveFailures = 3,
    int MaxIdenticalRepeats = 2,
    /// <summary>Steps with zero meaningful delta before the run is declared
    /// not progressing and terminated.</summary>
    int MaxNoProgressSteps = 4,
    /// <summary>An image point older than this is rejected even if the frame
    /// is technically still current.</summary>
    int MaxFrameAgeMs = 15_000,
    /// <summary>Model-facing history cap — older steps compact into
    /// HistorySummary; debug history is never truncated.</summary>
    int MaxHistoryEntries = 12,
    VisualAttachPolicy ScreenshotPolicy = VisualAttachPolicy.Auto,
    ObservationBudget Budget = default!,
    /// <summary>Approval hook for CONFIRM-classified actions.
    /// Return true to allow. Null → confirmation-required fails the step.</summary>
    Func<AgentAction, string?, bool>? Confirmer = null)
{
    public ObservationBudget BudgetOrDefault => Budget ?? ObservationBudget.Default;
}

/// <summary>Caps on what goes into one observation — keeps model context lean.
/// Character/byte budgets are applied after element pruning.</summary>
public sealed record ObservationBudget(
    int MaxElements = 120,
    int MaxEvents = 20,
    int MaxWindows = 12,
    int MaxNameLength = 80,
    int MaxCropRegions = 3,
    int CropMaxWidth = 800,
    int CropMaxHeight = 600,
    /// <summary>Hard cap on the serialized character count of the semantic
    /// payload (elements+windows+events+delta), approximated by field sizes.</summary>
    int MaxChars = 24_000,
    /// <summary>Cap on total PNG bytes attached per observation.</summary>
    long MaxScreenshotBytes = 1_500_000,
    /// <summary>Cap on total attached pixels (w*h) per observation.</summary>
    long MaxScreenshotPixels = 4_000_000)
{
    public static readonly ObservationBudget Default = new();
}

public enum AgentStatus
{
    /// <summary>Constructed, never run.</summary>
    Created,
    Running, Paused,
    /// <summary>Cancel requested; finishing in-flight cleanup.</summary>
    Cancelling,
    /// <summary>Run reached Finish.</summary>
    Completed,
    Failed, TimedOut,
    /// <summary>Abortive guards: secure desktop, kill switch, elevated target.</summary>
    Blocked,
    Cancelled,
}

public sealed record AgentResult(
    AgentStatus Status,
    int Steps,
    string? FinalResult,
    IReadOnlyList<StepRecord> History,
    string? Reason,
    string? RunId = null);

/// <summary>Pause/resume/cancel surface — the host owns the loop thread.
/// Methods return false when the transition is illegal for the current
/// lifecycle state (e.g. Resume on a Completed run).</summary>
public interface IAgentController
{
    AgentStatus Status { get; }
    bool Pause();
    bool Resume();
    bool Cancel();
}
