using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Inbrisk.Cli;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Tray;
using Inbrisk.Runtime;
using Inbrisk.Sdk;
using GhostBatchResult = Inbrisk.Runtime.GhostBatchResult;
using GhostInputExecutor = Inbrisk.Runtime.GhostInputExecutor;
using static Inbrisk.Cli.Commands.CliCommands;

// Exception resilience: a fault on any background thread, timer, async-void
// or event callback must never take down the MCP server/daemon. Log and
// keep the process alive — every worker boundary also has its own catch so
// in practice UnhandledException stays a last-resort diagnostic (on .NET
// Core a truly unhandled thread exception still terminates; the boundary
// catches are what keep the process alive).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        Console.Error.WriteLine(
            $"inbrisk: unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
    }
    catch { /* logging must never fault the handler */ }
};
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    try { Console.Error.WriteLine($"inbrisk: unobserved task exception: {e.Exception}"); }
    catch { }
    e.SetObserved(); // observed → never escalates to process termination
};

// This is a WinExe — no console is attached unless we ask for one. CLI/MCP
// modes attach to the parent console so terminal output keeps working;
// double-click (no console) goes to the setup app window instead.
var quietFlags = args.Any(a => a is "/quiet" or "/s" or "--quiet" or "-q");

// InbriskSetup.exe IS this binary — interactive runs always open the
// graphical installer (a WinExe can't own a console reliably: attaching to
// the parent's shares its input buffer with the shell, breaking the picker).
// /quiet keeps the headless console flow for scripted/AI-driven installs.
if (Inbrisk.Setup.SelfInstaller.RunningAsSetupExe)
{
    if (!quietFlags)
        return Inbrisk.Cli.Ui.SetupApp.Run(installerMode: true);
    Inbrisk.Cli.Ui.ConsoleAttach.Ensure();
    return await Inbrisk.Setup.SetupCommands.RunSetupExeAsync(args, Probes);
}

if (Inbrisk.Cli.GhostWorkerCommand.Matches(args))
{
    Inbrisk.Cli.Ui.ConsoleAttach.Ensure();
    return await Inbrisk.Cli.GhostWorkerCommand.RunAsync(args);
}

if (Inbrisk.Cli.GhostControlCommand.Matches(args))
{
    return await Inbrisk.Cli.GhostControlCommand.RunAsync(args);
}

if (args.Length > 0 && args[0].ToLowerInvariant() is "tray" or "shell")
    return RunDesktopShell();

if (args.Length > 0 && args[0].ToLowerInvariant() is "ui" or "app" or "settings" or "dashboard" or "control")
{
    if (Inbrisk.Platform.Windows.Topology.WindowService.TryActivateWindowByExactTitle("Inbrisk — Desktop Control Runtime"))
        return 0;
    return Inbrisk.Cli.Ui.SetupApp.Run(installerMode: false);
}

if (args.Length == 0)
{
    if (!Inbrisk.Cli.Ui.ConsoleAttach.Ensure())
        return RunDesktopShell();
    return Usage();
}
Inbrisk.Cli.Ui.ConsoleAttach.Ensure();

// Apply a staged update when idle — a pending payload swaps in the first
// moment no other inbrisk process is running (e.g. host restart). Before
// the mcp dispatch so a fresh server spawn picks up the new binary.
try
{
    var pend = Inbrisk.Setup.SelfInstaller.ApplyPendingUpdate();
    if (pend.Deferred == false && pend.Message != "no pending update")
        Console.Error.WriteLine($"inbrisk: {pend.Message}");
}
catch { /* update must never break the requested command */ }

// `inbrisk mcp [--stdio] [--log-level debug] [--ghost]` — runs the MCP stdio server
// in-process; the server owns its own InbriskRuntime via McpSession, so it
// must dispatch before the CLI's runtime is created.
if (args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
{
    var mcpArgs = args.Skip(1).ToArray();
    if (Has(args, "--ghost"))
    {
        Environment.SetEnvironmentVariable("INBRISK_GHOST", "1");
        if (!mcpArgs.Contains("--ghost", StringComparer.OrdinalIgnoreCase))
        {
            mcpArgs = mcpArgs.Append("--ghost").ToArray();
        }
    }
    return await Inbrisk.Mcp.McpHost.RunAsync(mcpArgs);
}

// Production surface (setup/doctor/install/status/…) — runs WITHOUT the
// computer-control runtime: no indicator, no input hooks, no session.
if (Inbrisk.Setup.SetupCommands.IsSetupCommand(args[0]))
    return await Inbrisk.Setup.SetupCommands.RunAsync(args, Probes);

using var inbrisk = new InbriskRuntime(new InbriskOptions(
    AutoConfirm: Has(args, "--yes") || Has(args, "-y"),
    // F32: primary audit log under %ProgramData%\Inbrisk\audit (ACL'd,
    // hash-chained); data-dir copy is an agent-readable mirror.
    TelemetryPath: AuditLog.Path("telemetry.jsonl"),
    TelemetryMirrorPath: AuditLog.MirrorPath("telemetry.jsonl")));

var cmd = args[0].ToLowerInvariant();
try
{
    if (args.ElementAtOrDefault(1) is "--help" or "-h")
        return CmdHelp(cmd);
    switch (cmd)
    {
        case "windows": return CmdWindows(inbrisk, args);
        case "launch": return CmdLaunch(inbrisk, args);
        case "shot": return CmdShot(inbrisk, args);
        case "run": return await CmdRunAsync(inbrisk, args).ConfigureAwait(false);
        case "monitors": return CmdMonitors(inbrisk);
        case "inspect": return CmdInspect(inbrisk, args);
        case "find": return CmdFind(inbrisk, args);
        case "invoke": return CmdAct(inbrisk, args, ActionKind.Invoke);
        case "click": return CmdAct(inbrisk, args, ActionKind.Click);
        case "clickat": return CmdClickAt(inbrisk, args);
        case "rightclick": return CmdRightClick(inbrisk, args);
        case "scroll": return CmdScroll(inbrisk, args);
        case "key": return CmdKey(inbrisk, args);
        case "hotkey": return CmdHotkey(inbrisk, args);
        case "drag": return CmdDrag(inbrisk, args);
        case "setvalue": return CmdSetValue(inbrisk, args);
        case "type": return CmdType(inbrisk, args);
        case "toggle": return CmdAct(inbrisk, args, ActionKind.Toggle);
        case "select": return CmdAct(inbrisk, args, ActionKind.Select);
        case "focus": return CmdFocus(inbrisk, args);
        case "observe": return CmdObserve(inbrisk, args);
        case "wait": return CmdWait(inbrisk, args);
        case "events": return CmdEvents(inbrisk, args);
        case "clipboard": return CmdClipboard(inbrisk, args);
        case "guards": return CmdGuards(inbrisk);
        case "reset-input": return CmdResetInput(inbrisk, args);
        default: return Usage();
    }
}
catch (InbriskException e)
{
    Console.Error.WriteLine($"error[{e.Code}]: {e.Message}");
    return 1;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

