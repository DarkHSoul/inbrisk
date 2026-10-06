using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Mcp;
using Inbrisk.Platform.Windows.Hud;
using Inbrisk.Platform.Windows.Indicator;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Tray;
using Inbrisk.Runtime;
using Inbrisk.Sdk;

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
    Inbrisk.Cli.Ui.ConsoleAttach.Ensure();
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

var json = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

static string? Opt(string[] args, string name) =>
    args.SkipWhile(a => a != name).Skip(1).FirstOrDefault(a => !a.StartsWith("--"));

static bool Has(string[] args, string name) => args.Contains(name);

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

// `inbrisk mcp [--stdio] [--log-level debug]` — runs the MCP stdio server
// in-process; the server owns its own InbriskRuntime via McpSession, so it
// must dispatch before the CLI's runtime is created.
if (args[0].Equals("mcp", StringComparison.OrdinalIgnoreCase))
    return await Inbrisk.Mcp.McpHost.RunAsync(args.Skip(1).ToArray());

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
    return cmd switch
    {
        "windows" => CmdWindows(inbrisk),
        "monitors" => CmdMonitors(inbrisk),
        "inspect" => CmdInspect(inbrisk, args),
        "find" => CmdFind(inbrisk, args),
        "invoke" => CmdAct(inbrisk, args, ActionKind.Invoke),
        "click" => CmdAct(inbrisk, args, ActionKind.Click),
        "clickat" => CmdClickAt(inbrisk, args),
        "rightclick" => CmdRightClick(inbrisk, args),
        "scroll" => CmdScroll(inbrisk, args),
        "key" => CmdKey(inbrisk, args),
        "hotkey" => CmdHotkey(inbrisk, args),
        "drag" => CmdDrag(inbrisk, args),
        "setvalue" => CmdSetValue(inbrisk, args),
        "type" => CmdType(inbrisk, args),
        "toggle" => CmdAct(inbrisk, args, ActionKind.Toggle),
        "select" => CmdAct(inbrisk, args, ActionKind.Select),
        "focus" => CmdFocus(inbrisk, args),
        "observe" => CmdObserve(inbrisk, args),
        "shot" => CmdShot(inbrisk, args),
        "wait" => CmdWait(inbrisk, args),
        "run" => CmdRun(inbrisk, args),
        "events" => CmdEvents(inbrisk, args),
        "clipboard" => CmdClipboard(inbrisk, args),
        "guards" => CmdGuards(inbrisk),
        "reset-input" => CmdResetInput(inbrisk, args),
        _ => Usage(),
    };
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

// Capability probes for doctor/setup — a temporary runtime with the
// indicator off; verifies UIA, capture, input and DPI awareness for real.
static async Task<(bool uia, bool capture, bool input, bool dpi)> Probes()
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

int CmdWindows(InbriskRuntime i)
{
    foreach (var w in i.Windows()) Console.WriteLine(w);
    return 0;
}

int CmdMonitors(InbriskRuntime i)
{
    foreach (var m in i.Monitors())
        Console.WriteLine($"[{m.Index}] {m.DeviceName} {m.Bounds} dpi={m.DpiX}x{m.DpiY} scale={m.ScaleX:0.##}x{(m.IsPrimary ? " primary" : "")}");
    return 0;
}

int CmdInspect(InbriskRuntime i, string[] args)
{
    var hwnd = ResolveWindowArg(i, args.ElementAtOrDefault(1), args,
        "inspect requires a window title/hwnd or --pid");
    var depth = int.TryParse(Opt(args, "--depth"), out var d) ? d : 8;
    var elements = i.Inspect(hwnd, new InspectOptions(MaxDepth: depth,
        IncludeOffscreen: Has(args, "--offscreen")));
    Console.WriteLine($"window '{i.Window(hwnd)?.Title}' — {elements.Count} elements (pruned)");
    foreach (var e in elements.Take(400))
        Console.WriteLine($"  {e.Id}  {e.Role,-12} {(e.Name ?? "-")}  {e.Bounds}  act=[{string.Join(",", e.Actions)}]" +
            (e.Props.TryGetValue("enabled", out var en) && en is false ? " DISABLED" : "") +
            (e.Props.TryGetValue("toggleState", out var ts) ? $" toggle={ts}" : ""));
    return 0;
}

int CmdFind(InbriskRuntime i, string[] args)
{
    var spec = BuildSpec(i, args);
    var found = i.Find(spec);
    if (found.Count == 0) { Console.WriteLine("no match"); return 3; }
    foreach (var e in found)
        Console.WriteLine($"{e.Id}  {e.Role,-12} '{e.Name}'  {e.Bounds}  hwnd=0x{e.Hwnd:X} act=[{string.Join(",", e.Actions)}]");
    if (found.Count >= spec.MaxResults)
        Console.WriteLine($"…result capped at {spec.MaxResults} — pass --limit N for more or refine the selector");
    return 0;
}

int CmdAct(InbriskRuntime i, string[] args, ActionKind kind)
{
    var target = ResolveTarget(i, args, $"{kind} requires an element id or a selector (--window/--role/--name/--automation-id)");
    var r = i.Perform(new ActionIntent(kind, target));
    PrintResult(r);
    return r.Success ? 0 : 4;
}

/// <summary>Element id (same process) or selector flags → TargetRef.</summary>
TargetRef ResolveTarget(InbriskRuntime i, string[] args, string error)
{
    var first = args.ElementAtOrDefault(1)
        ?? throw new InbriskException(ErrorCode.Unsupported, error);
    if (!first.StartsWith("--"))
        return TargetRef.Element(first);
    return TargetRef.Find(BuildSpec(i, args.Skip(1).ToArray()));
}

/// <summary>Window arg: positional title/hwnd, or --pid.</summary>
long ResolveWindowArg(InbriskRuntime i, string? pos, string[] args, string error)
{
    if (int.TryParse(Opt(args, "--pid"), out var pid)) return i.ResolvePidWindow(pid);
    return i.ResolveWindow(pos
        ?? throw new InbriskException(ErrorCode.Unsupported, error));
}

int CmdClickAt(InbriskRuntime i, string[] args)
{
    var button = (Opt(args, "--button") ?? "left").ToLowerInvariant() switch
    {
        "left" => ActionKind.Click, "right" => ActionKind.RightClick,
        "middle" => ActionKind.MiddleClick,
        var b => throw new InbriskException(ErrorCode.Unsupported,
            $"unknown --button '{b}' (left|right|middle)"),
    };
    var count = int.TryParse(Opt(args, "--count"), out var c) ? c : 1;
    if (count == 2 && button == ActionKind.Click) button = ActionKind.DoubleClick;
    var r = i.Perform(new ActionIntent(button,
        TargetRef.At(int.Parse(args[1]), int.Parse(args[2]))));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdRightClick(InbriskRuntime i, string[] args)
{
    ActionResult r;
    if (int.TryParse(args.ElementAtOrDefault(1), out var x) &&
        int.TryParse(args.ElementAtOrDefault(2), out var y))
        r = i.Perform(new ActionIntent(ActionKind.RightClick, TargetRef.At(x, y)));
    else
        r = i.Perform(new ActionIntent(ActionKind.RightClick,
            ResolveTarget(i, args, "rightclick requires <x> <y>, an element id, or a selector")));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdScroll(InbriskRuntime i, string[] args)
{
    var delta = int.TryParse(Opt(args, "--delta"), out var d) ? d : -120;
    TargetRef target;
    if (int.TryParse(args.ElementAtOrDefault(1), out var sx) &&
        int.TryParse(args.ElementAtOrDefault(2), out var sy))
        target = TargetRef.At(sx, sy);
    else if (args.ElementAtOrDefault(1)?.StartsWith("--") == false)
        target = TargetRef.Element(args[1]);
    else if (args.Skip(1).Any(a => a is "--window" or "--role" or "--name"
                 or "--automation-id" or "--pid" or "--hwnd"))
        target = TargetRef.Find(BuildSpec(i, args.Skip(1).ToArray()));
    else
        target = TargetRef.Window(i.ForegroundWindow()?.Hwnd
            ?? throw new InbriskException(ErrorCode.NotFound,
                "no foreground window — pass <x> <y> or a selector"));
    var r = i.Perform(new ActionIntent(ActionKind.Scroll, target,
        new Dictionary<string, object?> { ["delta"] = delta }));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdKey(InbriskRuntime i, string[] args)
{
    var name = args.ElementAtOrDefault(1)
        ?? throw new InbriskException(ErrorCode.Unsupported,
            "key requires a key name (enter, escape, tab, f5, pageup…)");
    if (!Enum.TryParse<KeyCode>(name, true, out var key))
        throw new InbriskException(ErrorCode.Unsupported, $"unknown key '{name}'");
    var actArgs = new Dictionary<string, object?> { ["key"] = key.ToString() };
    if (int.TryParse(Opt(args, "--count"), out var c)) actArgs["count"] = c;
    var r = i.Perform(new ActionIntent(ActionKind.KeyPress,
        OptionalTarget(i, args.Skip(2).ToArray()), actArgs));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdHotkey(InbriskRuntime i, string[] args)
{
    var chord = args.ElementAtOrDefault(1)
        ?? throw new InbriskException(ErrorCode.Unsupported,
            "hotkey requires a chord like ctrl+shift+escape");
    var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2 || !Enum.TryParse<KeyCode>(parts[^1], true, out var key))
        throw new InbriskException(ErrorCode.Unsupported,
            $"invalid chord '{chord}' — modifiers+key, e.g. ctrl+s");
    var mods = new List<object?>();
    foreach (var m in parts[..^1])
    {
        if (!Enum.TryParse<KeyCode>(m, true, out var mk))
            throw new InbriskException(ErrorCode.Unsupported, $"unknown modifier '{m}'");
        mods.Add(mk.ToString());
    }
    var r = i.Perform(new ActionIntent(ActionKind.Hotkey,
        OptionalTarget(i, args.Skip(2).ToArray()),
        new Dictionary<string, object?> { ["key"] = key.ToString(), ["modifiers"] = mods }));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdDrag(InbriskRuntime i, string[] args)
{
    var r = i.Perform(new ActionIntent(ActionKind.Drag,
        TargetRef.At(int.Parse(args[1]), int.Parse(args[2])),
        new Dictionary<string, object?>
        { ["to"] = new[] { int.Parse(args[3]), int.Parse(args[4]) } }));
    PrintResult(r); return r.Success ? 0 : 4;
}

/// <summary>Selector flags → TargetRef.Find, or an empty target (actions
/// like key/type/hotkey may target whatever currently has focus).</summary>
TargetRef OptionalTarget(InbriskRuntime i, string[] args)
{
    if (args.Any(a => a is "--window" or "--role" or "--name"
            or "--automation-id" or "--pid" or "--hwnd"))
        return TargetRef.Find(BuildSpec(i, args));
    return new TargetRef();
}

int CmdSetValue(InbriskRuntime i, string[] args)
{
    var target = ResolveTarget(i, args, "setvalue requires element id or selector");
    var text = args[1].StartsWith("--")
        ? (Opt(args, "--text") ?? "")
        : args.ElementAtOrDefault(2) ?? "";
    var r = i.Perform(new ActionIntent(ActionKind.SetValue, target,
        new Dictionary<string, object?> { ["text"] = text }));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdType(InbriskRuntime i, string[] args)
{
    TargetRef target;
    string text;
    var hasSelector = args.Skip(1).Any(a => a is "--window" or "--role" or "--name"
        or "--automation-id" or "--pid" or "--hwnd");
    if (args.ElementAtOrDefault(1) == "--focused" ||
        (args.ElementAtOrDefault(1)?.StartsWith("--") == true && !hasSelector))
    {
        // type into whatever currently has keyboard focus
        target = new TargetRef();
        text = Opt(args, "--text") ??
            string.Join(' ', args.Skip(args.ElementAtOrDefault(1) == "--focused" ? 2 : 1)
                .Where(a => !a.StartsWith("--")));
    }
    else
    {
        target = ResolveTarget(i, args,
            "type requires element id, selector, or --focused");
        text = args[1].StartsWith("--")
            ? (Opt(args, "--text") ?? "")
            : string.Join(' ', args.Skip(2));
    }
    var r = i.Perform(new ActionIntent(ActionKind.TypeText, target,
        new Dictionary<string, object?> { ["text"] = text }));
    PrintResult(r); return r.Success ? 0 : 4;
}

int CmdFocus(InbriskRuntime i, string[] args)
{
    var hwnd = ResolveWindowArg(i, args.ElementAtOrDefault(1), args,
        "focus requires a window title/hwnd or --pid");
    var r = i.Focus(hwnd);
    PrintResult(r); return r.Success ? 0 : 4;
}

long? ScopedHwnd(InbriskRuntime i, string[] args)
{
    if (Opt(args, "--window") is { } w) return i.ResolveWindow(w);
    if (int.TryParse(Opt(args, "--pid"), out var p)) return i.ResolvePidWindow(p);
    return null;
}

int CmdObserve(InbriskRuntime i, string[] args)
{
    var obs = i.Observe(ScopedHwnd(i, args), Has(args, "--shot"));
    Console.WriteLine(JsonSerializer.Serialize(obs, json));
    return 0;
}

int CmdShot(InbriskRuntime i, string[] args)
{
    var path = args.ElementAtOrDefault(1) ?? "shot.png";
    i.Shot(path, ScopedHwnd(i, args));
    Console.WriteLine($"saved {path}");
    return 0;
}

int CmdWait(InbriskRuntime i, string[] args)
{
    i.StartEvents();
    var kind = args.ElementAtOrDefault(1) ?? "stable";
    WaitResult r;
    switch (kind)
    {
        case "stable":
            var region = ScopedHwnd(i, args) is { } sh
                ? i.Window(sh)?.Bounds ?? i.VirtualDesktop()
                : i.VirtualDesktop();
            r = i.WaitForStable(region, timeoutMs: 20000);
            break;
        case "element":
            r = i.WaitForElement(BuildSpec(i, args.Skip(2).ToArray()));
            break;
        case "gone":
            r = i.WaitForGone(args[2]);
            break;
        default:
            throw new InbriskException(ErrorCode.Unsupported, $"unknown wait kind '{kind}'");
    }
    Console.WriteLine($"{(r.Success ? "ok" : "failed")} — {r.Reason} ({r.Elapsed.TotalMilliseconds:0}ms)");
    return r.Success ? 0 : 5;
}

/// <summary>
/// `inbrisk run plan.json` — deterministic multi-step UI work in ONE
/// process, so element ids minted by a `find` step stay alive for the
/// action steps that follow. Plan shape:
///   [{"do":"find","selector":{"pid":N,"role":"button","name":"Save"},"as":"b"},
///    {"do":"click","elementId":"$b"},
///    {"do":"key","key":"Enter"}, {"do":"hotkey","keys":"ctrl+s"},
///    {"do":"type","selector":{…},"text":"…"}, {"do":"scroll","at":[x,y],"delta":-240},
///    {"do":"drag","from":[x,y],"to":[x,y]}, {"do":"wait","ms":500},
///    {"do":"wait","kind":"element","selector":{…},"timeoutMs":5000},
///    {"do":"shot","path":"out.png"}, {"do":"focus","window":"Title"}]
/// Steps abort on first failure unless "continueOnError":true.
/// </summary>
int CmdRun(InbriskRuntime i, string[] args)
{
    var path = args.ElementAtOrDefault(1)
        ?? throw new InbriskException(ErrorCode.Unsupported,
            "run requires a plan.json path");
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    if (doc.RootElement.ValueKind != JsonValueKind.Array)
        throw new InbriskException(ErrorCode.Unsupported,
            "run plan must be a JSON array of steps");
    var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var stepNo = 0;
    foreach (var step in doc.RootElement.EnumerateArray())
    {
        stepNo++;
        var desc = step.TryGetProperty("do", out var d) ? d.GetString() ?? "?" : "?";
        try
        {
            Console.WriteLine($"[{stepNo}] {desc}: {RunStep(i, step, vars)}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"[{stepNo}] {desc}: FAILED {e.Message}");
            if (!step.TryGetProperty("continueOnError", out var ce) ||
                ce.ValueKind != JsonValueKind.True)
                return 4;
        }
    }
    return 0;
}

string RunStep(InbriskRuntime i, JsonElement s, Dictionary<string, string> vars)
{
    var action = (s.TryGetProperty("do", out var d) ? d.GetString() : null)
        ?? throw new InbriskException(ErrorCode.Unsupported, "step missing \"do\"");

    string? Str(string p) => s.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
        ? v.GetString() : null;
    int? Num(string p) => s.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number
        ? v.GetInt32() : null;
    (int, int)? Pt(string p) => s.TryGetProperty(p, out var v) &&
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2
        ? (v[0].GetInt32(), v[1].GetInt32()) : null;
    string Ref(string id) =>
        id.StartsWith('$') && vars.TryGetValue(id[1..], out var bound) ? bound : id;

    TargetRef? Target(bool required = true)
    {
        if (Str("elementId") is { } eid) return TargetRef.Element(Ref(eid));
        if (Pt("at") is { } at) return TargetRef.At(at.Item1, at.Item2);
        if (Num("x") is { } x && Num("y") is { } y) return TargetRef.At(x, y);
        if (s.TryGetProperty("selector", out var sel)) return TargetRef.Find(JsonSpec(i, sel));
        if (HasSelectorProps(s)) return TargetRef.Find(JsonSpec(i, s));
        if (required)
            throw new InbriskException(ErrorCode.Unsupported,
                $"{action} requires elementId|at|selector");
        return null;
    }

    ActionResult Do(ActionKind kind, TargetRef target,
        IReadOnlyDictionary<string, object?>? a = null)
    {
        var r = i.Perform(new ActionIntent(kind, target, a));
        if (!r.Success)
            throw new InbriskException(r.Error,
                r.ErrorMessage ?? $"{kind} failed");
        return r;
    }

    switch (action.ToLowerInvariant())
    {
        case "find":
        {
            var found = i.Find(JsonSpec(i, s.TryGetProperty("selector", out var sel)
                ? sel : s));
            if (Str("as") is { } name && found.Count > 0) vars[name] = found[0].Id;
            if (found.Count > 0) vars["last"] = found[0].Id;
            if (found.Count == 0)
                throw new InbriskException(ErrorCode.NotFound, "find matched nothing");
            return $"{found.Count} match(es), first {found[0].Id} '{found[0].Name}'";
        }
        case "click": return $"{Do(ActionKind.Click, Target()!).Method} ok";
        case "rightclick": return $"{Do(ActionKind.RightClick, Target()!).Method} ok";
        case "doubleclick": return $"{Do(ActionKind.DoubleClick, Target()!).Method} ok";
        case "invoke": return $"{Do(ActionKind.Invoke, Target()!).Method} ok";
        case "toggle": return $"{Do(ActionKind.Toggle, Target()!).Method} ok";
        case "select": return $"{Do(ActionKind.Select, Target()!).Method} ok";
        case "setvalue":
            return $"{Do(ActionKind.SetValue, Target()!,
                new Dictionary<string, object?> { ["text"] = Str("text") ?? "" }).Method} ok";
        case "type":
            return $"{Do(ActionKind.TypeText, Target(required: false) ?? new TargetRef(),
                new Dictionary<string, object?> { ["text"] = Str("text") ?? "" }).Method} ok";
        case "key":
            if (!Enum.TryParse<KeyCode>(Str("key"), true, out var k))
                throw new InbriskException(ErrorCode.Unsupported, "key requires a valid key name");
            return $"{Do(ActionKind.KeyPress, Target(required: false) ?? new TargetRef(),
                new Dictionary<string, object?> { ["key"] = k.ToString(),
                    ["count"] = Num("count") ?? 1 }).Method} ok";
        case "hotkey":
        {
            var chord = Str("keys") ?? Str("chord")
                ?? throw new InbriskException(ErrorCode.Unsupported, "hotkey requires \"keys\":\"ctrl+s\"");
            var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !Enum.TryParse<KeyCode>(parts[^1], true, out var hk))
                throw new InbriskException(ErrorCode.Unsupported, $"invalid chord '{chord}'");
            var mods = parts[..^1].Select(m => (object?)m).ToList();
            return $"{Do(ActionKind.Hotkey, Target(required: false) ?? new TargetRef(),
                new Dictionary<string, object?> { ["key"] = hk.ToString(),
                    ["modifiers"] = mods }).Method} ok";
        }
        case "scroll":
            return $"{Do(ActionKind.Scroll, Target()!,
                new Dictionary<string, object?> { ["delta"] = Num("delta") ?? -120 }).Method} ok";
        case "drag":
        {
            var from = Pt("from") ?? throw new InbriskException(ErrorCode.Unsupported,
                "drag requires \"from\":[x,y]");
            var to = Pt("to") ?? throw new InbriskException(ErrorCode.Unsupported,
                "drag requires \"to\":[x,y]");
            return $"{Do(ActionKind.Drag, TargetRef.At(from.Item1, from.Item2),
                new Dictionary<string, object?> { ["to"] = new[] { to.Item1, to.Item2 } }).Method} ok";
        }
        case "focus":
        {
            var hwnd = Num("pid") is { } fp ? i.ResolvePidWindow(fp)
                : Str("hwnd") is { } hh ? i.ResolveWindow(hh)
                : i.ResolveWindow(Str("window")
                    ?? throw new InbriskException(ErrorCode.Unsupported,
                        "focus requires window|hwnd|pid"));
            var r = i.Focus(hwnd);
            if (!r.Success) throw new InbriskException(r.Error, r.ErrorMessage ?? "focus failed");
            return $"focused '{i.Window(hwnd)?.Title}'";
        }
        case "wait":
        {
            if (Num("ms") is { } ms) { Thread.Sleep(ms); return $"slept {ms}ms"; }
            i.StartEvents();
            var wr = i.WaitForElement(JsonSpec(i,
                s.TryGetProperty("selector", out var sel2) ? sel2 : s),
                Num("timeoutMs") ?? 10000);
            if (!wr.Success) throw new InbriskException(ErrorCode.Timeout, wr.Reason);
            return $"wait ok — {wr.Reason}";
        }
        case "shot":
            i.Shot(Str("path") ?? $"run-step.png",
                Num("pid") is { } sp ? i.ResolvePidWindow(sp) : (long?)null);
            return "saved";
        default:
            throw new InbriskException(ErrorCode.Unsupported, $"unknown step \"{action}\"");
    }
}

static bool HasSelectorProps(JsonElement s) =>
    s.TryGetProperty("pid", out _) || s.TryGetProperty("hwnd", out _) ||
    s.TryGetProperty("window", out _) || s.TryGetProperty("role", out _) ||
    s.TryGetProperty("name", out _) || s.TryGetProperty("automationId", out _);

FindSpec JsonSpec(InbriskRuntime i, JsonElement s)
{
    long? hwnd = null;
    if (s.TryGetProperty("hwnd", out var h))
        hwnd = h.ValueKind == JsonValueKind.Number ? h.GetInt64()
            : i.ResolveWindow(h.GetString()!);
    if (hwnd == null && s.TryGetProperty("window", out var w) &&
        w.ValueKind == JsonValueKind.String)
        hwnd = i.ResolveWindow(w.GetString()!);
    int? pid = s.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number
        ? p.GetInt32() : null;
    Role? role = s.TryGetProperty("role", out var r) &&
        Enum.TryParse<Role>(r.GetString(), true, out var rr) ? rr : null;
    return new FindSpec(Hwnd: hwnd, Pid: pid, Role: role,
        Name: s.TryGetProperty("name", out var n) ? n.GetString() : null,
        AutomationId: s.TryGetProperty("automationId", out var a) ? a.GetString() : null,
        MaxResults: s.TryGetProperty("limit", out var l) ? l.GetInt32() : 50,
        MaxDepth: s.TryGetProperty("depth", out var dd) ? dd.GetInt32() : 12);
}

int CmdEvents(InbriskRuntime i, string[] args)
{
    var secs = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 10;
    i.Event += e => Console.WriteLine(e);
    i.StartEvents();
    Console.WriteLine($"listening {secs}s…");
    Thread.Sleep(secs * 1000);
    return 0;
}

int CmdClipboard(InbriskRuntime i, string[] args)
{
    if (args.ElementAtOrDefault(1) == "write")
    { i.ClipboardWrite(string.Join(' ', args.Skip(2))); Console.WriteLine("written"); }
    else Console.WriteLine(i.ClipboardRead() ?? "(empty)");
    return 0;
}

int CmdGuards(InbriskRuntime i)
{
    Console.WriteLine($"secureDesktop: {i.SecureDesktopActive()}");
    var fg = i.ForegroundWindow();
    if (fg != null)
        Console.WriteLine($"foreground: '{fg.Title}' integrity={i.IntegrityOf(fg.Hwnd)}");
    return 0;
}

int CmdResetInput(InbriskRuntime i, string[] args)
{
    var fix = Has(args, "--fix");
    i.Parts.Input.SweepAll();
    Console.WriteLine("swept: released all modifier keys + mouse buttons");
    var blockers = InputHealth.FindBlockingWindows();
    Console.WriteLine($"blocking windows: {blockers.Count}");
    foreach (var b in blockers)
    {
        var fixedIt = fix && InputHealth.MakeClickThrough(b.Hwnd);
        Console.WriteLine($"  0x{b.Hwnd:X} \"{b.Title}\" {b.ProcessName} pid={b.Pid} " +
            $"({b.Bounds.X},{b.Bounds.Y} {b.Bounds.Width}x{b.Bounds.Height}) " +
            $"monitor={b.MonitorIndex} coverage={b.Coverage:0.00} exStyle=0x{b.ExStyle:X8}" +
            (fixedIt ? "  [fixed → click-through]" : ""));
    }
    if (blockers.Count > 0 && !fix)
        Console.WriteLine("hint: re-run with --fix to make these windows click-through");
    return 0;
}

// ---------------------------------------------------------------- helpers

FindSpec BuildSpec(InbriskRuntime i, string[] args)
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

void PrintResult(ActionResult r)
{
    Console.WriteLine($"{(r.Success ? "SUCCESS" : "FAILED")}  backend={r.BackendUsed} method={r.Method} " +
        $"verify={r.Verification} {r.Duration.TotalMilliseconds:0}ms" +
        (r.ErrorMessage != null ? $"\n  error[{r.Error}]: {r.ErrorMessage}" : ""));
    foreach (var a in r.Attempts.SkipLast(1))
        Console.WriteLine($"  attempt {a.Backend}/{a.Method}: {(a.Success ? "ok" : a.Error)}");
}

static int CmdHelp(string cmd)
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
    ["shot"] = "shot [out.png] [--window W|--pid P] — capture a frame to PNG",
    ["wait"] = "wait stable [--window W|--pid P] | wait element --selectors | wait gone <element-id>",
    ["run"] = "run <plan.json> — multi-step plan in one process; find results bind via \"as\":\"name\", reference with \"$name\" or \"$last\"",
    ["events"] = "events [seconds] — stream semantic UIA/WinEvent events",
    ["clipboard"] = "clipboard read|write \"text\"",
    ["guards"] = "guards — secure-desktop + foreground integrity state",
    ["reset-input"] = "reset-input [--fix] — release stuck keys/buttons, list input-swallowing windows",
    ["windows"] = "windows — list top-level windows (hwnd shown as 0x-hex; decimal, 0x-hex and bare hex all accepted as input)",
    ["monitors"] = "monitors — monitor topology + DPI",
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

static int RunDesktopShell()
{
    Inbrisk.Platform.Windows.Native.DesktopBridge.TrySwitchCurrentThread();
    var settings = UserSettings.Load();
    using var control = EmergencyControl.Process;
    control.StartHotkeys(settings.PanicHotkey, settings.ResumeHotkey);

    using var hud = new ActivityHudService(
        enabled: settings.HudEnabled,
        animationsEnabled: settings.AnimationsEnabled,
        alwaysVisible: settings.HudAlwaysVisible,
        hudDockGapDip: settings.HudDockGapDip);
    hud.Start();
    hud.OnClick = () =>
    {
        try
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
        }
        catch { }
    };

    var idleC = UserSettings.ParseColor(settings.IdleColor);
    var emC = UserSettings.ParseColor(settings.EmergencyColor);
    var palette = idleC == null && emC == null
        ? null
        : GlowPalette.FromColors(
            idleC ?? (GlowPalette.Default.R, GlowPalette.Default.G, GlowPalette.Default.B),
            emC ?? (GlowPalette.Default.EmR, GlowPalette.Default.EmG, GlowPalette.Default.EmB));
    using var indicator0 = new ScreenIndicatorService(
        enabled: settings.PerimeterEnabled, palette: palette);
    var indicator = indicator0;
    indicator.Start();

    using var tray = new TrayIconService(settings);
    tray.OnOpenSettings = () =>
    {
        try
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "control") { UseShellExecute = true });
        }
        catch { }
    };
    tray.OnEmergencyStop = () =>
    {
        control.TriggerLocalPanic("tray icon");
        hud.SetEmergency(true, settings.PanicHotkey ?? "Ctrl+Alt+Pause", settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause");
        indicator.SetState(IndicatorState.EmergencyStopped);
    };
    tray.OnResume = () =>
    {
        control.TriggerLocalResume("tray icon");
        hud.SetEmergency(false);
        indicator.SetState(IndicatorState.Disconnected);
        if (settings.HudAlwaysVisible && settings.HudEnabled)
            hud.SetStandby("Inbrisk Hazır");
    };
    tray.OnToggleHud = enabled =>
    {
        var s = UserSettings.Load();
        s.HudEnabled = enabled;
        s.Save();
        hud.SetEnabled(enabled);
        if (enabled && s.HudAlwaysVisible)
            hud.SetStandby("Inbrisk Hazır");
    };
    tray.OnTogglePerimeter = enabled =>
    {
        var s = UserSettings.Load();
        s.PerimeterEnabled = enabled;
        s.Save();
        indicator.SetEnabled(enabled);
    };
    tray.OnPreviewHud = () =>
    {
        Task.Run(async () =>
        {
            try
            {
                hud.SetEnabled(true);
                hud.SetActivity("Spotify açılıyor…", "Spotify");
                await Task.Delay(2200);
                hud.SetSuccess("Spotify açıldı");
            }
            catch { /* preview is best-effort — never fault an unobserved task */ }
        });
    };

    var quitEvent = new ManualResetEventSlim();
    tray.OnQuit = () =>
    {
        quitEvent.Set();
    };
    tray.Start();

    var settingsSync = new System.Threading.Timer(_ =>
    {
        try
        {
            var s = UserSettings.LoadCached();
            var changed = false;
            if (s.PerimeterEnabled != indicator.Enabled) { indicator.SetEnabled(s.PerimeterEnabled); changed = true; }
            if (s.HudEnabled != hud.Enabled) { hud.SetEnabled(s.HudEnabled); changed = true; }
            hud.SetAnimationsEnabled(s.AnimationsEnabled);
            if (changed)
                tray.UpdateStatus(mcpConnected: false, clientName: "Standby",
                    working: false, emergency: EmergencyGate.IsStopped,
                    hudEnabled: s.HudEnabled, perimeterEnabled: s.PerimeterEnabled);
        }
        catch { }
    }, null, 1500, 1500);

    control.StateChanged += s =>
    {
        var isEmergency = s == ComputerControlState.EmergencyStopped;
        hud.SetEmergency(isEmergency, settings.PanicHotkey ?? "Ctrl+Alt+Pause", settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause");
        indicator.SetState(isEmergency ? IndicatorState.EmergencyStopped : IndicatorState.Disconnected);
        tray.UpdateStatus(
            mcpConnected: false,
            clientName: "Standby",
            working: false,
            emergency: isEmergency,
            hudEnabled: UserSettings.Load().HudEnabled,
            perimeterEnabled: UserSettings.Load().PerimeterEnabled);
    };

    if (settings.HudAlwaysVisible && settings.HudEnabled)
    {
        hud.SetStandby("Inbrisk Hazır");
    }

    if (!settings.StartMinimized)
    {
        tray.OpenSettings();
    }

    quitEvent.Wait();
    indicator.Dispose(); // disposes whichever instance is current
    return 0;
}

static int Usage()
{
    Console.WriteLine("""
        inbrisk — windows computer-use runtime (M1)

          ui                               setup app (install, hosts, safety, colors)
          windows                          list top-level windows
          monitors                         monitor topology + DPI
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
          shot [out.png] [--window W|--pid P]     capture frame
          wait stable|element|gone … [--pid P]
          run <plan.json>                  multi-step UI plan in one process —
                                           element ids stay alive between steps
          events [seconds]                 stream semantic events
          clipboard read|write "text"
          guards                           integrity/secure-desktop state
          reset-input [--fix]              release stuck keys/buttons + find
                                           input-swallowing windows (--fix
                                           makes them click-through)
          mcp [--stdio] [--log-level L]    MCP stdio server (protocol=stdout, logs=stderr)

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

          --yes / -y    auto-confirm CONFIRM-class actions
        """);
    return 2;
}
