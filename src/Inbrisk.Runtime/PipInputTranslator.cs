using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

#region Enums & Models

/// <summary>
/// High-level input event type translated from PiP window interactions.
/// </summary>
public enum GhostInputType
{
    MouseMove,
    MouseDown,
    MouseUp,
    DoubleClick,
    MouseWheel
}

/// <summary>
/// Data Transfer Object representing an input action forwarded from the PiP window to Session 2.
/// </summary>
public sealed record GhostInputPacket
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("pipX")]
    public int PipX { get; init; }

    [JsonPropertyName("pipY")]
    public int PipY { get; init; }

    [JsonPropertyName("button")]
    public string? Button { get; init; }

    [JsonPropertyName("delta")]
    public int Delta { get; init; }

    [JsonPropertyName("timestampTicks")]
    public long TimestampTicks { get; init; } = DateTime.UtcNow.Ticks;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public string ToJson() => JsonSerializer.Serialize(this, DefaultJsonOptions);

    public static GhostInputPacket? FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        return JsonSerializer.Deserialize<GhostInputPacket>(json, DefaultJsonOptions);
    }

    public static GhostInputPacket CreateMouseMove(int targetX, int targetY, int pipX, int pipY) =>
        new()
        {
            Type = nameof(GhostInputType.MouseMove),
            Action = "move",
            X = targetX,
            Y = targetY,
            PipX = pipX,
            PipY = pipY
        };

    public static GhostInputPacket CreateMouseDown(int targetX, int targetY, int pipX, int pipY, MouseButton button) =>
        new()
        {
            Type = nameof(GhostInputType.MouseDown),
            Action = "down",
            X = targetX,
            Y = targetY,
            PipX = pipX,
            PipY = pipY,
            Button = button.ToString()
        };

    public static GhostInputPacket CreateMouseUp(int targetX, int targetY, int pipX, int pipY, MouseButton button) =>
        new()
        {
            Type = nameof(GhostInputType.MouseUp),
            Action = "up",
            X = targetX,
            Y = targetY,
            PipX = pipX,
            PipY = pipY,
            Button = button.ToString()
        };

    public static GhostInputPacket CreateDoubleClick(int targetX, int targetY, int pipX, int pipY, MouseButton button = MouseButton.Left) =>
        new()
        {
            Type = nameof(GhostInputType.DoubleClick),
            Action = "doubleclick",
            X = targetX,
            Y = targetY,
            PipX = pipX,
            PipY = pipY,
            Button = button.ToString()
        };

    public static GhostInputPacket CreateMouseWheel(int targetX, int targetY, int pipX, int pipY, int delta) =>
        new()
        {
            Type = nameof(GhostInputType.MouseWheel),
            Action = "scroll",
            X = targetX,
            Y = targetY,
            PipX = pipX,
            PipY = pipY,
            Delta = delta
        };
}

/// <summary>
/// Dual-mode return type that can be awaited asynchronously or used directly as a synchronous <see cref="GhostInputPacket"/>.
/// </summary>
public readonly struct GhostInputTask : IEquatable<GhostInputTask>
{
    private readonly Task<GhostInputPacket> _task;
    private readonly GhostInputPacket _packet;

    public GhostInputTask(Task<GhostInputPacket> task, GhostInputPacket packet)
    {
        _task = task ?? throw new ArgumentNullException(nameof(task));
        _packet = packet ?? throw new ArgumentNullException(nameof(packet));
    }

    public GhostInputPacket Packet => _packet;
    public Task<GhostInputPacket> AsTask() => _task;

    public TaskAwaiter<GhostInputPacket> GetAwaiter() => _task.GetAwaiter();
    public ConfiguredTaskAwaitable<GhostInputPacket> ConfigureAwait(bool continueOnCapturedContext) =>
        _task.ConfigureAwait(continueOnCapturedContext);

    public static implicit operator GhostInputPacket(GhostInputTask task) => task._packet;
    public static implicit operator Task<GhostInputPacket>(GhostInputTask task) => task._task;

    public bool Equals(GhostInputTask other) => Equals(_task, other._task) && Equals(_packet, other._packet);
    public override bool Equals(object? obj) => obj is GhostInputTask other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_task, _packet);
    public static bool operator ==(GhostInputTask left, GhostInputTask right) => left.Equals(right);
    public static bool operator !=(GhostInputTask left, GhostInputTask right) => !left.Equals(right);
}

#endregion

#region Interface

/// <summary>
/// Contract for translating and dispatching PiP window mouse events to Session 2 desktop coordinates.
/// </summary>
public interface IPipInputTranslator
{
    int PipWidth { get; }
    int PipHeight { get; }
    int TargetWidth { get; }
    int TargetHeight { get; }
    bool IsInteractive { get; set; }

    (int TargetX, int TargetY) ScaleCoordinates(int pipX, int pipY);

    GhostInputTask TranslateMouseMove(int pipX, int pipY);
    GhostInputTask TranslateMouseDown(int pipX, int pipY, MouseButton button = MouseButton.Left);
    GhostInputTask TranslateMouseUp(int pipX, int pipY, MouseButton button = MouseButton.Left);
    GhostInputTask TranslateDoubleClick(int pipX, int pipY, MouseButton button = MouseButton.Left);
    GhostInputTask TranslateMouseWheel(int pipX, int pipY, int delta);

    Task<GhostInputPacket> TranslateMouseMoveAsync(int pipX, int pipY, CancellationToken ct = default);
    Task<GhostInputPacket> TranslateMouseDownAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task<GhostInputPacket> TranslateMouseUpAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task<GhostInputPacket> TranslateDoubleClickAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default);
    Task<GhostInputPacket> TranslateMouseWheelAsync(int pipX, int pipY, int delta, CancellationToken ct = default);
}

#endregion

/// <summary>
/// Picture-in-Picture Input Translator and Mouse Forwarder.
/// <para>
/// When interactive mode is enabled, converts native mouse movements, clicks, double clicks,
/// and wheel scroll operations on the PiP client area to virtual coordinates in Session 2,
/// packs them into <see cref="GhostInputPacket"/> instances, and immediately delivers them over
/// <see cref="IGhostIpcBus"/> to Session 2's <c>GhostWorkerDaemon</c> via the <c>forward_input</c> action.
/// </para>
/// <para>
/// Coordinate Scaling Formula:
/// <c>X_target = (X_pip * W_target) / W_pip</c>
/// <c>Y_target = (Y_pip * H_target) / H_pip</c>
/// </para>
/// </summary>
public class PipInputTranslator : IPipInputTranslator
{
    public const string ForwardInputAction = "forward_input";

    public const int DefaultPipWidth = 320;
    public const int DefaultPipHeight = 180;
    public const int DefaultTargetWidth = 1920;
    public const int DefaultTargetHeight = 1080;

    private readonly IGhostIpcBus? _ipcBus;
    private readonly object _dimLock = new();

    private int _pipWidth;
    private int _pipHeight;
    private int _targetWidth;
    private int _targetHeight;
    private volatile bool _isInteractive = true;

    /// <summary>
    /// Gets or sets whether interactive forwarding is enabled.
    /// When disabled, mouse inputs will not be forwarded to Session 2.
    /// </summary>
    public bool IsInteractive
    {
        get => _isInteractive;
        set => _isInteractive = value;
    }

    /// <summary>
    /// Gets or sets the default timeout for IPC forward requests.
    /// </summary>
    public TimeSpan IpcTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the current PiP client area width in pixels.</summary>
    public int PipWidth
    {
        get { lock (_dimLock) return _pipWidth; }
    }

    /// <summary>Gets the current PiP client area height in pixels.</summary>
    public int PipHeight
    {
        get { lock (_dimLock) return _pipHeight; }
    }

    /// <summary>Gets the target Session 2 virtual desktop width in pixels.</summary>
    public int TargetWidth
    {
        get { lock (_dimLock) return _targetWidth; }
    }

    /// <summary>Gets the target Session 2 virtual desktop height in pixels.</summary>
    public int TargetHeight
    {
        get { lock (_dimLock) return _targetHeight; }
    }

    /// <summary>Gets the underlying IPC bus used for forwarding.</summary>
    public IGhostIpcBus? IpcBus => _ipcBus;

    /// <summary>Raised whenever an input packet is successfully forwarded to Session 2.</summary>
    public event Action<GhostInputPacket>? InputForwarded;

    /// <summary>Raised whenever forwarding an input packet to Session 2 fails.</summary>
    public event Action<GhostInputPacket, Exception>? InputForwardFailed;

    /// <summary>
    /// Initializes a new instance of <see cref="PipInputTranslator"/>.
    /// </summary>
    /// <param name="ipcBus">IPC channel connected to Session 2 worker daemon.</param>
    /// <param name="pipWidth">Initial PiP client area width (default 320).</param>
    /// <param name="pipHeight">Initial PiP client area height (default 180).</param>
    /// <param name="targetWidth">Virtual desktop width in Session 2 (default 1920).</param>
    /// <param name="targetHeight">Virtual desktop height in Session 2 (default 1080).</param>
    public PipInputTranslator(
        IGhostIpcBus? ipcBus = null,
        int pipWidth = DefaultPipWidth,
        int pipHeight = DefaultPipHeight,
        int targetWidth = DefaultTargetWidth,
        int targetHeight = DefaultTargetHeight)
    {
        _ipcBus = ipcBus;
        _pipWidth = Math.Max(1, pipWidth);
        _pipHeight = Math.Max(1, pipHeight);
        _targetWidth = Math.Max(1, targetWidth);
        _targetHeight = Math.Max(1, targetHeight);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PipInputTranslator"/> backed by a <see cref="GhostIpcServer"/>.
    /// </summary>
    public PipInputTranslator(
        GhostIpcServer server,
        int pipWidth = DefaultPipWidth,
        int pipHeight = DefaultPipHeight,
        int targetWidth = DefaultTargetWidth,
        int targetHeight = DefaultTargetHeight)
        : this((IGhostIpcBus)server, pipWidth, pipHeight, targetWidth, targetHeight)
    {
    }

    /// <summary>
    /// Initializes a new instance of <see cref="PipInputTranslator"/> backed by a <see cref="GhostIpcClient"/>.
    /// </summary>
    public PipInputTranslator(
        GhostIpcClient client,
        int pipWidth = DefaultPipWidth,
        int pipHeight = DefaultPipHeight,
        int targetWidth = DefaultTargetWidth,
        int targetHeight = DefaultTargetHeight)
        : this((IGhostIpcBus)client, pipWidth, pipHeight, targetWidth, targetHeight)
    {
    }

    #region Dimension & Coordinate Calculations

    /// <summary>
    /// Dynamically updates the PiP window size and Session 2 target desktop resolution.
    /// </summary>
    public void UpdateDimensions(int pipWidth, int pipHeight, int? targetWidth = null, int? targetHeight = null)
    {
        lock (_dimLock)
        {
            if (pipWidth > 0) _pipWidth = pipWidth;
            if (pipHeight > 0) _pipHeight = pipHeight;
            if (targetWidth.HasValue && targetWidth.Value > 0) _targetWidth = targetWidth.Value;
            if (targetHeight.HasValue && targetHeight.Value > 0) _targetHeight = targetHeight.Value;
        }
    }

    /// <summary>
    /// Scales PiP window relative client coordinates (X_pip, Y_pip) to Session 2 desktop coordinates.
    /// Formula:
    /// <c>X_target = (X_pip * W_target) / W_pip</c>
    /// <c>Y_target = (Y_pip * H_target) / H_pip</c>
    /// </summary>
    public (int TargetX, int TargetY) ScaleCoordinates(int pipX, int pipY)
    {
        int pw, ph, tw, th;
        lock (_dimLock)
        {
            pw = _pipWidth;
            ph = _pipHeight;
            tw = _targetWidth;
            th = _targetHeight;
        }

        return ScaleCoordinates(pipX, pipY, pw, ph, tw, th);
    }

    /// <summary>
    /// Static helper for scaling coordinates given explicit PiP and target desktop dimensions.
    /// Clamps input coordinates to the PiP window rectangle and bounds output coordinates to [0, W_target - 1].
    /// </summary>
    public static (int TargetX, int TargetY) ScaleCoordinates(
        int pipX,
        int pipY,
        int pipWidth,
        int pipHeight,
        int targetWidth,
        int targetHeight)
    {
        if (pipWidth <= 0 || pipHeight <= 0 || targetWidth <= 0 || targetHeight <= 0)
        {
            return (0, 0);
        }

        int clampedX = Math.Clamp(pipX, 0, pipWidth);
        int clampedY = Math.Clamp(pipY, 0, pipHeight);

        int targetX = (int)Math.Clamp(((long)clampedX * targetWidth) / pipWidth, 0, targetWidth - 1);
        int targetY = (int)Math.Clamp(((long)clampedY * targetHeight) / pipHeight, 0, targetHeight - 1);

        return (targetX, targetY);
    }

    #endregion

    #region Translators & Input Forwarding

    /// <summary>
    /// Translates a mouse move event on the PiP client area and forwards it to Session 2.
    /// </summary>
    public GhostInputTask TranslateMouseMove(int pipX, int pipY)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseMove(tx, ty, pipX, pipY);
        var task = DispatchPacketAsync(packet, default);
        return new GhostInputTask(task, packet);
    }

    /// <summary>
    /// Translates a mouse down event (Left, Right, or Middle) on the PiP client area and forwards it to Session 2.
    /// </summary>
    public GhostInputTask TranslateMouseDown(int pipX, int pipY, MouseButton button = MouseButton.Left)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseDown(tx, ty, pipX, pipY, button);
        var task = DispatchPacketAsync(packet, default);
        return new GhostInputTask(task, packet);
    }

    /// <summary>
    /// Translates a mouse down event with string button name (e.g. "Left", "Right", "Middle", "Sol", "Sağ", "Orta").
    /// </summary>
    public GhostInputTask TranslateMouseDown(int pipX, int pipY, string? button) =>
        TranslateMouseDown(pipX, pipY, ParseMouseButton(button));

    /// <summary>
    /// Translates a mouse up event (Left, Right, or Middle) on the PiP client area and forwards it to Session 2.
    /// </summary>
    public GhostInputTask TranslateMouseUp(int pipX, int pipY, MouseButton button = MouseButton.Left)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseUp(tx, ty, pipX, pipY, button);
        var task = DispatchPacketAsync(packet, default);
        return new GhostInputTask(task, packet);
    }

    /// <summary>
    /// Translates a mouse up event with string button name (e.g. "Left", "Right", "Middle", "Sol", "Sağ", "Orta").
    /// </summary>
    public GhostInputTask TranslateMouseUp(int pipX, int pipY, string? button) =>
        TranslateMouseUp(pipX, pipY, ParseMouseButton(button));

    /// <summary>
    /// Translates a double click event on the PiP client area and forwards it to Session 2.
    /// </summary>
    public GhostInputTask TranslateDoubleClick(int pipX, int pipY, MouseButton button = MouseButton.Left)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateDoubleClick(tx, ty, pipX, pipY, button);
        var task = DispatchPacketAsync(packet, default);
        return new GhostInputTask(task, packet);
    }

    /// <summary>
    /// Translates a double click event with string button name.
    /// </summary>
    public GhostInputTask TranslateDoubleClick(int pipX, int pipY, string? button) =>
        TranslateDoubleClick(pipX, pipY, ParseMouseButton(button));

    /// <summary>
    /// Translates a mouse wheel scroll event on the PiP client area and forwards it to Session 2.
    /// </summary>
    public GhostInputTask TranslateMouseWheel(int pipX, int pipY, int delta)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseWheel(tx, ty, pipX, pipY, delta);
        var task = DispatchPacketAsync(packet, default);
        return new GhostInputTask(task, packet);
    }

    #endregion

    #region Async Translators

    public async Task<GhostInputPacket> TranslateMouseMoveAsync(int pipX, int pipY, CancellationToken ct = default)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseMove(tx, ty, pipX, pipY);
        await DispatchPacketAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    public async Task<GhostInputPacket> TranslateMouseDownAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseDown(tx, ty, pipX, pipY, button);
        await DispatchPacketAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    public Task<GhostInputPacket> TranslateMouseDownAsync(int pipX, int pipY, string? button, CancellationToken ct = default) =>
        TranslateMouseDownAsync(pipX, pipY, ParseMouseButton(button), ct);

    public async Task<GhostInputPacket> TranslateMouseUpAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseUp(tx, ty, pipX, pipY, button);
        await DispatchPacketAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    public Task<GhostInputPacket> TranslateMouseUpAsync(int pipX, int pipY, string? button, CancellationToken ct = default) =>
        TranslateMouseUpAsync(pipX, pipY, ParseMouseButton(button), ct);

    public async Task<GhostInputPacket> TranslateDoubleClickAsync(int pipX, int pipY, MouseButton button = MouseButton.Left, CancellationToken ct = default)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateDoubleClick(tx, ty, pipX, pipY, button);
        await DispatchPacketAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    public Task<GhostInputPacket> TranslateDoubleClickAsync(int pipX, int pipY, string? button, CancellationToken ct = default) =>
        TranslateDoubleClickAsync(pipX, pipY, ParseMouseButton(button), ct);

    public async Task<GhostInputPacket> TranslateMouseWheelAsync(int pipX, int pipY, int delta, CancellationToken ct = default)
    {
        var (tx, ty) = ScaleCoordinates(pipX, pipY);
        var packet = GhostInputPacket.CreateMouseWheel(tx, ty, pipX, pipY, delta);
        await DispatchPacketAsync(packet, ct).ConfigureAwait(false);
        return packet;
    }

    #endregion

    #region Dispatcher & Parsing Helpers

    private async Task<GhostInputPacket> DispatchPacketAsync(GhostInputPacket packet, CancellationToken ct)
    {
        if (!_isInteractive)
        {
            return packet;
        }

        if (_ipcBus == null || !_ipcBus.IsConnected)
        {
            return packet;
        }

        try
        {
            string payloadJson = packet.ToJson();
            await _ipcBus.SendRequestAsync(
                action: ForwardInputAction,
                payloadJson: payloadJson,
                timeout: IpcTimeout,
                ct: ct).ConfigureAwait(false);

            InputForwarded?.Invoke(packet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PipInputTranslator] Failed to forward input packet: {ex.Message}");
            InputForwardFailed?.Invoke(packet, ex);
        }

        return packet;
    }

    /// <summary>
    /// Parses Turkish and English button descriptors into <see cref="MouseButton"/>.
    /// </summary>
    public static MouseButton ParseMouseButton(string? button)
    {
        if (string.IsNullOrWhiteSpace(button))
        {
            return MouseButton.Left;
        }

        return button.Trim().ToLowerInvariant() switch
        {
            "left" or "l" or "sol" or "primary" or "button1" => MouseButton.Left,
            "right" or "r" or "sag" or "sağ" or "secondary" or "button2" => MouseButton.Right,
            "middle" or "m" or "orta" or "wheel" or "button3" => MouseButton.Middle,
            _ => MouseButton.Left
        };
    }

    #endregion
}
