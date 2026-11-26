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

public sealed partial class GhostMcpBridge : IDisposable
{
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
}
