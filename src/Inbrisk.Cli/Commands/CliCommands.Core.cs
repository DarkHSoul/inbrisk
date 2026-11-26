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

namespace Inbrisk.Cli.Commands;

internal static partial class CliCommands
{
    internal static readonly JsonSerializerOptions json = new() { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    internal static string? Opt(string[] args, string name) =>
        args.SkipWhile(a => a != name).Skip(1).FirstOrDefault(a => !a.StartsWith("--"));

    internal static bool Has(string[] args, string name) => args.Contains(name);

    // Capability probes for doctor/setup — a temporary runtime with the
    // indicator off; verifies UIA, capture, input and DPI awareness for real.
    internal static async Task<(bool uia, bool capture, bool input, bool dpi)> Probes()
    {
        try
        {
            using var i = new InbriskRuntime(new InbriskOptions(Indicator: false));
            var uia = true;
            try
            {
                var fg = i.ForegroundWindow();
                if (fg != null) i.Inspect(fg.Hwnd, new InspectOptions(MaxDepth: 1));
            }
            catch { uia = false; }
            bool cap;
            try
            {
                var f = i.CaptureRaw(new CaptureTarget.Region(new RectPx(0, 0, 32, 32)));
                cap = f.ToPng().Length > 0;
            }
            catch { cap = false; }
            await Task.CompletedTask;
            return (uia, cap, true,
                Inbrisk.Platform.Windows.Native.PlatformProbes.PerMonitorV2Aware());
        }
        catch { return (false, false, false, false); }
    }

    // ---------------------------------------------------------------- commands

    internal static string ResolveGhostDesktopName()
    {
        try
        {
            var statePath = Path.Combine(UserSettings.DataDir, "ghost-control.json");
            if (!File.Exists(statePath)) return GhostLocalDesktop.DefaultDesktopName;
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("desktopName", out var desktop) &&
                desktop.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(desktop.GetString()))
            {
                return desktop.GetString()!;
            }
        }
        catch { }
        return GhostLocalDesktop.DefaultDesktopName;
    }

    /// <summary>Element id (same process) or selector flags → TargetRef.</summary>
    internal static TargetRef ResolveTarget(InbriskRuntime i, string[] args, string error)
    {
        var first = args.ElementAtOrDefault(1)
            ?? throw new InbriskException(ErrorCode.Unsupported, error);
        if (!first.StartsWith("--"))
            return TargetRef.Element(first);
        return TargetRef.Find(BuildSpec(i, args.Skip(1).ToArray()));
    }

    /// <summary>Window arg: positional title/hwnd, or --pid.</summary>
    internal static long ResolveWindowArg(InbriskRuntime i, string? pos, string[] args, string error)
    {
        if (int.TryParse(Opt(args, "--pid"), out var pid)) return i.ResolvePidWindow(pid);
        return i.ResolveWindow(pos
            ?? throw new InbriskException(ErrorCode.Unsupported, error));
    }

    /// like key/type/hotkey may target whatever currently has focus).</summary>
    internal static TargetRef OptionalTarget(InbriskRuntime i, string[] args)
    {
        if (args.Any(a => a is "--window" or "--role" or "--name"
                or "--automation-id" or "--pid" or "--hwnd"))
            return TargetRef.Find(BuildSpec(i, args));
        return new TargetRef();
    }


    internal static long? ScopedHwnd(InbriskRuntime i, string[] args)
    {
        if (Opt(args, "--window") is { } w) return i.ResolveWindow(w);
        if (int.TryParse(Opt(args, "--pid"), out var p)) return i.ResolvePidWindow(p);
        return null;
    }


    internal static FindSpec BuildSpec(InbriskRuntime i, string[] args)
    {
        long? hwnd = null;
        var w = Opt(args, "--window");
        if (w != null) hwnd = i.ResolveWindow(w);
        if (Opt(args, "--hwnd") is { } hs) hwnd = i.ResolveWindow(hs);
        Role? role = Enum.TryParse<Role>(Opt(args, "--role"), true, out var r) ? r : null;
        int? pid = int.TryParse(Opt(args, "--pid"), out var p) ? p : null;
        var max = int.TryParse(Opt(args, "--limit"), out var lm) ? lm : 50;
        var depth = int.TryParse(Opt(args, "--depth"), out var dp) ? dp : 12;
        return new FindSpec(Hwnd: hwnd, WindowTitle: hwnd == null ? w : null,
            Pid: pid, Role: role, Name: Opt(args, "--name"),
            AutomationId: Opt(args, "--automation-id"),
            MaxDepth: depth, MaxResults: max,
            ValueContains: Opt(args, "--value"));
    }

    internal static void PrintResult(ActionResult r)
    {
        Console.WriteLine($"{(r.Success ? "SUCCESS" : "FAILED")}  backend={r.BackendUsed} method={r.Method} " +
            $"verify={r.Verification} {r.Duration.TotalMilliseconds:0}ms" +
            (r.ErrorMessage != null ? $"\n  error[{r.Error}]: {r.ErrorMessage}" : ""));
        foreach (var a in r.Attempts.SkipLast(1))
            Console.WriteLine($"  attempt {a.Backend}/{a.Method}: {(a.Success ? "ok" : a.Error)}");
    }

    internal static int CmdHelp(string cmd)
    {
        var Help = new Dictionary<string, string>
        {
        ["inspect"] = "inspect <window|--pid P> [--depth N] [--offscreen] — pruned UIA tree of a window",
        ["find"] = "find --window W|--pid P [--role R] [--name N] [--automation-id A] [--value V] [--limit N] [--depth N]",
        ["invoke"] = "invoke <element-id|--selectors> — invoke a control's default action",
        ["click"] = "click <element-id|--selectors> — semantic click with coordinate fallback",
        ["clickat"] = "clickat <x> <y> [--button left|right|middle] [--count N] — raw coordinate click",
        ["rightclick"] = "rightclick <x> <y>|<element-id|--selectors> — context-menu click",
        ["scroll"] = "scroll [<x> <y>|<element-id|--selectors>] [--delta N] — wheel scroll (negative = down)",
        ["key"] = "key <enter|escape|tab|space|backspace|delete|up|down|left|right|home|end|pageup|pagedown|f1..f12|a..z|0..9> [--count N] [--window W|--pid P] — keypress to the focused or targeted control",
        ["hotkey"] = "hotkey <ctrl+shift+key> [--window W|--pid P] — modifier chord, e.g. ctrl+s, shift+f10",
        ["drag"] = "drag <x1> <y1> <x2> <y2> — press-move-release",
        ["setvalue"] = "setvalue <element-id|--selectors> \"text\" — UIA ValuePattern write",
        ["type"] = "type <element-id|--selectors|--focused> \"text\" — keystrokes; --focused types into the current focus",
        ["toggle"] = "toggle <element-id|--selectors> — checkbox/toggle flip, post-verified",
        ["select"] = "select <element-id|--selectors> — list/tree item select, post-verified",
        ["focus"] = "focus <window|--pid P> — raise a window to the foreground",
        ["observe"] = "observe [--window W|--pid P] [--shot] — unified JSON observation",
        ["launch"] = "launch <app> [--ghost] — launch application (pass --ghost to launch on isolated Ghost desktop)",
        ["shot"] = "shot [out.png] [--window W|--pid P] [--ghost] — capture a frame to PNG (pass --ghost to capture Ghost desktop)",
        ["wait"] = "wait stable [--window W|--pid P] | wait element --selectors | wait gone <element-id>",
        ["run"] = "run <plan.json> [--ghost] — multi-step plan in one process (pass --ghost to dispatch to Ghost session / GhostBatchDispatcher)",
        ["events"] = "events [seconds] — stream semantic UIA/WinEvent events",
        ["clipboard"] = "clipboard read|write \"text\"",
        ["guards"] = "guards — secure-desktop + foreground integrity state",
        ["reset-input"] = "reset-input [--fix] — release stuck keys/buttons, list input-swallowing windows",
        ["windows"] = "windows [--ghost] — list top-level windows (pass --ghost to list Ghost desktop windows)",
        ["monitors"] = "monitors — monitor topology + DPI",
        ["mcp"] = "mcp [--stdio] [--log-level L] [--ghost] — MCP stdio server (pass --ghost to enable Ghost session mode)",
        };
        if (Help.TryGetValue(cmd, out var line))
        {
            Console.WriteLine($"  {line}");
            Console.WriteLine("  selectors: --window W | --pid P | --hwnd H | --role R | --name N | --automation-id A");
            Console.WriteLine("  note: element ids are stable across calls (persisted recipes); a panic stop denies all mutating commands");
            return 0;
        }
        Console.WriteLine($"no help for '{cmd}' — run `inbrisk` for the full command list");
        return 2;
    }


    internal static int Usage()
    {
        Console.WriteLine("""
            inbrisk — windows computer-use runtime (M1)

              ui                               setup app (install, hosts, safety, colors)
              windows [--ghost]                list top-level windows (--ghost for Ghost desktop)
              monitors                         monitor topology + DPI
              launch <app> [--ghost]           launch app (--ghost for Ghost desktop)
              inspect <window|--pid P> [--depth N]   pruned UIA tree
              find --window W|--pid P [--role R] [--name N] [--automation-id A]
                   [--limit N] [--depth N] [--value V]
              invoke|click|toggle|select <element-id|--selectors>
              clickat <x> <y> [--button left|right|middle] [--count N]
              rightclick <x> <y>|<element-id|--selectors>
              scroll [<x> <y>|<element-id|--selectors>] [--delta N]
              key <name> [--count N] [--window W|--pid P]
              hotkey <ctrl+shift+key> [--window W|--pid P]
              drag <x1> <y1> <x2> <y2>
              setvalue <element-id|--selectors> "text"
              type <element-id|--selectors|--focused> "text"
              focus <window|--pid P>
              observe [--window W|--pid P] [--shot]   unified JSON observation
              shot [out.png] [--window W|--pid P] [--ghost]     capture frame (--ghost for Ghost desktop)
              wait stable|element|gone … [--pid P]
              run <plan.json> [--ghost]        multi-step UI plan in one process (--ghost dispatches to Ghost session)
              events [seconds]                 stream semantic events
              clipboard read|write "text"
              guards                           integrity/secure-desktop state
              reset-input [--fix]              release stuck keys/buttons + find
                                               input-swallowing windows (--fix
                                               makes them click-through)
              mcp [--stdio] [--log-level L] [--ghost]    MCP stdio server (protocol=stdout, logs=stderr)

              ghost <tray|pip|status|stop|logs>  Ghost OS background session & PiP control suite

              setup [--non-interactive] [--host H]   onboard: detect hosts + connect
              install [--machine]              install to %LOCALAPPDATA%\Programs\Inbrisk
              doctor [--json]                  read-only health check (incl. real MCP handshake)
              hosts                            list detected MCP hosts
              connect <host> / disconnect <host>     add/remove Inbrisk MCP registration
              status [--json]                  install + host + emergency state
              version [--json]                 version + build provenance + SHA-256
              repair                           repoint stale host registrations
              update [--manifest url|file]     verified update (SHA-256 required)
              uninstall [--keep-data] [--yes]  remove registrations + binaries

              --ghost       target isolated Ghost session/desktop instead of interactive desktop
              --yes / -y    auto-confirm CONFIRM-class actions
            """);
        return 2;
    }
}
