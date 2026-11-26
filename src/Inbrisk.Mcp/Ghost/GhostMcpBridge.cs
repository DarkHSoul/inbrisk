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
/// Ghost OS MCP Bridge.
/// Exposes and dispatches isolated background computer control tools for Inbrisk MCP.
/// Coordinates Session 2 loopback RDP, zero-focus transparent PiP display, and high-speed cross-session IPC.
/// </summary>
[McpServerToolType]
public sealed partial class GhostMcpBridge : IDisposable
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
}
