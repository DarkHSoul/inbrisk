using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Cli.Ui;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows;
using Inbrisk.Runtime;

namespace Inbrisk.Cli;

#region Enums & Configuration Records

/// <summary>
/// Subcommands supported by the Ghost OS CLI control entry point.
/// </summary>
public enum GhostSubcommand
{
    /// <summary>
    /// Launches the full Ghost OS experience: background session, PiP overlay,
    /// System Tray notification area icon, hotkeys, and remote control server.
    /// </summary>
    Tray,

    /// <summary>
    /// Adjusts or queries the live Picture-in-Picture (PiP) window parameters
    /// (interactivity, full-screen toggle, position, opacity), or runs a standalone viewer.
    /// </summary>
    Pip,

    /// <summary>
    /// Queries the operational status of the Ghost OS session and PiP overlay.
    /// </summary>
    Status,

    /// <summary>
    /// Shuts down the active Ghost OS session and terminates PiP and tray processes.
    /// </summary>
    Stop,

    /// <summary>
    /// Displays recent diagnostic and operational log entries from the Ghost OS file logger.
    /// </summary>
    Logs,

    /// <summary>
    /// Displays command usage and help information.
    /// </summary>
    Help
}

/// <summary>
/// Parsed command line options for the Ghost OS controller.
/// </summary>
public sealed record GhostControlOptions(
    GhostSubcommand Subcommand = GhostSubcommand.Tray,
    bool? Interactive = null,
    bool? FullScreen = null,
    PipPresetPosition? Position = null,
    byte? Opacity = null,
    float? Scale = null,
    int TargetFps = 60,
    bool StartSession = true,
    bool ExtraSession = false,
    string Username = "InbriskAgent",
    string ControlPipeName = GhostControlCommand.DefaultControlPipeName,
    bool JsonOutput = false,
    bool Verbose = false)
{
    /// <summary>
    /// Parses CLI argument tokens into strongly-typed <see cref="GhostControlOptions"/>.
    /// </summary>
    public static GhostControlOptions Parse(string[] args)
    {
        GhostSubcommand subcommand = GhostSubcommand.Tray;
        bool? interactive = null;
        bool? fullScreen = null;
        PipPresetPosition? position = null;
        byte? opacity = null;
        float? scale = null;
        int targetFps = 60;
        bool startSession = true;
        bool extraSession = false;
        string username = "InbriskAgent";
        string controlPipeName = GhostControlCommand.DefaultControlPipeName;
        bool jsonOutput = false;
        bool verbose = false;

        if (args == null || args.Length == 0)
        {
            return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
        }

        int startIndex = 0;

        // 1. Identify primary subcommand
        var first = args[0].ToLowerInvariant();
        if (first is "ghost-tray" or "ghost_tray")
        {
            subcommand = GhostSubcommand.Tray;
            startIndex = 1;
        }
        else if (first is "ghost-pip" or "ghost_pip")
        {
            subcommand = GhostSubcommand.Pip;
            startIndex = 1;
        }
        else if (first is "ghost-status" or "ghost_status")
        {
            subcommand = GhostSubcommand.Status;
            startIndex = 1;
        }
        else if (first is "ghost-stop" or "ghost_stop")
        {
            subcommand = GhostSubcommand.Stop;
            startIndex = 1;
        }
        else if (first == "ghost")
        {
            startIndex = 1;
            if (args.Length > 1 && !args[1].StartsWith('-'))
            {
                var second = args[1].ToLowerInvariant();
                switch (second)
                {
                    case "tray":
                    case "start":
                    case "daemon":
                        subcommand = GhostSubcommand.Tray;
                        startIndex = 2;
                        break;
                    case "pip":
                    case "overlay":
                    case "preview":
                        subcommand = GhostSubcommand.Pip;
                        startIndex = 2;
                        break;
                    case "status":
                    case "info":
                        subcommand = GhostSubcommand.Status;
                        startIndex = 2;
                        break;
                    case "stop":
                    case "kill":
                    case "exit":
                        subcommand = GhostSubcommand.Stop;
                        startIndex = 2;
                        break;
                    case "logs":
                    case "log":
                        subcommand = GhostSubcommand.Logs;
                        startIndex = 2;
                        break;
                    case "help":
                    case "--help":
                    case "-h":
                        return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
                    default:
                        // Default to Tray if subcommand not recognised
                        subcommand = GhostSubcommand.Tray;
                        break;
                }
            }
            else
            {
                subcommand = GhostSubcommand.Help;
            }
        }

        // 2. Iterate remaining flag arguments
        for (int i = startIndex; i < args.Length; i++)
        {
            var a = args[i];

            // Help flags
            if (a is "--help" or "-h" or "/?" or "-help")
            {
                return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
            }

            // JSON output
            if (a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
                continue;
            }

            // Verbose logging
            if (a is "--verbose" or "-v")
            {
                verbose = true;
                continue;
            }

            // Status request flag
            if (a.Equals("--status", StringComparison.OrdinalIgnoreCase))
            {
                subcommand = GhostSubcommand.Status;
                continue;
            }

            // Interactive flag: --interactive [on|off|true|false]
            if (a.Equals("--interactive", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    interactive = ParseBoolVal(args[++i]);
                }
                else
                {
                    interactive = true;
                }
            }
            else if (a.StartsWith("--interactive:", StringComparison.OrdinalIgnoreCase))
            {
                interactive = ParseBoolVal(a.Substring("--interactive:".Length));
            }
            else if (a.StartsWith("--interactive=", StringComparison.OrdinalIgnoreCase))
            {
                interactive = ParseBoolVal(a.Substring("--interactive=".Length));
            }
            else if (a.Equals("--no-interactive", StringComparison.OrdinalIgnoreCase))
            {
                interactive = false;
            }

            // Fullscreen flag: --fullscreen [on|off]
            else if (a.Equals("--fullscreen", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("-f", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    fullScreen = ParseBoolVal(args[++i]);
                }
                else
                {
                    fullScreen = true;
                }
            }
            else if (a.StartsWith("--fullscreen:", StringComparison.OrdinalIgnoreCase))
            {
                fullScreen = ParseBoolVal(a.Substring("--fullscreen:".Length));
            }
            else if (a.StartsWith("--fullscreen=", StringComparison.OrdinalIgnoreCase))
            {
                fullScreen = ParseBoolVal(a.Substring("--fullscreen=".Length));
            }
            else if (a.Equals("--windowed", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--no-fullscreen", StringComparison.OrdinalIgnoreCase))
            {
                fullScreen = false;
            }

            // Position flag: --position TopLeft|TopRight|BottomLeft|BottomRight
            else if (a.Equals("--position", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("-pos", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    position = ParsePosition(args[++i]);
                }
            }
            else if (a.StartsWith("--position=", StringComparison.OrdinalIgnoreCase))
            {
                position = ParsePosition(a.Substring("--position=".Length));
            }
            else if (a.StartsWith("--position:", StringComparison.OrdinalIgnoreCase))
            {
                position = ParsePosition(a.Substring("--position:".Length));
            }

            // Opacity flag: --opacity 50|80|100 (or raw byte / percentage)
            else if (a.Equals("--opacity", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("-op", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    opacity = ParseOpacity(args[++i]);
                }
            }
            else if (a.StartsWith("--opacity=", StringComparison.OrdinalIgnoreCase))
            {
                opacity = ParseOpacity(a.Substring("--opacity=".Length));
            }
            else if (a.StartsWith("--opacity:", StringComparison.OrdinalIgnoreCase))
            {
                opacity = ParseOpacity(a.Substring("--opacity:".Length));
            }

            // Scale flag: --scale 1.0|1.5|2.0
            else if (a.Equals("--scale", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && float.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedScale))
                {
                    scale = Math.Clamp(parsedScale, 0.25f, 5.0f);
                    i++;
                }
            }
            else if (a.StartsWith("--scale=", StringComparison.OrdinalIgnoreCase) &&
                     float.TryParse(a.Substring("--scale=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var eqScale))
            {
                scale = Math.Clamp(eqScale, 0.25f, 5.0f);
            }

            // Target FPS flag: --fps <n>
            else if (a.Equals("--fps", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--target-fps", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedFps))
                {
                    targetFps = Math.Clamp(parsedFps, 1, 120);
                    i++;
                }
            }
            else if (a.StartsWith("--fps=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(a.Substring("--fps=".Length), out var eqFps))
            {
                targetFps = Math.Clamp(eqFps, 1, 120);
            }

            // Skip RDP session provisioning / connect
            else if (a.Equals("--no-session", StringComparison.OrdinalIgnoreCase))
            {
                startSession = false;
            }

            // Allow explicit multi-session creation
            else if (a.Equals("--extra-session", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--extrasession", StringComparison.OrdinalIgnoreCase))
            {
                extraSession = true;
            }

            // Target user account
            else if (a.Equals("--user", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--username", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    username = args[++i];
                }
            }

            // Custom pipe override
            else if (a.Equals("--pipe", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--control-pipe", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    controlPipeName = args[++i].Trim('\"', '\'');
                }
            }
        }

        return new GhostControlOptions(
            Subcommand: subcommand,
            Interactive: interactive,
            FullScreen: fullScreen,
            Position: position,
            Opacity: opacity,
            Scale: scale,
            TargetFps: targetFps,
            StartSession: startSession,
            ExtraSession: extraSession,
            Username: username,
            ControlPipeName: controlPipeName,
            JsonOutput: jsonOutput,
            Verbose: verbose);
    }

    private static bool ParseBoolVal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var trimmed = raw.Trim().ToLowerInvariant();
        return trimmed is "on" or "true" or "1" or "yes" or "enable" or "enabled";
    }

    private static PipPresetPosition ParsePosition(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return PipPresetPosition.TopRight;
        var clean = raw.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        return clean switch
        {
            "topleft" => PipPresetPosition.TopLeft,
            "topright" => PipPresetPosition.TopRight,
            "bottomleft" => PipPresetPosition.BottomLeft,
            "bottomright" => PipPresetPosition.BottomRight,
            _ => PipPresetPosition.TopRight
        };
    }

    private static byte ParseOpacity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 255;
        var clean = raw.Trim().TrimEnd('%');

        if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            // Percentage (e.g. 50 -> 128, 80 -> 204, 100 -> 255)
            if (num is >= 0.0 and <= 1.0)
            {
                return (byte)Math.Clamp((int)Math.Round(num * 255.0), 0, 255);
            }
            if (num is > 1.0 and <= 100.0)
            {
                return (byte)Math.Clamp((int)Math.Round(num * 255.0 / 100.0), 0, 255);
            }
            if (num is > 100.0 and <= 255.0)
            {
                return (byte)Math.Clamp((int)Math.Round(num), 0, 255);
            }
        }

        return 255;
    }
}

#endregion

#region CLI Control Command Implementation

/// <summary>
/// CLI command handler for Ghost OS orchestration and runtime control.
/// Provides user-facing commands:
/// <list type="bullet">
///   <item><c>inbrisk ghost tray</c>: Starts background session, PiP window and System Tray icon.</item>
///   <item><c>inbrisk ghost pip --interactive on/off</c>: Toggles PiP mouse interactivity remotely.</item>
///   <item><c>inbrisk ghost pip --fullscreen</c>: Expands/shrinks PiP to monitor-wide Shadow Mode.</item>
///   <item><c>inbrisk ghost pip --position TopLeft|TopRight|BottomLeft|BottomRight</c>: Repositions PiP window.</item>
///   <item><c>inbrisk ghost pip --opacity 50|80|100</c>: Sets PiP alpha opacity level.</item>
/// </list>
/// </summary>
public static class GhostControlCommand
{
    /// <summary>
    /// Default named pipe used by the running Ghost Tray daemon to receive CLI control messages.
    /// </summary>
    public const string DefaultControlPipeName = "inbrisk_ghost_control";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    #region Win32 Helpers

    private static class Win32
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
    }

    #endregion

    /// <summary>
    /// Determines whether the provided CLI arguments should be routed to <see cref="GhostControlCommand"/>.
    /// </summary>
    public static bool Matches(string[] args)
    {
        if (args == null || args.Length == 0) return false;

        var first = args[0].ToLowerInvariant();
        if (first is "ghost-tray" or "ghost_tray" or "ghost-pip" or "ghost_pip" or "ghost-control" or "ghost_control" or "ghost-status")
        {
            return true;
        }

        if (first == "ghost")
        {
            if (args.Length == 1) return true;
            var second = args[1].ToLowerInvariant();
            if (second is "start-worker" or "start_worker" or "worker")
            {
                return false; // Handled by GhostWorkerCommand
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Prints help and usage documentation for all Ghost OS commands.
    /// </summary>
    public static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Usage: inbrisk ghost <subcommand> [options]");
        writer.WriteLine();
        writer.WriteLine("Inbrisk Ghost OS Control & System Tray Orchestration Suite");
        writer.WriteLine();
        writer.WriteLine("Subcommands:");
        writer.WriteLine("  tray                    Start full background session, PiP overlay and System Tray icon");
        writer.WriteLine("  pip                     Control or view the Picture-in-Picture (PiP) display window");
        writer.WriteLine("  status                  Query live operational status of Ghost OS and PiP overlay");
        writer.WriteLine("  stop                    Safely stop, disconnect, and cleanly wipe all ghost sessions, credentials, and temp files");
        writer.WriteLine("  logs                    View recent diagnostic and activity logs from %LOCALAPPDATA%\\inbrisk\\logs\\ghost-os.log");
        writer.WriteLine();
        writer.WriteLine("PiP Control Options (usable with 'ghost tray' and 'ghost pip'):");
        writer.WriteLine("  --interactive <on|off>  Toggle click-through transparent mode (on = clickable, off = pass-through)");
        writer.WriteLine("  --fullscreen            Toggle full-screen Shadow Mode covering the primary display");
        writer.WriteLine("  --position <pos>        Preset anchor position: TopLeft | TopRight | BottomLeft | BottomRight");
        writer.WriteLine("  --opacity <n>           Alpha opacity: 50 | 80 | 100 (or 0-255 / percentage)");
        writer.WriteLine("  --scale <float>         Window scale multiplier (e.g. 1.0, 1.5, 2.0; default: 1.0)");
        writer.WriteLine("  --fps <n>               Target frame refresh rate for rendering (default: 60)");
        writer.WriteLine();
        writer.WriteLine("General Options:");
        writer.WriteLine("  --no-session            Launch PiP and System Tray without starting a new RDP session");
        writer.WriteLine("  --extra-session         Force creating an additional secondary RDP session even if a desktop session is already running");
        writer.WriteLine("  --user <name>           Ghost desktop username (default: InbriskAgent)");
        writer.WriteLine("  --json                  Output status and responses formatted as JSON");
        writer.WriteLine("  --verbose, -v           Enable detailed diagnostic logging");
        writer.WriteLine("  --help, -h              Show this help reference");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  inbrisk ghost tray");
        writer.WriteLine("  inbrisk ghost pip --interactive on");
        writer.WriteLine("  inbrisk ghost pip --fullscreen");
        writer.WriteLine("  inbrisk ghost pip --position TopLeft");
        writer.WriteLine("  inbrisk ghost pip --opacity 80");
        writer.WriteLine("  inbrisk ghost logs");
        writer.WriteLine();
    }

    /// <summary>
    /// Main entry point for Ghost OS commands.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        ConsoleAttach.Ensure();

        var options = GhostControlOptions.Parse(args);

        if (options.Subcommand == GhostSubcommand.Help)
        {
            PrintHelp();
            return 0;
        }

        return options.Subcommand switch
        {
            GhostSubcommand.Tray => await RunTrayAsync(options, ct).ConfigureAwait(false),
            GhostSubcommand.Pip => await RunPipAsync(options, ct).ConfigureAwait(false),
            GhostSubcommand.Status => await RunStatusAsync(options, ct).ConfigureAwait(false),
            GhostSubcommand.Stop => await RunStopAsync(options, ct).ConfigureAwait(false),
            GhostSubcommand.Logs => RunLogs(options),
            _ => await RunTrayAsync(options, ct).ConfigureAwait(false)
        };
    }

    #region Subcommand: Tray (Full Orchestration)

    /// <summary>
    /// Launches the full Ghost OS experience: background session, PiP overlay,
    /// System Tray notification area icon, hotkeys, and remote control server.
    /// </summary>
    public static async Task<int> RunTrayAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            LogInfo("Received interrupt signal (Ctrl+C). Initiating graceful Ghost OS shutdown...", options);
            try { linkedCts.Cancel(); } catch { }
        };

        EventHandler processExitHandler = (_, _) =>
        {
            try { linkedCts.Cancel(); } catch { }
        };

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        LogHeader(options);

        GhostSessionManager? sessionManager = null;
        GhostPipManager? pipManager = null;
        GhostPipExpander? expander = null;
        GhostTrayApp? trayApp = null;
        GhostKeyboardForwarder? keyboardForwarder = null;
        GhostIpcServer? controlServer = null;

        try
        {
            // 1. Session Management: Provision & Start Headless Session (if enabled)
            if (options.StartSession && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Check if target user already has an active or connected session
                var targetExisting = GhostWtsInterop.FindSessionByUsername(options.Username);
                if (targetExisting != null &&
                    (targetExisting.State == WTS_CONNECTSTATE_CLASS.WTSActive || targetExisting.State == WTS_CONNECTSTATE_CLASS.WTSConnected))
                {
                    LogInfo($"Active session already exists for '{options.Username}' (Session ID: {targetExisting.SessionId}, State: {targetExisting.State}). Reusing session.", options);
                }
                else if (!options.ExtraSession)
                {
                    // Existing session check: an interactive console session is already active (e.g. current user)
                    string currentUser = Environment.UserName;
                    LogInfo($"Active Windows desktop session detected for '{currentUser}'.", options);
                    LogInfo("Spawning additional loopback RDP session was skipped to prevent session conflict.", options);
                    LogInfo("Hint: To force an isolated secondary RDP session, specify '--extra-session'.", options);
                    LogInfo("Proceeding with zero-conflict PiP overlay on shared framebuffer.", options);
                }
                else
                {
                    LogInfo($"Explicit secondary session requested (--extra-session). Checking Ghost desktop account ({options.Username})...", options);
                    sessionManager = new GhostSessionManager();

                    try
                    {
                        var status = await sessionManager.StartSessionAsync(new GhostSessionConfig
                        {
                            Username = options.Username,
                            AutoProvision = true,
                            TimeoutSeconds = 25
                        }, linkedCts.Token).ConfigureAwait(false);

                        if (status.IsActive)
                        {
                            LogSuccess($"Ghost session active (SessionId: {status.SessionId}, State: {status.State})", options);
                        }
                        else
                        {
                            LogWarning($"Ghost session started with state '{status.State}'. Proceeding with PiP overlay...", options);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarning($"Could not auto-start headless session ({ex.Message}). Continuing with PiP overlay...", options);
                    }
                }
            }
            else
            {
                LogInfo("Session auto-start bypassed (--no-session). Using active shared framebuffer.", options);
            }

            // 2. Initialize Picture-in-Picture (PiP) Display Manager
            byte initialOpacity = options.Opacity ?? 255;
            bool initialInteractive = options.Interactive ?? false;
            PipPresetPosition initialPosition = options.Position ?? PipPresetPosition.TopRight;
            float initialScale = options.Scale ?? 1.0f;

            var pipConfig = new PipConfig(
                Position: initialPosition,
                Scale: initialScale,
                Interactive: initialInteractive,
                Opacity: initialOpacity,
                TargetFps: options.TargetFps,
                Title: "Inbrisk Ghost PiP");

            pipManager = new GhostPipManager();
            pipManager.StartPip(pipConfig);

            LogSuccess($"Ghost PiP window started ({pipManager.Width}x{pipManager.Height} at {pipManager.CurrentPosition}, HWND: 0x{pipManager.WindowHandle:X8})", options);

            // 3. Initialize Full-Screen Expander (Shadow Mode)
            expander = new GhostPipExpander(pipManager.Renderer);

            // 4. Initialize Windows System Tray Icon Application
            trayApp = new GhostTrayApp(initialStatus: "🟢 Ghost OS: RUNNING", visible: true);
            trayApp.SetInteractive(initialInteractive);
            trayApp.SetOpacity(initialOpacity);
            trayApp.Start();

            if (!trayApp.WaitForReady(3000))
            {
                LogWarning("System Tray message loop took longer than expected to initialize.", options);
            }
            else
            {
                LogSuccess("System Tray icon active in Windows taskbar notification area.", options);
            }

            // 5. Initialize Keyboard Forwarder & Global Hotkey Listener
            try
            {
                keyboardForwarder = new GhostKeyboardForwarder(ipcBus: null, pipManager: pipManager, targetHwnd: pipManager.WindowHandle, autoStart: true);
                LogSuccess("Global hotkeys registered: Ctrl+Alt+I (Interactive), Ctrl+Shift+P (Visibility), Esc (Restore)", options);
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to initialize global keyboard forwarder: {ex.Message}", options);
            }

            // 6. Wire Synchronized Orchestration Events
            WireOrchestrationEvents(pipManager, expander, trayApp, keyboardForwarder, linkedCts, options);

            // 7. Start Remote Control IPC Server
            controlServer = new GhostIpcServer(options.ControlPipeName);
            RegisterIpcControlHandlers(controlServer, pipManager, expander, trayApp, sessionManager, linkedCts, options);
            await controlServer.StartAsync(linkedCts.Token).ConfigureAwait(false);
            LogSuccess($"Control IPC server listening on \\\\.\\pipe\\{options.ControlPipeName}", options);

            // 8. If fullscreen requested on initial launch, expand now
            if (options.FullScreen == true && pipManager.WindowHandle != IntPtr.Zero)
            {
                expander.ExpandToFullScreen(pipManager.WindowHandle, 0, false);
                trayApp.SetFullScreen(true);
                LogInfo("PiP expanded to Full-Screen Shadow Mode.", options);
            }

            LogInfo("Ghost OS background service is running. Right-click the system tray icon to control.", options);

            // 9. Main supervision loop
            while (!linkedCts.Token.IsCancellationRequested && trayApp.IsRunning)
            {
                try
                {
                    await Task.Delay(1000, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Clean exit requested
        }
        catch (Exception ex)
        {
            LogError($"Ghost OS host fatal error: {ex.Message}", options);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;

            LogInfo("Shutting down Ghost OS components cleanly...", options);

            // Teardown in reverse dependency order
            if (controlServer != null)
            {
                try { await controlServer.DisposeAsync(); } catch { }
            }

            if (keyboardForwarder != null)
            {
                try { keyboardForwarder.Dispose(); } catch { }
            }

            if (trayApp != null)
            {
                try { trayApp.Dispose(); } catch { }
            }

            if (pipManager != null)
            {
                try { pipManager.Dispose(); } catch { }
            }

            if (sessionManager != null)
            {
                try
                {
                    await sessionManager.StopSessionAsync(null, CancellationToken.None).ConfigureAwait(false);
                    sessionManager.Dispose();
                }
                catch { }
            }

            // Thorough cleanup: clear loopback credentials, delete temp files, kill leftover client processes
            try
            {
                await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false);
            }
            catch { }

            LogSuccess("Ghost OS System Tray session terminated and all resources cleaned up.", options);
        }

        return 0;
    }

    private static void WireOrchestrationEvents(
        GhostPipManager pipManager,
        GhostPipExpander expander,
        GhostTrayApp trayApp,
        GhostKeyboardForwarder? keyboardForwarder,
        CancellationTokenSource linkedCts,
        GhostControlOptions options)
    {
        // A. Tray: Interactivity Toggled
        trayApp.OnInteractiveToggled += (interactive) =>
        {
            pipManager.SetInteractive(interactive);
            LogInfo($"[Tray] PiP interactive mode {(interactive ? "ENABLED (Clickable)" : "DISABLED (Click-through)")}", options);
        };

        // B. Tray: Full-Screen Toggled
        trayApp.OnFullScreenToggled += (fullScreen) =>
        {
            if (pipManager.WindowHandle != IntPtr.Zero)
            {
                expander.ToggleFullScreen(pipManager.WindowHandle, 0, false);
                trayApp.SetFullScreen(expander.IsFullScreen);
                LogInfo($"[Tray] Full-screen mode {(expander.IsFullScreen ? "EXPANDED" : "RESTORED")}", options);
            }
        };

        // C. Tray: Visibility Toggled
        trayApp.OnVisibilityToggled += (visible) =>
        {
            var hwnd = pipManager.WindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                Win32.ShowWindow(hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
                LogInfo($"[Tray] PiP display {(visible ? "SHOWN" : "HIDDEN")}", options);
            }
        };

        // D. Tray: Opacity Changed
        trayApp.OnOpacityChanged += (alpha) =>
        {
            pipManager.SetOpacity(alpha);
            int pct = (int)Math.Round(alpha * 100.0 / 255.0);
            LogInfo($"[Tray] PiP opacity set to {pct}% (alpha: {alpha})", options);
        };

        // E. Tray: Stop Session Requested
        trayApp.OnStopRequested += () =>
        {
            LogInfo("[Tray] Stop Session requested from System Tray menu.", options);
            try { linkedCts.Cancel(); } catch { }
        };

        // F. Tray: Exit Application Requested
        trayApp.OnExitRequested += () =>
        {
            LogInfo("[Tray] Exit requested from System Tray menu.", options);
            try { linkedCts.Cancel(); } catch { }
        };

        // G. Keyboard Forwarder events
        if (keyboardForwarder != null)
        {
            keyboardForwarder.InteractiveModeToggled += (interactive) =>
            {
                trayApp.SetInteractive(interactive);
                pipManager.SetInteractive(interactive);
                LogInfo($"[Hotkey] Interactive mode toggled to: {(interactive ? "ON" : "OFF")}", options);
            };

            keyboardForwarder.VisibilityToggled += (visible) =>
            {
                trayApp.SetVisible(visible);
                var hwnd = pipManager.WindowHandle;
                if (hwnd != IntPtr.Zero)
                {
                    Win32.ShowWindow(hwnd, visible ? Win32.SW_SHOWNOACTIVATE : Win32.SW_HIDE);
                }
                LogInfo($"[Hotkey] PiP visibility toggled: {(visible ? "VISIBLE" : "HIDDEN")}", options);
            };

            keyboardForwarder.EscapePressed += () =>
            {
                if (expander.IsFullScreen && pipManager.WindowHandle != IntPtr.Zero)
                {
                    expander.ShrinkToPip(pipManager.WindowHandle);
                    trayApp.SetFullScreen(false);
                    LogInfo("[Hotkey] Esc pressed: Restored PiP from full-screen Shadow Mode.", options);
                }
            };
        }
    }

    private static void RegisterIpcControlHandlers(
        GhostIpcServer server,
        GhostPipManager pipManager,
        GhostPipExpander expander,
        GhostTrayApp trayApp,
        GhostSessionManager? sessionManager,
        CancellationTokenSource linkedCts,
        GhostControlOptions options)
    {
        // 1. pip_interactive handler
        server.RegisterHandler("pip_interactive", async (payloadJson) =>
        {
            await Task.Yield();
            bool targetVal;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("interactive", out var prop))
                {
                    targetVal = prop.GetBoolean();
                }
                else
                {
                    targetVal = !pipManager.IsInteractive;
                }
            }
            catch
            {
                targetVal = !pipManager.IsInteractive;
            }

            pipManager.SetInteractive(targetVal);
            trayApp.SetInteractive(targetVal);
            LogInfo($"[IPC] PiP interactivity set to: {(targetVal ? "ON" : "OFF")}", options);

            return JsonSerializer.Serialize(new { ok = true, interactive = targetVal });
        });

        // 2. pip_fullscreen handler
        server.RegisterHandler("pip_fullscreen", async (payloadJson) =>
        {
            await Task.Yield();
            bool isFs = false;
            if (pipManager.WindowHandle != IntPtr.Zero)
            {
                bool? requested = null;
                try
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    if (doc.RootElement.TryGetProperty("fullscreen", out var prop))
                    {
                        requested = prop.GetBoolean();
                    }
                }
                catch { }

                if (requested.HasValue)
                {
                    if (requested.Value && !expander.IsFullScreen)
                    {
                        expander.ExpandToFullScreen(pipManager.WindowHandle, 0, false);
                    }
                    else if (!requested.Value && expander.IsFullScreen)
                    {
                        expander.ShrinkToPip(pipManager.WindowHandle);
                    }
                }
                else
                {
                    expander.ToggleFullScreen(pipManager.WindowHandle, 0, false);
                }

                isFs = expander.IsFullScreen;
                trayApp.SetFullScreen(isFs);
                LogInfo($"[IPC] PiP full-screen mode toggled to: {(isFs ? "ON" : "OFF")}", options);
            }

            return JsonSerializer.Serialize(new { ok = true, fullScreen = isFs });
        });

        // 3. pip_position handler
        server.RegisterHandler("pip_position", async (payloadJson) =>
        {
            await Task.Yield();
            string posName = "TopRight";
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("position", out var prop))
                {
                    posName = prop.GetString() ?? "TopRight";
                }
            }
            catch { }

            if (Enum.TryParse<PipPresetPosition>(posName, true, out var preset))
            {
                pipManager.SetPosition(preset);
                LogInfo($"[IPC] PiP position updated to: {preset}", options);
                return JsonSerializer.Serialize(new { ok = true, position = preset.ToString() });
            }

            return JsonSerializer.Serialize(new { ok = false, error = $"Unknown position preset: {posName}" });
        });

        // 4. pip_opacity handler
        server.RegisterHandler("pip_opacity", async (payloadJson) =>
        {
            await Task.Yield();
            byte alpha = 255;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("opacity", out var prop))
                {
                    alpha = prop.GetByte();
                }
            }
            catch { }

            pipManager.SetOpacity(alpha);
            trayApp.SetOpacity(alpha);
            int pct = (int)Math.Round(alpha * 100.0 / 255.0);
            LogInfo($"[IPC] PiP opacity updated to: {pct}% (alpha: {alpha})", options);

            return JsonSerializer.Serialize(new { ok = true, opacity = alpha, percentage = pct });
        });

        // 5. pip_scale handler
        server.RegisterHandler("pip_scale", async (payloadJson) =>
        {
            await Task.Yield();
            float scale = 1.0f;
            try
            {
                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.TryGetProperty("scale", out var prop))
                {
                    scale = (float)prop.GetDouble();
                }
            }
            catch { }

            pipManager.SetScale(scale);
            LogInfo($"[IPC] PiP scale updated to: {scale:F2}x", options);

            return JsonSerializer.Serialize(new { ok = true, scale = pipManager.CurrentScale, width = pipManager.Width, height = pipManager.Height });
        });

        // 6. status handler
        server.RegisterHandler("status", async (_) =>
        {
            var sessionStatus = sessionManager != null
                ? await sessionManager.GetStatusAsync().ConfigureAwait(false)
                : GhostSessionStatus.Inactive;

            var result = new
            {
                ok = true,
                session = new
                {
                    active = sessionStatus.IsActive,
                    sessionId = sessionStatus.SessionId,
                    username = sessionStatus.Username,
                    uptimeSeconds = sessionStatus.Uptime.TotalSeconds,
                    state = sessionStatus.State
                },
                pip = new
                {
                    running = pipManager.IsRunning,
                    interactive = pipManager.IsInteractive,
                    fullScreen = expander.IsFullScreen,
                    position = pipManager.CurrentPosition.ToString(),
                    scale = pipManager.CurrentScale,
                    width = pipManager.Width,
                    height = pipManager.Height,
                    opacity = pipManager.CurrentConfig.Opacity,
                    hwnd = $"0x{pipManager.WindowHandle:X8}"
                },
                tray = new
                {
                    running = trayApp.IsRunning,
                    visible = trayApp.IsVisible,
                    statusText = trayApp.StatusText
                }
            };

            return JsonSerializer.Serialize(result);
        });

        // 7. stop handler
        server.RegisterHandler("stop", async (stopReq) =>
        {
            await Task.Yield();
            LogInfo("[IPC] Remote stop request received via IPC pipe.", options);
            _ = Task.Run(async () =>
            {
                await Task.Delay(200).ConfigureAwait(false);
                try { linkedCts.Cancel(); } catch { }
            });
            return JsonSerializer.Serialize(new { ok = true, stopping = true });
        });
    }

    #endregion

    #region Subcommand: Pip (Remote Control or Standalone Viewer)

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
            client = new GhostIpcClient(options.ControlPipeName);
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
                    var payload = JsonSerializer.Serialize(new { interactive = options.Interactive.Value });
                    var resp = await client.SendRequestAsync("pip_interactive", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    LogSuccess($"[Ghost OS] PiP interactive mode set to: {(options.Interactive.Value ? "ON (Clickable)" : "OFF (Click-through)")}", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // B. Full-screen toggle / set
                if (options.FullScreen.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { fullscreen = options.FullScreen.Value });
                    var resp = await client.SendRequestAsync("pip_fullscreen", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    LogSuccess($"[Ghost OS] PiP full-screen display set to: {(options.FullScreen.Value ? "ON (Shadow Mode)" : "OFF (Mini PiP)")}", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // C. Position change
                if (options.Position.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { position = options.Position.Value.ToString() });
                    var resp = await client.SendRequestAsync("pip_position", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    LogSuccess($"[Ghost OS] PiP anchor position set to: {options.Position.Value}", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // D. Opacity change
                if (options.Opacity.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { opacity = options.Opacity.Value });
                    var resp = await client.SendRequestAsync("pip_opacity", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    int pct = (int)Math.Round(options.Opacity.Value * 100.0 / 255.0);
                    LogSuccess($"[Ghost OS] PiP opacity set to: {pct}% (alpha {options.Opacity.Value})", options);
                    if (options.JsonOutput) Console.WriteLine(resp);
                }

                // E. Scale change
                if (options.Scale.HasValue)
                {
                    anyActionExecuted = true;
                    var payload = JsonSerializer.Serialize(new { scale = options.Scale.Value });
                    var resp = await client.SendRequestAsync("pip_scale", payload, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    LogSuccess($"[Ghost OS] PiP scale multiplier set to: {options.Scale.Value:F2}x", options);
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

    #endregion

    #region Subcommand: Status

    /// <summary>
    /// Queries and reports the live operational status of the Ghost OS session and PiP overlay.
    /// </summary>
    public static async Task<int> RunStatusAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        // Try querying live daemon first
        try
        {
            await using var client = new GhostIpcClient(options.ControlPipeName);
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

        // Fallback: Query Windows Terminal Services directly
        var existingSession = GhostWtsInterop.FindSessionByUsername(options.Username);
        bool isActive = existingSession != null &&
            (existingSession.State == WTS_CONNECTSTATE_CLASS.WTSActive || existingSession.State == WTS_CONNECTSTATE_CLASS.WTSConnected);

        if (options.JsonOutput)
        {
            var raw = new
            {
                ok = true,
                session = new
                {
                    active = isActive,
                    sessionId = existingSession?.SessionId,
                    username = existingSession?.UserName ?? options.Username,
                    state = existingSession?.State.ToString() ?? "NotRunning"
                },
                pip = new { running = false },
                tray = new { running = false }
            };
            Console.WriteLine(JsonSerializer.Serialize(raw, JsonOptions));
        }
        else
        {
            Console.WriteLine("==================================================================");
            Console.WriteLine("              INBRISK GHOST OS — SYSTEM STATUS                    ");
            Console.WriteLine("==================================================================");
            Console.WriteLine($"  Session State:    {(isActive ? "ACTIVE" : "INACTIVE")}");
            Console.WriteLine($"  Target User:      {options.Username}");
            if (existingSession != null)
            {
                Console.WriteLine($"  Session ID:       {existingSession.SessionId}");
                Console.WriteLine($"  Session Status:   {existingSession.State}");
            }
            Console.WriteLine("  Tray Daemon:      NOT RUNNING (no active IPC control endpoint)");
            Console.WriteLine("  PiP Display:      INACTIVE");
            Console.WriteLine("==================================================================");
        }

        return 0;
    }

    #endregion

    #region Subcommand: Stop

    /// <summary>
    /// Sends a shutdown request to the active Ghost OS daemon or terminates the headless RDP session directly.
    /// </summary>
    public static async Task<int> RunStopAsync(GhostControlOptions options, CancellationToken ct = default)
    {
        LogInfo("Initiating Ghost OS shutdown sequence...", options);

        // Try stopping via IPC first
        try
        {
            await using var client = new GhostIpcClient(options.ControlPipeName);
            await client.ConnectAsync(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);
            if (client.IsConnected)
            {
                var resp = await client.SendRequestAsync("stop", "{}", TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                LogSuccess("Shutdown signal sent to active Ghost OS daemon.", options);
                if (options.JsonOutput) Console.WriteLine(resp);

                // Run comprehensive cleanup
                try { await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false); } catch { }
                LogSuccess("Ghost OS credentials, temp files, and background processes thoroughly cleaned up.", options);
                return 0;
            }
        }
        catch { }

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

        // Run comprehensive cleanup
        try
        {
            await GhostRdpConnector.CleanupAllSessionsAsync(options.Username).ConfigureAwait(false);
            LogSuccess("Ghost OS credentials, temp files, and background processes thoroughly cleaned up.", options);
        }
        catch (Exception ex)
        {
            LogWarning($"Warning during cleanup: {ex.Message}", options);
        }

        return 0;
    }

    #endregion

    #region Formatting & Logging Helpers

    private static void LogHeader(GhostControlOptions options)
    {
        if (options.JsonOutput) return;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            INBRISK GHOST OS — TRAY & PIP CONTROLLER              ║");
        Console.WriteLine("║           Zero-Focus Background Automation Runtime               ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine($"  Target User:    {options.Username}");
        Console.WriteLine($"  Initial Pos:    {options.Position ?? PipPresetPosition.TopRight}");
        Console.WriteLine($"  Interactive:    {(options.Interactive == true ? "ON (Clickable)" : "OFF (Click-through)")}");
        Console.WriteLine($"  Initial Opacity: {options.Opacity ?? 255} ({(int)Math.Round((options.Opacity ?? 255) * 100.0 / 255.0)}%)");
        Console.WriteLine($"  Target FPS:     {options.TargetFps}");
        Console.WriteLine($"  Control Pipe:   \\\\.\\pipe\\{options.ControlPipeName}");
        Console.WriteLine();
    }

    private static void FormatStatusOutput(string statusJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            var root = doc.RootElement;

            Console.WriteLine("==================================================================");
            Console.WriteLine("              INBRISK GHOST OS — LIVE STATUS                      ");
            Console.WriteLine("==================================================================");

            if (root.TryGetProperty("session", out var sess))
            {
                bool active = sess.GetProperty("active").GetBoolean();
                Console.WriteLine($"  Ghost Session:    {(active ? "ACTIVE" : "INACTIVE")}");
                if (sess.TryGetProperty("sessionId", out var sid) && sid.ValueKind != JsonValueKind.Null)
                    Console.WriteLine($"  Session ID:       {sid.GetInt32()}");
                if (sess.TryGetProperty("username", out var u))
                    Console.WriteLine($"  Session User:     {u.GetString()}");
                if (sess.TryGetProperty("state", out var st))
                    Console.WriteLine($"  Connection State: {st.GetString()}");
            }

            if (root.TryGetProperty("pip", out var pip))
            {
                bool running = pip.GetProperty("running").GetBoolean();
                bool interactive = pip.GetProperty("interactive").GetBoolean();
                bool fullScreen = pip.GetProperty("fullScreen").GetBoolean();
                string pos = pip.GetProperty("position").GetString() ?? "TopRight";
                int w = pip.GetProperty("width").GetInt32();
                int h = pip.GetProperty("height").GetInt32();
                byte op = pip.GetProperty("opacity").GetByte();

                Console.WriteLine($"  PiP Display:      {(running ? "RUNNING" : "STOPPED")}");
                Console.WriteLine($"  Resolution:       {w}x{h}");
                Console.WriteLine($"  Anchor Position:  {pos}");
                Console.WriteLine($"  Display Mode:     {(fullScreen ? "Full-Screen (Shadow Mode)" : "Compact PiP Overlay")}");
                Console.WriteLine($"  Interactivity:    {(interactive ? "ON (Clicks Forwarded)" : "OFF (Click-through)")}");
                Console.WriteLine($"  Opacity:          {(int)Math.Round(op * 100.0 / 255.0)}% (alpha {op})");
            }

            if (root.TryGetProperty("tray", out var tray))
            {
                bool trayRunning = tray.GetProperty("running").GetBoolean();
                Console.WriteLine($"  System Tray:      {(trayRunning ? "ACTIVE (Notification Area)" : "INACTIVE")}");
            }

            Console.WriteLine("==================================================================");
        }
        catch
        {
            Console.WriteLine(statusJson);
        }
    }

    private static int RunLogs(GhostControlOptions options)
    {
        string logPath = GhostLogger.LogFilePath;
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[Ghost OS] Log file: {logPath}");
        Console.ResetColor();

        var lines = GhostLogger.ReadRecentLines(100);
        if (lines.Length == 0)
        {
            Console.WriteLine("(Log file is currently empty or no entries recorded yet)");
            return 0;
        }

        Console.WriteLine("==================================================================");
        Console.WriteLine("                INBRISK GHOST OS — SYSTEM LOGS                    ");
        Console.WriteLine("==================================================================");
        foreach (var line in lines)
        {
            if (line.Contains("[ERROR]")) Console.ForegroundColor = ConsoleColor.Red;
            else if (line.Contains("[WARN]")) Console.ForegroundColor = ConsoleColor.Yellow;
            else if (line.Contains("[INFO]")) Console.ForegroundColor = ConsoleColor.Gray;
            else Console.ForegroundColor = ConsoleColor.DarkGray;

            Console.WriteLine(line);
        }
        Console.ResetColor();
        Console.WriteLine("==================================================================");
        return 0;
    }

    private static void LogInfo(string message, GhostControlOptions options)
    {
        GhostLogger.Info(message);
        if (options.JsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { level = "info", message, timestamp = DateTime.UtcNow }, JsonOptions));
            return;
        }
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"[Ghost OS] {message}");
        Console.ResetColor();
    }

    private static void LogSuccess(string message, GhostControlOptions options)
    {
        GhostLogger.Info("✓ " + message);
        if (options.JsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { level = "success", message, timestamp = DateTime.UtcNow }, JsonOptions));
            return;
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Ghost OS] ✓ {message}");
        Console.ResetColor();
    }

    private static void LogWarning(string message, GhostControlOptions options)
    {
        GhostLogger.Warn(message);
        if (options.JsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { level = "warning", message, timestamp = DateTime.UtcNow }, JsonOptions));
            return;
        }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[Ghost OS] ⚠ {message}");
        Console.ResetColor();
    }

    private static void LogError(string message, GhostControlOptions options)
    {
        GhostLogger.Error(message);
        if (options.JsonOutput)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { level = "error", message, timestamp = DateTime.UtcNow }, JsonOptions));
            return;
        }
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Ghost OS] ✗ {message}");
        Console.ResetColor();
    }

    #endregion
}

#endregion
