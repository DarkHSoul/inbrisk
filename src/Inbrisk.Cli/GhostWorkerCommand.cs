using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Cli.Ui;
using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Cli;

#region Options & Configuration

/// <summary>
/// Parsed command line options for configuring and launching the Ghost Worker Daemon.
/// </summary>
public sealed record GhostWorkerOptions(
    string PipeName = GhostIpcDefaults.PipeName,
    int TargetFps = 60,
    bool AutoCapture = true,
    bool JsonOutput = false,
    bool Verbose = false,
    int ReconnectDelayMs = 1000,
    string ServerName = ".")
{
    /// <summary>
    /// Converts these CLI options into the runtime configuration required by <see cref="GhostWorkerDaemon"/>.
    /// </summary>
    public GhostWorkerDaemonConfig ToDaemonConfig() => new()
    {
        PipeName = PipeName,
        ServerName = ServerName,
        TargetFps = TargetFps,
        AutoStartCapture = AutoCapture,
        ReconnectDelayMs = ReconnectDelayMs
    };

    /// <summary>
    /// Parses CLI argument tokens into a strongly-typed <see cref="GhostWorkerOptions"/> instance.
    /// Supports both root flags and subcommand prefixes (e.g. "ghost-worker", "ghost start-worker").
    /// </summary>
    public static GhostWorkerOptions Parse(string[] args)
    {
        string pipeName = GhostIpcDefaults.PipeName;
        int targetFps = 60;
        bool autoCapture = true;
        bool jsonOutput = false;
        bool verbose = false;
        int reconnectDelayMs = 1000;
        string serverName = ".";

        if (args == null || args.Length == 0)
        {
            return new GhostWorkerOptions();
        }

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];

            // 1. Skip leading subcommand routing tokens
            if (i == 0 && (a.Equals("ghost-worker", StringComparison.OrdinalIgnoreCase) ||
                           a.Equals("ghost_worker", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (a.Equals("ghost", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && (args[i + 1].Equals("start-worker", StringComparison.OrdinalIgnoreCase) ||
                                            args[i + 1].Equals("start_worker", StringComparison.OrdinalIgnoreCase) ||
                                            args[i + 1].Equals("worker", StringComparison.OrdinalIgnoreCase)))
                {
                    i++; // Skip 'start-worker'
                }
                continue;
            }
            if (a.Equals("start-worker", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("start_worker", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("worker", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 2. Named pipe parameter
            if (a.Equals("--pipe", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("--pipe-name", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("-p", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    pipeName = CleanPipeName(args[++i]);
                }
            }
            else if (a.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
            {
                pipeName = CleanPipeName(a.Substring("--pipe=".Length));
            }
            else if (a.StartsWith("--pipe-name=", StringComparison.OrdinalIgnoreCase))
            {
                pipeName = CleanPipeName(a.Substring("--pipe-name=".Length));
            }

            // 3. Target Framerate parameter
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
            else if (a.StartsWith("--target-fps=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(a.Substring("--target-fps=".Length), out var eqTargetFps))
            {
                targetFps = Math.Clamp(eqTargetFps, 1, 120);
            }

            // 4. Auto Capture enable / disable
            else if (a.Equals("--no-capture", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--auto-capture:false", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--auto-capture=false", StringComparison.OrdinalIgnoreCase))
            {
                autoCapture = false;
            }
            else if (a.Equals("--auto-capture", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--capture", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--auto-capture:true", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--auto-capture=true", StringComparison.OrdinalIgnoreCase))
            {
                autoCapture = true;
            }

            // 5. JSON logging mode
            else if (a.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                jsonOutput = true;
            }

            // 6. Verbose diagnostics mode
            else if (a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
            }

            // 7. Server name parameter
            else if (a.Equals("--server", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--server-name", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    serverName = args[++i];
                }
            }

            // 8. Reconnect delay parameter
            else if (a.Equals("--reconnect-delay", StringComparison.OrdinalIgnoreCase) &&
                     i + 1 < args.Length && int.TryParse(args[i + 1], out var parsedDelay))
            {
                reconnectDelayMs = Math.Max(100, parsedDelay);
                i++;
            }
        }

        return new GhostWorkerOptions(
            PipeName: pipeName,
            TargetFps: targetFps,
            AutoCapture: autoCapture,
            JsonOutput: jsonOutput,
            Verbose: verbose,
            ReconnectDelayMs: reconnectDelayMs,
            ServerName: serverName);
    }

    private static string CleanPipeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return GhostIpcDefaults.PipeName;
        raw = raw.Trim('\"', '\'');
        if (raw.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase))
            return raw.Substring(@"\\.\pipe\".Length);
        if (raw.StartsWith(@"//./pipe/", StringComparison.OrdinalIgnoreCase))
            return raw.Substring(@"//./pipe/".Length);
        return raw;
    }
}

#endregion

#region CLI Command & Entry Point

/// <summary>
/// CLI command handler for launching and hosting the Session 2 Ghost Worker Daemon.
/// Can be invoked directly via <c>inbrisk-cli ghost-worker</c> or <c>inbrisk-cli ghost start-worker</c>.
/// </summary>
public static class GhostWorkerCommand
{
    private static readonly JsonSerializerOptions JsonLogOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>
    /// Checks whether the provided command arguments match the ghost worker subcommand.
    /// </summary>
    public static bool Matches(string[] args)
    {
        if (args == null || args.Length == 0) return false;
        var first = args[0].ToLowerInvariant();
        if (first is "ghost-worker" or "ghost_worker") return true;
        if (first == "ghost" && args.Length > 1)
        {
            var second = args[1].ToLowerInvariant();
            if (second is "start-worker" or "start_worker" or "worker") return true;
        }
        return false;
    }

    /// <summary>
    /// Prints the CLI usage instructions for the ghost-worker command.
    /// </summary>
    public static void PrintHelp(TextWriter? writer = null)
    {
        writer ??= Console.Out;
        writer.WriteLine("Usage: inbrisk ghost-worker [options]");
        writer.WriteLine("       inbrisk ghost start-worker [options]");
        writer.WriteLine();
        writer.WriteLine("Runs the headless Ghost Worker Daemon inside Session 2 (InbriskAgent session).");
        writer.WriteLine("Listens on the cross-session IPC named pipe for automation commands and");
        writer.WriteLine("continuously feeds high-speed DXGI screen capture to shared memory.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --pipe, -p <name>       Named pipe name (default: inbrisk_ghost_bus)");
        writer.WriteLine("  --fps, --target-fps <n> Target DXGI capture framerate (default: 60, range: 1-120)");
        writer.WriteLine("  --no-capture            Disable automatic DXGI desktop capture");
        writer.WriteLine("  --json                  Output status and lifecycle events in JSON lines format");
        writer.WriteLine("  --verbose, -v           Enable detailed heartbeat and diagnostics logging");
        writer.WriteLine("  --server <name>         Host machine running the pipe server (default: .)");
        writer.WriteLine("  --help, -h              Show this help reference");
        writer.WriteLine();
    }

    /// <summary>
    /// Main entry point for the Ghost Worker command.
    /// Starts <see cref="GhostWorkerDaemon"/>, reports status, and remains active until Ctrl+C / SIGTERM.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ConsoleAttach.Ensure();

        if (args.Any(a => a is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        var options = GhostWorkerOptions.Parse(args);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true; // Prevent abrupt abort; unwind gracefully
            LogInfo("Received Ctrl+C interrupt signal. Gracefully stopping Ghost Worker...", options);
            try { linkedCts.Cancel(); } catch { }
        };

        EventHandler processExitHandler = (_, _) =>
        {
            try { linkedCts.Cancel(); } catch { }
        };

        Console.CancelKeyPress += cancelHandler;
        AppDomain.CurrentDomain.ProcessExit += processExitHandler;

        int sessionId = 0;
        try { sessionId = Process.GetCurrentProcess().SessionId; } catch { }
        int processId = Environment.ProcessId;

        LogHeader(options, sessionId, processId);

        try
        {
            await using var daemon = new GhostWorkerDaemon(options.ToDaemonConfig());
            await daemon.StartAsync(linkedCts.Token).ConfigureAwait(false);

            LogInfo($"Ghost Worker Daemon successfully started and listening on \\\\.\\pipe\\{options.PipeName}", options);
            if (options.AutoCapture)
            {
                LogInfo($"DXGI screen capture loop active at target {options.TargetFps} FPS", options);
            }
            else
            {
                LogInfo("Automatic screen capture disabled (--no-capture)", options);
            }

            var heartbeatInterval = options.Verbose ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(5);
            var lastConnectedState = daemon.IsConnected;

            // Worker lifecycle supervision loop
            while (!linkedCts.Token.IsCancellationRequested && daemon.IsRunning)
            {
                try
                {
                    await Task.Delay(heartbeatInterval, linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                bool isConnected = daemon.IsConnected;
                if (isConnected != lastConnectedState)
                {
                    lastConnectedState = isConnected;
                    if (isConnected)
                    {
                        LogInfo($"Connected to Session 1 host on pipe '{options.PipeName}'", options);
                    }
                    else
                    {
                        LogWarn($"Disconnected from Session 1 host. Auto-reconnecting...", options);
                    }
                }

                if (options.Verbose || isConnected)
                {
                    LogStatus(daemon.GetStatus(), options);
                }
            }

            LogInfo("Stopping Ghost Worker Daemon and releasing resources...", options);
            await daemon.StopAsync().ConfigureAwait(false);
            LogInfo("Ghost Worker Daemon stopped cleanly.", options);
            return 0;
        }
        catch (OperationCanceledException)
        {
            LogInfo("Ghost Worker Daemon cancelled cleanly.", options);
            return 0;
        }
        catch (Exception ex)
        {
            LogError($"Fatal error in Ghost Worker Daemon: {ex.Message}", ex, options);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            AppDomain.CurrentDomain.ProcessExit -= processExitHandler;
        }
    }

    #region Console & JSON Logging Helpers

    private static void LogHeader(GhostWorkerOptions options, int sessionId, int processId)
    {
        if (options.JsonOutput)
        {
            var headerObj = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                level = "INFO",
                @event = "worker_starting",
                pipe = options.PipeName,
                server = options.ServerName,
                fps = options.TargetFps,
                autoCapture = options.AutoCapture,
                sessionId,
                processId
            };
            Console.WriteLine(JsonSerializer.Serialize(headerObj, JsonLogOptions));
            return;
        }

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("          INBRISK GHOST WORKER DAEMON (Session 2 Engine)                        ");
            Console.WriteLine("================================================================================");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"  Pipe Name:     \\\\.\\pipe\\{options.PipeName}");
            Console.WriteLine($"  Server:        {options.ServerName}");
            Console.WriteLine($"  Target FPS:    {options.TargetFps} FPS");
            Console.WriteLine($"  Auto Capture:  {options.AutoCapture}");
            Console.WriteLine($"  Session ID:    {sessionId}");
            Console.WriteLine($"  Process ID:    {processId}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Press Ctrl+C to terminate the worker daemon at any time.");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    private static void LogInfo(string message, GhostWorkerOptions options)
    {
        if (options.JsonOutput)
        {
            var log = new { timestamp = DateTime.UtcNow.ToString("o"), level = "INFO", message };
            Console.WriteLine(JsonSerializer.Serialize(log, JsonLogOptions));
            return;
        }

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("[INFO] ");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    private static void LogWarn(string message, GhostWorkerOptions options)
    {
        if (options.JsonOutput)
        {
            var log = new { timestamp = DateTime.UtcNow.ToString("o"), level = "WARN", message };
            Console.WriteLine(JsonSerializer.Serialize(log, JsonLogOptions));
            return;
        }

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("[WARN] ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    private static void LogError(string message, Exception? ex, GhostWorkerOptions options)
    {
        if (options.JsonOutput)
        {
            var log = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                level = "ERROR",
                message,
                exception = ex?.ToString()
            };
            Console.WriteLine(JsonSerializer.Serialize(log, JsonLogOptions));
            return;
        }

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("[ERROR] ");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(message);
            if (ex != null && options.Verbose)
            {
                Console.WriteLine(ex.ToString());
            }
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    private static void LogStatus(GhostWorkerStatus status, GhostWorkerOptions options)
    {
        if (options.JsonOutput)
        {
            var statusObj = new
            {
                timestamp = DateTime.UtcNow.ToString("o"),
                level = "STATUS",
                connected = status.IsConnected,
                capturing = status.IsCapturing,
                fps = status.CurrentFps,
                targetFps = status.TargetFps,
                totalFrames = status.TotalFramesCaptured,
                reconnects = status.ReconnectCount,
                uptime = status.Uptime.ToString(@"hh\:mm\:ss"),
                lastError = status.LastError
            };
            Console.WriteLine(JsonSerializer.Serialize(statusObj, JsonLogOptions));
            return;
        }

        var prev = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
            Console.ForegroundColor = ConsoleColor.Blue;
            Console.Write("[STATUS] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"Connected: {status.IsConnected,-5} | ");
            Console.Write($"Capturing: {status.IsCapturing,-5} ({status.CurrentFps,2} FPS) | ");
            Console.Write($"Frames: {status.TotalFramesCaptured,6} | ");
            Console.Write($"Reconnects: {status.ReconnectCount,2} | ");
            Console.WriteLine($"Uptime: {status.Uptime:hh\\:mm\\:ss}");
        }
        finally
        {
            Console.ForegroundColor = prev;
        }
    }

    #endregion
}

#endregion
