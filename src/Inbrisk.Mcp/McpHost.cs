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
    private static readonly HashSet<string> CoreToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "computer_do",
        "computer_run",
        "computer_launch",
        "computer_close_window",
        "computer_windows",
        "computer_observe",
        "computer_find",
        "computer_inspect",
        "computer_click",
        "computer_type",
        "computer_hotkey",
        "computer_screenshot",
        "computer_reset_input",
        "computer_capabilities",
        "browser_browse",
        "browser_click",
        "browser_type",
        "browser_snapshot",
    };

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
        var toolProfile = Environment.GetEnvironmentVariable("INBRISK_TOOL_PROFILE")
            ?? userSettings.ToolProfile
            ?? "full";
        var isCoreProfile = toolProfile.Equals("core", StringComparison.OrdinalIgnoreCase);

        var serverBuilder = builder.Services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                    { Name = "inbrisk", Version = version };
                o.ServerInstructions =
                    "Windows desktop eyes and hands automation runtime.\n" +
                    "1. ACT IMMEDIATELY: Do NOT explore the codebase, docs, or shell to learn Inbrisk. Everything you need is in this schema.\n" +
                    "2. SPEED FIRST (ROUNDTRIP LATENCY): Roundtrips dominate latency. Combine multi-step actions into single-turn calls:\n" +
                    "   - Composite: computer_do(app: \"notepad\", type: \"hello\", hotkey: \"ctrl+s\")\n" +
                    "   - Plan: computer_run(steps: [{action: \"launch\", app: \"calc\"}, {action: \"click\", target: {name: \"Five\"}}])\n" +
                    "   Single-action tools (computer_click, computer_type) are strictly for exploration or unpredicted UI.\n" +
                    "3. APPS & LAUNCH: Use computer_launch(app: \"...\") for desktop apps. For web, use browser_browse(url: \"...\") directly.\n" +
                    "4. WINDOW LIFECYCLE: Never close or alter pre-existing user windows. Clean up windows you opened using computer_close_window(closeAllAgentWindows: true) or computer_close_window(hwnd: \"...\"). Never force-kill user applications. Protected IDEs/terminals/hosts are immune.\n" +
                    "5. MODAL DIALOGS: If a window is unresponsive, check computer_windows or computer_observe for [MODAL-POPUP-ACTIVE] and handle or dismiss it first.\n" +
                    "6. TARGETING: Prefer elementId or semantic target (process, name, role) over coordinates. Re-observe if Stale.\n" +
                    $"7. SAFETY & HOTKEYS: {control.PanicHotkey} halts computer control instantly. Only local user can resume with {control.ResumeHotkey}. If EmergencyStopped, stop immediately.\n" +
                    "8. RECOVERY: If mouse or keyboard modifiers feel stuck, call computer_reset_input.";
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        if (isCoreProfile)
        {
            serverBuilder.WithRequestFilters(f =>
            {
                f.AddListToolsFilter(next => async (req, ct) =>
                {
                    var res = await next(req, ct);
                    if (res?.Tools != null)
                    {
                        var filtered = res.Tools.Where(t => CoreToolNames.Contains(t.Name)).ToList();
                        res.Tools = filtered;
                    }
                    return res!;
                });
            });
        }

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
