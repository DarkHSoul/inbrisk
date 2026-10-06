using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows;
using Inbrisk.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>
/// Default implementation of the isolated Ghost desktop session lifecycle manager.
/// Coordinates headless loopback RDP connections, user provisioning, and Windows Terminal Services interop.
/// </summary>
public class GhostSessionManager : IGhostSessionManager, IDisposable
{
    private readonly object _gate = new();
    private GhostSessionConfig _currentConfig = new();
    private int? _currentSessionId;
    private int? _currentProcessId;
    private string? _currentUsername;
    private DateTime? _startedAtUtc;
    private int _disposed;

    /// <summary>Gets the current ghost session configuration.</summary>
    public GhostSessionConfig CurrentConfig
    {
        get { lock (_gate) return _currentConfig; }
        private set { lock (_gate) _currentConfig = value; }
    }

    /// <summary>Gets the current Terminal Services session ID if active.</summary>
    public int? CurrentSessionId
    {
        get { lock (_gate) return _currentSessionId; }
    }

    /// <summary>
    /// Ensures the dedicated ghost user account exists and has proper permissions.
    /// </summary>
    public async Task<GhostUserProvisionResult> EnsureProvisionedAsync(CancellationToken ct = default)
    {
        string username = CurrentConfig.Username;
        return await GhostUserProvisioner.EnsureGhostUserReadyAsync(username).ConfigureAwait(false);
    }

    /// <summary>
    /// Launches or connects to a headless ghost desktop session with the specified configuration.
    /// </summary>
    public async Task<GhostSessionStatus> StartSessionAsync(
        GhostSessionConfig? config = null,
        CancellationToken ct = default)
    {
        return await StartSessionAsync(config, password: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Launches or connects to a headless ghost desktop session with explicit password override.
    /// </summary>
    public async Task<GhostSessionStatus> StartSessionAsync(
        GhostSessionConfig? config,
        string? password,
        CancellationToken ct = default)
    {
        var targetConfig = config ?? CurrentConfig;
        CurrentConfig = targetConfig;

        if (targetConfig.AutoProvision)
        {
            var provResult = await EnsureProvisionedAsync(ct).ConfigureAwait(false);
            if (!provResult.Success)
            {
                Inbrisk.Core.GhostTelemetry.RecordSessionFail();
                return new GhostSessionStatus(
                    IsActive: false,
                    SessionId: null,
                    Username: targetConfig.Username,
                    Uptime: TimeSpan.Zero,
                    State: $"ProvisionFailed: {provResult.Error}",
                    ProcessId: null);
            }
        }

        try
        {
            string effectivePassword = string.IsNullOrEmpty(password)
                ? GhostUserProvisioner.DefaultGhostPassword
                : password;

            var rdpSession = await GhostRdpConnector.StartHeadlessRdpSessionAsync(
                targetConfig,
                password: effectivePassword,
                ct: ct).ConfigureAwait(false);

            lock (_gate)
            {
                _currentSessionId = rdpSession.SessionId;
                _currentProcessId = rdpSession.ProcessId;
                _currentUsername = rdpSession.Username;
                _startedAtUtc = DateTime.UtcNow;
            }

            return new GhostSessionStatus(
                IsActive: true,
                SessionId: rdpSession.SessionId,
                Username: rdpSession.Username,
                Uptime: TimeSpan.Zero,
                State: "Active",
                ProcessId: rdpSession.ProcessId);
        }
        catch (Exception ex)
        {
            Inbrisk.Core.GhostTelemetry.RecordSessionFail();
            return new GhostSessionStatus(
                IsActive: false,
                SessionId: null,
                Username: targetConfig.Username,
                Uptime: TimeSpan.Zero,
                State: $"StartFailed: {ex.Message}",
                ProcessId: null);
        }
    }

    /// <summary>
    /// Stops the target ghost session or the current active session if sessionId is omitted.
    /// </summary>
    public async Task<bool> StopSessionAsync(int? sessionId = null, CancellationToken ct = default)
    {
        int targetSessionId;
        lock (_gate)
        {
            targetSessionId = sessionId ?? _currentSessionId ?? 0;
            if (targetSessionId == 0)
            {
                return true;
            }
        }

        bool stopped = await GhostRdpConnector.StopHeadlessRdpSessionAsync(targetSessionId).ConfigureAwait(false);

        lock (_gate)
        {
            if (_currentSessionId == targetSessionId || sessionId == null)
            {
                _currentSessionId = null;
                _currentProcessId = null;
                _currentUsername = null;
                _startedAtUtc = null;
            }
        }

        return stopped;
    }

    /// <summary>
    /// Queries the current status of the ghost session.
    /// </summary>
    public Task<GhostSessionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_currentSessionId.HasValue)
            {
                var existing = GhostWtsInterop.FindSessionByUsername(CurrentConfig.Username);
                if (existing != null && (existing.State == WTS_CONNECTSTATE_CLASS.WTSActive || existing.State == WTS_CONNECTSTATE_CLASS.WTSConnected))
                {
                    return Task.FromResult(new GhostSessionStatus(
                        IsActive: true,
                        SessionId: existing.SessionId,
                        Username: existing.UserName ?? CurrentConfig.Username,
                        Uptime: TimeSpan.Zero,
                        State: existing.State.ToString(),
                        ProcessId: null));
                }

                return Task.FromResult(GhostSessionStatus.Inactive);
            }

            var all = GhostWtsInterop.GetAllSessions();
            var matched = all.FirstOrDefault(s => s.SessionId == _currentSessionId.Value);

            bool isActive = matched != null &&
                (matched.State == WTS_CONNECTSTATE_CLASS.WTSActive || matched.State == WTS_CONNECTSTATE_CLASS.WTSConnected);

            var uptime = (_startedAtUtc.HasValue && isActive)
                ? (DateTime.UtcNow - _startedAtUtc.Value)
                : TimeSpan.Zero;

            string state = matched?.State.ToString() ?? (isActive ? "Active" : "Disconnected");

            return Task.FromResult(new GhostSessionStatus(
                IsActive: isActive,
                SessionId: _currentSessionId,
                Username: _currentUsername,
                Uptime: uptime,
                State: state,
                ProcessId: _currentProcessId));
        }
    }

    /// <summary>
    /// Disposes the session manager and cleanly terminates tracked sessions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        int targetId = 0;
        lock (_gate)
        {
            if (_currentSessionId.HasValue)
            {
                targetId = _currentSessionId.Value;
                _currentSessionId = null;
            }
        }

        if (targetId > 0)
        {
            try
            {
                GhostRdpConnector.StopHeadlessRdpSessionAsync(targetId).GetAwaiter().GetResult();
            }
            catch { }
        }
    }
}

/// <summary>
/// Ghost OS MCP Bridge.
/// Exposes and dispatches isolated background computer control tools for Inbrisk MCP.
/// Coordinates Session 2 loopback RDP, zero-focus transparent PiP display, and high-speed cross-session IPC.
/// </summary>
[McpServerToolType]
public sealed class GhostMcpBridge : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IGhostSessionManager _sessionManager;
    private readonly GhostPipManager _pipManager;
    private readonly GhostIpcServer _ipcServer;
    private readonly bool _ownsPipManager;
    private readonly bool _ownsSessionManager;
    private readonly bool _ownsIpcServer;
    private int _disposed;

    /// <summary>
    /// Primary constructor for MCP Host dependency injection.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public GhostMcpBridge(McpSession? session = null)
        : this(sessionManager: null, pipManager: null, ipcServer: null)
    {
    }

    /// <summary>
    /// Explicit constructor allowing custom dependency overrides (ideal for testing).
    /// </summary>
    public GhostMcpBridge(
        IGhostSessionManager? sessionManager,
        GhostPipManager? pipManager = null,
        GhostIpcServer? ipcServer = null)
    {
        _sessionManager = sessionManager ?? new GhostSessionManager();
        _pipManager = pipManager ?? new GhostPipManager();
        _ipcServer = ipcServer ?? new GhostIpcServer(GhostIpcDefaults.PipeName);

        _ownsSessionManager = sessionManager == null;
        _ownsPipManager = pipManager == null;
        _ownsIpcServer = ipcServer == null;

        try
        {
            _ = _ipcServer.StartAsync();
        }
        catch
        {
            // Non-blocking listener startup
        }
    }

    #region MCP Tools

    /// <summary>
    /// Launches an isolated background desktop session (Session 2 via loopback RDP)
    /// and begins the zero-focus Picture-in-Picture (PiP) live stream on the primary monitor.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_start"), Description(
        "Starts the isolated background desktop session (Session 2 via loopback RDP) and launches the zero-focus Picture-in-Picture (PiP) live stream on the primary monitor. " +
        "The agent operates entirely in Session 2 with its own independent mouse and keyboard without stealing focus.")]
    public async Task<CallToolResult> StartGhost(
        [Description("Virtual desktop width in pixels (default: 1920)")] int? width = null,
        [Description("Virtual desktop height in pixels (default: 1080)")] int? height = null,
        [Description("Dedicated ghost Windows account username (default: InbriskAgent)")] string? username = null,
        [Description("Dedicated ghost Windows account password (optional; uses provisioned password if omitted)")] string? password = null,
        [Description("Whether to display the live Picture-in-Picture (PiP) overlay on the host monitor (default: true)")] bool? pip = null,
        [Description("PiP overlay scaling factor (0.25 to 5.0, default: 1.0: 320x180)")] float? scale = null,
        [Description("PiP anchor position preset: top_right|top_left|bottom_right|bottom_left|custom (default: top_right)")] string? position = null,
        [Description("Whether mouse clicks interact directly with PiP instead of click-through (default: false)")] bool? interactive = null,
        [Description("Desktop margin in pixels for PiP anchor positioning (default: 16)")] int? margin = null,
        [Description("Automatically provision local ghost user and RDP permissions if missing (default: true)")] bool? autoProvision = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostStart");

        // 1. Ensure cross-session IPC server is listening
        try
        {
            await _ipcServer.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostMcpBridge] Warning starting IPC server: {ex.Message}");
        }

        // 2. Prepare ghost session configuration
        int targetWidth = width ?? 1920;
        int targetHeight = height ?? 1080;
        string targetUser = string.IsNullOrWhiteSpace(username) ? GhostUserProvisioner.DefaultGhostUsername : username.Trim();
        bool shouldAutoProvision = autoProvision ?? true;

        var config = new GhostSessionConfig(
            Width: targetWidth,
            Height: targetHeight,
            Username: targetUser,
            AutoProvision: shouldAutoProvision);

        // 3. Launch or connect to the headless session via GhostSessionManager
        GhostSessionStatus status;
        if (_sessionManager is GhostSessionManager gsm)
        {
            status = await gsm.StartSessionAsync(config, password, ct).ConfigureAwait(false);
        }
        else
        {
            status = await _sessionManager.StartSessionAsync(config, ct).ConfigureAwait(false);
        }

        if (!status.IsActive)
        {
            return ErrorResult("SessionStartFailed", $"Failed to start ghost session for '{targetUser}'. State: {status.State}");
        }

        // 4. Initialize PiP display if requested
        bool shouldStartPip = pip ?? true;
        string? pipWarning = null;

        if (shouldStartPip)
        {
            PipPresetPosition preset = (position?.Trim().ToLowerInvariant()) switch
            {
                "top_left" or "topleft" => PipPresetPosition.TopLeft,
                "bottom_right" or "bottomright" => PipPresetPosition.BottomRight,
                "bottom_left" or "bottomleft" => PipPresetPosition.BottomLeft,
                "custom" => PipPresetPosition.Custom,
                _ => PipPresetPosition.TopRight
            };

            float clampedScale = Math.Clamp(scale ?? 1.0f, 0.25f, 5.0f);
            bool isInteractive = interactive ?? false;
            int pipMargin = margin ?? 16;

            var pipConfig = new PipConfig(
                Position: preset,
                Scale: clampedScale,
                Interactive: isInteractive,
                Margin: pipMargin,
                TargetFps: 60);

            try
            {
                _pipManager.StartPip(pipConfig);
            }
            catch (Exception pipEx)
            {
                pipWarning = $"PiP overlay failed to start: {pipEx.Message}";
                Debug.WriteLine($"[GhostMcpBridge] {pipWarning}");
            }
        }

        var result = new
        {
            success = true,
            sessionId = status.SessionId,
            username = status.Username,
            resolution = $"{targetWidth}x{targetHeight}",
            state = status.State,
            processId = status.ProcessId,
            pipActive = _pipManager.IsRunning,
            pipPosition = _pipManager.CurrentPosition.ToString(),
            pipScale = _pipManager.CurrentScale,
            pipWidth = _pipManager.Width,
            pipHeight = _pipManager.Height,
            pipInteractive = _pipManager.IsInteractive,
            pipWarning,
            ipcConnected = _ipcServer.IsConnected,
            message = "Ghost desktop session (Session 2) and PiP live overlay stream started successfully."
        };

        return SuccessResult(result);
    }

    /// <summary>
    /// Stops the background ghost desktop session (Session 2) and terminates the Picture-in-Picture (PiP) live stream.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_stop"), Description(
        "Stops the background ghost desktop session (Session 2) and terminates the Picture-in-Picture (PiP) live stream.")]
    public async Task<CallToolResult> StopGhost(
        [Description("Target session ID to stop (optional; defaults to the active ghost session)")] int? sessionId = null,
        [Description("Whether to stop the Picture-in-Picture (PiP) overlay window (default: true)")] bool? stopPip = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostStop");
        bool shouldStopPip = stopPip ?? true;
        bool pipWasRunning = _pipManager.IsRunning;

        if (shouldStopPip)
        {
            try
            {
                _pipManager.StopPip();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostMcpBridge] Warning stopping PiP: {ex.Message}");
            }
        }

        bool stopped = await _sessionManager.StopSessionAsync(sessionId, ct).ConfigureAwait(false);

        var res = new
        {
            success = stopped,
            sessionId,
            pipStopped = shouldStopPip && pipWasRunning,
            message = stopped
                ? "Ghost desktop session and PiP live stream successfully stopped."
                : "Failed to cleanly stop ghost desktop session (may have already exited)."
        };

        return new CallToolResult
        {
            IsError = !stopped,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(res, SerializerOptions) }]
        };
    }

    /// <summary>
    /// Forwards a declarative sequence of UI action steps directly to Session 2 via cross-session IPC.
    /// Actions are executed by the ghost agent's own mouse and keyboard inside the isolated session.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_batch"), Description(
        "Forwards a declarative sequence of UI action steps directly to Session 2 via cross-session IPC. " +
        "Actions are executed by the ghost agent's own mouse and keyboard inside the isolated session without interfering with the local user.")]
    public async Task<CallToolResult> GhostBatch(
        [Description("Ordered list of simple action steps to execute sequentially in Session 2 (mutually exclusive with 'set')")] InbriskTools.BatchStep[]? steps = null,
        [Description("Compact list of form fields to set in one turn in Session 2 (mutually exclusive with 'steps')")] InbriskTools.FormFieldSpec[]? set = null,
        [Description("Optional condition to wait for after steps execute (appears|disappears|windowAppears|stable)")] InbriskTools.BatchUntilSpec? until = null,
        [Description("Optional list of element targets to read/extract properties from upon completion")] InbriskTools.BatchReadSpec[]? read = null,
        [Description("Output verbosity: slim|full (default: slim)")] string? detail = null,
        [Description("Stop the batch at the first failing step (default: true)")] bool? failFast = null,
        [Description("Alias of failFast")] bool? fail_fast = null,
        [Description("Auto-dismiss policy for save-confirmation modals inside Session 2: off|save|discard|closeOnly (default: closeOnly)")] string? autoDismissModals = null,
        [Description("Alias of autoDismissModals")] string? auto_dismiss_modals = null,
        [Description("Execution timeout in milliseconds for the batch inside Session 2 (default: 30000)")] int? timeoutMs = null,
        [Description("Optional client operation ID for retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostBatch");

        if ((steps == null || steps.Length == 0) && (set == null || set.Length == 0))
        {
            return ErrorResult("Malformed", "computer_ghost_batch requires non-empty 'steps' or 'set'.");
        }

        if (steps is { Length: > 0 } && set is { Length: > 0 })
        {
            return ErrorResult("Malformed", "Ambiguous execution order: specify either 'set' or 'steps', not both simultaneously.");
        }

        // Verify active ghost session
        var status = await _sessionManager.GetStatusAsync(ct).ConfigureAwait(false);
        if (!status.IsActive)
        {
            return ErrorResult("SessionInactive", "Ghost session is not active. Call computer_ghost_start first to launch the background session.");
        }

        // Verify IPC connection to Session 2 worker (wait briefly if connection is warming up)
        if (!_ipcServer.IsConnected)
        {
            var sw = Stopwatch.StartNew();
            while (!_ipcServer.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(5) && !ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        if (!_ipcServer.IsConnected)
        {
            return ErrorResult("GhostWorkerNotConnected", "The Ghost session worker in Session 2 is not connected to the IPC bus. Ensure the session was started with computer_ghost_start and the Inbrisk worker is running inside Session 2.");
        }

        var payload = new
        {
            steps,
            set,
            until,
            read,
            detail = detail ?? "slim",
            failFast = failFast ?? fail_fast ?? true,
            autoDismissModals = autoDismissModals ?? auto_dismiss_modals ?? "closeOnly",
            operationId
        };
        string payloadJson = JsonSerializer.Serialize(payload, SerializerOptions);
        var timeout = TimeSpan.FromMilliseconds(timeoutMs ?? 30000);

        try
        {
            string responseJson = await _ipcServer.SendRequestAsync("batch", payloadJson, timeout, ct).ConfigureAwait(false);

            bool isErr = false;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                if (doc.RootElement.TryGetProperty("isError", out var ep) && ep.GetBoolean())
                {
                    isErr = true;
                }
                else if (doc.RootElement.TryGetProperty("success", out var sp) && !sp.GetBoolean())
                {
                    isErr = true;
                }
            }
            catch
            {
                // Fall through on non-JSON response string
            }

            return new CallToolResult
            {
                IsError = isErr,
                Content = [new TextContentBlock { Text = responseJson }]
            };
        }
        catch (GhostIpcException gex)
        {
            return ErrorResult("GhostIpcError", $"IPC communication with Session 2 failed: {gex.Message}");
        }
        catch (TimeoutException tex)
        {
            return ErrorResult("Timeout", $"Ghost batch execution timed out after {timeout.TotalSeconds:F0}s: {tex.Message}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ErrorResult("Cancelled", "Ghost batch operation was cancelled.");
        }
        catch (Exception ex)
        {
            return ErrorResult(ex.GetType().Name, $"Unexpected error executing ghost batch: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns diagnostic status for the Ghost OS session and PiP overlay,
    /// including session active state, uptime, resolution, PID, and live PiP window metrics.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_status"), Description(
        "Returns diagnostic status for the Ghost OS session and PiP overlay, including session active state, uptime, resolution, PID, and live PiP window metrics.")]
    public async Task<CallToolResult> GetGhostStatus(CancellationToken ct = default)
    {
        PerfTrace.Count("ghostStatus");
        var sessionStatus = await _sessionManager.GetStatusAsync(ct).ConfigureAwait(false);

        int width = 1920;
        int height = 1080;
        if (_sessionManager is GhostSessionManager gsm)
        {
            width = gsm.CurrentConfig.Width;
            height = gsm.CurrentConfig.Height;
        }

        var pipStatus = new
        {
            isActive = _pipManager.IsRunning,
            position = _pipManager.CurrentPosition.ToString(),
            scale = _pipManager.CurrentScale,
            width = _pipManager.Width,
            height = _pipManager.Height,
            x = _pipManager.X,
            y = _pipManager.Y,
            interactive = _pipManager.IsInteractive,
            windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null
        };

        var ipcStatus = new
        {
            isConnected = _ipcServer.IsConnected,
            pipeName = _ipcServer.PipeName
        };

        var result = new
        {
            success = true,
            isActive = sessionStatus.IsActive,
            sessionId = sessionStatus.SessionId,
            username = sessionStatus.Username,
            state = sessionStatus.State,
            uptime = sessionStatus.Uptime.ToString(@"hh\:mm\:ss"),
            uptimeSeconds = Math.Round(sessionStatus.Uptime.TotalSeconds, 1),
            processId = sessionStatus.ProcessId,
            resolution = $"{width}x{height}",
            pip = pipStatus,
            ipc = ipcStatus
        };

        return SuccessResult(result);
    }

    #endregion

    #region Helper Methods

    private static CallToolResult SuccessResult(object data) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data, SerializerOptions) }]
    };

    private static CallToolResult ErrorResult(string kind, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new
            {
                success = false,
                error = kind,
                message
            }, SerializerOptions)
        }]
    };

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes resources owned by the Ghost MCP bridge.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_ownsPipManager)
        {
            try { _pipManager.Dispose(); } catch { }
        }

        if (_ownsIpcServer)
        {
            try { _ipcServer.Dispose(); } catch { }
        }

        if (_ownsSessionManager && _sessionManager is IDisposable dispSession)
        {
            try { dispSession.Dispose(); } catch { }
        }
    }

    #endregion
}
