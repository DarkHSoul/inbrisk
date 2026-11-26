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
    /// </summary>
    internal static async Task<int> CmdRunAsync(InbriskRuntime i, string[] args)
    {
        var path = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))
            ?? throw new InbriskException(ErrorCode.Unsupported,
                "run requires a plan.json path");
        var planText = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(planText);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InbriskException(ErrorCode.Unsupported,
                "run plan must be a JSON array of steps");

        if (Has(args, "--ghost"))
        {
            return await RunGhostPlanAsync(planText).ConfigureAwait(false);
        }

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

    internal static async Task<int> RunGhostPlanAsync(string planText)
    {
        if (EmergencyGate.IsStopped)
        {
            Console.Error.WriteLine("error: Computer control stopped by emergency hotkey.");
            return 1;
        }

        GhostBatchResult? result = null;
        try
        {
            await using var ipcServer = new GhostIpcServer(GhostIpcDefaults.PipeName);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await ipcServer.StartAsync(cts.Token).ConfigureAwait(false);
                var sw = Stopwatch.StartNew();
                while (!ipcServer.IsConnected && sw.Elapsed < TimeSpan.FromSeconds(3) && !cts.IsCancellationRequested)
                {
                    await Task.Delay(100, cts.Token).ConfigureAwait(false);
                }
            }
            catch { }

            if (ipcServer.IsConnected)
            {
                var dispatcher = new GhostBatchDispatcher(ipcServer);
                result = await dispatcher.DispatchBatchAsync(planText, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostBatchDispatcher] Remote dispatch attempt: {ex.Message}");
        }

        if (result == null)
        {
            try
            {
                var localExecutor = new GhostInputExecutor();
                result = await localExecutor.ExecuteBatchAsync(planText).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: Failed to execute ghost batch: {ex.Message}");
                return 4;
            }
        }

        if (result.Results != null && result.Results.Count > 0)
        {
            for (int idx = 0; idx < result.Results.Count; idx++)
            {
                var r = result.Results[idx];
                var stepNum = r.Step > 0 ? r.Step : (idx + 1);
                var desc = !string.IsNullOrEmpty(r.ActionName) ? r.ActionName : (r.Do ?? "?");
                if (r.Ok)
                {
                    Console.WriteLine($"[{stepNum}] {desc}: ok ({r.Duration}ms)");
                }
                else
                {
                    Console.WriteLine($"[{stepNum}] {desc}: FAILED {r.Error ?? "unknown error"} ({r.Duration}ms)");
                }
            }
        }

        if (!result.Success)
        {
            if (!string.IsNullOrEmpty(result.Error))
            {
                Console.Error.WriteLine($"error: Ghost batch execution failed: {result.Error}");
            }
            return 4;
        }

        Console.WriteLine($"Ghost batch execution finished: {result.StepsExecuted} step(s) completed in {result.TotalDurationMs}ms.");
        return 0;
    }

    internal static byte[] ConvertRawBgraToPng(int width, int height, int stride, byte[] rawBgra)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = Math.Min(stride, bmpData.Stride);
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(rawBgra, y * stride, IntPtr.Add(bmpData.Scan0, y * bmpData.Stride), rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(bmpData);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    internal static string RunStep(InbriskRuntime i, JsonElement s, Dictionary<string, string> vars)
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

    internal static bool HasSelectorProps(JsonElement s) =>
        s.TryGetProperty("pid", out _) || s.TryGetProperty("hwnd", out _) ||
        s.TryGetProperty("window", out _) || s.TryGetProperty("role", out _) ||
        s.TryGetProperty("name", out _) || s.TryGetProperty("automationId", out _);

    internal static FindSpec JsonSpec(InbriskRuntime i, JsonElement s)
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
}
