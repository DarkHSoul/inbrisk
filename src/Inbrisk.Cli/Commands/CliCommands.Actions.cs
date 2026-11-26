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

    internal static int CmdAct(InbriskRuntime i, string[] args, ActionKind kind)
    {
        var target = ResolveTarget(i, args, $"{kind} requires an element id or a selector (--window/--role/--name/--automation-id)");
        var r = i.Perform(new ActionIntent(kind, target));
        PrintResult(r);
        return r.Success ? 0 : 4;
    }



    internal static int CmdClickAt(InbriskRuntime i, string[] args)
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

    internal static int CmdRightClick(InbriskRuntime i, string[] args)
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

    internal static int CmdScroll(InbriskRuntime i, string[] args)
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

    internal static int CmdKey(InbriskRuntime i, string[] args)
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

    internal static int CmdHotkey(InbriskRuntime i, string[] args)
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

    internal static int CmdDrag(InbriskRuntime i, string[] args)
    {
        var r = i.Perform(new ActionIntent(ActionKind.Drag,
            TargetRef.At(int.Parse(args[1]), int.Parse(args[2])),
            new Dictionary<string, object?>
            { ["to"] = new[] { int.Parse(args[3]), int.Parse(args[4]) } }));
        PrintResult(r); return r.Success ? 0 : 4;
    }

    /// <summary>Selector flags → TargetRef.Find, or an empty target (actions


    internal static int CmdSetValue(InbriskRuntime i, string[] args)
    {
        var target = ResolveTarget(i, args, "setvalue requires element id or selector");
        var text = args[1].StartsWith("--")
            ? (Opt(args, "--text") ?? "")
            : args.ElementAtOrDefault(2) ?? "";
        var r = i.Perform(new ActionIntent(ActionKind.SetValue, target,
            new Dictionary<string, object?> { ["text"] = text }));
        PrintResult(r); return r.Success ? 0 : 4;
    }

    internal static int CmdType(InbriskRuntime i, string[] args)
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
}
