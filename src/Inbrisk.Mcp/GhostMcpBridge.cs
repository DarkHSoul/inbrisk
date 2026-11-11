using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
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
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly IGhostSessionManager _sessionManager;
    private readonly GhostPipManager _pipManager;
    private readonly GhostIpcServer _ipcServer;
    private readonly bool _ownsPipManager;
    private readonly bool _ownsSessionManager;
    private readonly bool _ownsIpcServer;
    private readonly Func<GhostSessionStatus?>? _localBackendStatusProvider;
    private readonly object _desktopGate = new();
    private GhostLocalDesktop? _localDesktop;
    private Inbrisk.Platform.Windows.GhostDesktopInput? _desktopInput;
    private readonly McpSession? _session;
    private InbriskTools? _inbriskTools;
    private int _disposed;

    /// <summary>Reachability probe for the isolated desktop. Reports either an
    /// active RDP/WTS session or the local Ghost Control backend that owns the
    /// named isolated desktop.</summary>
    public Task<GhostSessionStatus> GetSessionStatusAsync(CancellationToken ct = default) =>
        GetEffectiveSessionStatusAsync(ct);

    private InbriskTools GetTools()
    {
        if (_inbriskTools != null) return _inbriskTools;
        var sess = _session ?? new McpSession(EmergencyControl.Process);
        _inbriskTools = new InbriskTools(sess);
        return _inbriskTools;
    }

    private static string GetBackendDesktopName() =>
        TryReadGhostControlDaemonInfo()?.DesktopName ?? GhostDesktopNative.DefaultGhostDesktopName;

    private GhostLocalDesktop GetOrCreateDesktop()
    {
        lock (_desktopGate)
        {
            var desktopName = GetBackendDesktopName();
            if (_localDesktop == null || !_localDesktop.IsCreated ||
                !string.Equals(_localDesktop.DesktopName, desktopName, StringComparison.OrdinalIgnoreCase))
            {
                _localDesktop?.Dispose();
                _localDesktop = new GhostLocalDesktop(desktopName, createIfNotExists: true);
            }
            return _localDesktop;
        }
    }

    private Inbrisk.Platform.Windows.GhostDesktopInput GetOrCreateInput()
    {
        lock (_desktopGate)
        {
            var desktopName = GetBackendDesktopName();
            if (_desktopInput == null ||
                !string.Equals(_desktopInput.DesktopName, desktopName, StringComparison.OrdinalIgnoreCase))
            {
                _desktopInput?.Dispose();
                _desktopInput = new Inbrisk.Platform.Windows.GhostDesktopInput(desktopName);
            }
            return _desktopInput;
        }
    }

    /// <summary>
    /// The control layer historically only treated an RDP/WTS session as
    /// active. Ghost Control can also run the supported local backend: the
    /// isolated Win32 desktop recorded by its daemon plus the capture process. When that backend is
    /// alive it is a valid target for GhostDesktopInput/GhostLocalDesktop.
    /// </summary>
    private async Task<GhostSessionStatus> GetEffectiveSessionStatusAsync(CancellationToken ct)
    {
        GhostSessionStatus? status = null;
        try
        {
            status = await _sessionManager.GetStatusAsync(ct).ConfigureAwait(false);
            if (status.IsActive) return status;
        }
        catch
        {
            if (TryGetLocalBackendStatus() is { } fallback) return fallback;
            throw;
        }

        return TryGetLocalBackendStatus() ?? status;
    }

    private GhostSessionStatus? TryGetLocalBackendStatus()
    {
        if (_localBackendStatusProvider != null)
        {
            return _localBackendStatusProvider();
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var daemon = TryReadGhostControlDaemonInfo();
        if (daemon == null || !IsGhostControlDaemonAlive(daemon) ||
            !string.Equals(daemon.Status, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        IntPtr desktopHandle = GhostDesktopNative.OpenDesktop(
            daemon.DesktopName,
            GhostDesktopNative.DESKTOP_READOBJECTS | GhostDesktopNative.DESKTOP_ENUMERATE,
            inherit: false);
        if (desktopHandle == IntPtr.Zero)
        {
            return null;
        }
        GhostDesktopNative.CloseDesktop(desktopHandle);

        var uptime = daemon.StartTimeUtc.HasValue
            ? DateTime.UtcNow - daemon.StartTimeUtc.Value
            : TimeSpan.Zero;
        if (uptime < TimeSpan.Zero) uptime = TimeSpan.Zero;

        return new GhostSessionStatus(
            IsActive: true,
            SessionId: null,
            Username: Environment.UserName,
            Uptime: uptime,
            State: "LocalDesktop",
            ProcessId: daemon.Pid);
    }

    // ---------------------------------------------------------------
    // Ghost action execution
    //
    // These shims used to forward straight back into the unified tools
    // with ghost:true — but a unified tool's ghost path delegates to
    // GhostSessionCoordinator, which calls right back into THIS class.
    // That mutual recursion killed the whole process with a stack
    // overflow on the first ghost action (computer_batch(ghost:true),
    // computer_click(ghost:true), computer_windows(ghost:true), ...).
    //
    // The bridge owns the isolated-desktop primitives (GhostDesktopInput
    // for injection, GhostLocalDesktop for windows/processes), so the
    // bridge executes and the cycle is cut.
    // ---------------------------------------------------------------

    /// <summary>Ghost actions only run inside an ACTIVE ghost session —
    /// otherwise the injection primitives would address whatever desktop
    /// the caller happens to be on (i.e. the user's own).</summary>
    private async Task<CallToolResult?> RequireActiveSessionAsync(CancellationToken ct)
    {
        GhostSessionStatus status;
        try { status = await GetEffectiveSessionStatusAsync(ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            return ErrorResult("SessionUnavailable",
                $"ghost session status query failed: {ex.Message}");
        }
        return status.IsActive
            ? null
            : ErrorResult("SessionInactive",
                $"no active ghost session (state: {status.State}) — start one with " +
                "computer_ghost_control(action:\"open\") or computer_ghost_start before " +
                "acting on the isolated desktop");
    }

    private static MouseButton ParseMouseButton(string? button) =>
        (button ?? "left").Trim().ToLowerInvariant() switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left,
        };

    private async Task<CallToolResult> ExecuteGhostClickAsync(
        int x, int y, string? button, bool doubleClick, CancellationToken ct)
    {
        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        if (await TrySendSurfaceInputAsync("ghost_surface_click", new Dictionary<string, object?>
        {
            ["x"] = x,
            ["y"] = y,
            ["button"] = button ?? "left",
            ["doubleClick"] = doubleClick
        }, ct).ConfigureAwait(false))
        {
            return SuccessResult(new
            {
                success = true,
                action = "click",
                x,
                y,
                button = button ?? "left",
                doubleClick,
                backend = "dwmWindowSurface"
            });
        }

        var btn = ParseMouseButton(button);
        try { GetOrCreateInput().SendMouseClick(x, y, btn, doubleClick); }
        catch (Exception ex)
        { return ErrorResult("InjectionFailed", $"ghost click failed: {ex.Message}"); }
        return SuccessResult(new
        {
            success = true,
            action = "click",
            x,
            y,
            button = btn.ToString(),
            doubleClick,
            desktop = GetBackendDesktopName(),
        });
    }

    private async Task<CallToolResult> ExecuteGhostTypeAsync(
        string text, CancellationToken ct)
    {
        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        if (await TrySendSurfaceInputAsync("ghost_surface_type", new Dictionary<string, object?>
        {
            ["text"] = text
        }, ct).ConfigureAwait(false))
        {
            return SuccessResult(new
            {
                success = true,
                action = "type",
                chars = text.Length,
                backend = "dwmWindowSurface"
            });
        }

        try { GetOrCreateInput().SendText(text); }
        catch (Exception ex)
        { return ErrorResult("InjectionFailed", $"ghost type failed: {ex.Message}"); }
        return SuccessResult(new
        {
            success = true,
            action = "type",
            chars = text.Length,
            desktop = GetBackendDesktopName(),
        });
    }

    private async Task<CallToolResult> ExecuteGhostWindowsAsync(CancellationToken ct)
    {
        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        var surfaceStatus = await TryGetSurfaceStatusAsync(ct).ConfigureAwait(false);
        if (surfaceStatus != null && surfaceStatus.Count > 0)
        {
            return SuccessResult(new
            {
                success = true,
                action = "windows",
                backend = "dwmWindowSurface",
                count = surfaceStatus.Count,
                windows = surfaceStatus.Windows,
                activeHwnd = surfaceStatus.ActiveHwnd
            });
        }

        var desktop = GetOrCreateDesktop();
        List<IntPtr> handles;
        try { handles = GhostDesktopNative.EnumerateWindows(desktop.DesktopHandle); }
        catch (Exception ex)
        { return ErrorResult("EnumerationFailed", $"ghost desktop window enumeration failed: {ex.Message}"); }

        var windows = new List<object>(handles.Count);
        foreach (var h in handles)
        {
            try
            {
                var (title, className, visible) = GhostDesktopNative.GetWindowDetails(h);
                GetWindowThreadProcessId(h, out var pid);
                GetWindowRect(h, out var r);
                windows.Add(new
                {
                    hwnd = $"0x{h.ToInt64():X}",
                    title,
                    className,
                    visible,
                    pid,
                    rect = new
                    {
                        left = r.Left,
                        top = r.Top,
                        width = r.Right - r.Left,
                        height = r.Bottom - r.Top,
                    },
                });
            }
            catch { /* one unreadable window never fails the listing */ }
        }

        return SuccessResult(new
        {
            success = true,
            action = "windows",
            desktop = GetBackendDesktopName(),
            count = windows.Count,
            windows,
        });
    }

    private async Task<CallToolResult> ExecuteGhostLaunchAsync(
        string program, string? args, string? workingDir, CancellationToken ct)
    {
        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        var surfaceLaunch = await TryLaunchDwmSurfaceAsync(program, args, workingDir, ct).ConfigureAwait(false);
        if (surfaceLaunch != null)
        {
            return surfaceLaunch;
        }

        var desktop = GetOrCreateDesktop();
        try
        {
            var proc = GhostDesktopNative.CreateProcessWithDesktop(
                desktop.DesktopName, program,
                string.IsNullOrWhiteSpace(args) ? null : args,
                string.IsNullOrWhiteSpace(workingDir) ? null : workingDir);
            return SuccessResult(new
            {
                success = true,
                action = "launch",
                program,
                pid = proc.Id,
                desktop = desktop.DesktopName,
            });
        }
        catch (Exception ex)
        {
            return ErrorResult("LaunchFailed",
                $"launching '{program}' on the ghost desktop failed: {ex.Message}");
        }
    }

    private sealed record SurfaceStatusSnapshot(string? ActiveHwnd, int Count, List<object> Windows);

    private static string? SurfaceRequestToken() => TryReadGhostControlDaemonInfo()?.Token;

    private static async Task<string?> SendSurfaceRequestAsync(
        string action,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken ct)
    {
        var daemon = TryReadGhostControlDaemonInfo();
        if (daemon == null || !IsGhostControlDaemonAlive(daemon) || string.IsNullOrWhiteSpace(daemon.Token))
            return null;

        var client = await ConnectGhostControlAsync(daemon.PipeName, TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
        if (client == null) return null;

        await using (client)
        {
            var body = new Dictionary<string, object?>(payload) { ["token"] = daemon.Token };
            return await client.SendRequestAsync(action, JsonSerializer.Serialize(body, SerializerOptions), TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        }
    }

    private static async Task<SurfaceStatusSnapshot?> TryGetSurfaceStatusAsync(CancellationToken ct)
    {
        var raw = await SendSurfaceRequestAsync("ghost_surface_status", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;

            var windows = new List<object>();
            if (root.TryGetProperty("windows", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    windows.Add(item.Clone());
                }
            }

            string? active = root.TryGetProperty("activeHwnd", out var a) ? a.GetString() : null;
            int count = root.TryGetProperty("count", out var c) && c.TryGetInt32(out var parsed)
                ? parsed
                : windows.Count;
            return new SurfaceStatusSnapshot(active, count, windows);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<CallToolResult?> TryLaunchDwmSurfaceAsync(
        string program,
        string? args,
        string? workingDir,
        CancellationToken ct)
    {
        var raw = await SendSurfaceRequestAsync("ghost_surface_launch", new Dictionary<string, object?>
        {
            ["program"] = program,
            ["args"] = args,
            ["workingDir"] = workingDir
        }, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return null;

            return SuccessResult(new
            {
                success = true,
                action = "launch",
                program,
                pid = root.TryGetProperty("pid", out var pid) ? pid.GetInt32() : (int?)null,
                hwnd = root.TryGetProperty("hwnd", out var h) ? h.GetString() : null,
                title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                backend = "dwmWindowSurface"
            });
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> TrySendSurfaceInputAsync(
        string action,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken ct)
    {
        var status = await TryGetSurfaceStatusAsync(ct).ConfigureAwait(false);
        if (status == null || string.IsNullOrWhiteSpace(status.ActiveHwnd))
            return false;

        var raw = await SendSurfaceRequestAsync(action, payload, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Primary constructor for MCP Host dependency injection.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public GhostMcpBridge(McpSession? session = null)
        : this(sessionManager: null, pipManager: null, ipcServer: null, session: session)
    {
    }

    /// <summary>
    /// Explicit constructor allowing custom dependency overrides (ideal for testing).
    /// </summary>
    public GhostMcpBridge(
        IGhostSessionManager? sessionManager,
        GhostPipManager? pipManager = null,
        GhostIpcServer? ipcServer = null,
        McpSession? session = null,
        InbriskTools? inbriskTools = null,
        Func<GhostSessionStatus?>? localBackendStatusProvider = null)
    {
        _session = session;
        _inbriskTools = inbriskTools;
        _localBackendStatusProvider = localBackendStatusProvider;
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
        [Description("Alias of autoProvision")] bool? auto_provision = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostStart");

        if (EmergencyGate.IsStopped)
        {
            return EmergencyResult();
        }

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
        bool shouldAutoProvision = autoProvision ?? auto_provision ?? true;

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
            windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null,
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
        [Description("Alias of sessionId")] int? session_id = null,
        [Description("Whether to stop the Picture-in-Picture (PiP) overlay window (default: true)")] bool? stopPip = null,
        [Description("Alias of stopPip")] bool? stop_pip = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostStop");
        int? targetSessionId = sessionId ?? session_id;
        bool shouldStopPip = stopPip ?? stop_pip ?? true;
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

        bool stopped = await _sessionManager.StopSessionAsync(targetSessionId, ct).ConfigureAwait(false);

        // Terminate any processes and close windows on the local isolated Ghost desktop station
        bool localDeskCleaned = false;
        try
        {
            var hDesktop = GhostDesktopNative.OpenDesktop(GetBackendDesktopName(), GhostDesktopNative.DESKTOP_ALL_ACCESS, false);
            if (hDesktop != IntPtr.Zero)
            {
                var windows = GhostDesktopNative.EnumerateWindows(hDesktop);
                foreach (var hwnd in windows)
                {
                    try
                    {
                        GhostDesktopNative.GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid > 0 && pid != Environment.ProcessId)
                        {
                            try { Process.GetProcessById((int)pid).Kill(entireProcessTree: true); } catch { }
                        }
                    }
                    catch { }
                }
                GhostDesktopNative.CloseDesktop(hDesktop);
                localDeskCleaned = true;
            }
        }
        catch { }

        bool isStopped = stopped || localDeskCleaned || pipWasRunning;

        var res = new
        {
            success = isStopped,
            sessionId = targetSessionId,
            pipActive = _pipManager.IsRunning,
            windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null,
            pipStopped = shouldStopPip && pipWasRunning,
            localDesktopCleaned = localDeskCleaned,
            message = isStopped
                ? "Ghost desktop session, local isolated desktop processes, and PiP stream successfully stopped."
                : "Ghost desktop was not active or had already exited."
        };

        return new CallToolResult
        {
            IsError = false,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(res, SerializerOptions) }]
        };
    }

    /// <summary>
    /// Legacy shim forwarding batch execution to the unified computer_batch tool with ghost: true.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_batch"), Description(
        "Legacy shim forwarding to unified computer_batch with ghost: true. " +
        "Executes a declarative sequence of UI action steps inside Session 2 without interfering with the local user.")]
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
        [Description("Alias of timeoutMs")] int? timeout_ms = null,
        [Description("Optional client operation ID for retry deduplication")] string? operationId = null,
        [Description("Alias of operationId")] string? operation_id = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostBatch");

        // Schema validation first: an empty or ambiguous request must be
        // rejected before any session or desktop work happens.
        if (steps is { Length: > 0 } && set is { Length: > 0 })
        {
            return ErrorResult("Malformed",
                "'steps' and 'set' are mutually exclusive — specify either an " +
                "ordered step list or a compact form list, not both");
        }
        if (steps is null or { Length: 0 } && set is null or { Length: 0 })
        {
            return ErrorResult("Malformed",
                "'steps' or 'set' is required — nothing to execute");
        }

        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        // Steps run against the isolated desktop primitives. Semantic
        // targets (uia_*/elementId) need a ghost-desktop UIA pass that is
        // not wired yet, so those are refused explicitly instead of
        // silently landing on the user's focused window.
        var failFastOn = failFast ?? true;
        var results = new List<object>();
        var completed = 0;
        string? failure = null;

        if (set is { Length: > 0 })
        {
            failure = "compact form 'set' on the isolated desktop is not wired yet — " +
                "use explicit 'steps' (type/click/key/wait) against the ghost desktop";
        }
        else
        {
            var desktopName = GetOrCreateDesktop().DesktopName;
            foreach (var step in steps!)
            {
                var doWhat = (step.Do ?? step.Action ?? "").Trim().ToLowerInvariant();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                string? stepError = null;
                // BatchStep carries x/y inside its Target object, not as
                // first-class fields — unwrap it once per step.
                var target = step.Target is { } tEl ? tEl : default(JsonElement?);
                try
                {
                    switch (doWhat)
                    {
                        case "wait":
                            await Task.Delay(
                                Math.Clamp(step.Ms ?? 100, 0, 60_000), ct).ConfigureAwait(false);
                            break;
                        case "type":
                            {
                                var text = step.V
                                    ?? (step.Text is { ValueKind: JsonValueKind.String } te
                                        ? te.GetString() : null);
                                if (string.IsNullOrEmpty(text))
                                    stepError = "type step needs text ('v')";
                                else
                                    GetOrCreateInput().SendText(text);
                                break;
                            }
                        case "click":
                            {
                                if (step.T is { } named && !string.IsNullOrWhiteSpace(named))
                                    stepError = "targeted click is not supported on the " +
                                        "isolated desktop — pass target:{x,y} coordinates";
                                else if (target is { ValueKind: JsonValueKind.Object } tj
                                    && tj.TryGetProperty("x", out var xe)
                                    && tj.TryGetProperty("y", out var ye)
                                    && xe.TryGetInt32(out var gx)
                                    && ye.TryGetInt32(out var gy))
                                {
                                    var btnName = tj.TryGetProperty("button", out var be)
                                        && be.ValueKind == JsonValueKind.String ? be.GetString() : null;
                                    var dbl = tj.TryGetProperty("doubleClick", out var de)
                                        && de.ValueKind == JsonValueKind.True;
                                    GetOrCreateInput().SendMouseClick(gx, gy, ParseMouseButton(btnName), dbl);
                                }
                                else
                                {
                                    stepError = "click step needs coordinates " +
                                        "(target:{x,y}) on the isolated desktop";
                                }
                                break;
                            }
                        case "scroll":
                            {
                                static int Coord(JsonElement? el, string name) =>
                                    el is { ValueKind: JsonValueKind.Object } o
                                    && o.TryGetProperty(name, out var v)
                                    && v.TryGetInt32(out var i) ? i : 0;
                                GetOrCreateInput().SendMouseScroll(
                                    Coord(target, "x"), Coord(target, "y"), step.Ms ?? 0);
                                break;
                            }
                        case "key":
                        case "press_key":
                            {
                                var key = GhostKeyMap.Parse(step.V ?? step.T ?? "");
                                if (key is null) stepError = $"unknown key '{step.V ?? step.T}'";
                                else GetOrCreateInput().SendKeyPress(key.Value);
                                break;
                            }
                        default:
                            stepError = $"'{doWhat}' is not executable on the isolated desktop " +
                                "(supported: wait, type, click, scroll, key)";
                            break;
                    }
                }
                catch (Exception ex) { stepError = ex.Message; }
                watch.Stop();

                results.Add(new
                {
                    index = completed,
                    action = doWhat,
                    ok = stepError is null,
                    error = stepError,
                    ms = (int)watch.ElapsedMilliseconds,
                });
                if (stepError is not null)
                {
                    failure = $"step {completed} ({doWhat}) failed: {stepError}";
                    if (failFastOn) break;
                }
                else completed++;
            }

            if (failure is null)
            {
                return SuccessResult(new
                {
                    success = true,
                    action = "batch",
                    desktop = desktopName,
                    stepsCompleted = completed,
                    totalSteps = results.Count,
                    results,
                });
            }
        }

        return ErrorResult("BatchFailed", failure);
    }

    /// <summary>Minimal key-name → <see cref="KeyCode"/> mapping for ghost
    /// batch steps. Anything the enum already names ("A".."Z", "F1".."F12",
    /// digits, "Left", "Enter"…) resolves through Enum parsing; the short
    /// aliases below cover the spellings models actually emit.</summary>
    private static class GhostKeyMap
    {
        private static readonly Dictionary<string, KeyCode> Aliases =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["esc"] = KeyCode.Escape,
                ["return"] = KeyCode.Enter,
                ["del"] = KeyCode.Delete,
                ["pgup"] = KeyCode.PageUp,
                ["pgdn"] = KeyCode.PageDown,
                ["page_up"] = KeyCode.PageUp,
                ["page_down"] = KeyCode.PageDown,
                ["spacebar"] = KeyCode.Space,
                ["arrowup"] = KeyCode.Up,
                ["arrowdown"] = KeyCode.Down,
                ["arrowleft"] = KeyCode.Left,
                ["arrowright"] = KeyCode.Right,
            };

        public static KeyCode? Parse(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var key = name.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
            if (Aliases.TryGetValue(key, out var mapped)) return mapped;
            // "Up"/"Down"/"PageUp" enum spellings with no separators
            return Enum.TryParse<KeyCode>(key, ignoreCase: true, out var parsed) ? parsed : null;
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
        var sessionStatus = await GetEffectiveSessionStatusAsync(ct).ConfigureAwait(false);

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
            pipActive = _pipManager.IsRunning,
            windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null,
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

    /// <summary>
    /// Window listing for the isolated Ghost Desktop — enumerates the
    /// ghost desktop directly. Forwarding to computer_windows(ghost:true)
    /// would route right back here through the coordinator.
    /// </summary>
    [McpServerTool(Name = "ghost_desktop_status"), Description(
        "Returns the operational status and active windows of the isolated Ghost Desktop.")]
    public Task<CallToolResult> GhostDesktopStatus(CancellationToken ct = default)
    {
        PerfTrace.Count("ghostDesktopStatus");
        return ExecuteGhostWindowsAsync(ct);
    }

    /// <summary>
    /// Process launch on the isolated Ghost Desktop — the process is
    /// created with the ghost desktop as its startup desktop. Forwarding
    /// to computer_launch(ghost:true) would route back here.
    /// </summary>
    [McpServerTool(Name = "ghost_desktop_launch"), Description(
        "Launches an application or command-line process directly on the isolated Ghost Desktop without interfering with the user's desktop.")]
    public Task<CallToolResult> GhostDesktopLaunch(
        [Description("Executable path or command name to run (e.g. 'cmd.exe', 'notepad.exe', 'chrome.exe')")] string program,
        [Description("Optional command-line arguments")] string? args = null,
        [Description("Optional working directory")] string? workingDir = null,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostDesktopLaunch");
        if (string.IsNullOrWhiteSpace(program))
        {
            return Task.FromResult(ErrorResult("Malformed",
                "'program' must not be null or empty."));
        }
        return ExecuteGhostLaunchAsync(program, args, workingDir, ct);
    }

    /// <summary>
    /// Legacy shim for mouse clicks on the isolated Ghost Desktop.
    /// Executes directly through the ghost-desktop input primitives — it
    /// must never forward back into computer_click(ghost:true), whose
    /// ghost path routes right back here.
    /// </summary>
    [McpServerTool(Name = "ghost_desktop_click"), Description(
        "Performs a mouse click at the specified coordinates on the isolated Ghost Desktop without interfering with the user's desktop.")]
    public Task<CallToolResult> GhostDesktopClick(
        [Description("Physical X coordinate on the ghost desktop (0 to 1919)")] int x,
        [Description("Physical Y coordinate on the ghost desktop (0 to 1079)")] int y,
        [Description("Mouse button: left|right|middle (default: left)")] string? button = "left",
        [Description("Whether to perform a double-click (default: false)")] bool? doubleClick = false,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostDesktopClick");
        return ExecuteGhostClickAsync(x, y, button, doubleClick == true, ct);
    }

    /// <summary>
    /// Types text into the focused window on the isolated Ghost Desktop —
    /// executed through the ghost input primitives, never via
    /// computer_type(ghost:true) (same cycle as ghost_desktop_click).
    /// </summary>
    [McpServerTool(Name = "ghost_desktop_type"), Description(
        "Types text into the active/focused window on the isolated Ghost Desktop.")]
    public Task<CallToolResult> GhostDesktopType(
        [Description("Text string to type into the ghost desktop")] string text,
        CancellationToken ct = default)
    {
        PerfTrace.Count("ghostDesktopType");
        return ExecuteGhostTypeAsync(text ?? "", ct);
    }

    /// <summary>
    /// Screenshot of the isolated Ghost Desktop. Reads the latest frame written
    /// by the Ghost Control daemon's desktop capture pipeline instead of routing
    /// back through computer_screenshot(ghost:true).
    /// </summary>
    [McpServerTool(Name = "ghost_desktop_screenshot"), Description(
        "Captures a screenshot from the isolated Ghost Desktop frame buffer.")]
    public async Task<CallToolResult> GhostDesktopScreenshot(CancellationToken ct = default)
    {
        PerfTrace.Count("ghostDesktopScreenshot");
        if (await RequireActiveSessionAsync(ct).ConfigureAwait(false) is { } inactive)
            return inactive;

        var png = TryReadSharedFrameToPng(out var width, out var height, out var frameIndex);
        if (png == null)
        {
            return ErrorResult("CaptureUnavailable",
                "no ghost desktop frame is available yet — ensure the Ghost Control daemon " +
                "is running with desktop capture enabled");
        }

        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(new
                    {
                        success = true,
                        action = "screenshot",
                        desktop = GetBackendDesktopName(),
                        width,
                        height,
                        frameIndex,
                        bytes = png.Length,
                    }, SerializerOptions)
                },
                ImageContentBlock.FromBytes(png, "image/png"),
            ]
        };
    }

    /// <summary>
    /// Alias for computer_ghost_control. Opens or controls the Inbrisk Ghost Control suite (System Tray daemon, PiP window, isolated Ghost Desktop).
    /// </summary>
    [McpServerTool(Name = "ghost_control_open"), Description(
        "Alias for computer_ghost_control. Opens or controls the Inbrisk Ghost Control suite (System Tray daemon, PiP window, isolated Ghost Desktop). Starts the daemon if not running.")]
    public async Task<CallToolResult> GhostControlOpen(
        [Description("Action to perform: 'open'|'tray' (starts daemon if not running), 'pip' (configures PiP), 'status' (queries status), 'stop' (stops daemon). Default: 'open'")] string? action = "open",
        [Description("Toggle click-through transparent mode (true = clickable/interactive, false = click-through transparent).")] bool? interactive = null,
        [Description("Toggle full-screen Shadow Mode covering the primary display.")] bool? fullScreen = null,
        [Description("Alias of fullScreen")] bool? fullscreen = null,
        [Description("Alias of fullScreen")] bool? full_screen = null,
        [Description("Preset anchor position: TopLeft | TopRight | BottomLeft | BottomRight")] string? position = null,
        [Description("Alpha opacity: 50 | 80 | 100 (or 0-255 / percentage)")] byte? opacity = null,
        [Description("Window scale multiplier (e.g. 1.0, 1.5, 2.0; default: 1.0)")] float? scale = null,
        CancellationToken ct = default)
    {
        return await ComputerGhostControl(action, interactive, fullScreen, fullscreen, full_screen, position, opacity, scale, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sole presentation tool for Ghost PiP and daemon. Controls the Ghost OS desktop, tray daemon, and PiP overlay.
    /// </summary>
    [McpServerTool(Name = "computer_ghost_control"), Description(
        "Sole presentation tool for Ghost PiP and daemon. Controls the Ghost OS desktop, tray daemon, and PiP overlay (action: open|tray|pip|status|stop, interactive, fullscreen, position, opacity, scale).")]
    public async Task<CallToolResult> ComputerGhostControl(
        [Description("Action to perform: 'open'|'tray' (starts daemon if not running), 'pip' (configures PiP), 'status' (queries status), 'stop' (stops daemon). Default: 'open'")] string? action = "open",
        [Description("Toggle click-through transparent mode (true = clickable/interactive, false = click-through transparent).")] bool? interactive = null,
        [Description("Toggle full-screen Shadow Mode covering the primary display.")] bool? fullScreen = null,
        [Description("Alias of fullScreen")] bool? fullscreen = null,
        [Description("Alias of fullScreen")] bool? full_screen = null,
        [Description("Preset anchor position: TopLeft | TopRight | BottomLeft | BottomRight")] string? position = null,
        [Description("Alpha opacity: 50 | 80 | 100 (or 0-255 / percentage)")] byte? opacity = null,
        [Description("Window scale multiplier (e.g. 1.0, 1.5, 2.0; default: 1.0)")] float? scale = null,
        CancellationToken ct = default)
    {
        return await ExecuteGhostControlAsync(action, interactive, fullScreen ?? fullscreen ?? full_screen, position, opacity, scale, ct).ConfigureAwait(false);
    }

    private async Task<CallToolResult> ExecuteGhostControlAsync(
        string? action,
        bool? interactive,
        bool? fullScreen,
        string? position,
        byte? opacity,
        float? scale,
        CancellationToken ct)
    {
        PerfTrace.Count("ghostControlOpen");
        string act = (action ?? "open").Trim().ToLowerInvariant();

        if (EmergencyGate.IsStopped && act is not ("stop" or "status"))
        {
            return EmergencyResult();
        }

        if (!string.IsNullOrWhiteSpace(position) &&
            !Enum.TryParse<PipPresetPosition>(position, ignoreCase: true, out _))
        {
            return ErrorResult("Malformed", $"Unknown PiP position preset: '{position}'.");
        }

        var controlDaemon = TryReadGhostControlDaemonInfo();
        string controlPipeName = controlDaemon?.PipeName ?? "inbrisk_ghost_control";
        string? controlToken = GhostControlToken();

        // 1. Check if the daemon is already running via its control pipe. If the
        // state file proves a live daemon but IPC is still warming up, wait a bit
        // longer rather than launching a duplicate tray process.
        bool isDaemonRunning = false;
        GhostIpcClient? testClient = null;
        try
        {
            testClient = await ConnectGhostControlAsync(controlPipeName, TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (testClient == null && IsGhostControlDaemonAlive(controlDaemon))
            {
                testClient = await ConnectGhostControlAsync(controlPipeName, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            }
            isDaemonRunning = testClient?.IsConnected == true;
            if (testClient?.IsConnected == true)
            {
                using (testClient)
                {
                    if (act == "stop")
                    {
                        var stopResp = await testClient.SendRequestAsync("stop", JsonSerializer.Serialize(new { token = controlToken }), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                        object? respObj = null;
                        try { using var d = JsonDocument.Parse(stopResp); respObj = d.RootElement.Clone(); } catch { respObj = stopResp; }
                        return SuccessResult(new
                        {
                            success = true,
                            action = "stop",
                            sessionId = (int?)null,
                            pipActive = false,
                            windowHandle = (string?)null,
                            state = "stopped",
                            message = "Ghost Control daemon stopped.",
                            response = respObj
                        });
                    }

                    var actionsTaken = new List<string>();

                    if (interactive.HasValue)
                    {
                        var p = JsonSerializer.Serialize(new { interactive = interactive.Value, token = controlToken });
                        await testClient.SendRequestAsync("pip_interactive", p, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        actionsTaken.Add($"interactive={(interactive.Value ? "on" : "off")}");
                    }

                    if (fullScreen.HasValue)
                    {
                        var p = JsonSerializer.Serialize(new { fullscreen = fullScreen.Value, token = controlToken });
                        await testClient.SendRequestAsync("pip_fullscreen", p, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        actionsTaken.Add($"fullscreen={fullScreen.Value}");
                    }

                    if (!string.IsNullOrWhiteSpace(position))
                    {
                        var p = JsonSerializer.Serialize(new { position, token = controlToken });
                        await testClient.SendRequestAsync("pip_position", p, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        actionsTaken.Add($"position={position}");
                    }

                    if (opacity.HasValue)
                    {
                        var p = JsonSerializer.Serialize(new { opacity = opacity.Value, token = controlToken });
                        await testClient.SendRequestAsync("pip_opacity", p, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        actionsTaken.Add($"opacity={opacity.Value}");
                    }

                    if (scale.HasValue)
                    {
                        var p = JsonSerializer.Serialize(new { scale = scale.Value, token = controlToken });
                        await testClient.SendRequestAsync("pip_scale", p, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                        actionsTaken.Add($"scale={scale.Value}");
                    }

                    string statusJson = await testClient.SendRequestAsync("status", "{}", TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);

                    int? daemonSessionId = null;
                    bool daemonPipActive = false;
                    string? daemonWindowHandle = null;
                    object? parsedStatus = null;

                    try
                    {
                        using var doc = JsonDocument.Parse(statusJson);
                        parsedStatus = doc.RootElement.Clone();

                        if (doc.RootElement.TryGetProperty("session", out var sessProp) &&
                            sessProp.TryGetProperty("sessionId", out var sidProp) &&
                            sidProp.ValueKind == JsonValueKind.Number)
                        {
                            daemonSessionId = sidProp.GetInt32();
                        }

                        if (doc.RootElement.TryGetProperty("pip", out var pipProp))
                        {
                            if (pipProp.TryGetProperty("running", out var runProp) &&
                                (runProp.ValueKind == JsonValueKind.True || runProp.ValueKind == JsonValueKind.False))
                            {
                                daemonPipActive = runProp.GetBoolean();
                            }
                            if (pipProp.TryGetProperty("hwnd", out var hwndProp) &&
                                hwndProp.ValueKind == JsonValueKind.String)
                            {
                                daemonWindowHandle = hwndProp.GetString();
                            }
                        }
                    }
                    catch
                    {
                        parsedStatus = statusJson;
                    }

                    return SuccessResult(new
                    {
                        success = true,
                        daemonRunning = true,
                        sessionId = daemonSessionId ?? (_sessionManager as GhostSessionManager)?.CurrentSessionId,
                        pipActive = daemonPipActive || _pipManager.IsRunning,
                        windowHandle = daemonWindowHandle ?? (_pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null),
                        state = "running",
                        pipeName = "inbrisk_ghost_control",
                        actionsApplied = actionsTaken,
                        message = actionsTaken.Count > 0
                            ? $"Applied settings to running Ghost Control: {string.Join(", ", actionsTaken)}"
                            : "Ghost Control daemon is already active and running.",
                        status = parsedStatus
                    });
                }
            }
        }
        catch { }
        finally
        {
            try { testClient?.Dispose(); } catch { }
        }

        if (!isDaemonRunning && IsGhostControlDaemonAlive(controlDaemon))
        {
            if (act == "stop")
            {
                try
                {
                    using var daemonProc = Process.GetProcessById(controlDaemon!.Pid);
                    daemonProc.Kill(entireProcessTree: true);
                    daemonProc.WaitForExit(3000);
                }
                catch { }

                return SuccessResult(new
                {
                    success = true,
                    action = "stop",
                    daemonRunning = false,
                    sessionId = (int?)null,
                    pipActive = false,
                    windowHandle = (string?)null,
                    state = "stopped",
                    pipeConnected = false,
                    message = "Ghost Control daemon process stopped (control pipe was not reachable)."
                });
            }

            var startingStatus = await GetEffectiveSessionStatusAsync(ct).ConfigureAwait(false);
            return SuccessResult(new
            {
                success = true,
                daemonRunning = true,
                sessionId = startingStatus.SessionId,
                pipActive = _pipManager.IsRunning,
                windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null,
                state = startingStatus.IsActive ? startingStatus.State : "starting",
                pipeName = controlPipeName,
                pipeConnected = false,
                message = "Ghost Control daemon process is already running; control IPC is still initializing."
            });
        }

        if (act == "stop")
        {
            return SuccessResult(new
            {
                success = true,
                action = "stop",
                sessionId = (int?)null,
                pipActive = false,
                windowHandle = (string?)null,
                state = "stopped",
                message = "Ghost Control daemon is not running."
            });
        }

        if (act == "status")
        {
            var localStatus = await GetEffectiveSessionStatusAsync(ct).ConfigureAwait(false);
            return SuccessResult(new
            {
                success = true,
                daemonRunning = false,
                sessionId = localStatus.SessionId,
                pipActive = _pipManager.IsRunning,
                windowHandle = _pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null,
                state = localStatus.IsActive ? localStatus.State : "stopped",
                message = localStatus.IsActive
                    ? "Ghost session is active in-process; Ghost Control daemon is not running."
                    : "Ghost Control daemon is not running."
            });
        }

        // 2. Daemon not running -> Start in background via Process.Start("inbrisk.exe", "ghost tray")
        try
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            }

            var argsList = new List<string> { "ghost", "tray", "--no-session" };
            if (interactive == true) argsList.Add("--interactive on");
            else if (interactive == false) argsList.Add("--no-interactive");

            if (fullScreen == true) argsList.Add("--fullscreen");
            if (!string.IsNullOrWhiteSpace(position)) argsList.Add($"--position {position}");
            if (opacity.HasValue) argsList.Add($"--opacity {opacity.Value}");
            if (scale.HasValue) argsList.Add(FormattableString.Invariant($"--scale {Math.Clamp(scale.Value, 0.25f, 5.0f)}"));

            string startArgs = string.Join(" ", argsList);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = startArgs,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            var proc = Process.Start(psi);

            // Wait briefly for pipe to warm up
            var sw = Stopwatch.StartNew();
            bool connected = false;
            int? launchedSessionId = null;
            bool launchedPipActive = false;
            string? launchedHwnd = null;
            object? parsedStatus = null;

            while (sw.ElapsedMilliseconds < 3500 && !ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                GhostIpcClient? check = null;
                try
                {
                    check = new GhostIpcClient(controlPipeName);
                    await check.ConnectAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                    if (check.IsConnected)
                    {
                        connected = true;
                        try
                        {
                            string statusResp = await check.SendRequestAsync("status", "{}", TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                            using var doc = JsonDocument.Parse(statusResp);
                            parsedStatus = doc.RootElement.Clone();

                            if (doc.RootElement.TryGetProperty("session", out var sessProp) &&
                                sessProp.TryGetProperty("sessionId", out var sidProp) &&
                                sidProp.ValueKind == JsonValueKind.Number)
                            {
                                launchedSessionId = sidProp.GetInt32();
                            }

                            if (doc.RootElement.TryGetProperty("pip", out var pipProp))
                            {
                                if (pipProp.TryGetProperty("running", out var runProp) &&
                                    (runProp.ValueKind == JsonValueKind.True || runProp.ValueKind == JsonValueKind.False))
                                {
                                    launchedPipActive = runProp.GetBoolean();
                                }
                                if (pipProp.TryGetProperty("hwnd", out var hwndProp) &&
                                    hwndProp.ValueKind == JsonValueKind.String)
                                {
                                    launchedHwnd = hwndProp.GetString();
                                }
                            }
                        }
                        catch { }
                        break;
                    }
                }
                catch { }
                finally
                {
                    try { check?.Dispose(); } catch { }
                }
            }

            return SuccessResult(new
            {
                success = true,
                started = true,
                processId = proc?.Id,
                sessionId = launchedSessionId ?? (_sessionManager as GhostSessionManager)?.CurrentSessionId,
                pipActive = launchedPipActive || _pipManager.IsRunning,
                windowHandle = launchedHwnd ?? (_pipManager.WindowHandle != IntPtr.Zero ? $"0x{_pipManager.WindowHandle:X}" : null),
                executable = exe,
                arguments = startArgs,
                pipeConnected = connected,
                status = parsedStatus,
                message = connected
                    ? "Ghost Control daemon successfully started and listening."
                    : "Ghost Control process launched. Initializing background session and PiP overlay."
            });
        }
        catch (Exception ex)
        {
            return ErrorResult("LaunchFailed", $"Failed to start Ghost Control daemon: {ex.Message}");
        }
    }

    #endregion

    #region Helper Methods

    private sealed record GhostControlDaemonInfo(
        int Pid,
        DateTime? StartTimeUtc,
        string? Token,
        string PipeName,
        string DesktopName,
        string? InstanceId,
        string? Status,
        string? Backend,
        DateTime? LastHeartbeatUtc);

    private static GhostControlDaemonInfo? TryReadGhostControlDaemonInfo()
    {
        try
        {
            var path = Path.Combine(UserSettings.DataDir, "ghost-control.json");
            if (!File.Exists(path)) return null;

            JsonDocument doc;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                doc = JsonDocument.Parse(reader.ReadToEnd());
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("pid", out var pidProp) || pidProp.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }

                DateTime? startedAt = null;
                if (root.TryGetProperty("startTimeUtc", out var startProp) &&
                    DateTime.TryParse(startProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedStart))
                {
                    startedAt = parsedStart;
                }

                var token = root.TryGetProperty("token", out var tokenProp) &&
                    tokenProp.ValueKind == JsonValueKind.String
                    ? tokenProp.GetString()
                    : null;
                var pipeName = root.TryGetProperty("pipeName", out var pipeProp) &&
                    pipeProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(pipeProp.GetString())
                    ? pipeProp.GetString()!
                    : "inbrisk_ghost_control";
                var desktopName = root.TryGetProperty("desktopName", out var desktopProp) &&
                    desktopProp.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(desktopProp.GetString())
                    ? desktopProp.GetString()!
                    : GhostDesktopNative.DefaultGhostDesktopName;
                var instanceId = root.TryGetProperty("instanceId", out var instanceProp) &&
                    instanceProp.ValueKind == JsonValueKind.String
                    ? instanceProp.GetString()
                    : null;
                var status = root.TryGetProperty("status", out var statusProp) &&
                    statusProp.ValueKind == JsonValueKind.String
                    ? statusProp.GetString()
                    : null;
                var backend = root.TryGetProperty("backend", out var backendProp) &&
                    backendProp.ValueKind == JsonValueKind.String
                    ? backendProp.GetString()
                    : null;
                DateTime? heartbeat = root.TryGetProperty("lastHeartbeatUtc", out var heartbeatProp) &&
                    DateTime.TryParse(heartbeatProp.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedHeartbeat)
                    ? parsedHeartbeat
                    : null;

                return new GhostControlDaemonInfo(pidProp.GetInt32(), startedAt, token, pipeName,
                    desktopName, instanceId, status, backend, heartbeat);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool IsGhostControlDaemonAlive(GhostControlDaemonInfo? daemon)
    {
        if (daemon == null || daemon.Pid <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(daemon.Pid);
            if (process.HasExited)
            {
                return false;
            }

            return !daemon.StartTimeUtc.HasValue ||
                Math.Abs((process.StartTime.ToUniversalTime() - daemon.StartTimeUtc.Value).TotalSeconds) <= 2;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<GhostIpcClient?> ConnectGhostControlAsync(
        string pipeName,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var client = new GhostIpcClient(pipeName);
        try
        {
            await client.ConnectAsync(timeout, ct).ConfigureAwait(false);
            return client.IsConnected ? client : null;
        }
        catch
        {
            try { client.Dispose(); } catch { }
            return null;
        }
    }

    private static byte[]? TryReadSharedFrameToPng(out int width, out int height, out long frameIndex)
    {
        width = 1920;
        height = 1080;
        frameIndex = 0;

        try
        {
            if (GhostSharedFrameBuffer.TryOpenConsumer(out var consumer, GhostSharedFrameBuffer.DefaultMapName) && consumer != null)
            {
                using (consumer)
                {
                    int approxSize = 1920 * 1080 * 4;
                    byte[] buffer = new byte[approxSize];
                    if (consumer.TryReadLatestFrame(buffer, out var header))
                    {
                        width = header.Width;
                        height = header.Height;
                        frameIndex = header.FrameIndex;
                        return ConvertRawBgraToPng(header.Width, header.Height, header.Stride, buffer);
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private static byte[] ConvertRawBgraToPng(int width, int height, int stride, byte[] rawBgra)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = Math.Min(stride, bmpData.Stride);
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(rawBgra, y * stride, IntPtr.Add(bmpData.Scan0, y * bmpData.Stride), rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static CallToolResult EmergencyResult() =>
        ErrorResult("EmergencyStopped", "Computer control is stopped by the local emergency hotkey; only the local user can resume.");

    private static string? GhostControlToken()
    {
        return TryReadGhostControlDaemonInfo()?.Token;
    }

    private static CallToolResult SuccessResult(object data) => new()
    {
        IsError = false,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(data, SerializerOptions) }]
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct GhostRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out GhostRect lpRect);

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

        lock (_desktopGate)
        {
            if (_localDesktop != null)
            {
                try { _localDesktop.Dispose(); } catch { }
                _localDesktop = null;
            }

            if (_desktopInput != null)
            {
                try { _desktopInput.Dispose(); } catch { }
                _desktopInput = null;
            }
        }

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
