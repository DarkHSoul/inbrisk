using System.Diagnostics;
using System.Reflection;
using Inbrisk.Platform.Windows.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Inbrisk.Mcp;

/// <summary>The MCP server host — callable both from the standalone
/// inbrisk-mcp executable and from `inbrisk mcp` in the CLI.</summary>
public static class McpHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o =>
            o.LogToStandardErrorThreshold = LogLevel.Trace);
        var logLevel = args.SkipWhile(a => a != "--log-level").Skip(1)
            .FirstOrDefault();
        builder.Logging.SetMinimumLevel(logLevel?.Equals("debug",
            StringComparison.OrdinalIgnoreCase) == true
            ? LogLevel.Debug : LogLevel.Information);

        var version = typeof(McpHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(McpHost).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        var control = EmergencyControl.Process;
        // chord precedence: env var → settings.json (setup app) → defaults
        var userSettings = Inbrisk.Core.UserSettings.Load();
        control.StartHotkeys(
            Environment.GetEnvironmentVariable("INBRISK_PANIC_HOTKEY") ?? userSettings.PanicHotkey,
            Environment.GetEnvironmentVariable("INBRISK_RESUME_HOTKEY") ?? userSettings.ResumeHotkey);

        var tray = new TrayIconService(userSettings);
        tray.OnOpenSettings = () =>
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "settings") { UseShellExecute = true });
        };
        tray.OnEmergencyStop = () => control.TriggerLocalPanic("tray icon");
        tray.OnResume = () => control.TriggerLocalResume("tray icon");
        tray.OnToggleHud = enabled =>
        {
            var s = Inbrisk.Core.UserSettings.Load();
            s.HudEnabled = enabled;
            s.Save();
        };
        tray.OnTogglePerimeter = enabled =>
        {
            var s = Inbrisk.Core.UserSettings.Load();
            s.PerimeterEnabled = enabled;
            s.Save();
        };
        tray.OnQuit = () => Environment.Exit(0);
        tray.Start();

        control.StateChanged += s =>
        {
            tray.UpdateStatus(
                mcpConnected: true,
                clientName: "Host",
                working: false,
                emergency: s == ComputerControlState.EmergencyStopped,
                hudEnabled: Inbrisk.Core.UserSettings.Load().HudEnabled,
                perimeterEnabled: Inbrisk.Core.UserSettings.Load().PerimeterEnabled);
        };

                                                                                                                        builder.Services.AddSingleton(control);
        builder.Services.AddSingleton(sp => new McpSession(
            sp.GetRequiredService<EmergencyControl>()));
        builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                    { Name = "inbrisk", Version = version };
                o.ServerInstructions =
                    "Windows desktop eyes and hands. Prefer elementIds and " +
                    "semantic targets over coordinates; use image points only " +
                    "for pixel-only targets; re-observe after Stale/" +
                    "StaleFrame results. When beginning a conversation in " +
                    "which you intend to use Inbrisk computer-control tools, " +
                    $"inform the user once that {control.PanicHotkey} immediately " +
                    "stops Inbrisk computer control, and that only the local " +
                    $"user can resume it with {control.ResumeHotkey} — an " +
                    "emergency stop can never be cleared through the MCP API " +
                    "by the model itself. If ANY tool returns controlState:" +
                    "EmergencyStopped or an EmergencyStopped error, STOP " +
                    "acting immediately — do not retry it, work around it, " +
                    "or fall back to shell/PowerShell; tell the user that " +
                    "control is stopped and only they can resume it with " +
                    "the local resume hotkey or the tray icon. Do not " +
                    "repeat this before every action. When the user asks " +
                    "to interact with an " +
                    "application that is not currently running, prefer " +
                    "computer_launch (or a computer_run launch step) instead " +
                    "of shell/PowerShell to locate or start it. For " +
                    "multi-step deterministic UI work, " +
                    "prefer computer_run over issuing many individual " +
                    "computer tools. Use individual tools for exploration or " +
                    "when the next action depends on information not yet " +
                    "visible. Insert checkpoints only where model reasoning " +
                    "is actually required. Targeting: prefer process over " +
                    "window (titles are localized); name is a substring — add " +
                    "role or labelledBy to disambiguate; AmbiguousTarget " +
                    "means refine, not retry. computer_run step fields are " +
                    "validated strictly — unknown fields fail; call " +
                    "computer_capabilities once if unsure about step syntax " +
                    "(e.g. hotkey takes key+modifiers or keys:\"ctrl+s\"). " +
                    "If desktop input seems stuck — clicks land nowhere or " +
                    "act as if a key is held — call computer_reset_input: it " +
                    "releases every modifier/button (including holds left by " +
                    "dead processes) and can neutralize input-swallowing " +
                    "overlay windows.";
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        var app = builder.Build();
        try { await app.RunAsync(); }
        finally
        {
            tray.Dispose();
            control.Dispose();
        }
        // Host disposal → McpSession.Dispose → input ReleaseAll + capture teardown.
        return 0;
    }
}
