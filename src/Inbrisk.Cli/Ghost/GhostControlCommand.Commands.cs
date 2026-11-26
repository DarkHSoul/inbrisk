using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Cli.Ui;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Runtime;

namespace Inbrisk.Cli;

public static partial class GhostControlCommand
{


    /// <summary>
    /// Executes the <c>ghost pip</c> command: dispatches changes to an active Ghost OS tray host,
    /// or starts a standalone PiP window viewer if no active host is running.
    /// </summary>
    public static async Task<int> RunPipAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        // 1. Check if a live Ghost Tray host is listening on the control named pipe
        bool connected = false;
        GhostIpcClient? client = null;

        try
        {
            client = new GhostIpcClient(ReadControlState()?.PipeName ?? options.ControlPipeName);
            await client.ConnectAsync(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            connected = client.IsConnected;
        }
        catch
        {
            connected = false;
        }

        if (connected && client != null)
        {
            await using (client)
            {
                bool anyActionExecuted = false;

                // A. Interactive mode change
                if (options.Interactive.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { interactive = options.Interactive.Value, token = ControlToken() });
                    var resp = await client.SendRequestAsync("pip_interactive", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (ResponseOk(resp))
                        LogSuccess($"[Ghost OS] PiP interactive mode set to: {(options.Interactive.Value ? "ON (Clickable)" : "OFF (Click-through)")}", options);
                    else
                        LogWarning("[Ghost OS] PiP interactive request was rejected by the daemon.", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // B. Full-screen toggle / set
                if (options.FullScreen.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { fullscreen = options.FullScreen.Value, token = ControlToken() });
                    var resp = await client.SendRequestAsync("pip_fullscreen", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (ResponseOk(resp))
                        LogSuccess($"[Ghost OS] PiP full-screen display set to: {(options.FullScreen.Value ? "ON (Shadow Mode)" : "OFF (Mini PiP)")}", options);
                    else
                        LogWarning("[Ghost OS] PiP full-screen request was rejected by the daemon.", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // C. Position change
                if (options.Position.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { position = options.Position.Value.ToString(), token = ControlToken() });
                    var resp = await client.SendRequestAsync("pip_position", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (ResponseOk(resp))
                        LogSuccess($"[Ghost OS] PiP anchor position set to: {options.Position.Value}", options);
                    else
                        LogWarning("[Ghost OS] PiP position request was rejected by the daemon.", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // D. Opacity change
                if (options.Opacity.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { opacity = options.Opacity.Value, token = ControlToken() });
                    var resp = await client.SendRequestAsync("pip_opacity", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    int pct = (int)Math.Round(options.Opacity.Value * 100.0 / 255.0);
                    if (ResponseOk(resp))
                        LogSuccess($"[Ghost OS] PiP opacity set to: {pct}% (alpha {options.Opacity.Value})", options);
                    else
                        LogWarning("[Ghost OS] PiP opacity request was rejected by the daemon.", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // E. Scale change
                if (options.Scale.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { scale = options.Scale.Value, token = ControlToken() });
                    var resp = await client.SendRequestAsync("pip_scale", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (ResponseOk(resp))
                        LogSuccess($"[Ghost OS] PiP scale multiplier set to: {options.Scale.Value:F2}x", options);
                    else
                        LogWarning("[Ghost OS] PiP scale request was rejected by the daemon.", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // If no specific mutation parameter was supplied, query and display live status
                if (!anyActionExecuted)
                {
                    var statusJson = await client.SendRequestAsync("status", "{}", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    if (options.JsonOutput)
                    {
                        Console.WriteLine(statusJson);
                    }
                    else
                    {
                        FormatStatusOutput(statusJson);
                    }
                }

                return 0;
            }
        }

        // 2. Fallback: No active Tray daemon found -> Launch Standalone PiP Overlay
        LogInfo($"No active Ghost Tray host detected on \\\\.\\pipe\\{options.ControlPipeName}.", options);
        LogInfo("Starting standalone Ghost PiP display overlay...", options);

        using var standaloneCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            try { standaloneCts.Cancel(); } catch { }
        };

        var config = new PipConfig(
            Position: options.Position ?? PipPresetPosition.TopRight,
            Scale: options.Scale ?? 1.0f,
            Interactive: options.Interactive ?? false,
            Opacity: options.Opacity ?? 255,
            TargetFps: options.TargetFps,
            Title: "Inbrisk Ghost PiP (Standalone)");

        using var standalonePip = new GhostPipManager();
        standalonePip.StartPip(config);

        var expander = new GhostPipExpander(standalonePip.Renderer);
        if (options.FullScreen == true && standalonePip.WindowHandle != IntPtr.Zero)
        {
            expander.ExpandToFullScreen(standalonePip.WindowHandle, 0, false);
        }

        LogSuccess($"Standalone PiP running ({standalonePip.Width}x{standalonePip.Height} at {standalonePip.CurrentPosition}). Press Ctrl+C to close.", options);

        while (!standaloneCts.Token.IsCancellationRequested && standalonePip.IsRunning)
        {
            try
            {
                await Task.Delay(500, standaloneCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        LogInfo("Standalone PiP viewer closed.", options);
        return 0;
    }



    /// <summary>
    /// Queries and reports the live operational status of the Ghost OS session and PiP overlay.
    /// </summary>
    public static async Task<int> RunStatusAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        // Try querying live daemon first
        try
        {
            await using var client = new GhostIpcClient(ReadControlState()?.PipeName ?? options.ControlPipeName);
            await client.ConnectAsync(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            if (client.IsConnected)
            {
                var resp = await client.SendRequestAsync("status", "{}", TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                if (options.JsonOutput)
                {
                    Console.WriteLine(resp);
                }
                else
                {
                    FormatStatusOutput(resp);
                }
                return 0;
            }
        }
        catch { }

        // Fallback: distinguish an unreachable IPC endpoint from "not running".
        // The daemon records its PID/start time before the pipe comes online and
        // owns the named local desktop independently of any RDP/WTS session.
        var existingSession = GhostWtsInterop.FindSessionByUsername(options.Username);
        bool rdpActive = existingSession != null &&
            (existingSession.State == WTS_CONNECTSTATE_CLASS.WTSActive || existingSession.State == WTS_CONNECTSTATE_CLASS.WTSConnected);

        var daemonState = ReadControlState();
        bool daemonAlive = daemonState != null && ControlStateAlive(daemonState);
        bool daemonReady = daemonAlive &&
            string.Equals(daemonState?.Status, "Ready", StringComparison.OrdinalIgnoreCase);
        bool localDesktopActive = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var hDesktop = GhostDesktopNative.OpenDesktop(
                daemonState?.DesktopName ?? GhostDesktopNative.DefaultGhostDesktopName,
                GhostDesktopNative.DESKTOP_READOBJECTS | GhostDesktopNative.DESKTOP_ENUMERATE,
                false);
            if (hDesktop != IntPtr.Zero)
            {
                localDesktopActive = true;
                GhostDesktopNative.CloseDesktop(hDesktop);
            }
        }

        bool isActive = rdpActive || (daemonReady && localDesktopActive);
        string backendState = rdpActive
            ? existingSession!.State.ToString()
            : (isActive ? "LocalDesktop" : (daemonAlive ? "DaemonStarting" : "NotRunning"));

        if (options.JsonOutput)
        {
            var raw = new
            {
                ok = true,
                session = new
                {
                    active = isActive,
                    sessionId = existingSession?.SessionId,
                    username = existingSession?.UserName ?? (isActive ? Environment.UserName : options.Username),
                    state = backendState,
                    backend = rdpActive ? "rdp" : (isActive ? "localDesktop" : "none"),
                    desktop = daemonState?.DesktopName
                },
                pip = new { running = localDesktopActive },
                tray = new { running = daemonAlive, pipeConnected = false }
            };
            Console.WriteLine(JsonSerializer.Serialize(raw, JsonOptions));
        }
        else
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine("              INBRISK GHOST OS — SYSTEM STATUS                    ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"  Session State:    {(isActive ? "ACTIVE" : "INACTIVE")} ({backendState})");
            Console.WriteLine($"  Target User:      {options.Username}");
            if (existingSession != null)
            {
                Console.WriteLine($"  Session ID:       {existingSession.SessionId}");
                Console.WriteLine($"  Session Status:   {existingSession.State}");
            }
            Console.WriteLine($"  Tray Daemon:      {(daemonAlive ? "RUNNING (control IPC unavailable)" : "NOT RUNNING")}");
            Console.WriteLine($"  PiP Display:      {(localDesktopActive ? "LOCAL DESKTOP ACTIVE" : "INACTIVE")}");
            Console.WriteLine("==================================================================");
        }

        return 0;
    }



    /// <summary>
    /// Sends a shutdown request to the active Ghost OS daemon or terminates the headless RDP session directly.
    /// </summary>
    public static async Task<int> RunStopAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        LogInfo("Initiating Ghost OS shutdown sequence...", options);

        var daemonState = ReadControlState();

        // Try stopping via IPC first
        try
        {
            await using var client = new GhostIpcClient(daemonState?.PipeName ?? options.ControlPipeName);
            await client.ConnectAsync(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            if (client.IsConnected)
            {
                var resp = await client.SendRequestAsync("stop", JsonSerializer.Serialize(new { token = daemonState?.Token }), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (ResponseOk(resp))
                    LogSuccess("Shutdown signal sent to active Ghost OS daemon.", options);
                else
                    LogWarning("Shutdown request was rejected by the daemon; forcing local termination.", options);
                if (options.JsonOutput) Console.WriteLine(resp);

                if (daemonState != null && ControlStateAlive(daemonState))
                {
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
                    while (ControlStateAlive(daemonState) && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(250, ct).ConfigureAwait(false);
                    }
                    if (ControlStateAlive(daemonState))
                    {
                        try
                        {
                            Process.GetProcessById(daemonState.Pid).Kill(entireProcessTree: true);
                            LogWarning("Ghost daemon did not exit on its own; forced termination.", options);
                        }
                        catch { }
                    }
                }
                try { File.Delete(ControlStatePath); } catch { }

                // Run comprehensive cleanup
                try { await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false); } catch { }
                LogSuccess("Ghost OS credentials, temp files, and background processes thoroughly cleaned up.", options);
                return 0;
            }
        }
        catch { }

        // Fallback: terminate the recorded daemon process directly if it is still alive
        if (daemonState != null && ControlStateAlive(daemonState))
        {
            try
            {
                Process.GetProcessById(daemonState.Pid).Kill(entireProcessTree: true);
                LogSuccess($"Terminated Ghost daemon process {daemonState.Pid}.", options);
            }
            catch { }
        }
        try { File.Delete(ControlStatePath); } catch { }

        // Fallback: Terminate headless session directly via GhostRdpConnector
        try
        {
            var session = GhostWtsInterop.FindSessionByUsername(options.Username);
            if (session != null)
            {
                LogInfo($"Terminating session {session.SessionId} ({session.UserName})...", options);
                bool stopped = await GhostRdpConnector.StopHeadlessRdpSessionAsync(session.SessionId).ConfigureAwait(false);
                if (stopped)
                {
                    LogSuccess($"Headless session {session.SessionId} stopped successfully.", options);
                }
                else
                {
                    LogWarning($"Could not disconnect session {session.SessionId}.", options);
                }
            }
            else
            {
                LogInfo($"No active session found for user '{options.Username}'.", options);
            }
        }
        catch (Exception ex)
        {
            LogError($"Failed to stop headless session: {ex.Message}", options);
            return 1;
        }

        // Run comprehensive cleanup (RDP sessions + local isolated desktop)
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var hDesktop = GhostDesktopNative.OpenDesktop(
                    daemonState?.DesktopName ?? GhostDesktopNative.DefaultGhostDesktopName,
                    GhostDesktopNative.DESKTOP_ALL_ACCESS,
                    false);
                if (hDesktop != IntPtr.Zero)
                {
                    var windows = GhostDesktopNative.EnumerateWindows(hDesktop);
                    foreach (var hwnd in windows)
                    {
                        try { Win32.PostMessageW(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
                    }
                    GhostDesktopNative.CloseDesktop(hDesktop);
                    LogSuccess("Isolated Ghost Desktop windows and handles closed.", options);
                }
            }
            catch { }
        }

        try
        {
            await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false);
            LogSuccess("Ghost OS credentials, temp files, and background processes thoroughly cleaned up.", options);
        }
        catch (Exception ex)
        {
            LogWarning($"Warning during cleanup: {ex.Message}", options);
        }

        // Full teardown (--remove-user/--purge): delete the dedicated ghost
        // account and its stored machine credential. Routine stops keep the
        // account so the next start needs no admin rights.
        if (options.RemoveUser)
        {
            try
            {
                bool removed = await GhostUserProvisioner
                    .RemoveGhostUserAsync(options.Username).ConfigureAwait(false);
                if (removed)
                    LogSuccess($"Ghost user '{options.Username}' and stored credential removed.", options);
                else
                    LogWarning($"Ghost user '{options.Username}' could not be fully removed.", options);
            }
            catch (Exception ex)
            {
                LogWarning($"Ghost user removal failed: {ex.Message}", options);
            }
        }

        return 0;
    }
}
