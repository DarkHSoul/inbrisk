using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<string, IList<Tool>> CachedToolsByProfile = new(StringComparer.OrdinalIgnoreCase);
    private static readonly DynamicToolset.ToolsetSchemaCache DynamicSchemaCache = new();

    public static DynamicToolset.ToolsetSchemaCache SchemaCache => DynamicSchemaCache;

    public static void InvalidateToolSchemaCache()
    {
        CachedToolsByProfile.Clear();
        DynamicSchemaCache.Invalidate();
    }

    private static Tool CloneTool(Tool t) => new Tool
    {
        Name = t.Name,
        Description = t.Description,
        InputSchema = t.InputSchema,
        Annotations = t.Annotations != null ? new ToolAnnotations
        {
            ReadOnlyHint = t.Annotations.ReadOnlyHint,
            IdempotentHint = t.Annotations.IdempotentHint,
            DestructiveHint = t.Annotations.DestructiveHint,
        } : null,
    };

    public static IList<Tool> GetCachedTools(string profile) =>
        CachedToolsByProfile.TryGetValue(profile, out var tools)
            ? tools.Select(CloneTool).ToList().AsReadOnly()
            : Array.Empty<Tool>();

    public static void SetCachedTools(string profile, IList<Tool> tools) =>
        CachedToolsByProfile[profile] = Array.AsReadOnly(tools.Select(CloneTool).ToArray());
    private static readonly HashSet<string> CoreToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "computer_batch",
        "computer_do",
        "computer_run",
        "computer_launch",
        "computer_close_window",
        "computer_windows",
        "computer_observe",
        "computer_find",
        "computer_inspect",
        "computer_read",
        "computer_click",
        "computer_type",
        "computer_hotkey",
        "computer_screenshot",
        "computer_reset_input",
        "computer_capabilities",
    };

    public static IReadOnlySet<string> CoreTools => CoreToolNames;

    public static IList<Tool> FilterAndAnnotateTools(IEnumerable<Tool> sourceTools, bool isCoreProfile)
    {
        var tools = isCoreProfile
            ? sourceTools.Where(t => CoreToolNames.Contains(t.Name)).ToList()
            : sourceTools.ToList();

        foreach (var t in tools)
        {
            var name = t.Name;
            var isReadOnly = name is "computer_observe" or "computer_windows" or "computer_find"
                or "computer_inspect" or "computer_read" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "browser_screenshot" or "computer_screen_memory" or "computer_ui_status"
                or "computer_leases_status" or "computer_list_adapters" or "computer_apps"
                or "computer_app_status" or "computer_list_recipes" or "browser_tabs" or "browser_content";

            var isDestructive = name is "computer_batch" or "computer_do" or "computer_run"
                or "computer_click" or "computer_type" or "computer_hotkey"
                or "computer_close_window" or "browser_click" or "browser_type"
                or "computer_app_shutdown" or "computer_app_restart" or "computer_cancel_task";

            var isIdempotent = name is "computer_windows" or "computer_observe" or "computer_find"
                or "computer_inspect" or "computer_read" or "computer_screenshot" or "computer_capabilities"
                or "browser_snapshot" or "computer_reset_input" or "computer_screen_memory"
                or "computer_ui_status" or "computer_leases_status" or "browser_content"
                or "computer_wait" or "computer_wait_for" or "computer_wait_for_stable"
                or "computer_apps" or "computer_app_status" or "computer_list_recipes"
                or "computer_list_adapters" or "browser_tabs" or "browser_screenshot";

            t.Annotations = new ToolAnnotations
            {
                ReadOnlyHint = isReadOnly ? true : null,
                IdempotentHint = isIdempotent ? true : null,
                DestructiveHint = isDestructive ? true : null,
            };
        }
        return tools;
    }

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
            Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
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
                o.ServerInstructions = GetServerInstructions(control);
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        ConfigureFilters(serverBuilder, toolProfile);

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

    public static string GetServerInstructions(EmergencyControl control) =>
        "Windows desktop eyes and hands automation runtime.\n" +
        "1. ACT IMMEDIATELY: Do NOT explore the codebase, docs, or shell to learn Inbrisk. Everything you need is in this schema.\n" +
        "2. SPEED FIRST (ROUNDTRIP LATENCY): Roundtrips dominate latency. Combine multi-step actions into single-turn calls:\n" +
        "   - Batch Actions: ALWAYS PREFER computer_batch(steps: [...]) or computer_do(...) when 2+ deterministic actions are known. Do not batch if the next step depends on observing unpredicted UI.\n" +
        "   - Batch Reads: For multi-target reads or queries, use computer_read(targets: [...]), computer_find(queries: [...]), or computer_inspect(hwnds: [...]).\n" +
        "   - Plan: computer_run(steps: [{action: \"launch\", app: \"calc\"}, {action: \"click\", target: {name: \"Five\"}}])\n" +
        "   Single-action tools (computer_click, computer_type) are strictly for exploration or unpredicted UI.\n" +
        "3. APPS & LAUNCH: Call computer_launch(app: \"...\") directly without preceding computer_apps discovery calls. computer_launch resolves aliases and automatically returns an initialMap of key controls and landmarks (next: \"none\"). For web, use browser_browse(url: \"...\") directly.\n" +
        "4. NO REDUNDANT FOLLOW-UP: When a mutating tool reports verified: true and next: \"none\", DO NOT execute immediate observe/inspect/windows. Proceed directly to your next user objective.\n" +
        "5. WINDOW LIFECYCLE: Never close or alter pre-existing user windows. Do NOT automatically close windows merely because you opened them; 'When in doubt, leave it open.' Leave user-facing results open (Notepad documents, browser tabs, Calculator, Explorer folders). Only close purely ephemeral helper windows or windows the user explicitly commanded to close. Untargeted close will never select the foreground window. Never force-kill user applications or modal dialogs. Protected IDEs/terminals/hosts are immune.\n" +
        "6. MODAL DIALOGS: If a window is unresponsive, check computer_windows or computer_observe for [MODAL-POPUP-ACTIVE] and handle or dismiss it first.\n" +
        "7. TARGETING: Prefer elementId or semantic target (process, name, role) over coordinates. Re-observe if Stale.\n" +
        $"8. SAFETY & HOTKEYS: {control.PanicHotkey} halts computer control instantly. Only local user can resume with {control.ResumeHotkey}. If EmergencyStopped, stop immediately.\n" +
        "9. RECOVERY: If mouse or keyboard modifiers feel stuck, call computer_reset_input.\n" +
        "10. CONCURRENT READS: Independent read-only queries (e.g. inspecting different windows/processes, querying capabilities, checking status) may be issued concurrently by MCP hosts supporting parallel tool calls. Do not serialize independent reads solely for verification.";

    public static void ConfigureFilters(IMcpServerBuilder serverBuilder, string toolProfile = "full")
    {
        var isCoreProfile = toolProfile.Equals("core", StringComparison.OrdinalIgnoreCase);
        serverBuilder.WithRequestFilters(f =>
        {
            f.AddCallToolFilter(next => async (req, ct) =>
            {
                var session = req.Services?.GetService<McpSession>();
                using var token = session?.Rt.Activity.BeginActivity(req.Params?.Name, session?.SessionId);
                return await next(req, ct);
            });

            f.AddListToolsFilter(next => async (req, ct) =>
            {
                if (CachedToolsByProfile.TryGetValue(toolProfile, out var cached))
                {
                    return new ListToolsResult { Tools = cached };
                }

                var res = await next(req, ct);
                if (res?.Tools != null)
                {
                    var tools = FilterAndAnnotateTools(res.Tools, isCoreProfile);
                    var readOnly = (tools as List<Tool>)?.AsReadOnly() ?? Array.AsReadOnly(tools.ToArray());
                    CachedToolsByProfile[toolProfile] = readOnly;
                    res.Tools = readOnly;
                }
                return res!;
            });
        });
    }
}
