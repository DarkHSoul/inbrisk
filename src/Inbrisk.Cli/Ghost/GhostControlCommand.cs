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
public static partial class GhostControlCommand
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


    private static class Win32
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOW = 5;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint WM_CLOSE = 0x0010;

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

        [DllImport("user32.dll")]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    }

    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        try
        {
            int length = Win32.GetWindowTextLengthW(hwnd);
            if (length <= 0) return string.Empty;
            var sb = new StringBuilder(length + 1);
            _ = Win32.GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }



    private static string? _controlToken;

    private static string? _instanceId;

    private sealed record ControlDaemonState(
        int Pid,
        DateTime StartTimeUtc,
        string Token,
        string PipeName,
        string DesktopName,
        string? InstanceId,
        string? Status,
        string? Backend,
        DateTime? LastHeartbeatUtc);

    private static string ControlStatePath =>
        Path.Combine(UserSettings.DataDir, "ghost-control.json");

    private static void TraceStartup(string stage)
    {
        try
        {
            var dir = Path.Combine(UserSettings.DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "ghost-startup.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} {stage}{Environment.NewLine}");
        }
        catch { }
    }

    private static bool WriteControlState(string token, string pipeName, string desktopName, string status)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            string? tmp = null;
            try
            {
                Directory.CreateDirectory(UserSettings.DataDir);
                using var p = Process.GetCurrentProcess();
                tmp = Path.Combine(UserSettings.DataDir,
                    $"ghost-control.{p.Id}.{Guid.NewGuid():N}.tmp");
                var json = JsonSerializer.Serialize(new
                {
                    version = 1,
                    instanceId = _instanceId,
                    backend = "LocalDesktop",
                    status,
                    pid = p.Id,
                    startTimeUtc = p.StartTime.ToUniversalTime().ToString("O"),
                    token,
                    pipeName,
                    desktopName,
                    lastHeartbeatUtc = DateTime.UtcNow.ToString("O")
                }, JsonOptions);

                using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmp, ControlStatePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                try { if (tmp != null && File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt == 2)
                {
                    GhostLogger.Warn($"[GhostControl] Failed to write daemon state '{ControlStatePath}': {ex}");
                    try { Console.Error.WriteLine($"[Ghost OS] ⚠ Failed to write daemon state '{ControlStatePath}': {ex.GetType().Name}: {ex.Message}"); } catch { }
                }
                Thread.Sleep(100);
            }
        }

        // Sandboxed or AV-filtered environments can reject rename/replace even
        // though a direct bounded write is allowed. Prefer a last-resort direct
        // write over running an unowned daemon or leaving stale state behind.
        try
        {
            using var p = Process.GetCurrentProcess();
            var json = JsonSerializer.Serialize(new
            {
                version = 1,
                instanceId = _instanceId,
                backend = "LocalDesktop",
                status,
                pid = p.Id,
                startTimeUtc = p.StartTime.ToUniversalTime().ToString("O"),
                token,
                pipeName,
                desktopName,
                lastHeartbeatUtc = DateTime.UtcNow.ToString("O")
            }, JsonOptions);
            using var stream = new FileStream(ControlStatePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception directEx)
        {
            GhostLogger.Warn($"[GhostControl] Direct daemon state write failed '{ControlStatePath}': {directEx}");
            try { Console.Error.WriteLine($"[Ghost OS] ⚠ Failed to write daemon state '{ControlStatePath}': {directEx.GetType().Name}: {directEx.Message}"); } catch { }
        }
        return false;
    }

    private static ControlDaemonState? ReadControlState()
    {
        try
        {
            if (!File.Exists(ControlStatePath))
            {
                return null;
            }
            JsonDocument doc;
            using (var stream = new FileStream(ControlStatePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                doc = JsonDocument.Parse(reader.ReadToEnd());
            }
            using (doc)
            {
                var r = doc.RootElement;
                if (!r.TryGetProperty("pid", out var pidProp) || pidProp.ValueKind != JsonValueKind.Number)
                {
                    return null;
                }
                var start = r.TryGetProperty("startTimeUtc", out var s) &&
                    DateTime.TryParse(s.GetString(), null, DateTimeStyles.RoundtripKind, out var st)
                    ? st
                    : DateTime.MinValue;
                var token = r.TryGetProperty("token", out var t) ? t.GetString() ?? string.Empty : string.Empty;
                var pipe = r.TryGetProperty("pipeName", out var pn) ? pn.GetString() ?? DefaultControlPipeName : DefaultControlPipeName;
                var desktop = r.TryGetProperty("desktopName", out var dn) &&
                    !string.IsNullOrWhiteSpace(dn.GetString())
                    ? dn.GetString()!
                    : GhostDesktopNative.DefaultGhostDesktopName;
                var instanceId = r.TryGetProperty("instanceId", out var iid) ? iid.GetString() : null;
                var status = r.TryGetProperty("status", out var stat) ? stat.GetString() : null;
                var backend = r.TryGetProperty("backend", out var be) ? be.GetString() : null;
                DateTime? heartbeat = r.TryGetProperty("lastHeartbeatUtc", out var hb) &&
                    DateTime.TryParse(hb.GetString(), null, DateTimeStyles.RoundtripKind, out var hbt)
                    ? hbt
                    : null;
                return new ControlDaemonState(pidProp.GetInt32(), start, token, pipe, desktop,
                    instanceId, status, backend, heartbeat);
            }
        }
        catch { return null; }
    }

    private static bool ControlStateAlive(ControlDaemonState state)
    {
        try
        {
            using var p = Process.GetProcessById(state.Pid);
            if (p.HasExited)
            {
                return false;
            }
            if (state.StartTimeUtc != DateTime.MinValue &&
                Math.Abs((p.StartTime.ToUniversalTime() - state.StartTimeUtc).TotalSeconds) > 2)
            {
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static string? ControlToken() => ReadControlState()?.Token;

    /// <summary>
    /// Sends a real-window surface launch request to the running Ghost daemon.
    /// Returns null when no daemon supports the request; callers may then fall
    /// back to the older isolated-Win32-desktop path.
    /// </summary>
    public static async Task<string?> DaemonSurfaceLaunchAsync(
        string program,
        string? arguments,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        var daemon = ReadControlState();
        if (daemon == null || !ControlStateAlive(daemon) ||
            !string.Equals(daemon.Status, "Ready", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            await using var client = new GhostIpcClient(daemon.PipeName);
            await client.ConnectAsync(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            if (!client.IsConnected) return null;

            var payload = JsonSerializer.Serialize(new
            {
                token = daemon.Token,
                program,
                args = arguments,
                workingDir = workingDirectory
            });
            var response = await client.SendRequestAsync("ghost_surface_launch", payload, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            return response;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsAuthorized(string? payloadJson)
    {
        var expected = _controlToken;
        if (expected == null || string.IsNullOrEmpty(payloadJson))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("token", out var t) &&
                t.ValueKind == JsonValueKind.String &&
                string.Equals(t.GetString(), expected, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void DeleteControlStateIfOurs()
    {
        try
        {
            var s = ReadControlState();
            if (s != null && s.Pid == Environment.ProcessId &&
                (s.InstanceId == null || s.InstanceId == _instanceId))
            {
                File.Delete(ControlStatePath);
            }
        }
        catch { }
    }

    private static bool ResponseOk(string? resp)
    {
        if (string.IsNullOrEmpty(resp))
        {
            return true;
        }
        try
        {
            using var doc = JsonDocument.Parse(resp);
            return !doc.RootElement.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.False;
        }
        catch { return true; }
    }


    /// <summary>
    /// Determines whether the provided CLI arguments should be routed to <see cref="GhostControlCommand"/>.
    /// </summary>
    public static bool Matches(string[] args)
    {
        if (args == null || args.Length == 0) return false;

        var first = args[0].ToLowerInvariant();
        if (first is "ghost-tray" or "ghost_tray" or "ghost-pip" or "ghost_pip" or "ghost-control" or "ghost_control" or "ghost-status" or "ghost_status" or "ghost-stop" or "ghost_stop" or "ghost-logs" or "ghost_logs" or "ghost-help" or "ghost_help")
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
        writer.WriteLine("  --session               Launch secondary RDP session (default: off to avoid single-session kick)");
        writer.WriteLine("  --no-session            Launch PiP and System Tray without starting a new RDP session (default)");
        writer.WriteLine("  --extra-session         Force creating an additional secondary RDP session even if a desktop session is already running");
        writer.WriteLine("  --initial-shell         Start a cmd shell on the isolated desktop (default: off; use only for diagnostics)");
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
        var options = GhostControlOptions.Parse(args);
        if (options.Subcommand == GhostSubcommand.Tray)
        {
            TraceStartup("arguments parsed");
        }

        ConsoleAttach.Ensure();
        if (options.Subcommand == GhostSubcommand.Tray)
        {
            TraceStartup("console attach completed");
        }

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


    private static GhostLocalDesktop? CreateIsolatedDesktop(GhostControlOptions options)
    {
        TraceStartup($"desktop create begin name={GhostLocalDesktop.DefaultDesktopName}");
        try
        {
            var desktop = new GhostLocalDesktop(GhostLocalDesktop.DefaultDesktopName, createIfNotExists: true);
            TraceStartup($"desktop create success name={desktop.DesktopName}");
            return desktop;
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to initialize GhostLocalDesktop '{GhostLocalDesktop.DefaultDesktopName}': {ex.Message}", options);
        }

        // A previously killed daemon can leave an inaccessible desktop object in
        // WinSta0. Never fall back to the user's Default desktop: create a fresh
        // per-daemon desktop and publish its name in ghost-control.json instead.
        var fallbackName = $"{GhostLocalDesktop.DefaultDesktopName}_{Environment.ProcessId}";
        TraceStartup($"desktop fallback begin name={fallbackName}");
        try
        {
            var desktop = new GhostLocalDesktop(fallbackName, createIfNotExists: true);
            TraceStartup($"desktop fallback success name={desktop.DesktopName}");
            LogWarning($"Using fresh fallback Ghost Desktop '{fallbackName}'.", options);
            return desktop;
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize fallback GhostLocalDesktop '{fallbackName}': {ex.Message}", options);
            return null;
        }
    }



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
}
