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
    bool StartSession = false,
    bool ExtraSession = false,
    bool StartInitialShell = false,
    string Username = "InbriskAgent",
    string ControlPipeName = GhostControlCommand.DefaultControlPipeName,
    bool JsonOutput = false,
    bool Verbose = false,
    bool RemoveUser = false)
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
        bool startSession = false;
        bool extraSession = false;
        bool startInitialShell = false;
        string username = "InbriskAgent";
        string controlPipeName = GhostControlCommand.DefaultControlPipeName;
        bool jsonOutput = false;
        bool verbose = false;
        bool removeUser = false;

        if (args == null || args.Length == 0)
        {
            return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
        }

        int startIndex = 0;

        // 1. Identify primary subcommand
        var first = args[0].ToLowerInvariant();
        if (first is "ghost-tray" or "ghost_tray" or "ghost-control" or "ghost_control")
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
        else if (first is "ghost-logs" or "ghost_logs")
        {
            subcommand = GhostSubcommand.Logs;
            startIndex = 1;
        }
        else if (first is "ghost-help" or "ghost_help")
        {
            return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
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
                        return new GhostControlOptions(Subcommand: GhostSubcommand.Help);
                }
            }
            else if (args.Length == 1)
            {
                subcommand = GhostSubcommand.Help;
            }
            else
            {
                subcommand = GhostSubcommand.Tray;
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

            // Full teardown: also delete the dedicated ghost account and
            // its stored machine credential (default keeps the account for
            // the next session start).
            if (a is "--remove-user" or "--purge")
            {
                removeUser = true;
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

            // Explicitly enable RDP session provisioning / connect
            else if (a.Equals("--session", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--start-session", StringComparison.OrdinalIgnoreCase))
            {
                startSession = true;
            }

            // Allow explicit multi-session creation
            else if (a.Equals("--extra-session", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--extrasession", StringComparison.OrdinalIgnoreCase))
            {
                extraSession = true;
            }

            // Optional initial shell on the isolated desktop (off by default;
            // an empty desktop is the normal Ghost Control canvas).
            else if (a.Equals("--initial-shell", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--start-shell", StringComparison.OrdinalIgnoreCase))
            {
                startInitialShell = true;
            }
            else if (a.Equals("--no-initial-shell", StringComparison.OrdinalIgnoreCase) ||
                     a.Equals("--no-shell", StringComparison.OrdinalIgnoreCase))
            {
                startInitialShell = false;
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
            StartInitialShell: startInitialShell,
            Username: username,
            ControlPipeName: controlPipeName,
            JsonOutput: jsonOutput,
            Verbose: verbose,
            RemoveUser: removeUser);
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
