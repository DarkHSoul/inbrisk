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
}
