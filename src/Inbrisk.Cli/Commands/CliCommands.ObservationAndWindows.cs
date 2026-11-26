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

    internal static int CmdWindows(InbriskRuntime i, string[] args)
    {
        if (Has(args, "--ghost"))
        {
            try
            {
                using var desktop = new GhostLocalDesktop(ResolveGhostDesktopName(), createIfNotExists: false);
                var handles = GhostDesktopNative.EnumerateWindows(desktop.DesktopHandle);
                int count = 0;
                foreach (var hWnd in handles)
                {
                    if (hWnd == IntPtr.Zero || !GhostDesktopNative.IsWindowVisible(hWnd)) continue;
                    var (title, className, _) = GhostDesktopNative.GetWindowDetails(hWnd);
                    GhostDesktopNative.GetWindowThreadProcessId(hWnd, out uint pid);
                    if (!string.IsNullOrEmpty(title) || !string.IsNullOrEmpty(className))
                    {
                        Console.WriteLine($"0x{hWnd.ToInt64():X8}  pid={pid,-6}  class='{className}'  '{title}'");
                        count++;
                    }
                }
                if (count == 0)
                {
                    Console.WriteLine("No visible windows on Ghost Desktop.");
                }
                return 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("Ghost Desktop is not currently active (no background session running).");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: Could not inspect Ghost Desktop windows: {ex.Message}");
                return 1;
            }
        }

        foreach (var w in i.Windows()) Console.WriteLine(w);
        return 0;
    }

    internal static int CmdLaunch(InbriskRuntime i, string[] args)
    {
        int appIndex = -1;
        for (int ai = 1; ai < args.Length; ai++)
        {
            if (!args[ai].StartsWith("--", StringComparison.Ordinal))
            {
                appIndex = ai;
                break;
            }
        }

        var app = appIndex >= 0 ? args[appIndex] : null;
        if (string.IsNullOrWhiteSpace(app))
            throw new InbriskException(ErrorCode.Unsupported, "launch requires an app name or path");

        var appArgs = appIndex >= 0
            ? string.Join(' ', args.Skip(appIndex + 1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
            : null;
        var isGhost = Has(args, "--ghost");
        if (isGhost)
        {
            try
            {
                var surfaceResponse = GhostControlCommand.DaemonSurfaceLaunchAsync(
                    app,
                    string.IsNullOrWhiteSpace(appArgs) ? null : appArgs,
                    workingDirectory: null).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(surfaceResponse))
                {
                    using var doc = JsonDocument.Parse(surfaceResponse);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                    {
                        var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown surface launch error";
                        Console.Error.WriteLine($"error: Failed to launch '{app}' as Ghost window surface: {error}");
                        return 1;
                    }
                    var hwnd = root.TryGetProperty("hwnd", out var h) ? h.GetString() : null;
                    var pid = root.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
                    Console.WriteLine($"launched '{app}' as Ghost window surface (PID: {pid}, HWND: {hwnd})");
                    return 0;
                }

                var ghostDesktopName = ResolveGhostDesktopName();
                var desktop = new GhostLocalDesktop(ghostDesktopName, createIfNotExists: true);
                var proc = desktop.StartProcess(app, string.IsNullOrWhiteSpace(appArgs) ? null : appArgs);
                GC.SuppressFinalize(desktop);
                Console.WriteLine($"launched '{app}' on Ghost Desktop '{ghostDesktopName}' (PID: {proc.Id})");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: Failed to launch '{app}' on Ghost Desktop: {ex.Message}");
                return 1;
            }
        }
        else
        {
            var spec = File.Exists(app)
                ? new LaunchSpec(Path: app)
                : new LaunchSpec(App: app);
            var r = i.Launch(spec);
            if (r.Success)
            {
                Console.WriteLine($"launched '{app}' (PID: {r.Pid}, HWND: 0x{r.Hwnd:X}, State: {r.LaunchState})");
                return 0;
            }
            else
            {
                Console.Error.WriteLine($"launch failed: {r.Error ?? r.ErrorDetail ?? r.LaunchState}");
                return 1;
            }
        }
    }

    internal static int CmdMonitors(InbriskRuntime i)
    {
        foreach (var m in i.Monitors())
            Console.WriteLine($"[{m.Index}] {m.DeviceName} {m.Bounds} dpi={m.DpiX}x{m.DpiY} scale={m.ScaleX:0.##}x{(m.IsPrimary ? " primary" : "")}");
        return 0;
    }

    internal static int CmdInspect(InbriskRuntime i, string[] args)
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

    internal static int CmdFind(InbriskRuntime i, string[] args)
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


    internal static int CmdFocus(InbriskRuntime i, string[] args)
    {
        var hwnd = ResolveWindowArg(i, args.ElementAtOrDefault(1), args,
            "focus requires a window title/hwnd or --pid");
        var r = i.Focus(hwnd);
        PrintResult(r); return r.Success ? 0 : 4;
    }


    internal static int CmdObserve(InbriskRuntime i, string[] args)
    {
        var obs = i.Observe(ScopedHwnd(i, args), Has(args, "--shot"));
        Console.WriteLine(JsonSerializer.Serialize(obs, json));
        return 0;
    }

    internal static int CmdShot(InbriskRuntime i, string[] args)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "shot.png";
        if (Has(args, "--ghost"))
        {
            try
            {
                byte[]? pngBytes = null;
                if (GhostSharedFrameBuffer.TryOpenConsumer(out var consumer, GhostSharedFrameBuffer.DefaultMapName) && consumer != null)
                {
                    using (consumer)
                    {
                        int approxSize = 1920 * 1080 * 4;
                        byte[] buffer = new byte[approxSize];
                        if (consumer.TryReadLatestFrame(buffer, out var header))
                        {
                            pngBytes = ConvertRawBgraToPng(header.Width, header.Height, header.Stride, buffer);
                        }
                    }
                }

                if (pngBytes == null)
                {
                    pngBytes = GhostDesktopCapture.CaptureDesktopToPng(ResolveGhostDesktopName(), 1920, 1080);
                }

                File.WriteAllBytes(path, pngBytes);
                Console.WriteLine($"saved {path} (Ghost Desktop)");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: Failed to capture Ghost Desktop shot: {ex.Message}");
                return 1;
            }
        }

        i.Shot(path, ScopedHwnd(i, args));
        Console.WriteLine($"saved {path}");
        return 0;
    }

    internal static int CmdWait(InbriskRuntime i, string[] args)
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


    internal static int CmdEvents(InbriskRuntime i, string[] args)
    {
        var secs = int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 10;
        i.Event += e => Console.WriteLine(e);
        i.StartEvents();
        Console.WriteLine($"listening {secs}s…");
        Thread.Sleep(secs * 1000);
        return 0;
    }

    internal static int CmdClipboard(InbriskRuntime i, string[] args)
    {
        if (args.ElementAtOrDefault(1) == "write")
        { i.ClipboardWrite(string.Join(' ', args.Skip(2))); Console.WriteLine("written"); }
        else Console.WriteLine(i.ClipboardRead() ?? "(empty)");
        return 0;
    }

    internal static int CmdGuards(InbriskRuntime i)
    {
        Console.WriteLine($"secureDesktop: {i.SecureDesktopActive()}");
        var fg = i.ForegroundWindow();
        if (fg != null)
            Console.WriteLine($"foreground: '{fg.Title}' integrity={i.IntegrityOf(fg.Hwnd)}");
        return 0;
    }

    internal static int CmdResetInput(InbriskRuntime i, string[] args)
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
}
