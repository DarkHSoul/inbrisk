namespace Inbrisk.Core;

public sealed record ActionTelemetry(
    int Sequence,
    DateTimeOffset At,
    ActionKind Intent,
    string TargetDescription,
    string? ElementId,
    BackendId? BackendUsed,
    string Method,
    IReadOnlyList<Attempt> Attempts,
    VerifyResult Verification,
    bool Success,
    ErrorCode Error,
    string? ErrorMessage,
    TimeSpan Duration,
    /// <summary>Correlation ids — join action → step → run for debugging
    /// concurrent agent runs.</summary>
    string? RunId = null,
    int? StepId = null,
    string? ActionId = null);

/// <summary>Pipeline-side observability record (capture/diff/stability/OCR/vision timings).</summary>
public sealed record PipelineTelemetry(
    DateTimeOffset At,
    string Category,            // "capture" | "diff" | "stability" | "ocr" | "vision"
    string? Target = null,
    string? Backend = null,
    double? CaptureFps = null,
    double? SampledFps = null,
    long? FramesReceived = null,
    long? FramesDropped = null,
    int? FrameWidth = null,
    int? FrameHeight = null,
    double? ChangedFraction = null,
    int? ChangedRegions = null,
    double? StableForMs = null,
    double? DurationMs = null,
    int? ElementCount = null,
    RectPx? Crop = null);

public interface ITelemetrySink
{
    void Emit(ActionTelemetry record);
    void EmitPipeline(PipelineTelemetry record) { }
}
