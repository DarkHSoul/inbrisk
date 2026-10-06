namespace Inbrisk.Core;

/// <summary>
/// Result of comparing two consecutive frames: how much changed and where.
/// Regions are in the compared frames' image space; DesktopRegions maps them
/// through the frame's transform.
/// </summary>
public sealed record FrameDiff(
    long FromSequence,
    long ToSequence,
    DateTimeOffset At,
    double ChangedFraction,
    IReadOnlyList<RectPx> ImageRegions,
    FrameTransform Transform)
{
    public bool IsFirst => FromSequence < 0;
    public bool HasChange => ChangedFraction > 0;
    public IEnumerable<RectPx> DesktopRegions()
    {
        foreach (var r in ImageRegions)
        {
            var (x, y) = Transform.ImageToDesktop(r.X, r.Y);
            var (rr, bb) = Transform.ImageToDesktop(r.Right, r.Bottom);
            yield return new RectPx(x, y, rr - x, bb - y);
        }
    }
}

public enum StabilityState { Unknown, Stable, Changing }

public sealed record StabilityInfo(
    StabilityState State,
    DateTimeOffset? LastChangeAt,
    TimeSpan StableFor,
    double LastChangedFraction);

// ---------- perception backends ----------

/// <summary>An OCR text hit in desktop space (mapped before it reaches a scene).</summary>
public sealed record TextSpan(
    string Text,
    RectPx Bounds,
    double? Confidence,
    BackendId Source = BackendId.Ocr);

/// <summary>
/// One OCR pass: the words found (desktop-space bounds), the engine that ran,
/// the image size actually fed to the engine (post-downscale), wall time, and
/// an optional warning (unsupported-language fallback, timeout, engine
/// failure). WinRT OcrWord exposes no per-word confidence —
/// TextSpan.Confidence stays null.
/// </summary>
public sealed record OcrResult(
    IReadOnlyList<TextSpan> Words,
    double ElapsedMs,
    string Engine,
    string Language,
    int ImageWidth,
    int ImageHeight,
    string? RequestedLanguage = null,
    string? Warning = null)
{
    public bool TimedOut => Warning?.Contains("timed out") == true;
}

/// <summary>
/// OCR backend contract. Implementations take raw BGRA frames (or capture a
/// window/region themselves when wired with capture services) and return
/// words already mapped to desktop space. Platform-neutral so the runtime
/// layer can consume it without referencing a WinRT assembly.
/// </summary>
public interface IOcrService
{
    bool Available { get; }
    /// <summary>BCP-47 tag of the default (profile-language) engine.</summary>
    string Language { get; }
    /// <summary>Recognize text in an already-captured frame.</summary>
    OcrResult Recognize(RawFrame frame, string? language = null, TimeSpan? timeout = null);
    /// <summary>Capture the window's visible bounds and recognize. Throws
    /// NotFound when the window is gone, CaptureFailed when minimized/off-
    /// desktop, Unsupported when no capture services are wired.</summary>
    OcrResult RecognizeWindow(long hwnd, string? language = null, TimeSpan? timeout = null);
    /// <summary>Capture a desktop-space rect and recognize.</summary>
    OcrResult RecognizeRegion(RectPx region, string? language = null, TimeSpan? timeout = null);
}

/// <summary>
/// What a vision/grounding provider sees. Elements are produced in the frame's
/// image space and carry confidence; conversion to desktop space happens only
/// through FrameTransform/CoordinateMapper during scene merge or grounding.
/// </summary>
public sealed record VisionQuery(
    string? Describe = null,
    string? FindTarget = null,
    IReadOnlyList<RectPx>? HintImageRects = null);

public sealed record VisionElement(
    string? Label,
    string? Description,
    RectPx ImageBounds,
    double? Confidence,
    string? RoleHint = null);

public sealed record VisionResult(
    IReadOnlyList<VisionElement> Elements,
    string? Description,
    TimeSpan Duration,
    string BackendName);

/// <summary>Pluggable vision backend. M2 ships a deterministic test backend;
/// real providers (GPT/Claude/Gemini/local VLMs) plug in later.</summary>
public interface IVisionBackend
{
    string Name { get; }
    /// <summary>Analyze a frame. Returned elements MUST be in frame image space.</summary>
    VisionResult Analyze(RawFrame frame, VisionQuery query);
}

/// <summary>
/// Grounding = "find the point to act on". Same contract as IVisionBackend but
/// returns candidate points/regions for a target description.
/// </summary>
public interface IGroundingBackend
{
    string Name { get; }
    /// <summary>Locate a described target inside a frame. Image-space rects.</summary>
    VisionResult Locate(RawFrame frame, string targetDescription);
}
