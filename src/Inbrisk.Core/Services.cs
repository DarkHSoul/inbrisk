namespace Inbrisk.Core;

/// <summary>A captured frame: PNG bytes plus the transform mapping its
/// pixels back to desktop space.</summary>
public sealed record Frame(byte[] PngData, FrameTransform Transform, DateTimeOffset At);

public interface IWindowService
{
    IReadOnlyList<WindowInfo> ListWindows();
    WindowInfo? GetWindow(long hwnd);
    WindowInfo? GetForegroundWindow();
    IReadOnlyList<MonitorInfo> GetMonitors();
    RectPx GetVirtualDesktopBounds();
    bool FocusWindow(long hwnd);
    /// <summary>Graceful close (WM_CLOSE). May surface save prompts.</summary>
    bool CloseWindow(long hwnd);
    /// <summary>Checks if the window is currently blocked by an active modal popup dialog.</summary>
    WindowInfo? GetModalPopup(long hwnd);
    /// <summary>Checks if any modal popup, error dialog, or modern system flyout (e.g. Share) is currently active and blocking desktop interaction.</summary>
    WindowInfo? GetActiveBlockingPopup(long? targetHwnd = null) => null;
    /// <summary>Is the window enabled (not disabled by a modal dialog or system state)?</summary>
    bool IsWindowEnabled(long hwnd);
    /// <summary>Finds all visible dialogs/popups across the desktop (error boxes, modals, confirmations).</summary>
    IReadOnlyList<WindowInfo> FindSystemDialogs();
    /// <summary>Determines if a window belongs to the host environment, terminal, IDE, or critical session.</summary>
    bool IsWindowProtected(long hwnd, out string? reason);
}

public interface IIntegrityService
{
    /// <summary>Is the process owning <paramref name="hwnd"/> above our integrity level?</summary>
    IntegrityRelation CheckTarget(long hwnd);
    bool IsSecureDesktopActive();
}

public interface ICaptureService
{
    /// <summary>Capture a desktop-space rect. Returns PNG + transform.</summary>
    Frame Capture(RectPx region, int maxImageWidth = 1600);
    Frame CaptureWindow(long hwnd, int maxImageWidth = 1600);
    /// <summary>Capture a desktop-space rect as raw BGRA pixels (no PNG encode).</summary>
    RawFrame CaptureRaw(RectPx region);
    /// <summary>Coarse fraction (0..1) of changed pixels between two captures of the same rect.</summary>
    double DiffFraction(RectPx region, int sampleScale = 8);
    /// <summary>Small downsampled byte snapshot of a region for diff loops.</summary>
    byte[] Sample(RectPx region, int scale = 8);
    /// <summary>Open a continuous capture stream. Backend chosen by the platform.</summary>
    ICaptureSession CreateSession(CaptureTarget target);
}

/// <summary>
/// A perception/control backend. UIA is the M1 implementation; CDP/Vision/OCR
/// implement the same contract later.
/// </summary>
public interface IElementBackend
{
    BackendId Id { get; }
    IReadOnlyList<UiElement> Inspect(long hwnd, InspectOptions options, CancellationToken ct = default);
    IReadOnlyList<UiElement> Find(FindSpec spec, CancellationToken ct = default);
    /// <summary>Try a native semantic action. Returns null if the backend
    /// cannot perform this kind of action on this element.</summary>
    ActionResult? PerformNative(UiElement element, ActionIntent intent, CancellationToken ct = default);
    /// <summary>Re-resolve a stale element via its recipe.</summary>
    UiElement? ReResolve(ElementHandle handle, CancellationToken ct = default);
    /// <summary>Is the underlying backend object still usable?</summary>
    bool IsAlive(UiElement element);
}

public interface IEventSource : IDisposable
{
    event Action<ObservedEvent>? Event;
    void Start();
}

public interface IInputService
{
    void MoveMouse(int x, int y);
    void Click(int x, int y, MouseButton button = MouseButton.Left, int count = 1);
    void Drag(int fromX, int fromY, int toX, int toY, int durationMs = 300,
        CancellationToken ct = default);
    void Scroll(int x, int y, int wheelDelta);
    void KeyPress(KeyCode key);
    void KeyDown(KeyCode key);
    void KeyUp(KeyCode key);
    void Hotkey(IReadOnlyList<KeyCode> modifiers, KeyCode key);
    void TypeText(string text);
    (int X, int Y) CursorPosition();
    /// <summary>Release every held key/button — cancel/cleanup path.
    /// Idempotent; must never leave stuck modifiers.</summary>
    void ReleaseAll();
    /// <summary>Unconditional release sweep — sends UP for every modifier
    /// and mouse button, tracked or not. Recovery for input held by a dead
    /// injector. Default falls back to ReleaseAll for other backends.</summary>
    void SweepAll() => ReleaseAll();
    /// <summary>Panic path: reject NEW input, let open transactions unwind
    /// within a bounded drain window, force-release all owned input, then
    /// reconcile against actual keyboard/mouse state.</summary>
    void EmergencyCleanup(int drainMs = 150) => ReleaseAll();
    /// <summary>Lift the emergency input block applied by EmergencyCleanup —
    /// called only by the local resume path.</summary>
    void ClearEmergency() { }
    /// <summary>Held-input + diagnostics snapshot for recovery reporting
    /// (computer_reset_input before/after state).</summary>
    IReadOnlyDictionary<string, object?> DiagnoseInput() =>
        new Dictionary<string, object?>();
}

public enum MouseButton { Left, Right, Middle }

/// <summary>Virtual keys supported by the input layer.</summary>
public enum KeyCode
{
    Enter, Escape, Tab, Space, Backspace, Delete,
    Left, Right, Up, Down, Home, End, PageUp, PageDown,
    Ctrl, Shift, Alt, Win, Pause,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    A, B, C, D, E, F, G, H, I, J, K, L, M,
    N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
}

public interface IClipboardService
{
    string? ReadText();
    void WriteText(string text);
    void Clear();
}
