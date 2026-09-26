using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Element backend for Vision/OCR-produced elements. They carry no semantic
/// actions — PerformNative returns null so the Executor falls through to
/// coordinate input (SendInput) at the element's desktop-space bounds.
/// Re-resolution is not possible from a recipe; such elements are volatile
/// (re-analyze the frame to get fresh ones).
/// </summary>
public sealed class VisionElementBackend : IElementBackend
{
    public VisionElementBackend(BackendId id) => Id = id;

    public BackendId Id { get; }

    public IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default) =>
        Array.Empty<UiElement>();

    public IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default) => Array.Empty<UiElement>();

    public ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default) => null;

    public UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default) => null;

    /// <summary>Volatile: alive while registered (snapshot-true).</summary>
    public bool IsAlive(UiElement element) => true;
}
