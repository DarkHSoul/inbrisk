using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>
/// The MCP tool surface. Every mutating tool funnels through
/// ActionResolver — the same schema→target→frame-guard→safety→execute→
/// auto-verify pipeline the internal agent loop uses. This layer never calls
/// SendInput or UIA directly.
/// </summary>
[McpServerToolType]
public sealed class InbriskTools
{
    private static readonly JsonSerializerOptions J = new() { WriteIndented = false };
    private readonly McpSession _s;
    public InbriskTools(McpSession s) => _s = s;

    /// <summary>Resolved output verbosity — per-call `detail` wins, then
    /// INBRISK_DETAIL env, then settings.json outputDetail, else "full".
    /// Slim exists for small-context models: every fat tool result
    /// (find/observe/run/capabilities) honours it.</summary>
    private readonly string _detailDefault =
        Environment.GetEnvironmentVariable("INBRISK_DETAIL")
        ?? Core.UserSettings.Load().OutputDetail
        ?? "full";

    private bool Slim(string? detail) =>
        string.Equals(detail ?? _detailDefault, "slim",
            StringComparison.OrdinalIgnoreCase);

    private CallToolResult? BadDetail(string? detail) =>
        detail is null || Slim(detail) ||
        string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase)
            ? null
            : Error(OutcomeKind.Malformed,
                $"detail must be \"slim\" or \"full\", got '{detail}'");

    /// <summary>Compact one-line element form for slim output — drops the
    /// always-noise fields (hwnd=0x0, repeated pid, enabled=True,
    /// actions=/bounds= keys) and pads names.</summary>
    private static string SlimEl(UiElement e,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        var b = e.Bounds;
        var s = $"[{e.Id}] {e.Role}";
        if (e.Name is { Length: > 0 } n)
            s += $" \"{TruncEdges(n.Trim(), 80)}\"";
        if (e.Props.TryGetValue("value", out var v) &&
            v?.ToString() is { Length: > 0 } vv)
            s += $" value=\"{TruncEdges(vv, 80)}\"";
        if (e.Props.TryGetValue("state", out var st) && st != null)
            s += $" state={st}";
        if (e.Props.TryGetValue("labelledBy", out var lb) && lb != null)
            s += $" labelledBy=\"{lb}\"";
        s += $" ({b.X},{b.Y} {b.Width}x{b.Height})";
        if (e.Actions.Count > 0) s += $" {string.Join(",", e.Actions)}";
        if (e.Hwnd is { } h) s += $" h=0x{h:X}";
        if (e.Props.TryGetValue("enabled", out var en) && en is false)
            s += " disabled";
        if (tags != null && tags.TryGetValue(e.Id, out var tag))
            s += $" dialogRole={tag}";
        return s;
    }

    /// <summary>Compact one-line observation element — same shape as
    /// SlimEl but for ObsElement (direct Value/State fields).</summary>
    private static string SlimObsEl(ObsElement e)
    {
        var b = e.Bounds;
        return $"[{e.Id}] {e.Role}" +
            (e.Name is { Length: > 0 } n ? $" \"{TruncEdges(n.Trim(), 80)}\"" : "") +
            (!string.IsNullOrEmpty(e.Value)
                ? $" value=\"{TruncEdges(e.Value, 80)}\"" : "") +
            (e.State != null ? $" state={e.State}" : "") +
            $" ({b.X},{b.Y} {b.Width}x{b.Height})" +
            (e.Actions.Count > 0 ? $" {string.Join(",", e.Actions)}" : "");
    }

    /// <summary>EmergencyStopped detail — always names the resume chord so
    /// a zero-knowledge agent can tell the user how to restore control.</summary>
    private string StoppedDetail =>
        $"computer control stopped by local hotkey; only the local user can " +
        $"resume with {_s.Control.ResumeHotkey}";

    // ------------------------------------------------------------ perception

    [McpServerTool(Name = "computer_observe"), Description(
        "Semantic desktop snapshot — exploration and recovery tool. " +
        "Do NOT call it after actions with verificationHint indicating success " +
        "(action results already carry post-state + changes). Only call computer_observe " +
        "for initial screen discovery, unexpected dialogs, or when action status is " +
        "ObservedChange or Unverified and concrete confirmation is needed. Returns active " +
        "window, elements with ids/actions/labelledBy, recent events, delta vs previous " +
        "observation, and optionally frames (mode=visual|both).")]
    public CallToolResult Observe(
        [Description("auto|semantic|visual|both — auto picks pixels only when semantics are thin")] string? mode = null,
        [Description("window handle to scope the snapshot (hex or decimal)")] string? hwnd = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("max elements listed — default 60 (slim) / all (full)")] int? maxElements = null,
        [Description("return only delta vs base snapshot instead of full element tree")] bool? deltaOnly = null,
        [Description("snapshot ID to compute delta against (defaults to previous observation)")] long? baseSnapshotId = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        using var trace = PerfTrace.Begin("tool", "computer_observe");
        ct.ThrowIfCancellationRequested();
        using var cancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;
        if (_s.Control.State == ComputerControlState.EmergencyStopped)
            return Text($"controlState: EmergencyStopped\nemergencyHotkey: {_s.Control.PanicHotkey}\n" +
                $"emergencyHotkeyAvailable: {_s.Control.PanicAvailable}\n" +
                $"computer control is disabled; only the local user can resume with {_s.Control.ResumeHotkey}");
        if (mode != null && mode is not ("auto" or "semantic" or "visual" or "both"))
            return Error(OutcomeKind.Malformed, $"unknown observation mode '{mode}'");
        var policy = (mode ?? "auto").ToLowerInvariant() switch
        {
            "semantic" => VisualAttachPolicy.Never,
            "visual" or "both" => VisualAttachPolicy.Always,
            _ => VisualAttachPolicy.Auto,
        };
        var hint = ParseHwnd(hwnd);
        BuiltObservation built;
        using (PerfTrace.Stage("observe"))
            built = _s.Observe(hint, ObservationBudget.Default, policy, baseSnapshotId);
        if (_s.Control.State == ComputerControlState.EmergencyStopped)
            return Text($"controlState: EmergencyStopped\nemergencyHotkey: {_s.Control.PanicHotkey}");
        var o = built.Observation;

        var slim = Slim(detail);
        var sb = new StringBuilder();
        if (!slim || _s.Control.State != ComputerControlState.Active)
        {
            sb.AppendLine($"controlState: {_s.Control.State}");
            sb.AppendLine($"emergencyHotkey: {_s.Control.PanicHotkey}");
            sb.AppendLine($"emergencyHotkeyAvailable: {_s.Control.PanicAvailable}");
        }
        var blockers = InputHealth.FindBlockingWindows();
        if (blockers.Count > 0)
        {
            sb.AppendLine($"inputBlockers: {blockers.Count} — windows swallowing clicks " +
                "(computer_reset_input fixBlockingWindows:true remediates):");
            foreach (var b in blockers)
                sb.AppendLine($"  0x{b.Hwnd:X} \"{b.Title}\" {b.ProcessName} " +
                    $"coverage={b.Coverage:0.00}");
        }
        sb.AppendLine($"observationId: {o.ObservationId}");
        if (o.BaseObservationId.HasValue)
            sb.AppendLine($"baseObservationId: {o.BaseObservationId.Value}");
        if (o.ActiveWindow != null)
            sb.AppendLine($"active window: \"{o.ActiveWindow.Title}\" hwnd=0x{o.ActiveWindow.Hwnd:X} process={o.ActiveWindow.Process}");
        else sb.AppendLine("active window: (none)");

        if (deltaOnly == true)
        {
            if (o.Delta is { IsEmpty: false } del)
            {
                var baseLabel = o.BaseObservationId.HasValue ? $"snapshot #{o.BaseObservationId.Value}" : "last observe";
                sb.AppendLine($"delta since {baseLabel}:");
                foreach (var n in del.Notes) sb.AppendLine($"  {n}");
                if (del.Added.Count > 0)
                {
                    sb.AppendLine($"  added ({del.Added.Count}):");
                    foreach (var a in del.Added.Take(15))
                        sb.AppendLine(slim ? "    " + SlimObsEl(a) : $"    [{a.Id}] {a.Role} \"{a.Name}\" bounds={a.Bounds}");
                }
                if (del.Changed.Count > 0)
                {
                    sb.AppendLine($"  changed ({del.Changed.Count}):");
                    foreach (var c in del.Changed.Take(15))
                        sb.AppendLine(slim ? "    " + SlimObsEl(c) : $"    [{c.Id}] {c.Role} \"{c.Name}\" {c.State ?? c.Value}");
                }
                if (del.Offscreen is { Count: > 0 } off)
                {
                    sb.AppendLine($"  scrolled offscreen ({off.Count}):");
                    foreach (var sc in off.Take(10))
                        sb.AppendLine($"    [{sc.Id}] {sc.Role} \"{sc.Name}\"");
                }
                if (del.PrunedByBudget is { Count: > 0 } pruned)
                {
                    sb.AppendLine($"  omitted by budget ({pruned.Count}):");
                    foreach (var pr in pruned.Take(5))
                        sb.AppendLine($"    [{pr.Id}] {pr.Role} \"{pr.Name}\"");
                }
                if (del.Removed.Count > 0)
                {
                    sb.AppendLine($"  removed ({del.Removed.Count}):");
                    foreach (var r in del.Removed.Take(10))
                        sb.AppendLine($"    [{r.Id}] {r.Role} \"{r.Name}\"");
                }
            }
            else
            {
                sb.AppendLine("delta: (no changes detected)");
            }
        }
        else
        {
            var elCap = maxElements ?? (slim ? 60 : int.MaxValue);
            sb.AppendLine($"elements ({Math.Min(o.Elements.Count, elCap)} of {o.Elements.Count} shown):");
            foreach (var e in o.Elements.Take(elCap))
                sb.AppendLine(slim
                    ? "  " + SlimObsEl(e)
                    : $"  [{e.Id}] {e.Role}" +
                        (e.Name != null ? $" \"{e.Name}\"" : "") +
                        (e.Value != null ? $" value=\"{e.Value}\"" : "") +
                        (e.State != null ? $" state={e.State}" : "") +
                        (e.Actions.Count > 0 ? $" actions=[{string.Join(",", e.Actions)}]" : "") +
                        $" bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})");
            if (o.Elements.Count > elCap)
                sb.AppendLine($"  …{o.Elements.Count - elCap} more — pass maxElements to see them");
            if (o.RecentEvents.Count > 0)
            {
                sb.AppendLine("recent events:");
                foreach (var ev in o.RecentEvents.TakeLast(8))
                    sb.AppendLine($"  {ev.Kind} hwnd={(ev.Hwnd is { } h ? $"0x{h:X}" : "-")} {ev.Detail}");
            }
            if (o.Delta is { IsEmpty: false } d)
            {
                var baseLabel = o.BaseObservationId.HasValue ? $"snapshot #{o.BaseObservationId.Value}" : "last observe";
                sb.AppendLine($"delta since {baseLabel}:");
                foreach (var n in d.Notes) sb.AppendLine($"  {n}");
                foreach (var c in d.Changed.Take(10))
                    sb.AppendLine($"  changed: [{c.Id}] {c.Role} \"{c.Name}\" {c.State ?? c.Value}");
                if (d.Offscreen is { Count: > 0 } off)
                {
                    foreach (var sc in off.Take(5))
                        sb.AppendLine($"  scrolled offscreen: [{sc.Id}] {sc.Role} \"{sc.Name}\"");
                }
                if (d.PrunedByBudget is { Count: > 0 } pruned)
                {
                    foreach (var pr in pruned.Take(5))
                        sb.AppendLine($"  omitted by budget: [{pr.Id}] {pr.Role} \"{pr.Name}\"");
                }
            }
        }
        if (o.PrevAction is { } p)
            sb.AppendLine($"previous action: {p.Kind} {(p.Success ? "ok" : "FAILED")} {p.Detail}");
        sb.AppendLine(o.Frames.Count == 0
            ? "visual: omitted (semantic representation sufficient)"
            : $"visual: {o.Frames.Count} frame(s) attached — image content below");
        foreach (var f in o.Frames)
            sb.AppendLine($"  frameId={f.FrameId} observationId={o.ObservationId} size={f.Width}x{f.Height}");
        sb.AppendLine($"stats: chars={o.Stats.EstimatedCharacters} images={o.Stats.ImageCount} pixels={o.Stats.ImagePixels}");

        var content = new List<ContentBlock> { new TextContentBlock { Text = sb.ToString() } };
        foreach (var f in o.Frames)
            if (f.Png != null)
                content.Add(ImageContentBlock.FromBytes(f.Png, "image/png"));
        return new CallToolResult { Content = content, IsError = false };
    }

    [McpServerTool(Name = "computer_windows"), Description(
        "List top-level windows (hwnd, title, process, bounds, active flag). " +
        "Windows matching the input-blocking signature (visible, layered, " +
        "not click-through, covering a monitor — e.g. a glitched overlay) " +
        "are marked [INPUT-BLOCKING]; see computer_reset_input.")]
    public CallToolResult Windows(CancellationToken ct = default)
    {
        var wins = _s.Rt.Windows();
        var blockers = InputHealth.FindBlockingWindows()
            .Select(b => b.Hwnd).ToHashSet();
        var sb = new StringBuilder();
        foreach (var w in wins)
            sb.AppendLine($"0x{w.Hwnd:X}  \"{w.Title}\"  {w.ProcessName}  " +
                $"({w.Bounds.X},{w.Bounds.Y} {w.Bounds.Width}x{w.Bounds.Height})" +
                (w.IsForeground ? "  [active]" : "") +
                (blockers.Contains(w.Hwnd) ? "  [INPUT-BLOCKING]" : ""));
        return Text(sb.ToString());
    }

    [McpServerTool(Name = "computer_reset_input"), Description(
        "Input recovery — call when the desktop seems frozen, clicks land " +
        "nowhere, or keys act as if a modifier is held (e.g. after an " +
        "injecting process was killed mid-action). Unconditionally releases " +
        "every modifier key and mouse button — including holds left behind " +
        "by dead processes — then scans for input-swallowing windows: " +
        "visible layered windows that are not click-through and cover a " +
        "monitor (glitched GPU/HUD overlays hit this state). " +
        "fixBlockingWindows:true additionally makes such windows " +
        "click-through without killing them. This tool never clicks, types, " +
        "or presses anything — safe to call anytime, including while " +
        "emergency-stopped.")]
    public CallToolResult ResetInput(
        [Description("also make detected input-blocking windows click-through")] bool? fixBlockingWindows = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var input = _s.Rt.Parts.Input;
        var before = input.DiagnoseInput();
        input.SweepAll(); // release-only — allowed even when EmergencyStopped
        var after = input.DiagnoseInput();
        var blockers = InputHealth.FindBlockingWindows();
        var fixedHwnds = new List<long>();
        if (fixBlockingWindows == true)
            foreach (var b in blockers)
                if (InputHealth.MakeClickThrough(b.Hwnd))
                    fixedHwnds.Add(b.Hwnd);

        var sb = new StringBuilder();
        // root-cause report: what was held (Inbrisk-owned vs physical), what
        // was released, what remains — the data a lockup diagnosis needs
        sb.AppendLine(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["resetReason"] = "manual computer_reset_input",
            ["before"] = before,
            ["recovery"] = "sent UP for all modifiers + mouse buttons (untracked holds included)",
            ["after"] = after,
        }, J));
        sb.AppendLine($"blocking windows: {blockers.Count}" +
            (fixedHwnds.Count > 0 ? $" — made click-through: {fixedHwnds.Count}" : ""));
        foreach (var b in blockers)
            sb.AppendLine($"  0x{b.Hwnd:X} \"{b.Title}\" {b.ProcessName} pid={b.Pid} " +
                $"({b.Bounds.X},{b.Bounds.Y} {b.Bounds.Width}x{b.Bounds.Height}) " +
                $"monitor={b.MonitorIndex} coverage={b.Coverage:0.00} " +
                $"exStyle=0x{b.ExStyle:X8}" +
                (fixedHwnds.Contains(b.Hwnd) ? "  [fixed → click-through]" : ""));
        if (blockers.Count > 0 && fixBlockingWindows != true)
            sb.AppendLine("hint: call again with fixBlockingWindows:true to make " +
                "these windows click-through");
        return Text(sb.ToString());
    }

    [McpServerTool(Name = "computer_launch"), Description(
        "Launch a Windows application by friendly name — the supported " +
        "replacement for shelling out to PowerShell/Start-Process. " +
        "Resolves through Windows app registration in a deterministic " +
        "pipeline: already-running window → Start Menu → App Paths → " +
        "packaged apps (AUMID) → executable on PATH → registered URI " +
        "scheme. With waitFor:\"window\" (default) it returns only after a " +
        "usable, UIA-reachable top-level window exists — not merely when a " +
        "process was spawned. Idempotent: a running app is reused " +
        "(launchState=AlreadyRunning) unless newInstance:true. This is a " +
        "mutating computer action — denied while emergency-stopped, and it " +
        "never runs shell interpreters; arguments are a structured array, " +
        "never a command line. A friendly name matching several DISTINCT " +
        "apps returns AmbiguousApplication + candidates — pick one and " +
        "call computer_launch again immediately with the candidate's " +
        "identifier; do not narrate intermediate steps. " +
        "search: searches the installed-apps catalog (computer_apps) and " +
        "opens the best match — same pipeline as app.")]
    public CallToolResult Launch(
        [Description("friendly app name (\"Spotify\", \"Notepad\", \"Calculator\") — preferred")] string? app = null,
        [Description("search installed apps and open the best match — alias of app (\"Unreal\" finds UnrealEditor)")] string? search = null,
        [Description("executable name or path (\"notepad.exe\")")] string? executable = null,
        [Description("explicit exe path")] string? path = null,
        [Description("packaged-app AUMID (PackageFamilyName!AppId)")] string? aumid = null,
        [Description("URI with a registered handler (\"spotify:\")")] string? uri = null,
        [Description("structured arguments — one element = one verbatim argument")] string[]? arguments = null,
        [Description("spawn a new instance instead of reusing a running window — default false")] bool? newInstance = null,
        [Description("window (default) | process | none")] string? waitFor = null,
        [Description("readiness timeout ms — default 10000")] int? timeoutMs = null,
        [Description("optional remote debugging port for Chrome DevTools Protocol / CDP (e.g. 9222)")] int? debugPort = null,
        CancellationToken ct = default)
    {
        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return Error(OutcomeKind.EmergencyStopped,
                StoppedDetail);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);

        var ids = new[]
        {
            app != null, search != null, executable != null, path != null,
            aumid != null, uri != null,
        }.Count(b => b);
        if (ids == 0)
            return Error(OutcomeKind.Malformed,
                "one of app|search|executable|path|aumid|uri is required");
        if (ids > 1)
            return Error(OutcomeKind.Malformed,
                "launch takes exactly one identifier: app|search|executable|path|aumid|uri");
        if (arguments is { Length: > 0 } && (aumid != null || uri != null))
            return Error(OutcomeKind.Malformed,
                "arguments only apply to executable launches");

        LaunchResult r;
        try
        {
            r = _s.Rt.Launch(new LaunchSpec(app ?? search, executable, path,
                aumid, uri, arguments, newInstance ?? false,
                waitFor ?? "window", timeoutMs ?? 10000, debugPort), linked.Token);
        }
        catch (OperationCanceledException)
        {
            return Error(OutcomeKind.Cancelled, "launch cancelled");
        }

        if (!r.Success)
        {
            var kind = r.Error switch
            {
                "AmbiguousApplication" => OutcomeKind.AmbiguousApplication,
                "PolicyDenied" => OutcomeKind.PolicyDenied,
                "Timeout" => OutcomeKind.Timeout,
                "TargetNotFound" => OutcomeKind.TargetNotFound,
                "EmergencyStopped" => OutcomeKind.EmergencyStopped,
                _ => OutcomeKind.Failed,
            };
            var sb = new StringBuilder(r.ErrorDetail ?? r.Error);
            if (r.Candidates is { Count: > 0 } cs)
            {
                sb.Append("\ncandidates:");
                foreach (var c in cs)
                    sb.Append($"\n  {c.Name}  [{c.Method}]  {c.Identifier}");
                sb.Append("\n→ pick one and call computer_launch again " +
                    "now with that identifier — do not narrate");
            }
            if (r.Error == "TargetNotFound" && (app ?? search) is { } q)
            {
                var near = ClosestApps(q, 3);
                if (near.Count > 0)
                {
                    sb.Append("\nclosest registered apps:");
                    foreach (var a in near)
                        sb.Append($"\n  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}");
                    sb.Append("\n→ retry computer_launch with one of these " +
                        "args now — do not narrate");
                }
            }
            return Error(kind, sb.ToString());
        }

        // record the window as session scope so a following
        // computer_run within:/wait_for can target it
        if (r.Hwnd is { } h)
            _s.ScopeHwnd = h;
        return Text(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["status"] = "Verified",
            ["app"] = r.ResolvedName,
            ["method"] = r.Method?.ToString(),
            ["process"] = r.Pid is { } p
                ? new { pid = p } : null,
            ["window"] = r.Hwnd is { } wh
                ? new { hwnd = $"0x{wh:X}", title = r.WindowTitle } : null,
            ["launchState"] = r.LaunchState,
            ["alreadyRunning"] = r.LaunchState == "AlreadyRunning",
            ["note"] = r.LaunchState == "AlreadyRunning"
                ? "already open — reused the running window" : null,
            ["durationMs"] = r.LaunchMs + r.ReadyMs,
            ["launchMs"] = r.LaunchMs,
            ["readyMs"] = r.ReadyMs,
        }, J));
    }

    [McpServerTool(Name = "computer_apps"), Description(
        "SEARCH the launchable applications registered on this machine — " +
        "pass name and get back only matching entries, each with the " +
        "exact computer_launch argument that opens it. To OPEN an app " +
        "you usually skip this tool entirely: computer_launch{search:" +
        "\"name\"} searches AND launches in one call. Without name this " +
        "tool returns counts only — the full list needs all:true and is " +
        "huge; always prefer a name query. kind filters installed " +
        "(user apps) vs system (Windows inbox components). Results are " +
        "candidates — pick one and call computer_launch with its arg " +
        "immediately; do not narrate intermediate steps.")]
    public CallToolResult Apps(
        [Description("case-insensitive name query — the app's name or a distinctive substring (\"unreal\", \"epic\"). Always pass this first")] string? name = null,
        [Description("installed|system|all — default all")] string? kind = null,
        [Description("list the whole catalog (hundreds of lines) — only when a name search is genuinely not possible")] bool? all = null,
        [Description("max entries listed — default 100 (slim) / 250 (full)")] int? limit = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        if (kind != null && kind is not ("installed" or "system" or "all"))
            return Error(OutcomeKind.Malformed,
                $"kind must be installed|system|all, got '{kind}'");
        var apps = _s.Rt.Parts.Apps.ListApps();
        if (kind is "installed" or "system")
            apps = apps.Where(a => a.Kind == kind).ToList();
        var pool = apps;

        // Search-first contract: no name → no dump. A bare call would
        // spend context on hundreds of entries the model doesn't need;
        // counts + the query hint cost one line instead.
        if (string.IsNullOrWhiteSpace(name) && all != true)
        {
            var inst = pool.Count(a => a.Kind == "installed");
            var sys = pool.Count(a => a.Kind == "system");
            return Text($"{apps.Count} app(s) registered " +
                $"({inst} installed, {sys} system) — " +
                "search first: computer_apps{name:\"<substring>\"}, or " +
                "open directly in one call: computer_launch{search:" +
                "\"<name>\"}. Full listing only via all:true.");
        }

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(name))
        {
            var norm = NormStr(name);
            // Match name AND the launch identifier — package PFNs carry
            // canonical English ids, so "calculator" finds the app whose
            // localized display name is "Hesap Makinesi"
            apps = apps.Where(a => NormStr(a.Name).Contains(norm) ||
                NormStr(a.Launch).Contains(norm)).ToList();
            if (apps.Count == 0)
            {
                // unregistered executables — engines/dev tools live under
                // install roots with no Start Menu/App Path entry at all
                var exeHits = _s.Rt.Parts.Apps.FindExecutables(name);
                // fuzzy fallback over the catalog — never leave the model
                // at a dead end
                var near = pool.Select(a => (a, s: Sim(NormStr(a.Name), norm)))
                    .Where(x => x.s >= 0.30)
                    .OrderByDescending(x => x.s).Take(5).ToList();
                sb.AppendLine($"0 registered app(s) matching \"{name}\"");
                foreach (var x in exeHits)
                    sb.AppendLine($"  {Path.GetFileName(x)}  " +
                        $"[Executable/installed]  path:\"{x}\"");
                if (exeHits.Count > 0)
                    sb.AppendLine("  → call computer_launch{path:\"…\"} or " +
                        "{search:\"…\"} now — do not narrate");
                foreach (var (a, _) in near)
                    sb.AppendLine($"  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}" +
                        "  (closest)");
                if (exeHits.Count == 0 && near.Count == 0)
                    sb.AppendLine("  no close matches — try a shorter " +
                        "substring or computer_apps{kind:\"installed\"}");
                else if (exeHits.Count == 0)
                    sb.AppendLine("  → retry with a closer name or its " +
                        "launch arg — do not narrate");
                return Text(sb.ToString());
            }
        }
        var slim = Slim(detail);
        var cap = limit ?? (slim ? 100 : 250);
        sb.AppendLine($"{apps.Count} app(s)" +
            (kind is "installed" or "system" ? $" kind={kind}" : "") +
            (string.IsNullOrWhiteSpace(name)
                ? " — open any with computer_launch{search:\"…\"}:"
                : $" matching \"{name}\" — open with computer_launch{{search:\"…\"}} or the launch arg:"));
        foreach (var a in apps.Take(cap))
            sb.AppendLine($"  {a.Name}  [{a.Method}/{a.Kind}]  {a.Launch}");
        if (apps.Count > cap)
            sb.AppendLine($"  …{apps.Count - cap} more — pass name to filter or limit to widen");
        return Text(sb.ToString());
    }

    private static string NormStr(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray())
            .ToLowerInvariant();

    /// <summary>Character-bigram Dice similarity — tolerates typos, case
    /// and spacing differences ("unrel" ≈ "unreal", "epic games" ≈
    /// "EpicGamesLauncher" partially). 0..1.</summary>
    private static double Sim(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2) return a == b ? 1 : 0;
        var ba = new HashSet<int>();
        for (var i = 0; i + 1 < a.Length; i++) ba.Add((a[i] << 16) | a[i + 1]);
        var bb = new HashSet<int>();
        for (var i = 0; i + 1 < b.Length; i++) bb.Add((b[i] << 16) | b[i + 1]);
        var inter = ba.Count(x => bb.Contains(x));
        return 2.0 * inter / (ba.Count + bb.Count);
    }

    /// <summary>Fuzzy catalog suggestions for a failed friendly-name
    /// launch — up to <paramref name="n"/> closest app names, so a
    /// TargetNotFound error still hands the model a usable next step.</summary>
    private List<AppInfo> ClosestApps(string query, int n)
    {
        var norm = NormStr(query);
        return _s.Rt.Parts.Apps.ListApps()
            .Select(a => (a, s: Sim(NormStr(a.Name), norm)))
            .Where(x => x.s >= 0.30)
            .OrderByDescending(x => x.s).Take(n)
            .Select(x => x.a).ToList();
    }

    [McpServerTool(Name = "computer_find"), Description(
        "DISCOVERY tool — locate elements when you don't have an elementId " +
        "yet. NOT needed when you already know the target: pass a semantic " +
        "target straight to the action tool instead. Returns id, role, name, " +
        "value, state, bounds, actions, hwnd, pid, enabled, labelledBy and " +
        "(for file dialogs) dialogRole — enough to act without inspect. " +
        "name is a case-insensitive SUBSTRING — prefer exact names plus a " +
        "role to avoid label/control collisions. Prefer process over window " +
        "(titles are localized). Example: {process:\"notepad\", " +
        "role:\"document\"} → the Notepad text area.")]
    public CallToolResult Find(
        [Description("role filter, e.g. button/edit/checkbox/listitem")] string? role = null,
        [Description("name contains (case-insensitive)")] string? name = null,
        [Description("AutomationId")] string? automationId = null,
        [Description("window handle (hex or decimal)")] string? hwnd = null,
        [Description("process name")] string? process = null,
        [Description("enabled only")] bool? enabled = null,
        [Description("reject when name contains this (case-insensitive)")] string? nameNotContains = null,
        [Description("exact value-property match")] string? value = null,
        [Description("value-property substring, e.g. \"/artist/\" in a hyperlink URL")] string? valueContains = null,
        [Description("exact className-property match")] string? className = null,
        [Description("elementId or hwnd — search only that element's subtree/window")] string? within = null,
        [Description("max elements to list — default 20 (slim) / 60 (full)")] int? limit = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("alias of name — free-text query, e.g. \"Open Project\"")] string? query = null,
        [Description("object form — same filters nested ({process,name,role,hwnd,…}); merged with the flat args")] TargetSpec? target = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        using var trace = PerfTrace.Begin("tool", "computer_find");
        ct.ThrowIfCancellationRequested();
        using var cancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;
        // models generalize the action-tool target:{} shape onto find —
        // accept it: merge nested + query alias into the flat filters
        name ??= query ?? target?.Name ?? target?.NameContains;
        role ??= target?.Role;
        process ??= target?.Process;
        hwnd ??= target?.Hwnd ?? target?.Window;
        automationId ??= target?.AutomationId;
        within ??= target?.Within ?? target?.ElementId;
        nameNotContains ??= target?.NameNotContains;
        value ??= target?.Value;
        valueContains ??= target?.ValueContains;
        className ??= target?.ClassName;
        List<UiElement> els; CallToolResult? err;
        using (PerfTrace.Stage("find"))
            els = FindElements(role, name, automationId, hwnd, process,
                out err, within: within, valueContains: valueContains,
                valueEquals: value, className: className,
                enabled: enabled == true ? true : null);
        if (err != null) return err;
        els = ApplyPropFilters(els, new TargetSpec(
            NameNotContains: nameNotContains, Value: value,
            ValueContains: valueContains, ClassName: className));
        if (enabled == true)
            els = els.Where(e => e.Props.TryGetValue("enabled", out var en)
                && en is true).ToList();
        // canonical file-dialog affordances — annotate per owning window.
        // DialogTags only tags file dialogs, and real file dialogs are
        // #32770 (or a small owned DirectUI window) — skip the subtree
        // snapshot entirely for ordinary windows.
        var tags = new Dictionary<string, string>();
        using (PerfTrace.Stage("dialogTags"))
            foreach (var owner in els.Select(e =>
                    e.Handle.Recipe.Hwnd ?? e.Hwnd).OfType<long>().Distinct())
            {
                if (!LooksLikeDialog(owner)) continue;
                foreach (var kv in DialogTags(Snapshot(owner)))
                    tags[kv.Key] = kv.Value;
            }
        var sb = new StringBuilder();
        var slim = Slim(detail);
        var cap = limit ?? (slim ? 20 : 60);
        if (els.Count == 0)
        {
            var targetSpec = new TargetSpec(
                Role: role,
                Name: name,
                AutomationId: automationId,
                Hwnd: hwnd,
                Process: process,
                Within: within,
                Value: value,
                ValueContains: valueContains,
                ClassName: className,
                NameNotContains: nameNotContains);
            var diag = DiagnoseNotFound(targetSpec, nativeCount: 0, relErr: null);
            sb.AppendLine("found 0 element(s):");
            sb.AppendLine($"  diagnosis: {diag.Summary}");
            if (diag.SuggestedAction != null)
                sb.AppendLine($"  actionableFix: {diag.SuggestedAction}");
            if (diag.CloseMatches is { Count: > 0 })
                sb.AppendLine($"  closeMatches: [{string.Join(", ", diag.CloseMatches.Select(m => $"\"{m}\""))}]");
            return Text(sb.ToString());
        }
        sb.AppendLine($"found {els.Count} element(s)" +
            (els.Count > cap ? $" — showing {cap}:" : ":"));
        foreach (var e in els.Take(cap))
            sb.AppendLine(slim
                ? "  " + SlimEl(e, tags)
                : $"  [{e.Id}] {e.Role} \"{e.Name}\" " +
                    (e.Props.TryGetValue("value", out var v) && v is { } vv
                        ? $"value=\"{TruncEdges(vv.ToString(), 160)}\" " : "") +
                    (e.Props.TryGetValue("state", out var st) && st != null
                        ? $"state={st} " : "") +
                    (e.Props.TryGetValue("labelledBy", out var lb) && lb != null
                        ? $"labelledBy=\"{lb}\" " : "") +
                    $"hwnd=0x{e.Hwnd ?? 0:X} pid={e.Pid} " +
                    $"bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height}) " +
                    $"actions=[{string.Join(",", e.Actions)}] " +
                    $"enabled={Prop(e, "enabled") ?? "?"}" +
                    (tags.TryGetValue(e.Id, out var tag) ? $" dialogRole={tag}" : ""));
        if (els.Count > cap)
            sb.AppendLine($"  …{els.Count - cap} more — refine target or pass limit");
        return Text(sb.ToString());
    }

    /// <summary>Shared deterministic find — used by computer_find and by
    /// semantic-target resolution inside action tools. `within` narrows the
    /// UIA search itself to the container's subtree (elementId → that
    /// element's subtree, hwnd → that window); pushable property selectors
    /// (value/valueContains/className/enabled) go into the native UIA
    /// condition so providers never enumerate non-matching subtrees.</summary>
    private List<UiElement> FindElements(string? role, string? name,
        string? automationId, string? hwnd, string? process,
        out CallToolResult? error, string? within = null,
        string? valueContains = null, string? valueEquals = null,
        string? className = null, bool? enabled = null,
        bool firstOnly = false, bool includeOffscreen = false)
    {
        error = null;
        Core.Role? r = role != null && Enum.TryParse<Core.Role>(role, true,
            out var rr) ? rr : null;
        if (role != null && r == null)
        {
            error = Error(OutcomeKind.Malformed, $"unknown role '{role}'");
            return [];
        }
        long? scopeHwnd = null;
        string? scopeId = null;
        if (within is { } wref)
        {
            if (ParseHwnd(wref) is { } wh)
                scopeHwnd = wh;
            else
            {
                var container = _s.Rt.Parts.Registry.EnsureAlive(wref);
                if (container == null)
                {
                    error = Error(OutcomeKind.TargetNotFound,
                        $"within reference '{wref}' not found");
                    return [];
                }
                scopeId = container.Handle.BackendRef;
                scopeHwnd = container.Handle.Recipe.Hwnd ?? container.Hwnd;
            }
        }
        int? pid = null;
        if (process != null)
        {
            var w = _s.Rt.Windows().FirstOrDefault(w =>
                (w.ProcessName ?? "").Contains(process, StringComparison.OrdinalIgnoreCase));
            if (w == null)
            {
                error = Error(OutcomeKind.TargetNotFound,
                    $"no window for process '{process}'");
                return [];
            }
            pid = w.Pid;
        }
        var spec = new FindSpec(Hwnd: scopeHwnd ?? ParseHwnd(hwnd), Pid: pid,
            Role: r, Name: name, AutomationId: automationId,
            ScopeElementId: scopeId, ValueContains: valueContains,
            ValueEquals: valueEquals, ClassName: className, Enabled: enabled,
            FirstOnly: firstOnly, IncludeOffscreen: includeOffscreen);
        // short-lived result cache: an identical query on an unchanged UI
        // reuses the list — element ids stay valid because they come from
        // the same registry. Invalidated by any semantic event attributable
        // to the scope (matching hwnd/pid, or unattributable) and bounded by
        // a 400ms TTL for providers that under-report.
        var key = string.Join('|', scopeHwnd, hwnd, pid, r, name,
            automationId, scopeId, valueContains, valueEquals, className,
            enabled, includeOffscreen ? "1" : "0");
        var buf = _s.Rt.Parts.EventBuffer;
        var now = DateTimeOffset.UtcNow;
        bool Fresh((List<UiElement> Els, DateTimeOffset At, HashSet<long> Hwnds,
            HashSet<int> Pids, long Ver) hit) =>
            hit.Ver == _s.Rt.MutationVersion &&   // a performed action
            (now - hit.At).TotalMilliseconds < 400 &&   // may have changed the tree
            !buf.Snapshot(100).Any(e => e.At >= hit.At &&
                (hit.Hwnds.Contains(e.Hwnd ?? 0) ||
                 (e.Pid is { } ep && hit.Pids.Contains(ep)) ||
                 (e.Hwnd is null or 0 && e.Pid == null)));
        // a full-result entry answers a firstOnly query too (take [0]);
        // a firstOnly entry must never serve a full query
        if ((_findCache.TryGetValue(key, out var hit) && Fresh(hit)) ||
            (firstOnly && _findCache.TryGetValue(key + "|1", out hit) &&
             Fresh(hit)))
        {
            PerfTrace.Count("find.cacheHit");
            return hit.Els;
        }
        var result = _s.Rt.Find(spec).ToList();
        var hwnds = new HashSet<long>(result.Select(e => e.Hwnd ?? 0));
        if (spec.Hwnd is { } sh) hwnds.Add(sh);
        var pids = new HashSet<int>(result.Select(e => e.Pid ?? 0));
        if (spec.Pid is { } sp) pids.Add(sp);
        _findCache[firstOnly ? key + "|1" : key] = (result,
            DateTimeOffset.UtcNow, hwnds, pids, _s.Rt.MutationVersion);
        if (_findCache.Count > 64) _findCache.Clear();
        PerfTrace.Count("find.cacheMiss");
        return result;
    }

    /// <summary>Per-session find-result cache — invalidated by scope-matched
    /// UI events and bounded by a 400ms TTL (see FindElements).</summary>
    private readonly Dictionary<string, (List<UiElement> Els,
        DateTimeOffset At, HashSet<long> Hwnds, HashSet<int> Pids,
        long Ver)> _findCache = new();

    [McpServerTool(Name = "computer_inspect"), Description(
        "Deep view of one window's element tree (with dialogRole " +
        "annotations for file dialogs) or one element's full property set. " +
        "Pass relational=true to group list/table rows with their child actions and labels.")]
    public CallToolResult Inspect(
        [Description("window handle to inspect (hex or decimal); default = active")] string? hwnd = null,
        [Description("elementId for single-element detail")] string? elementId = null,
        [Description("max elements to list — default 40 (slim) / 80 (full)")] int? maxElements = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("group rows/items relationally with their child actions and labels — ideal for lists/tables")] bool relational = false,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        using var trace = PerfTrace.Begin("tool", "computer_inspect");
        ct.ThrowIfCancellationRequested();
        using var cancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;
        if (elementId != null)
        {
            var parts = _s.Rt.Parts;
            var registered = parts.Registry.Get(elementId);
            if (registered == null)
                return Error(OutcomeKind.TargetNotFound, $"unknown element '{elementId}'");
            var backend = parts.Backends.FirstOrDefault(b => b.Id == registered.Handle.Backend);
            UiElement? el;
            using (PerfTrace.Stage("reResolve"))
                el = backend?.ReResolve(registered.Handle);
            if (el == null)
                return Error(OutcomeKind.Stale, $"element '{elementId}' could not be re-resolved");
            el = el with { Id = elementId };
            parts.Registry.Register([el]);
            var sb = new StringBuilder();
            sb.AppendLine($"[{el.Id}] {el.Role} \"{el.Name}\" stale={el.IsStale}");
            sb.AppendLine($"bounds=({el.Bounds.X},{el.Bounds.Y} {el.Bounds.Width}x{el.Bounds.Height}) hwnd=0x{el.Hwnd ?? 0:X} pid={el.Pid}");
            sb.AppendLine($"actions=[{string.Join(",", el.Actions)}]");
            foreach (var kv in el.Props) sb.AppendLine($"  {kv.Key} = {kv.Value}");
            return Text(sb.ToString());
        }
        var h = ParseHwnd(hwnd) ?? _s.Rt.ForegroundWindow()?.Hwnd;
        if (h == null) return Error(OutcomeKind.Malformed, "no hwnd and no active window");
        IReadOnlyList<UiElement> els;
        using (PerfTrace.Stage("inspect.uia"))
            els = _s.Rt.Inspect(h.Value);
        var win = _s.Rt.Window(h.Value);
        var app = win?.ProcessName ?? "";
        var screen = _s.Memory.RecordObservation(h.Value, app, win?.Title ?? "", els);
        var tags = DialogTags(els);
        var slim = Slim(detail);
        var cap = maxElements ?? (slim ? 40 : 80);

        if (relational)
        {
            var rows = RelationalInspector.ExtractRows(els);
            if (rows.Count > 0)
            {
                var rb = new StringBuilder();
                rb.AppendLine($"window 0x{h:X} [screen: {screen}]: {rows.Count} relational rows:");
                foreach (var row in rows.Take(cap))
                {
                    rb.Append($"  [{row.ElementId}] {row.Role}: ");
                    if (!string.IsNullOrWhiteSpace(row.Title)) rb.Append($"\"{row.Title}\" ");
                    if (row.Details.Count > 0) rb.Append($"({string.Join(" | ", row.Details)}) ");
                    if (row.Actions.Count > 0)
                    {
                        var actStrs = row.Actions.Select(a => $"{a.Action}({a.ElementId}: \"{a.Name}\")");
                        rb.Append($"actions=[{string.Join(", ", actStrs)}]");
                    }
                    rb.AppendLine();
                }
                if (rows.Count > cap)
                    rb.AppendLine($"  …{rows.Count - cap} more rows — pass maxElements to see them");
                return Text(rb.ToString().TrimEnd());
            }
        }
        var b = new StringBuilder();
        b.AppendLine($"window 0x{h:X} [screen: {screen}]: {els.Count} elements " +
            $"(showing {Math.Min(els.Count, cap)}):");
        foreach (var e in els.Take(cap))
            b.AppendLine(slim
                ? "  " + SlimEl(e, tags)
                : $"  [{e.Id}] {e.Role} \"{e.Name}\" " +
                    (e.Props.TryGetValue("value", out var v) && v != null
                        ? $"value=\"{v}\" " : "") +
                    (e.Props.TryGetValue("labelledBy", out var lb) && lb != null
                        ? $"labelledBy=\"{lb}\" " : "") +
                    $"actions=[{string.Join(",", e.Actions)}]" +
                    (tags.TryGetValue(e.Id, out var tag) ? $" dialogRole={tag}" : ""));
        if (els.Count > cap)
            b.AppendLine($"  …{els.Count - cap} more — pass maxElements to see them");
        return Text(b.ToString());
    }

    // ------------------------------------------------------------ actions
    // Mutating tools self-resolve: pass elementId (from find/observe) OR a
    // semantic `target` {window,process,role,name,automationId} — the runtime
    // does find → re-resolve → foreground → act → verify → post-state in one
    // call. No computer_observe needed when the target is already known.

    [McpServerTool(Name = "computer_click"), Description(
        "Click an element. Prefer computer_invoke for buttons/menu items " +
        "(no focus/geometry needed). Three targeting styles: elementId, " +
        "semantic target {window,process,role,name,automationId,labelledBy," +
        "nearText,within,ancestor}, or image point x+y with frameId+" +
        "observationId (stale frames rejected). button:left|right|double. " +
        "Do NOT click coordinates when an elementId exists.")]
    public Task<CallToolResult> Click(
        [Description("elementId from observe/find")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("frameId the point was derived from")] long? frameId = null,
        [Description("observationId the point was derived from")] long? observationId = null,
        [Description("image-space x")] int? x = null,
        [Description("image-space y")] int? y = null,
        [Description("left|right|double")] string? button = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "invoke");
        if (err != null) return Task.FromResult(err);
        return Act(new AgentAction(button?.Equals("right", StringComparison.OrdinalIgnoreCase) == true
                ? AgentActionKind.RightClick
                : button?.Equals("double", StringComparison.OrdinalIgnoreCase) == true
                    ? AgentActionKind.DoubleClick
                    : AgentActionKind.Click,
            ElementId: el?.Id, Point: MakePoint(frameId, observationId, x, y)), ct, observe);
    }

    [McpServerTool(Name = "computer_invoke"), Description(
        "Trigger an element's default action via UIA InvokePattern — the " +
        "preferred way to press buttons and menu items: works without " +
        "foreground focus or pixel hit-testing. Accepts elementId or " +
        "semantic target. Example: {process:\"notepad\", role:\"menuitem\", " +
        "name:\"Dosya\"} opens the File menu.")]
    public Task<CallToolResult> Invoke(
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "invoke");
        if (err != null) return Task.FromResult(err);
        if (el == null) return Task.FromResult(
            Error(OutcomeKind.Malformed, "elementId or target required"));
        return Act(new AgentAction(AgentActionKind.Invoke, ElementId: el.Id), ct, observe);
    }

    [McpServerTool(Name = "computer_set_value"), Description(
        "Atomically set an element's whole value via UIA ValuePattern — " +
        "verified by readback. Best for text fields and combo boxes; use " +
        "computer_type for keystroke-level editing or append/insert at a " +
        "caret position. The resolver prefers editable controls — a Text " +
        "label with the same name never wins unless you ask for role:Text. " +
        "Example: {target:{process:\"notepad\", role:\"edit\", " +
        "labelledBy:\"Dosya adı:\"}, value:\"x.txt\"}")]
    public Task<CallToolResult> SetValue(
        [Description("value to set")] string value,
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "edit");
        if (err != null) return Task.FromResult(err);
        if (el == null) return Task.FromResult(
            Error(OutcomeKind.Malformed, "elementId or target required"));
        return Act(new AgentAction(AgentActionKind.SetValue, ElementId: el.Id,
            Text: value), ct, observe);
    }

    [McpServerTool(Name = "computer_type"), Description(
        "High-level text editing in ONE call — focus, foreground and caret " +
        "navigation are internal. mode=replace|append|insert, " +
        "position=current|start|end, submit=Enter afterwards. Uses " +
        "ValuePattern when the control supports it (atomic + verified), " +
        "else real keystrokes. To append at document end: " +
        "{target:{process:\"notepad\",role:\"document\"},text:\"...\", " +
        "mode:\"append\",position:\"end\"}. Do NOT pre-click/focus/Ctrl+End " +
        "with separate calls — this tool does it.")]
    public Task<CallToolResult> Type(
        [Description("text to type")] string text,
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("replace|append|insert")] string? mode = null,
        [Description("current|start|end")] string? position = null,
        [Description("press Enter after typing")] bool submit = false,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "edit");
        if (err != null) return Task.FromResult(err);
        var modeN = (mode ?? "insert").ToLowerInvariant();
        var posN = (position ?? (modeN == "append" ? "end" : "current"))
            .ToLowerInvariant();
        if (modeN is not ("replace" or "append" or "insert") ||
            posN is not ("current" or "start" or "end"))
            return Task.FromResult(Error(OutcomeKind.Malformed,
                "mode must be replace|append|insert; position current|start|end"));

        var steps = BuildTypeSteps(el, text, modeN, posN, submit);
        return ActChain(steps, el?.Id, ct, observe);
    }

    /// <summary>Expand a high-level type request into primitive actions:
    /// click-to-focus + caret navigation + optional select-all + type +
    /// optional submit. UIA ValuePattern is preferred when the element
    /// supports it — atomic and readback-verified.</summary>
    private static List<AgentAction> BuildTypeSteps(UiElement? el, string text,
        string modeN, string posN, bool submit)
    {
        var steps = new List<AgentAction>();
        var canSet = el?.Actions.Contains("setvalue") == true;
        var cur = Prop(el, "value")?.ToString() ?? "";
        if (canSet && (modeN == "replace" ||
                (modeN == "append" && posN is "end" or "start")))
        {
            var combined = modeN == "replace" ? text
                : posN == "start" ? text + cur : cur + text;
            steps.Add(new AgentAction(AgentActionKind.SetValue,
                ElementId: el!.Id, Text: combined));
        }
        else if (el != null)
        {
            // navigate first: click focuses + positions caret; ctrl+end/home
            // moves to document edges — all internal to this one call
            if (posN != "current")
                steps.Add(new AgentAction(AgentActionKind.Click, ElementId: el.Id));
            if (posN == "end")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "End",
                    Modifiers: new[] { "ctrl" }));
            else if (posN == "start")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "Home",
                    Modifiers: new[] { "ctrl" }));
            if (modeN == "replace")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "A",
                    Modifiers: new[] { "ctrl" }));
            steps.Add(new AgentAction(AgentActionKind.Type,
                ElementId: el.Id, Text: text));
        }
        else
        {
            if (posN == "end")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "End",
                    Modifiers: new[] { "ctrl" }));
            else if (posN == "start")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "Home",
                    Modifiers: new[] { "ctrl" }));
            else if (modeN == "replace")
                steps.Add(new AgentAction(AgentActionKind.Hotkey, Key: "A",
                    Modifiers: new[] { "ctrl" }));
            steps.Add(new AgentAction(AgentActionKind.Type, Text: text));
        }
        if (submit) steps.Add(new AgentAction(AgentActionKind.Key, Key: "enter"));
        return steps;
    }

    [McpServerTool(Name = "computer_run"), Description(
        "Run a declarative UI plan as ONE call — the preferred tool for deterministic multi-step work. " +
        "Steps: launch{target:{process}|text,as,ms}, find{target,as,select,orderBy,index}, " +
        "assert{elementId,contains|notContains|exact|state|enabled|value}, checkpoint{note}, " +
        "focus/focus_window{elementId|target|hwnd}, click|invoke|toggle|select|hover{elementId|target}, " +
        "set_value{elementId|target,text|value}, type{elementId|target,text,mode,position,submit}, " +
        "key{key,count}, hotkey{key,modifiers OR keys:\"ctrl+s\"}, scroll{delta,target}, " +
        "scroll_into_view{elementId|target}, drag{target,toX,toY}, wait{ms}, " +
        "wait_for{query,ms,target-scope:{within|process|window}}, wait_for_change/wait_for_stable{ms}, " +
        "scan/for_each{target,asVar,steps,where,collect,limit,maxPages,stopOn}. " +
        "Dynamic variables ($var, {{var}}, $item.name, $item.value, $item.role, $item.id) interpolate automatically " +
        "inside target names, text, values, and element IDs. " +
        "Targets resolve LAZILY at each step, so later steps can act on UI created by earlier ones. " +
        "find{as:\"x\"} binds an element for later steps via elementId:\"$x\" (also inside target.within). " +
        "scan iterates server-side over matched items without LLM roundtrips, binding $asVar and properties. " +
        "When several candidates match, selection stays server-side: select:\"first|last|nth\" + index + " +
        "orderBy:\"visual\" | \"tree\" | \"score\". " +
        "ifExists/ifNotExists/ifEnabled/ifValue skip steps; retry/timeout bound them. " +
        "STRICT: unknown or wrong-action fields are Malformed, never ignored. " +
        "First failure pauses with step/error/observationDelta/availableElements — resume with the same runId. " +
        "checkpoint pauses deliberately for model reasoning. An unexpected dialog pauses with UnexpectedModalOpened.")]
    public async Task<CallToolResult> RunPlan(
        [Description("ordered plan steps")] RunStep[] steps,
        [Description("resume an existing run — keeps its element bindings")] string? runId = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        var sw = Stopwatch.StartNew();
        try
        {
            return await RunPlanCore(steps, runId, sw, ct, detail);
        }
        catch (OperationCanceledException)
        {
            return _s.Control.State == ComputerControlState.EmergencyStopped
                ? Error(OutcomeKind.EmergencyStopped,
                    StoppedDetail, sw)
                : Error(OutcomeKind.Cancelled, "request cancelled", sw);
        }
        catch (Exception e)
        {
            return Error(OutcomeKind.Failed, e.Message, sw);
        }
    }

    [McpServerTool(Name = "computer_save_recipe"), Description(
        "Save a proven sequence of RunSteps as a reusable semantic recipe/macro. " +
        "Recipes can have parameterized inputs (e.g. {{songTitle}}, {{targetPlaylist}}) " +
        "and allow future executions to run locally without re-discovering the entire UI.")]
    public Task<CallToolResult> SaveRecipe(
        [Description("unique recipe identifier name (e.g. 'spotify_add_to_playlist')")] string name,
        [Description("ordered recipe steps with optional {{parameter}} placeholders")] RunStep[] steps,
        [Description("target application name (e.g. 'Spotify')")] string? app = null,
        [Description("human/model readable explanation of what this recipe accomplishes")] string? description = null,
        [Description("parameter declarations expected by this recipe")] RecipeParameter[]? parameters = null,
        [Description("precondition hints checked before execution")] string[]? preconditions = null,
        [Description("postcondition hints verified upon completion")] string[]? postconditions = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Task.FromResult(Error(OutcomeKind.Malformed, "recipe name is required"));
        if (steps is not { Length: > 0 })
            return Task.FromResult(Error(OutcomeKind.Malformed, "recipe requires at least 1 step"));

        for (var i = 0; i < steps.Length; i++)
        {
            if (ValidateStep(steps[i]) is { } verr)
                return Task.FromResult(Error(OutcomeKind.Malformed, $"recipe step [{i}]: {verr}"));
        }

        var stepsJson = JsonSerializer.Serialize(steps, new JsonSerializerOptions { WriteIndented = true });
        var def = new TaskRecipeDefinition(
            Name: name,
            Description: description,
            App: app,
            Parameters: parameters ?? Array.Empty<RecipeParameter>(),
            Preconditions: preconditions ?? Array.Empty<string>(),
            StepsJson: stepsJson,
            Postconditions: postconditions ?? Array.Empty<string>(),
            CreatedAt: DateTimeOffset.UtcNow);

        _s.RecipeStore.Save(def);
        return Task.FromResult(Text($"✓ Recipe '{name}' saved successfully with {steps.Length} steps."));
    }

    [McpServerTool(Name = "computer_run_recipe"), Description(
        "Execute a previously saved semantic task recipe. Replaces {{parameter}} " +
        "placeholders with provided parameter values and runs the plan locally. " +
        "If a step fails or UI changes, pauses with diagnostic context for recovery.")]
    public async Task<CallToolResult> RunRecipe(
        [Description("name of the saved recipe to execute")] string name,
        [Description("key-value parameters for placeholder substitution {{param}}")] Dictionary<string, string>? parameters = null,
        [Description("optional resume runId")] string? runId = null,
        [Description("output verbosity: slim|full")] string? detail = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Error(OutcomeKind.Malformed, "recipe name is required");

        var recipe = _s.RecipeStore.Get(name);
        if (recipe == null)
        {
            var known = _s.RecipeStore.List().Select(r => r.Name);
            return Error(OutcomeKind.TargetNotFound,
                $"recipe '{name}' not found. Available recipes: [{string.Join(", ", known)}]");
        }

        var json = recipe.StepsJson;
        if (parameters != null)
        {
            foreach (var (k, v) in parameters)
            {
                json = json.Replace("{{" + k + "}}", v)
                           .Replace("{{" + k.ToLowerInvariant() + "}}", v);
            }
        }

        RunStep[] steps;
        try
        {
            steps = JsonSerializer.Deserialize<RunStep[]>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("null deserialization");
        }
        catch (Exception ex)
        {
            return Error(OutcomeKind.Malformed, $"failed to deserialize substituted recipe steps: {ex.Message}");
        }

        var sw = Stopwatch.StartNew();
        var res = await RunPlanCore(steps, runId, sw, ct, detail);
        var success = res.IsError != true;
        _s.RecipeStore.RecordRun(name, success);
        return res;
    }

    [McpServerTool(Name = "computer_list_recipes"), Description(
        "List all available semantic task recipes with their parameters, apps, and success history.")]
    public Task<CallToolResult> ListRecipes()
    {
        var recipes = _s.RecipeStore.List();
        if (recipes.Count == 0)
            return Task.FromResult(Text("No task recipes currently saved. Save new workflows via computer_save_recipe."));

        var sb = new StringBuilder();
        sb.AppendLine($"Found {recipes.Count} saved task recipe(s):");
        foreach (var r in recipes)
        {
            var paramStr = r.Parameters.Count > 0
                ? string.Join(", ", r.Parameters.Select(p => p.Name))
                : "none";
            var successRate = (r.SuccessCount + r.FailureCount) > 0
                ? $"{(double)r.SuccessCount / (r.SuccessCount + r.FailureCount) * 100:F0}%"
                : "untested";

            sb.AppendLine($"• {r.Name} [App: {r.App ?? "Generic"}]");
            if (!string.IsNullOrWhiteSpace(r.Description)) sb.AppendLine($"  Desc: {r.Description}");
            sb.AppendLine($"  Params: [{paramStr}] | Success Rate: {successRate} ({r.SuccessCount} pass / {r.FailureCount} fail)");
            if (r.LastRunAt != null) sb.AppendLine($"  Last Run: {r.LastRunAt:yyyy-MM-dd HH:mm}");
        }
        return Task.FromResult(Text(sb.ToString().TrimEnd()));
    }

    [McpServerTool(Name = "computer_adapter"), Description(
        "Direct specialist application adapter execution (for browser work prefer the dedicated browser_* tools — " +
        "browser_browse, browser_click, browser_type, browser_evaluate, browser_content, browser_tabs — " +
        "the one-call self-healing path). Chrome DevTools Protocol / CDP for DOM, script, tabs; media controls for Spotify/VLC; " +
        "specialist app APIs, CLI tools). Bypasses coordinate clicking and executes semantic commands in <5ms.")]
    public async Task<CallToolResult> AdapterExecute(
        [Description("action to execute, e.g. navigate, click, type, evaluate, get_content, list_tabs, new_tab, close_tab, play, pause, next, volume_up, read_state")] string action,
        [Description("optional preferred adapter ID: chrome_devtools | media | testapp")] string? adapter = null,
        [Description("optional target application specification")] TargetSpec? target = null,
        [Description("optional arguments payload for the adapter")] Dictionary<string, object?>? args = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var processName = target?.Process ?? _s?.Rt?.ForegroundWindow()?.ProcessName;
        var hwnd = target?.Hwnd != null ? ParseHwnd(target.Hwnd) : _s?.Rt?.ForegroundWindow()?.Hwnd;

        TargetRef? targetRef = null;
        if (hwnd.HasValue) targetRef = TargetRef.Window(hwnd.Value);
        else if (!string.IsNullOrEmpty(target?.ElementId)) targetRef = TargetRef.Element(target.ElementId);

        var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
        (bool handled, Inbrisk.Core.AdapterResult? res) = await registry.TryExecuteAsync(
            action: action,
            target: targetRef,
            args: args,
            processName: processName,
            hwnd: hwnd,
            preferredAdapterId: adapter,
            ct: ct);

        if (!handled || res == null)
            return Error(OutcomeKind.Malformed, $"no adapter available to handle action '{action}' for process '{processName}'", sw);

        if (!res.Success)
            return Error(res.Error == ErrorCode.NotFound ? OutcomeKind.TargetNotFound : OutcomeKind.Failed, res.Detail ?? "adapter execution failed", sw);

        return Json(new
        {
            success = true,
            method = res.Method,
            detail = res.Detail,
            data = res.Data,
            durationMs = sw.ElapsedMilliseconds
        });
    }

    // ---------------- browser_* — first-class Chrome/CDP tools ----------------
    // One-call, self-healing paths over the chrome_devtools adapter: no
    // computer_launch needed — a dead CDP port auto-spawns a debug browser.

    /// <summary>Shared dispatch for the browser_* family — every call goes
    /// through the emergency-stop token and the chrome_devtools adapter.</summary>
    private async Task<CallToolResult> BrowserCall(
        string action, Dictionary<string, object?> args, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);

        var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
        (bool handled, Inbrisk.Core.AdapterResult? res) = await registry.TryExecuteAsync(
            action: action,
            target: null,
            args: args,
            processName: "chrome",
            hwnd: null,
            preferredAdapterId: "chrome_devtools",
            ct: linked.Token);

        if (!handled || res == null)
            return Error(OutcomeKind.Failed, "chrome_devtools adapter unavailable", sw);
        if (!res.Success)
            return Error(res.Error == ErrorCode.NotFound
                    ? OutcomeKind.TargetNotFound : OutcomeKind.Failed,
                res.Detail ?? $"browser {action} failed", sw);

        return Json(new
        {
            success = true,
            method = res.Method,
            detail = res.Detail,
            data = res.Data,
            durationMs = sw.ElapsedMilliseconds
        });
    }

    [McpServerTool(Name = "browser_browse"), Description(
        "ONE-CALL browser navigation — ALWAYS prefer this for \"open/navigate " +
        "to a page in Chrome\" requests instead of chaining computer_launch + " +
        "computer_adapter. Attaches to a debug-enabled Chromium browser on the " +
        "CDP port (default 9222); if none is reachable it auto-spawns one " +
        "with its own profile — no separate launch step, no setup. Bare hosts " +
        "like \"google.com\" get https:// automatically. Returns the live tab " +
        "(tabId/url/title) AND the page's text content — a \"go read X\" task " +
        "is ONE call, no browser_content follow-up needed (pass " +
        "includeContent via args to skip). Pass tabId to the other browser_* " +
        "tools to pin the same tab.")]
    public Task<CallToolResult> BrowserBrowse(
        [Description("URL to open — \"google.com\" becomes https://google.com")] string url,
        [Description("open in a new tab instead of navigating the active tab")] bool newTab = false,
        [Description("CDP port — default 9222")] int? port = null,
        [Description("pin a specific tab from browser_tabs")] string? tabId = null,
        CancellationToken ct = default)
    {        if (string.IsNullOrWhiteSpace(url))
            return Task.FromResult(Error(OutcomeKind.Malformed, "url is required"));
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "https://" + url.Trim();

        var args = new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["url"] = url,
        };
        if (tabId != null) args["tabId"] = tabId;
        return BrowserCall(newTab ? "new_tab" : "navigate", args, ct);
    }

    [McpServerTool(Name = "browser_click"), Description(
        "Click a DOM element by CSS selector in the debug browser (CDP, " +
        "sub-second, no coordinate guessing). Element is scrolled into view " +
        "first. Prefer this over computer_click for anything inside a web page.")]
    public Task<CallToolResult> BrowserClick(
        [Description("CSS selector, e.g. \"#search-btn\", \"button[name='q']\" — omit when using uid")] string? selector = null,
        [Description("element uid from browser_snapshot — the reliable path: no selector guessing")] int? uid = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("click", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["selector"] = selector ?? "",
            ["uid"] = uid,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_type"), Description(
        "Fill an input/textarea by CSS selector in the debug browser — sets " +
        "value and fires input+change events (CDP, no keystrokes). Prefer this " +
        "over computer_type for web forms.")]
    public Task<CallToolResult> BrowserType(
        [Description("CSS selector of the input/textarea")] string selector,
        [Description("text to fill in")] string text,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("type", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["selector"] = selector,
            ["text"] = text,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_evaluate"), Description(
        "Run JavaScript in the active page of the debug browser and return " +
        "the result (CDP Runtime.evaluate, awaitPromise + returnByValue). " +
        "The escape hatch for anything the other browser_* tools don't cover.")]
    public Task<CallToolResult> BrowserEvaluate(
        [Description("JavaScript expression to evaluate")] string expression,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("evaluate", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["expression"] = expression,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_content"), Description(
        "Get the page's text/HTML (optionally scoped to a CSS selector) from " +
        "the debug browser — fast DOM read over CDP, no screenshots or UIA.")]
    public Task<CallToolResult> BrowserContent(
        [Description("optional CSS selector to scope extraction")] string? selector = null,
        [Description("text (default) | html")] string? format = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall(format == "html" ? "get_html" : "get_text",
            new Dictionary<string, object?>
            {
                ["port"] = port ?? 9222,
                ["selector"] = selector,
                ["tabId"] = tabId,
            }, ct);

    [McpServerTool(Name = "browser_tabs"), Description(
        "List tabs/targets of the debug browser (self-heals like the other " +
        "browser_* tools). Returns tabId values usable in browser_browse, " +
        "browser_click, browser_type, browser_evaluate, browser_content.")]
    public Task<CallToolResult> BrowserTabs(
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("list_tabs", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
        }, ct);

    [McpServerTool(Name = "browser_screenshot"), Description(
        "Capture the active page of the debug browser as PNG (base64) via CDP.")]
    public Task<CallToolResult> BrowserScreenshot(
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("screenshot", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_capture"), Description(
        "ONE-CALL DevTools-style inspection of the debug browser page: reloads " +
        "(or navigates) while collecting ALL network requests (count, per-type " +
        "breakdown, largest transfers), console messages and exceptions, then " +
        "returns runtime vitals (title/href/DOM+link+script counts, resource " +
        "count, render-blocking count, TTFB/DCL/load, CLS, LCP when available). " +
        "Use this instead of hand-rolling CDP scripts for 'analyze network/" +
        "console/performance of this page' tasks.")]
    public Task<CallToolResult> BrowserCapture(
        [Description("optional URL to navigate to first; otherwise reloads the tab")] string? url = null,
        [Description("reload the page during capture — default true, ignored when url is set")] bool reload = true,
        [Description("bypass HTTP cache on reload — default true")] bool ignoreCache = true,
        [Description("how long to collect events in ms — default 6000, clamped 500..60000")] int? durationMs = null,
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("capture", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["url"] = url ?? "",
            ["reload"] = reload,
            ["ignoreCache"] = ignoreCache,
            ["durationMs"] = durationMs,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "browser_snapshot"), Description(
        "Take an accessibility-tree snapshot of the debug-browser page (like " +
        "chrome-devtools-mcp take_snapshot): returns elements as " +
        "uid=N role \"name\" — pass uid to browser_click(uid=..) for reliable " +
        "interaction without CSS-selector guessing. Snapshot instead of " +
        "screenshot whenever text/structure is enough. Uids reset on " +
        "navigation — re-snapshot after page changes.")]
    public Task<CallToolResult> BrowserSnapshot(
        [Description("pin a specific tab from browser_tabs/browser_browse")] string? tabId = null,
        [Description("CDP port — default 9222")] int? port = null,
        CancellationToken ct = default)
        => BrowserCall("snapshot", new Dictionary<string, object?>
        {
            ["port"] = port ?? 9222,
            ["tabId"] = tabId,
        }, ct);

    [McpServerTool(Name = "computer_list_adapters"), Description(
        "List all active specialist application adapters and their supported semantic actions.")]
    public Task<CallToolResult> ListAdapters()
    {
        var registry = _s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry();
        var caps = registry.GetCapabilities();
        var sb = new StringBuilder();
        sb.AppendLine($"Active Application Adapters ({caps.Count}):");
        foreach (var c in caps)
        {
            sb.AppendLine($"• [{c.AdapterId}] {c.DisplayName}");
            sb.AppendLine($"  Supported actions: {string.Join(", ", c.SupportedActions)}");
        }
        return Task.FromResult(Text(sb.ToString().TrimEnd()));
    }

    [McpServerTool(Name = "computer_cancel_task"), Description(
        "Cancel a specific running automation plan or task by runId/ownerId without interrupting " +
        "other concurrent tasks or shutting down the desktop runtime.")]
    public CallToolResult CancelTask(
        [Description("runId or ownerId of the task to cancel")] string taskId,
        [Description("optional cancellation reason")] string? reason = null)
    {
        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        var found = arbiter.CancelTask(taskId, reason);
        return Json(new
        {
            success = found,
            taskId = taskId,
            reason = reason ?? "cancelled by user or client request",
            detail = found ? $"Task '{taskId}' was located and cancelled." : $"Task '{taskId}' was not found among active tasks."
        });
    }

    [McpServerTool(Name = "computer_leases_status"), Description(
        "Query current desktop concurrency leases: active physical input owner, concurrent read-only observers, and active tasks.")]
    public CallToolResult LeasesStatus()
    {
        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        var leases = arbiter.GetActiveLeases();
        var owner = arbiter.GetExclusiveOwner();
        return Json(new
        {
            hasExclusiveOwner = owner != null,
            exclusiveOwner = owner,
            activeLeasesCount = leases.Count,
            leases = leases
        });
    }

    [McpServerTool(Name = "computer_pause_run"), Description(
        "Request a clean pause of an ongoing or planned run at the next safe step boundary. " +
        "Releases physical input leases so the human user can interact freely. " +
        "Updates the floating pill HUD with the pause reason and next planned step.")]
    public CallToolResult PauseRun(
        [Description("runId of the plan to pause (default: currently active or most recent run)")] string? runId = null,
        [Description("human-readable explanation of why the run is pausing (e.g. 'Waiting for user to solve CAPTCHA')")] string? reason = null)
    {
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "Human takeover requested" : reason;
        RunState? state = null;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            _s.Runs.TryGetValue(runId, out state);
        }
        else
        {
            state = _s.Runs.Values.LastOrDefault();
        }

        if (state == null)
            return Error(OutcomeKind.TargetNotFound, "No active or recorded plan run found to pause.");

        state.SafePointPauseRequested = true;
        state.IsPausedForHuman = true;
        state.PauseReason = effectiveReason;
        state.PausedAt = DateTimeOffset.UtcNow;

        var fg = _s.Rt.ForegroundWindow();
        state.PrePauseForegroundHwnd = fg?.Hwnd;
        state.PrePauseForegroundTitle = fg?.Title;

        var nextStepDesc = (state.PlannedSteps != null && state.PausedStepIndex >= 0 && state.PausedStepIndex < state.PlannedSteps.Length)
            ? DescribeStep(state.PlannedSteps[state.PausedStepIndex])
            : null;

        _s.Rt.PostHumanTakeover(state.PauseReason, nextStepDesc);

        // Cancel any in-flight physical lease / signal arbiter task for this run
        _s.Arbiter.CancelTask(state.RunId, state.PauseReason);

        return Json(new
        {
            success = true,
            runId = state.RunId,
            status = "PausedForHuman",
            reason = state.PauseReason,
            pausedStep = state.PausedStepIndex,
            nextStep = nextStepDesc,
            foregroundWindow = state.PrePauseForegroundTitle,
            instruction = "Human user now has control. When ready, call computer_resume_run to re-observe and continue."
        });
    }

    [McpServerTool(Name = "computer_resume_run"), Description(
        "Seamlessly resume a paused run after human takeover or step failure. " +
        "Automatically invalidates stale element handles, performs a fresh observation " +
        "to detect human changes (window switches, dialog closures, selections), " +
        "and resumes the remaining plan steps without restarting from scratch.")]
    public async Task<CallToolResult> ResumeRun(
        [Description("runId of the paused run to resume")] string runId,
        [Description("whether to skip the paused step (e.g. 'Bu adımı ben yaptım' / user completed it manually) — default false")] bool skipPausedStep = false,
        [Description("whether to perform a fresh observation to detect human changes and invalidate stale handles — default true")] bool reobserve = true,
        [Description("optional replacement or appended steps for the remainder of the plan")] RunStep[]? remainingSteps = null,
        [Description("optional updated variable bindings after human intervention")] Dictionary<string, string>? bindings = null,
        [Description("output verbosity: slim|full")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        if (!_s.Runs.TryGetValue(runId, out var state))
        {
            var known = _s.Runs.Keys;
            return Error(OutcomeKind.TargetNotFound,
                $"runId '{runId}' not found. Active/known runs: [{string.Join(", ", known)}]");
        }

        var sw = Stopwatch.StartNew();

        // 1. Invalidate stale element handles from pre-takeover state
        _s.InvalidateStaleHandles();
        state.LastElementId = null;

        // 2. Merge bindings if provided
        if (bindings != null)
        {
            foreach (var kv in bindings)
                state.Bindings[kv.Key] = kv.Value;
        }

        // 3. Re-observe desktop and detect human modifications
        if (reobserve)
        {
            var fg = _s.Rt.ForegroundWindow();
            var fgHwnd = fg?.Hwnd;
            var fgTitle = fg?.Title ?? "";
            var windowChanged = state.PrePauseForegroundHwnd.HasValue && state.PrePauseForegroundHwnd.Value != fgHwnd;

            if (fgHwnd.HasValue && (state.ScopeHwnd == null || windowChanged))
            {
                state.ScopeHwnd = fgHwnd.Value;
            }

            var freshObs = _s.Observe(state.ScopeHwnd,
                new ObservationBudget(MaxElements: 120),
                VisualAttachPolicy.Never);

            state.HumanChanges = new Dictionary<string, object?>
            {
                ["prePauseWindow"] = state.PrePauseForegroundTitle,
                ["currentWindow"] = fgTitle,
                ["windowChanged"] = windowChanged,
                ["observationId"] = freshObs.Observation.ObservationId,
                ["elementCount"] = freshObs.Observation.Elements.Count,
                ["resumedAt"] = DateTimeOffset.UtcNow.ToString("O")
            };
        }

        // 4. Clear pause flags & reset HUD status
        state.IsPausedForHuman = false;
        state.SafePointPauseRequested = false;
        state.PauseStatus = null;
        state.PauseReason = null;

        // 5. Resolve remaining steps
        RunStep[] stepsToRun;
        if (remainingSteps != null && remainingSteps.Length > 0)
        {
            stepsToRun = remainingSteps;
        }
        else if (state.PlannedSteps != null)
        {
            var startIdx = skipPausedStep ? state.PausedStepIndex + 1 : state.PausedStepIndex;
            if (startIdx < 0) startIdx = 0;
            if (startIdx >= state.PlannedSteps.Length)
            {
                _s.Rt.PostSuccess("✓ Tamamlandı");
                return RunReport(state, "Completed", sw, [],
                    internalActions: 0, executed: 0, skipped: 0);
            }
            stepsToRun = state.PlannedSteps[startIdx..];
        }
        else
        {
            return Error(OutcomeKind.Malformed,
                "No remaining steps recorded for this run. Provide remainingSteps explicitly to resume.");
        }

        // 6. Run remaining steps
        _s.Rt.PostActivity("Plan devam ettiriliyor…");
        return await RunPlanCore(stepsToRun, state.RunId, sw, ct, detail);
    }

    [McpServerTool(Name = "computer_screen_memory"), Description(
        "Query the persistent desktop state and navigation memory: known screens, previously seen elements, and navigation transitions for an application.")]
    public CallToolResult ScreenMemory(
        [Description("action: 'summary' | 'screens' | 'elements' | 'path' | 'find'")] string action = "summary",
        [Description("process/app name (defaults to active foreground app)")] string? app = null,
        [Description("target element name to find across all screens")] string? query = null,
        [Description("starting screen for path search")] string? fromScreen = null,
        [Description("destination screen for path search")] string? toScreen = null,
        CancellationToken ct = default)
    {
        var targetApp = app;
        if (string.IsNullOrWhiteSpace(targetApp))
            targetApp = _s.Rt.ForegroundWindow()?.ProcessName;

        var act = (action ?? "summary").ToLowerInvariant();
        switch (act)
        {
            case "find":
            {
                if (string.IsNullOrWhiteSpace(query))
                    return Error(OutcomeKind.Malformed, "query is required for 'find'");
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var sug = _s.Memory.SuggestRecovery(query, null, targetApp);
                var matches = _s.Memory.GetKnownElements(targetApp)
                    .Where(e => e.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
                    .Select(e => new
                    {
                        name = e.Name,
                        role = e.Role,
                        screen = e.ScreenId,
                        presence = e.Presence.ToString(),
                        lastSeen = e.LastSeen,
                    }).ToList();
                return Json(new
                {
                    app = targetApp,
                    matches = matches,
                    suggestion = sug != null ? new
                    {
                        targetScreen = sug.TargetScreen,
                        hint = sug.NavigationHint,
                        path = sug.Path,
                    } : null
                });
            }
            case "path":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                if (string.IsNullOrWhiteSpace(fromScreen) || string.IsNullOrWhiteSpace(toScreen))
                    return Error(OutcomeKind.Malformed, "fromScreen and toScreen are required for 'path'");
                var path = _s.Memory.FindNavigationPath(targetApp, fromScreen, toScreen);
                return Json(new
                {
                    app = targetApp,
                    from = fromScreen,
                    to = toScreen,
                    reachable = path != null,
                    steps = path?.Select(p => p.ActionDescription).ToList() ?? []
                });
            }
            case "screens":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var screens = _s.Memory.GetScreens(targetApp);
                return Json(new
                {
                    app = targetApp,
                    currentScreen = _s.Memory.GetCurrentScreen(targetApp),
                    screens = screens.Select(s => new
                    {
                        screenId = s.ScreenId,
                        title = s.TitlePattern,
                        landmarks = s.Landmarks,
                        lastActive = s.LastActive,
                    }).ToList()
                });
            }
            case "elements":
            {
                if (string.IsNullOrWhiteSpace(targetApp))
                    return Error(OutcomeKind.Malformed, "app is required or a foreground window must exist");
                var elements = _s.Memory.GetKnownElements(targetApp, fromScreen);
                return Json(new
                {
                    app = targetApp,
                    screen = fromScreen,
                    elements = elements.Select(e => new
                    {
                        name = e.Name,
                        role = e.Role,
                        screen = e.ScreenId,
                        presence = e.Presence.ToString(),
                    }).Take(60).ToList()
                });
            }
            case "summary":
            default:
            {
                var summary = _s.Memory.GetSummary(targetApp);
                return Json(summary);
            }
        }
    }

    private async Task<CallToolResult> RunPlanCore(RunStep[] steps,
        string? runId, Stopwatch sw, CancellationToken ct,
        string? detail = null)
    {
        if (steps is not { Length: > 0 })
            return Error(OutcomeKind.Malformed, "steps required", sw);

        using var trace = PerfTrace.Begin("computer_run",
            runId ?? "run.pending");
        trace.Set("stepCount", steps.Length);

        var state = runId != null && _s.Runs.TryGetValue(runId, out var existing)
            ? existing
            : null;
        var explicitHwnd = steps.Select(s => ParseHwnd(s.Hwnd) ?? ParseHwnd(s.Target?.Hwnd)).FirstOrDefault(h => h != null);
        if (state == null)
        {
            state = new RunState
            {
                RunId = !string.IsNullOrWhiteSpace(runId) ? runId
                    : "r_" + Guid.NewGuid().ToString("N")[..8],
                ScopeHwnd = explicitHwnd ?? _s.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd,
            };
            using (PerfTrace.Stage("plan.baseline"))
            {
                // Read-only plans (find/assert/wait…) never mutate the UI —
                // defer the baseline entirely: Delta() can then prove "no
                // relevant events" and skip both snapshots. Mutating plans
                // keep the pre-run snapshot for an honest element-level diff.
                var readOnly = steps.All(s => ReadOnlyStepActions.Contains(
                    s.Action ?? ""));
                state.BaselineDeferred = readOnly;
                state.RunStartedAt = DateTimeOffset.Now;
                state.BaselineHwnd = state.ScopeHwnd;
                state.BaselineAt = DateTimeOffset.Now;
                if (!readOnly)
                    state.Baseline = Snapshot(state.ScopeHwnd);
                else
                    PerfTrace.Count("plan.baselineDeferred");
            }
            _s.Runs[state.RunId] = state;
        }
        else if (explicitHwnd != null)
        {
            state.ScopeHwnd = explicitHwnd;
        }
        state.Slim = Slim(detail);
        trace.Id = state.RunId;

        var epoch = _s.Control.ActionToken();
        if (epoch == null)
            return RunReport(state, "EmergencyStopped", sw, [],
                pauseStep: -1, pauseStatus: "EmergencyStopped",
                pauseError: StoppedDetail);

        var scope = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        var octx = new ObsContext(
            _s.LastObservation?.ObservationId ?? 0, scope, _s.Frames,
            () => _s.MonitorFor(scope ?? 0), MaxFrameAgeMs: 15_000,
            _s.ValidObsIds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _s.SessionCts.Token, epoch.Value);
        using var cancelReg = linked.Token.CanBeCanceled
            ? linked.Token.Register(() => _s.Rt.PurgePendingWork())
            : default;

        var report = new List<Dictionary<string, object?>>();
        var internalActions = 0;
        var executed = 0;
        var skipped = 0;
        var pre = state.LastElementId != null
            ? ReadElementState(state.LastElementId) : null;
        var fgBefore = _s.Rt.ForegroundWindow()?.Hwnd;

        // outer lease: the run reads as one continuous Active session —
        // waits between steps don't flicker the indicator. Disposed when
        // this call returns (pause/checkpoint → grace → ConnectedIdle).
        using var runLease = _s.Rt.Activity.BeginActivity();
        state.PlannedSteps = steps;

        for (var i = 0; i < steps.Length; i++)
        {
            var s = steps[i];
            var action = (s.Action ?? "").ToLowerInvariant();

            // 1. Safe-point pause requested (e.g. via computer_pause_run)
            if (state.SafePointPauseRequested)
            {
                state.PausedStepIndex = i;
                state.IsPausedForHuman = true;
                state.PauseStatus = OutcomeKind.PausedForHuman.ToString();
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = DescribeStep(s);
                _s.Rt.PostHumanTakeover(state.PauseReason ?? "Güvenli noktada duraklatıldı", nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "PausedForHuman",
                    pauseError: state.PauseReason ?? "Paused at safe point for human takeover",
                    includeDelta: true, includeAvailable: true);
            }

            // 2. Explicit human takeover step action
            if (action is "human" or "human_takeover" or "pause_for_human")
            {
                state.PausedStepIndex = i;
                state.IsPausedForHuman = true;
                state.PauseStatus = OutcomeKind.PausedForHuman.ToString();
                state.PauseReason = s.Reason ?? s.Note ?? "İnsan müdahalesi gerekiyor";
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = (i + 1 < steps.Length) ? DescribeStep(steps[i + 1]) : null;
                _s.Rt.PostHumanTakeover(state.PauseReason, nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "PausedForHuman",
                    pauseError: state.PauseReason,
                    includeDelta: true, includeAvailable: true);
            }

            using var stepSpan = PerfTrace.Stage($"step.{action}");
            if (ValidateStep(s) is { } verr)
            {
                report.Add(StepEntry(i, s, "Malformed", detail: verr));
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Malformed", pauseError: verr);
            }
            if (linked.Token.IsCancellationRequested)
            {
                _s.Rt.PurgePendingWork();
                if (epoch.Value.IsCancellationRequested)
                    return RunReport(state, "EmergencyStopped", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: "EmergencyStopped",
                        pauseError: StoppedDetail);

                return RunReport(state, "Cancelled", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Cancelled",
                    pauseError: "execution cancelled");
            }

            if (state.Bindings.Count > 0)
            {
                s = s with
                {
                    Text = SubstituteVariables(s.Text, state),
                    Value = SubstituteVariables(s.Value, state),
                };
            }

            // ---- declarative conditions → skip, never fail ----
            string? skip; string? condKind, condDetail;
            using (PerfTrace.Stage("step.condition"))
                skip = EvalConditions(s, state, out condKind, out condDetail);
            if (condKind != null)
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: condKind, pauseError: condDetail);
            if (skip != null)
            {
                skipped++;
                report.Add(StepEntry(i, s, "Skipped", detail: skip));
                continue;
            }

            // ---- checkpoint: hand control back with fresh context ----
            if (action == "checkpoint")
            {
                return RunReport(state, "Checkpoint", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "Checkpoint",
                    pauseError: s.Note ?? "checkpoint reached",
                    includeDelta: true);
            }

            // ---- scan / for_each: local iteration, filtering, and collection over items ----
            if (action is "scan" or "for_each")
            {
                var scanRes = await ExecuteScanStep(s, state, octx, linked.Token);
                internalActions += scanRes.InternalActions;
                if (!scanRes.Outcome.Success)
                {
                    report.Add(StepEntry(i, s, scanRes.Outcome.Kind.ToString(), detail: scanRes.Outcome.Detail, ms: (int)scanRes.DurationMs));
                    return RunReport(state, "Paused", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: scanRes.Outcome.Kind.ToString(),
                        pauseError: scanRes.Outcome.Detail,
                        includeDelta: true, includeAvailable: true,
                        pauseDiagnosis: scanRes.Outcome.Diagnosis);
                }
                executed++;
                state.TotalExecuted++;
                report.Add(StepEntry(i, s, "Ok", detail: scanRes.Outcome.Detail, ms: (int)scanRes.DurationMs, actions: scanRes.SubStepsCount > 0 ? scanRes.SubStepsCount : null));
                if (scanRes.PostId != null) { state.LastElementId = scanRes.PostId; _s.NoteElementRef(scanRes.PostId); }
                continue;
            }

            // ---- application adapters: specialist direct execution (media, testapp, CLI) ----
            if (action is "adapter" or "media" or "play" or "pause" or "play_pause" or "toggle_playback"
                or "next" or "previous" or "stop" or "volume_up" or "volume_down" or "mute" || s.Adapter != null)
            {
                var adapterStepSw = Stopwatch.StartNew();
                var proc = s.Target?.Process ?? _s?.Rt?.ForegroundWindow()?.ProcessName;
                var targetHwnd = s.Target?.Hwnd != null ? ParseHwnd(s.Target.Hwnd) : _s?.Rt?.ForegroundWindow()?.Hwnd;

                var adapterAction = action == "adapter" ? (s.Query ?? s.Text ?? "execute") : action;
                var effectiveArgs = s.Args != null ? new Dictionary<string, object?>(s.Args) : new Dictionary<string, object?>();
                if (s.Text != null && !effectiveArgs.ContainsKey("text")) effectiveArgs["text"] = s.Text;
                if (s.Value != null && !effectiveArgs.ContainsKey("value")) effectiveArgs["value"] = s.Value;

                TargetRef? stepTargetRef = null;
                if (targetHwnd.HasValue) stepTargetRef = TargetRef.Window(targetHwnd.Value);
                else if (!string.IsNullOrEmpty(s.Target?.ElementId)) stepTargetRef = TargetRef.Element(s.Target.ElementId);

                (bool handled, Inbrisk.Core.AdapterResult? adapterRes) = await (_s?.Adapters ?? new Inbrisk.Runtime.Adapters.ApplicationAdapterRegistry()).TryExecuteAsync(
                    action: adapterAction,
                    target: stepTargetRef,
                    args: effectiveArgs,
                    processName: proc,
                    hwnd: targetHwnd,
                    preferredAdapterId: s.Adapter,
                    ct: linked.Token);

                if (handled && adapterRes != null)
                {
                    internalActions++;
                    if (!adapterRes.Success)
                    {
                        report.Add(StepEntry(i, s, "Failed", detail: adapterRes.Detail ?? "adapter action failed", ms: (int)adapterStepSw.ElapsedMilliseconds));
                        return RunReport(state, "Paused", sw, report,
                            internalActions, executed, skipped,
                            pauseStep: i, pauseStatus: "Failed",
                            pauseError: adapterRes.Detail,
                            includeDelta: true);
                    }

                    executed++;
                    state.TotalExecuted++;
                    if (s.As != null && adapterRes.Data != null)
                    {
                        foreach (var kv in adapterRes.Data)
                            state.Bindings[$"{s.As}.{kv.Key}"] = kv.Value?.ToString() ?? "";
                    }
                    report.Add(StepEntry(i, s, "Ok", method: adapterRes.Method, detail: adapterRes.Detail, ms: (int)adapterStepSw.ElapsedMilliseconds));
                    continue;
                }
            }

            // ---- execute with bounded retry; target re-resolves per try ----
            var tries = Math.Clamp((s.Retry ?? 0) + 1, 1, 20);
            var retryWait = Math.Clamp(s.RetryInterval ?? 400, 50, 5000);
            StepOutcome? outcome = null;
            string? errKind = null, errDetail = null;
            TargetDiagnosis? stepDiag = null;
            var summaries = new List<string>();
            var retryReasons = new List<string>();
            string? postId = null;
            var stepSw = Stopwatch.StartNew();
            var isPseudo = action is "find" or "assert" or "launch";
            List<AgentAction>? built = null;

            var targetName = s.Target?.Process ?? s.Target?.Window ?? s.Target?.Name ?? (s.Target?.ElementId != null ? "element" : null);
            if (action == "launch")
            {
                var app = s.Target?.Process ?? s.Text ?? "Uygulama";
                _s.Rt.PostActivity(ActivityGranularity.FormatAction(app, "launch"), app);
            }
            else if (!string.IsNullOrEmpty(targetName))
            {
                _s.Rt.PostActivity(ActivityGranularity.FormatAction(targetName, action), targetName);
            }

            for (var t = 0; t < tries; t++)
            {
                summaries.Clear();
                if (isPseudo)
                {
                    using (PerfTrace.Stage("step.pseudo"))
                        outcome = PseudoOutcome(s, state, linked.Token, out postId)!;
                    if (outcome.Success) break;
                    var (healed, healDetail) = await TrySelfHeal(s, state, outcome.Kind.ToString(), outcome.Detail, octx, linked.Token);
                    if (healed)
                    {
                        internalActions++;
                        summaries.Add(healDetail);
                        tries++;
                        continue;
                    }
                    // only transient states retry — element absent yet, stale
                    // mid-refresh, or an assert whose postcondition hasn't
                    // settled (async UI flip). Malformed/AmbiguousTarget/
                    // PolicyDenied/EmergencyStopped break immediately.
                    var transient = outcome.Kind is OutcomeKind.TargetNotFound
                        or OutcomeKind.TargetOffscreen
                        or OutcomeKind.TargetOnDifferentScreen
                        or OutcomeKind.Stale
                        || (action == "assert" && outcome.Kind is OutcomeKind.Failed
                            or OutcomeKind.Unverified);
                    if (transient && t + 1 < tries)
                    {
                        retryReasons.Add(outcome.Kind.ToString());
                        linked.Token.WaitHandle.WaitOne(retryWait); continue;
                    }
                    break;
                }
                using (PerfTrace.Stage("step.resolve"))
                    built = BuildStepActions(s, state,
                        out errKind, out errDetail, out postId, out stepDiag);
                if (built == null)
                {
                    var (healed, healDetail) = await TrySelfHeal(s, state, errKind, errDetail, octx, linked.Token);
                    if (healed)
                    {
                        internalActions++;
                        summaries.Add(healDetail);
                        tries++;
                        continue;
                    }

                    outcome = new StepOutcome(
                        Enum.TryParse<OutcomeKind>(errKind, out var okk)
                            ? okk : OutcomeKind.Malformed,
                        false, "plan", errDetail, 0,
                        Diagnosis: stepDiag);
                    if (errKind is "TargetNotFound" or "TargetOffscreen" or "TargetOnDifferentScreen" or "AmbiguousTarget" &&
                        t + 1 < tries)
                    {
                        retryReasons.Add(errKind);
                        linked.Token.WaitHandle.WaitOne(retryWait); continue;
                    }
                    break;
                }
                outcome = null;
                // snapshot the target BEFORE its first mutation so the run's
                // changes{} reflects the whole plan, not just the last step
                using (PerfTrace.Stage("verify.targeted"))
                    pre ??= postId != null ? ReadElementState(postId) : null;
                var stepCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                if (s.Timeout is > 0) stepCts.CancelAfter(s.Timeout.Value);
                var actx = new ActionContext(stepCts.Token, null, _s.SessionId, null, null);

                IInputLease? physicalLease = null;
                var requiresPhysical = built.Any(a => a.Kind is AgentActionKind.Click or AgentActionKind.RightClick or AgentActionKind.DoubleClick or AgentActionKind.Drag or AgentActionKind.Scroll or AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey or AgentActionKind.FocusWindow or AgentActionKind.FocusElement);
                if (requiresPhysical)
                {
                    try
                    {
                        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
                        physicalLease = await arbiter.AcquireAsync(
                            ownerId: state.RunId,
                            kind: LeaseKind.PhysicalInput,
                            description: $"Run '{state.RunId}' step {i}: {action}",
                            timeout: TimeSpan.FromSeconds(5),
                            ct: stepCts.Token).ConfigureAwait(false);
                    }
                    catch (TimeoutException tex)
                    {
                        stepCts.Dispose();
                        errKind = "ConcurrencyConflict";
                        errDetail = tex.Message;
                        outcome = new StepOutcome(OutcomeKind.ConcurrencyConflict, false, "Arbiter", tex.Message, (int)stepSw.ElapsedMilliseconds);
                        break;
                    }
                }

                try
                {
                    foreach (var a in built)
                    {
                        // the local resume chord is human-only, even inside a plan
                        if (a.Kind is AgentActionKind.Hotkey or AgentActionKind.Key &&
                            _s.Control.IsLocalResumeKey(a.Key,
                                a.Kind == AgentActionKind.Hotkey ? a.Modifiers : null))
                        {
                            errKind = "PolicyDenied";
                            errDetail = "local resume hotkey is reserved for the human";
                            outcome = new StepOutcome(OutcomeKind.PolicyDenied,
                                false, "plan", errDetail, 0);
                            break;
                        }
                        if (BadPoint(a, out var pointErr))
                        {
                            errKind = "StaleFrame"; errDetail = pointErr;
                            outcome = new StepOutcome(OutcomeKind.StaleFrame,
                                false, "plan", pointErr, 0);
                            break;
                        }
                        summaries.Add(a.Summary());
                        using (PerfTrace.Stage("step.execute"))
                            outcome = await Task.Run(
                                () => _s.Resolver.Execute(a, octx, actx), stepCts.Token);
                        internalActions++;
                        if (a.ElementId != null) postId = a.ElementId;
                        if (!outcome.Success) break;
                        if (stepCts.IsCancellationRequested &&
                            !linked.IsCancellationRequested)
                        {
                            outcome = new StepOutcome(OutcomeKind.Timeout, false,
                                "plan", $"step exceeded timeout {s.Timeout}ms", 0);
                            break;
                        }
                    }
                }
                finally
                {
                    physicalLease?.Dispose();
                    stepCts.Dispose();
                }

                if (outcome?.Success == true || errKind is "PolicyDenied" or "StaleFrame")
                    break;
                if (RecoveryPolicy.IsFatal(outcome?.Kind ?? OutcomeKind.Failed))
                    break;
                if (t + 1 < tries)
                {
                    retryReasons.Add(outcome?.Kind.ToString() ?? "?");
                    using (PerfTrace.Stage("step.retryWait"))
                        linked.Token.WaitHandle.WaitOne(retryWait);
                }
            }

            if (isPseudo || retryReasons.Count > 0)
                UiaPerf.Write(new
                {
                    kind = "run.step",
                    runId = state.RunId,
                    step = i,
                    action,
                    status = outcome?.Kind.ToString() ?? errKind,
                    ms = stepSw.ElapsedMilliseconds,
                    retryCount = retryReasons.Count,
                    retryReasons = retryReasons.Count > 0 ? retryReasons : null,
                });
            var ok = outcome?.Success == true;
            var okDetail = summaries.Count > 0
                ? string.Join(" → ", summaries) : outcome?.Detail;
            report.Add(StepEntry(i, s, ok ? (outcome?.Kind.ToString() ?? "Ok")
                    : (outcome?.Kind.ToString() ?? errKind ?? "Failed"),
                method: outcome?.Method, ms: (int)stepSw.ElapsedMilliseconds,
                detail: ok ? okDetail : outcome?.Detail ?? errDetail,
                actions: summaries.Count > 1 ? summaries.Count : null));
            if (s.Observe == true && ok)
            {
                try
                {
                    var scopeHwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
                    var obs = _s.Observe(scopeHwnd, ObservationBudget.Default, VisualAttachPolicy.Never);
                    var oObs = obs.Observation;
                    report[^1]["observation"] = new Dictionary<string, object?>
                    {
                        ["observationId"] = oObs.ObservationId,
                        ["activeWindow"] = oObs.ActiveWindow != null ? new
                        {
                            hwnd = $"0x{oObs.ActiveWindow.Hwnd:X}",
                            title = oObs.ActiveWindow.Title,
                            process = oObs.ActiveWindow.Process,
                        } : null,
                        ["elements"] = oObs.Elements.Take(30).Select(e => new
                        {
                            id = e.Id,
                            role = e.Role,
                            name = e.Name,
                            value = e.Value,
                            state = e.State,
                            actions = e.Actions,
                        }).ToList(),
                    };
                }
                catch { }
            }
            if (postId != null) { state.LastElementId = postId; _s.NoteElementRef(postId); }
            if (!ok)
            {
                state.PausedStepIndex = i;
                state.PausedAt = DateTimeOffset.UtcNow;
                var fg = _s.Rt.ForegroundWindow();
                state.PrePauseForegroundHwnd = fg?.Hwnd;
                state.PrePauseForegroundTitle = fg?.Title;
                var nextStepDesc = DescribeStep(steps[i]);
                _s.Rt.PostHumanTakeover($"Adım durdu: {outcome?.Kind.ToString() ?? errKind ?? "Hata"}", nextStepDesc);

                // a step cancelled by panic must not read as a resumable
                // pause — the control state is EmergencyStopped
                if (_s.Control.State == ComputerControlState.EmergencyStopped)
                    return RunReport(state, "EmergencyStopped", sw, report,
                        internalActions, executed, skipped,
                        pauseStep: i, pauseStatus: "EmergencyStopped",
                        pauseError: StoppedDetail);
                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: outcome?.Kind.ToString() ?? errKind ?? "Failed",
                    pauseError: outcome?.Detail ?? errDetail,
                    includeDelta: true, includeAvailable: true,
                    pauseDiagnosis: outcome?.Diagnosis ?? stepDiag);
            }
            executed++;
            state.TotalExecuted++;
            var fgNow = _s.Rt.ForegroundWindow();
            if (fgNow != null)
            {
                var app = fgNow.ProcessName ?? "";
                var scrBefore = _s.Memory.GetCurrentScreen(app);
                var scrAfter = DesktopStateMemory.InferScreenName(app, fgNow.Title, []);
                if (!string.Equals(scrBefore, scrAfter, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(scrBefore) && !string.IsNullOrEmpty(scrAfter))
                {
                    var actionSummary = $"{s.Action} '{(s.Target?.Name ?? s.Text ?? s.Key ?? s.Query ?? s.ElementId)}'";
                    _s.Memory.RecordTransition(app, scrBefore, scrAfter, actionSummary, s.Target?.Name, s.Target?.Role);
                }
            }
            // an unexpected modal must interrupt the plan — the next steps
            // were written for a UI that may no longer be on screen
            (long hwnd, string title, bool dialogLikely, List<string> elements)? newWin = null;
            using (PerfTrace.Stage("step.modalCheck"))
                if (action is not ("focus" or "focus_window"))
                    newWin = DetectNewWindow(fgBefore,
                        built?.LastOrDefault(b => b.Kind == AgentActionKind.FocusWindow)?.Hwnd);
            if (newWin is { dialogLikely: true } nw)
            {
                state.PausedStepIndex = i;
                state.PausedAt = DateTimeOffset.UtcNow;
                state.PrePauseForegroundHwnd = nw.hwnd;
                state.PrePauseForegroundTitle = nw.title;
                var nextStepDesc = (i + 1 < steps.Length) ? DescribeStep(steps[i + 1]) : null;
                _s.Rt.PostHumanTakeover("Beklenmeyen pencere açıldı", nextStepDesc);

                return RunReport(state, "Paused", sw, report,
                    internalActions, executed, skipped,
                    pauseStep: i, pauseStatus: "UnexpectedModalOpened",
                    pauseError: $"new window 0x{nw.hwnd:X} \"{nw.title}\" " +
                        "appeared — inspect its elements before resuming",
                    includeDelta: true, includeAvailable: true);
            }
            fgBefore = _s.Rt.ForegroundWindow()?.Hwnd;
        }

        UiElement? post;
        using (PerfTrace.Stage("verify.targeted"))
            post = state.LastElementId != null
                ? ReadElementState(state.LastElementId) : null;
        _s.Rt.PostSuccess("✓ Tamamlandı");
        return RunReport(state, "Completed", sw, report,
            internalActions, executed, skipped, pre: pre, post: post,
            includeDelta: true);
    }

    // ------------------------------------------------------------ plan steps

    private static string DescribeStep(RunStep? s)
    {
        if (s == null) return "Bilinmeyen adım";
        var act = (s.Action ?? "step").ToLowerInvariant();
        var targetName = s.Target?.Name ?? s.Target?.Role ?? s.ElementId ?? s.Query;
        if (act is "click" or "rightclick" or "doubleclick" or "invoke" or "toggle" or "select")
            return $"{act} \"{targetName ?? "öğe"}\"";
        if (act is "type" or "set_value" or "setvalue")
            return $"{act} \"{s.Text ?? s.Value ?? ""}\"";
        if (act is "key" or "hotkey")
            return $"{act} {s.Keys ?? s.Key ?? ""}";
        if (act is "find")
            return $"bul \"{targetName ?? s.As ?? "öğe"}\"";
        if (act is "wait" or "wait_for" or "wait_for_change" or "wait_for_stable")
            return $"bekle {s.Query ?? (s.Ms != null ? $"{s.Ms}ms" : "")}".TrimEnd();
        if (act is "adapter" or "media" or "play" or "pause")
            return $"medya: {act}";
        if (act is "launch")
            return $"başlat \"{s.App ?? s.Executable ?? s.Path ?? s.Uri ?? ""}\"";
        if (!string.IsNullOrEmpty(s.Reason ?? s.Note))
            return $"{act}: {s.Reason ?? s.Note}";
        return act;
    }

    /// <summary>Strict per-action validation — an unknown field, a field
    /// meaningless for the step's action, or a step with no effective
    /// operation is Malformed. A misspelled field must never produce a
    /// Verified-looking step.</summary>
    private static string? ValidateStep(RunStep s)
    {
        if (s.Extra is { Count: > 0 } extra)
            return $"unknown step field(s): {string.Join(", ", extra.Keys)} — " +
                "see computer_capabilities for the step schema";
        var a = (s.Action ?? "").ToLowerInvariant();
        if (a.Length == 0) return "step is missing 'action'";

        var set = new List<string>();
        if (s.Target != null) set.Add("target");
        if (s.ElementId != null) set.Add("elementId");
        if (s.As != null) set.Add("as");
        if (s.Text != null) set.Add("text");
        if (s.Value != null) set.Add("value");
        if (s.Mode != null) set.Add("mode");
        if (s.Position != null) set.Add("position");
        if (s.Submit) set.Add("submit");
        if (s.Key != null) set.Add("key");
        if (s.Keys != null) set.Add("keys");
        if (s.Modifiers != null) set.Add("modifiers");
        if (s.Count != null) set.Add("count");
        if (s.Delta != null) set.Add("delta");
        if (s.Ms != null) set.Add("ms");
        if (s.Query != null) set.Add("query");
        if (s.Hwnd != null) set.Add("hwnd");
        if (s.X != null) set.Add("x");
        if (s.Y != null) set.Add("y");
        if (s.FrameId != null) set.Add("frameId");
        if (s.ObservationId != null) set.Add("observationId");
        if (s.ToX != null) set.Add("toX");
        if (s.ToY != null) set.Add("toY");
        if (s.ToFrameId != null) set.Add("toFrameId");
        if (s.ToObservationId != null) set.Add("toObservationId");
        if (s.Note != null) set.Add("note");
        if (s.Contains != null) set.Add("contains");
        if (s.NotContains != null) set.Add("notContains");
        if (s.Exact != null) set.Add("exact");
        if (s.State != null) set.Add("state");
        if (s.Enabled != null) set.Add("enabled");
        if (s.App != null) set.Add("app");
        if (s.Search != null) set.Add("search");
        if (s.Executable != null) set.Add("executable");
        if (s.Path != null) set.Add("path");
        if (s.Aumid != null) set.Add("aumid");
        if (s.Uri != null) set.Add("uri");
        if (s.Arguments is { Length: > 0 }) set.Add("arguments");
        if (s.NewInstance == true) set.Add("newInstance");
        if (s.WaitFor != null) set.Add("waitFor");
        if (s.DebugPort != null) set.Add("debugPort");
        if (s.Where != null) set.Add("where");
        if (s.Steps is { Length: > 0 }) set.Add("steps");
        if (s.MaxItems != null) set.Add("maxItems");
        if (s.MaxPages != null) set.Add("maxPages");
        if (s.StopOn is { Length: > 0 }) set.Add("stopOn");
        if (s.Collect is { Length: > 0 }) set.Add("collect");
        if (s.ExpectedState != null) set.Add("expectedState");
        if (s.ExpectedValue != null) set.Add("expectedValue");
        if (s.Gone) set.Add("gone");
        if (s.MinCount != null) set.Add("minCount");
        if (s.StopOnDialog != null) set.Add("stopOnDialog");
        if (s.Adapter != null) set.Add("adapter");
        if (s.Args != null) set.Add("args");
        if (s.Reason != null) set.Add("reason");

        string[]? allowed = a switch
        {
            "find" => ["target", "elementId", "as", "select", "index", "orderby"],
            "assert" => ["elementId", "target", "contains", "notContains",
                "exact", "state", "enabled", "value", "note",
                "select", "index", "orderby"],
            "checkpoint" => ["note", "reason"],
            "human" or "human_takeover" or "pause_for_human" => ["note", "reason"],
            "adapter" or "media" or "play" or "pause" or "play_pause" or "toggle_playback"
                or "next" or "previous" or "stop" or "volume_up" or "volume_down" or "mute" =>
                ["target", "adapter", "args", "query", "text", "value", "as", "elementId", "select", "index", "orderby", "timeout", "note", "retry", "retryInterval"],
            "scan" or "for_each" => ["target", "elementId", "as", "where",
                "steps", "maxItems", "maxPages", "stopOn", "collect", "timeout", "note", "select", "index", "orderby"],
            "focus" or "focus_window" => ["hwnd", "elementId", "target",
                "select", "index", "orderby"],
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" or "invoke" or "toggle" or "select"
                or "hover" => ["elementId", "target", "x", "y",
                    "frameId", "observationId", "select", "index", "orderby"],
            "set_value" or "setvalue" => ["elementId", "target", "text", "value",
                "select", "index", "orderby"],
            "type" => ["elementId", "target", "text", "value", "mode",
                "position", "submit", "select", "index", "orderby"],
            "key" => ["elementId", "target", "key", "count",
                "select", "index", "orderby"],
            "hotkey" => ["elementId", "target", "key", "keys", "modifiers",
                "select", "index", "orderby"],
            "scroll" => ["elementId", "target", "delta", "x", "y",
                "frameId", "observationId", "select", "index", "orderby"],
            "scroll_into_view" => ["elementId", "target", "select", "index", "orderby"],
            "drag" => ["elementId", "target", "x", "y", "frameId",
                "observationId", "toX", "toY", "toFrameId", "toObservationId",
                "select", "index", "orderby"],
            "wait" => ["ms"],
            "wait_for" => ["query", "ms", "target", "elementId", "expectedState", "expectedValue", "gone", "minCount", "stopOnDialog"],
            "wait_for_gone" => ["query", "ms", "target", "elementId", "stopOnDialog"],
            "wait_for_change" or "wait_for_stable" => ["ms"],
            "launch" => ["target", "text", "value", "as", "ms", "app",
                "search", "executable", "path", "aumid", "uri", "arguments",
                "newInstance", "waitFor", "debugPort"],
            _ => null,
        };
        if (allowed == null)
            return $"unknown plan action '{s.Action}' — supported: scan, for_each, find, " +
                "assert, checkpoint, human, human_takeover, pause_for_human, adapter, media, play, pause, next, previous, volume_up, volume_down, mute, " +
                "focus, focus_window, click, rightclick, doubleclick, invoke, toggle, select, hover, set_value, type, " +
                "key, hotkey, scroll, scroll_into_view, drag, wait, wait_for, wait_for_gone, wait_for_change, " +
                "wait_for_stable, launch";
        var bad = set.Where(f => !allowed.Contains(f,
            StringComparer.OrdinalIgnoreCase)).ToList();
        if (bad.Count > 0)
            return $"field(s) [{string.Join(", ", bad)}] have no effect on " +
                $"action '{a}' — see computer_capabilities for valid fields";

        if (a is "scan" or "for_each")
        {
            if (s.Steps is not { Length: > 0 } && s.Collect is not { Length: > 0 })
                return $"{a} step requires at least 'steps' to execute or 'collect' to extract properties";
            if (s.Steps is { Length: > 0 } subSteps)
            {
                for (var j = 0; j < subSteps.Length; j++)
                {
                    if (ValidateStep(subSteps[j]) is { } subErr)
                        return $"sub-step {a}[{j}]: {subErr}";
                }
            }
        }

        var hasTarget = s.ElementId != null || s.Target != null;
        var hasPoint = s.X != null && s.Y != null;
        if (s.Select?.ToLowerInvariant() is { } sel &&
            sel is not ("first" or "last" or "nth"))
            return "select must be first|last|nth";
        if (s.OrderBy?.ToLowerInvariant() is { } ord &&
            ord is not ("visual" or "tree" or "score"))
            return "orderBy must be visual|tree|score";
        if (s.Index != null && s.Select?.ToLowerInvariant() != "nth")
            return "index only applies with select:\"nth\"";
        return a switch
        {
            "find" when !hasTarget => "find requires target or elementId",
            "assert" when !hasTarget => "assert requires elementId or target",
            "focus" when !hasTarget && s.Hwnd == null =>
                "focus requires elementId, target, or hwnd",
            "focus_window" when s.Hwnd == null && s.Target == null =>
                "focus_window requires hwnd or a window/process target",
            "invoke" or "toggle" or "select" or "hover" or "scroll_into_view" when !hasTarget =>
                $"{a} requires elementId or target",
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" when !hasTarget && !hasPoint =>
                $"{a} requires elementId, target, or x+y",
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" when hasPoint &&
                    (s.FrameId == null || s.ObservationId == null) =>
                "x/y require frameId + observationId",
            "set_value" or "setvalue" when !hasTarget =>
                "set_value requires elementId or target",
            "set_value" or "setvalue" when s.Text == null && s.Value == null =>
                "set_value requires text or value",
            "type" when s.Text == null && s.Value == null =>
                "type requires text",
            "key" when s.Key == null => "key requires a key name",
            "hotkey" when s.Key == null && s.Keys == null =>
                "hotkey requires key+modifiers, or keys shorthand like \"ctrl+s\"",
            "scroll" when s.Delta == null => "scroll requires delta",
            "drag" when s.ToX == null || s.ToY == null =>
                "drag requires a toX+toY destination",
            "wait" when s.Ms == null => "wait requires ms",
            "wait_for" when s.Query == null && s.Target == null && s.ElementId == null && s.ExpectedState == null && s.ExpectedValue == null && !s.Gone =>
                "wait_for requires query, target, or condition",
            "wait_for_gone" when s.Query == null && s.Target == null && s.ElementId == null =>
                "wait_for_gone requires query, target, or elementId",
            "launch" when s.App == null && s.Search == null &&
                s.Executable == null &&
                s.Path == null && s.Aumid == null && s.Uri == null &&
                s.Target?.Process == null && s.Text == null &&
                s.Value == null =>
                "launch requires app (\"Spotify\") — or executable/path/" +
                "aumid/uri; text/target.process accepted as the app name",
            _ => null,
        };
    }

    /// <summary>$name → bound elementId; anything else passes through.</summary>
    private static string? BoundId(string? id, RunState state)
        => id != null && id.StartsWith('$')
            ? state.Bindings.GetValueOrDefault(id[1..])
            : id;

    /// <summary>Substitutes $var, {{var}}, $var.name, $var.value, etc. using run bindings.</summary>
    private static string? SubstituteVariables(string? input, RunState state)
    {
        if (string.IsNullOrEmpty(input)) return input;
        if (state.Bindings.Count == 0) return input;

        if (input.StartsWith('$') && !input.Contains(' ') && state.Bindings.TryGetValue(input[1..], out var direct))
            return direct;

        var result = input;
        foreach (var (k, v) in state.Bindings.OrderByDescending(kv => kv.Key.Length))
        {
            if (result.Contains("{{" + k + "}}", StringComparison.OrdinalIgnoreCase))
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result, @"\{\{" + System.Text.RegularExpressions.Regex.Escape(k) + @"\}\}", v ?? "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            if (result.Contains("$" + k, StringComparison.OrdinalIgnoreCase))
            {
                result = System.Text.RegularExpressions.Regex.Replace(
                    result, @"\$" + System.Text.RegularExpressions.Regex.Escape(k) + @"(?![a-zA-Z0-9_\.])", v ?? "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        }
        return result;
    }

    private UiElement? ResolveRunTarget(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out int matchCount,
        out TargetDiagnosis? diagnosis)
    {
        errKind = null; errDetail = null; matchCount = 0; diagnosis = null;
        // an unbound $ref must fail loudly — never degrade to "untargeted"
        // (a click with no target) or silently drop a within constraint
        string? Unbound(string? raw) =>
            raw?.StartsWith('$') == true && BoundId(raw, state) == null ? raw : null;
        if (Unbound(s.ElementId) is { } ub)
        { errKind = "Malformed"; errDetail = $"unbound $ref '{ub}'"; return null; }
        var id = BoundId(s.ElementId, state);
        var target = s.Target;
        if (target != null)
        {
            if (Unbound(target.ElementId) is { } ube)
            { errKind = "Malformed"; errDetail = $"unbound $ref '{ube}'"; return null; }
            if (Unbound(target.Within) is { } ubw)
            { errKind = "Malformed"; errDetail = $"unbound $ref '{ubw}' in within"; return null; }
            target = target with
            {
                Name = SubstituteVariables(target.Name, state),
                NameContains = SubstituteVariables(target.NameContains, state),
                ElementId = BoundId(target.ElementId, state),
                Within = BoundId(target.Within, state),
                Value = SubstituteVariables(target.Value, state),
                ValueContains = SubstituteVariables(target.ValueContains, state),
            };
        }
        var purpose = (s.Action ?? "").ToLowerInvariant() switch
        {
            "set_value" or "setvalue" or "type" => "edit",
            "click" or "rightclick" or "right_click" or "doubleclick"
                or "double_click" or "invoke" or "hover" => "invoke",
            "toggle" => "toggle",
            "select" => "select",
            _ => (string?)null,
        };
        var sel = s.Select != null
            ? new Selection(s.Select.ToLowerInvariant(), s.Index,
                s.OrderBy?.ToLowerInvariant() ?? "score")
            : null;
        var el = ResolveTargetElement(id, target, out var err, out matchCount,
            purpose, sel: sel);
        if (err != null)
        {
            (errKind, errDetail, diagnosis) = ErrPartsWithDiag(err);
            return null;
        }
        if (el == null && id == null && target == null)
            return null; // untargeted step (key/wait/hotkey) is legal
        return el;
    }

    private UiElement? ResolveRunTarget(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out int matchCount)
        => ResolveRunTarget(s, state, out errKind, out errDetail, out matchCount, out _);

    /// <summary>Plan-level steps that produce no executor action: find binds
    /// a name, assert validates state. Returns null when the step is a real
    /// action; otherwise a synthetic outcome.</summary>
    private StepOutcome? PseudoOutcome(RunStep s, RunState state,
        CancellationToken ct, out string? postId)
    {
        postId = null;
        var action = (s.Action ?? "").ToLowerInvariant();
        if (action is not ("find" or "assert" or "launch")) return null;
        if (action == "launch") return LaunchOutcome(s, state, ct);

        UiElement? el; string? ek, ed; int mc; TargetDiagnosis? diag;
        using (PerfTrace.Stage("step.resolve"))
            el = ResolveRunTarget(s, state, out ek, out ed, out mc, out diag);
        if (el == null)
            return new StepOutcome(
                Enum.TryParse<OutcomeKind>(ek, out var ekk)
                    ? ekk : OutcomeKind.Malformed,
                false, "plan", ed ?? $"no element for {action}", 0, Diagnosis: diag);
        postId = el.Id;
        if (action == "find")
        {
            if (!string.IsNullOrWhiteSpace(s.As))
                state.Bindings[s.As.TrimStart('$')] = el.Id;
            return new StepOutcome(OutcomeKind.Verified, true, "UIA.find",
                $"{el.Id} {el.Role} \"{Trunc(el.Name, 60)}\"" +
                (s.As != null ? $" → ${s.As.TrimStart('$')}" : "") +
                (mc > 0 ? $" ({mc} match{(mc > 1 ? "es" : "")})" : ""), 0);
        }
        // assert
        UiElement? fresh;
        using (PerfTrace.Stage("verify.targeted"))
            fresh = ReadElementState(el.Id) ?? el;
        var failures = new List<string>();
        if (s.Contains == null && s.NotContains == null && s.Exact == null &&
            s.State == null && s.Enabled == null && s.Value == null)
            return new StepOutcome(OutcomeKind.Malformed, false, "assert",
                "assert needs contains|notContains|exact|state|enabled|value", 0);
        if (s.Contains != null &&
            !(Prop(fresh, "value")?.ToString()?.Contains(s.Contains,
                StringComparison.OrdinalIgnoreCase) == true) &&
            !(fresh.Name?.Contains(s.Contains,
                StringComparison.OrdinalIgnoreCase) == true))
            failures.Add($"neither value nor name contains \"{s.Contains}\"");
        if (s.NotContains != null &&
            (Prop(fresh, "value")?.ToString()?.Contains(s.NotContains,
                StringComparison.OrdinalIgnoreCase) == true ||
             fresh.Name?.Contains(s.NotContains,
                StringComparison.OrdinalIgnoreCase) == true))
            failures.Add($"value/name unexpectedly contains \"{s.NotContains}\"");
        if (s.Exact != null &&
            Prop(fresh, "value")?.ToString() != s.Exact && fresh.Name != s.Exact)
            failures.Add($"value/name != \"{s.Exact}\"");
        if (s.State != null &&
            Prop(fresh, "state")?.ToString()?.Contains(s.State,
                StringComparison.OrdinalIgnoreCase) != true)
            failures.Add($"state is \"{Prop(fresh, "state")}\", expected \"{s.State}\"");
        if (s.Enabled is { } want &&
            (Prop(fresh, "enabled") as bool?) != want)
            failures.Add($"enabled={Prop(fresh, "enabled")}, expected {want}");
        if (s.Value != null &&
            Prop(fresh, "value")?.ToString() != s.Value)
            failures.Add($"value=\"{Trunc(Prop(fresh, "value")?.ToString(), 60)}\", expected \"{Trunc(s.Value, 60)}\"");
        return failures.Count == 0
            ? new StepOutcome(OutcomeKind.Verified, true, "assert",
                $"assertions held on {el.Id}", 0)
            : new StepOutcome(OutcomeKind.Failed, false, "assert",
                string.Join("; ", failures), 0);
    }

    /// <summary>Declarative per-step conditions. Returns a skip reason, or
    /// null to execute. Condition evaluation errors (unknown role etc.)
    /// surface via out params and pause the run — never silently skip.</summary>
    private string? EvalConditions(RunStep s, RunState state,
        out string? errKind, out string? errDetail)
    {
        errKind = null; errDetail = null;
        string? Eval(TargetSpec? spec, string? elId, out UiElement? found,
            bool existenceOnly = false)
        {
            found = null;
            // an unbound $ref in a condition means "the element does not
            // exist" → skip semantics, never a condition error or pause
            if (elId?.StartsWith('$') == true && BoundId(elId, state) == null &&
                spec == null)
                return null;
            if (spec?.ElementId?.StartsWith('$') == true &&
                BoundId(spec.ElementId, state) == null)
                return null;
            if (spec?.Within?.StartsWith('$') == true &&
                BoundId(spec.Within, state) == null)
                return null;
            var id = BoundId(elId ?? spec?.ElementId, state);
            if (spec == null && id == null) return "no target";
            var t = spec == null ? null : spec with
            {
                ElementId = BoundId(spec.ElementId, state),
                Within = BoundId(spec.Within, state),
            };
            // existence-only fast path is safe iff every filter is pushed
            // into the native UIA condition — post-filters (negations,
            // relations, window TITLE) still need full enumeration, and
            // prop-reading conditions (ifEnabled/ifValue) need the same
            // deterministic pick as before → caller opts in per condition
            var firstOnly = existenceOnly && t != null && PureNativeSpec(t);
            var el = ResolveTargetElement(id, t, out var err, out _,
                anyMatch: true, firstOnly: firstOnly);
            if (err != null)
            {
                var (k, d) = ErrParts(err);
                return k == "TargetNotFound" ? null : d ?? "condition error";
            }
            found = el;
            return null;
        }
        if (s.IfExists != null || s.IfExistsId != null)
        {
            var e = Eval(s.IfExists, s.IfExistsId, out var el,
                existenceOnly: true);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifExists target absent";
        }
        if (s.IfNotExists != null || s.IfNotExistsId != null)
        {
            var e = Eval(s.IfNotExists, s.IfNotExistsId, out var el,
                existenceOnly: true);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el != null) return $"skipped: ifNotExists matched {el.Id}";
        }
        if (s.IfEnabled != null || s.IfEnabledId != null)
        {
            var e = Eval(s.IfEnabled, s.IfEnabledId, out var el);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifEnabled target absent";
            if (Prop(el, "enabled") is not true)
                return $"skipped: {el.Id} not enabled";
        }
        if (s.IfValue != null)
        {
            var c = s.IfValue;
            var e = Eval(c.Target, c.ElementId, out var el);
            if (e != null) { errKind = "Condition"; errDetail = e; return null; }
            if (el == null) return "skipped: ifValue target absent";
            var val = Prop(el, "value")?.ToString() ?? "";
            var name = el.Name ?? "";
            if (c.Contains != null &&
                !val.Contains(c.Contains, StringComparison.OrdinalIgnoreCase) &&
                !name.Contains(c.Contains, StringComparison.OrdinalIgnoreCase))
                return $"skipped: {el.Id} value lacks \"{c.Contains}\"";
            if (c.Exact != null && val != c.Exact && name != c.Exact)
                return $"skipped: {el.Id} value != \"{c.Exact}\"";
        }
        return null;
    }

    /// <summary>launch: resolve + start an app through IAppService
    /// (deterministic Windows registration pipeline — never a shell) and
    /// bind its top-level hwnd for later within:/wait_for scoping.
    /// Idempotent by default: a running instance is reused unless
    /// newInstance.</summary>
    private StepOutcome LaunchOutcome(RunStep s, RunState state,
        CancellationToken ct)
    {
        var spec = StepLaunchSpec(s, out var err);
        if (err != null)
            return new StepOutcome(OutcomeKind.Malformed, false, "launch",
                err, 0);
        LaunchResult r;
        try
        {
            using (PerfTrace.Stage("step.launch"))
                r = _s.Rt.Launch(spec!, ct);
        }
        catch (OperationCanceledException)
        {
            return new StepOutcome(OutcomeKind.Cancelled, false, "launch",
                "cancelled", 0);
        }
        var via = r.Method is { } m ? $"launch.{m}" : "launch";
        if (!r.Success)
        {
            var kind = r.Error switch
            {
                "AmbiguousApplication" => OutcomeKind.AmbiguousApplication,
                "PolicyDenied" => OutcomeKind.PolicyDenied,
                "EmergencyStopped" => OutcomeKind.EmergencyStopped,
                "Timeout" => OutcomeKind.Timeout,
                "Cancelled" => OutcomeKind.Cancelled,
                "TargetNotFound" => OutcomeKind.TargetNotFound,
                "Malformed" => OutcomeKind.Malformed,
                _ => OutcomeKind.Failed,
            };
            var cand = r.Candidates is { Count: > 0 } cs
                ? $" — candidates: {string.Join(", ",
                    cs.Select(c => $"{c.Name} ({c.Method})"))}"
                : "";
            return new StepOutcome(kind, false, via,
                (r.ErrorDetail ?? r.Error ?? "launch failed") + cand, 0);
        }
        if (!string.IsNullOrWhiteSpace(s.As) && r.Hwnd is { } h)
            state.Bindings[s.As.TrimStart('$')] = h.ToString();
        return new StepOutcome(OutcomeKind.Verified, true, via,
            $"{r.ResolvedName} {r.LaunchState} pid={r.Pid}" +
            (r.Hwnd is { } hh
                ? $" hwnd=0x{hh:X} \"{Trunc(r.WindowTitle, 40)}\""
                : "") +
            (s.As != null && r.Hwnd != null
                ? $" → ${s.As.TrimStart('$')}" : ""), 0);
    }

    private sealed record ScanResult(
        StepOutcome Outcome,
        long DurationMs,
        int SubStepsCount,
        int InternalActions,
        string? PostId);

    private async Task<ScanResult> ExecuteScanStep(
        RunStep s, RunState state, ObsContext octx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var asVar = !string.IsNullOrWhiteSpace(s.As) ? s.As.TrimStart('$') : "item";
        var maxItems = Math.Clamp(s.MaxItems ?? 20, 1, 200);
        var maxPages = Math.Clamp(s.MaxPages ?? 0, 0, 20);
        var stopOn = s.StopOn != null ? new HashSet<string>(s.StopOn, StringComparer.OrdinalIgnoreCase) : null;
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var processedCount = 0;
        var subStepsExecuted = 0;
        var internalActions = 0;
        string? lastPostId = null;

        // 1. Resolve container or target root
        UiElement? container = null;
        if (s.Target != null || s.ElementId != null)
        {
            container = ResolveRunTarget(s, state, out var errKind, out var errDetail, out _, out var scanDiag);
            if (container == null)
            {
                return new ScanResult(
                    new StepOutcome(Enum.TryParse<OutcomeKind>(errKind, out var ok) ? ok : OutcomeKind.TargetNotFound,
                        false, "scan", errDetail ?? "scan target container not found", (int)sw.ElapsedMilliseconds, Diagnosis: scanDiag),
                    sw.ElapsedMilliseconds, 0, 0, null);
            }
        }

        var scopeHwnd = container?.Hwnd ?? state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;

        for (var page = 0; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();

            // Fetch candidate elements under container or window
            IReadOnlyList<UiElement> candidates;
            Inbrisk.Core.Role? roleFilter = null;
            if (!string.IsNullOrWhiteSpace(s.Where?.Role) && Enum.TryParse<Inbrisk.Core.Role>(s.Where.Role, true, out var r))
                roleFilter = r;
            else if (container?.Role == Inbrisk.Core.Role.List) roleFilter = Inbrisk.Core.Role.ListItem;
            else if (container?.Role == Inbrisk.Core.Role.Table) roleFilter = Inbrisk.Core.Role.DataItem;
            else if (container?.Role == Inbrisk.Core.Role.Tree) roleFilter = Inbrisk.Core.Role.TreeItem;

            if (container != null)
            {
                var spec = new FindSpec(
                    Hwnd: scopeHwnd,
                    ScopeElementId: container.Id,
                    Role: roleFilter,
                    IncludeOffscreen: true,
                    MaxResults: Math.Max(50, maxItems * 2));
                candidates = _s.Rt.Find(spec);
                if (candidates.Count == 0 && (container.Role is Inbrisk.Core.Role.ListItem or Inbrisk.Core.Role.DataItem or Inbrisk.Core.Role.Button or Inbrisk.Core.Role.Edit))
                {
                    candidates = [container];
                }
            }
            else
            {
                var spec = new FindSpec(
                    Hwnd: scopeHwnd,
                    Role: roleFilter,
                    IncludeOffscreen: true,
                    MaxResults: Math.Max(50, maxItems * 2));
                candidates = _s.Rt.Find(spec);
            }

            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var key = ObservationBuilder.StableKey(item, item.Handle?.Recipe?.AutomationId);
                if (!seenKeys.Add(key)) continue;

                // Match filter
                if (!MatchesItemFilter(item, s.Where)) continue;

                lastPostId = item.Id;

                // Collect properties
                if (s.Collect is { Length: > 0 })
                {
                    var rec = new Dictionary<string, object?>();
                    foreach (var col in s.Collect)
                    {
                        var c = col.ToLowerInvariant();
                        if (c == "id") rec["id"] = item.Id;
                        else if (c == "name") rec["name"] = item.Name;
                        else if (c == "role") rec["role"] = item.Role.ToString();
                        else if (c == "value") rec["value"] = Prop(item, "value");
                        else if (item.Props.TryGetValue(col, out var pv)) rec[col] = pv;
                    }
                    state.Collected.Add(rec);
                }

                // Execute sub-steps if provided
                if (s.Steps is { Length: > 0 } subSteps)
                {
                    state.Bindings[asVar] = item.Id;
                    state.Bindings[asVar + ".id"] = item.Id;
                    state.Bindings[asVar + ".name"] = item.Name ?? "";
                    state.Bindings[asVar + ".role"] = item.Role.ToString();
                    if (Prop(item, "value") is { } vVal)
                        state.Bindings[asVar + ".value"] = vVal.ToString() ?? "";
                    state.LastElementId = item.Id;

                    foreach (var sub in subSteps)
                    {
                        ct.ThrowIfCancellationRequested();
                        var subAction = (sub.Action ?? "").ToLowerInvariant();

                        var effectiveSub = (sub.ElementId == null && sub.Target == null && sub.X == null &&
                            subAction is not ("wait" or "wait_for" or "wait_for_change" or "wait_for_stable" or "key" or "hotkey" or "launch"))
                            ? sub with { ElementId = $"${asVar}" }
                            : sub;

                        effectiveSub = effectiveSub with
                        {
                            ElementId = SubstituteVariables(effectiveSub.ElementId, state),
                            Text = SubstituteVariables(effectiveSub.Text, state),
                            Value = SubstituteVariables(effectiveSub.Value, state),
                            Target = effectiveSub.Target != null ? effectiveSub.Target with
                            {
                                Name = SubstituteVariables(effectiveSub.Target.Name, state),
                                NameContains = SubstituteVariables(effectiveSub.Target.NameContains, state),
                                ElementId = BoundId(effectiveSub.Target.ElementId, state),
                                Within = BoundId(effectiveSub.Target.Within, state),
                                Value = SubstituteVariables(effectiveSub.Target.Value, state),
                                ValueContains = SubstituteVariables(effectiveSub.Target.ValueContains, state),
                            } : null
                        };

                        var isSubPseudo = subAction is "find" or "assert" or "launch";

                        StepOutcome? subOutcome = null;
                        if (isSubPseudo)
                        {
                            subOutcome = PseudoOutcome(effectiveSub, state, ct, out var subPost);
                            if (subPost != null) { lastPostId = subPost; state.LastElementId = subPost; }
                        }
                        else
                        {
                            var built = BuildStepActions(effectiveSub, state, out var subErrKind, out var subErrDetail, out var subPost, out var subDiag);
                            if (built == null)
                            {
                                subOutcome = new StepOutcome(
                                    Enum.TryParse<OutcomeKind>(subErrKind, out var sok) ? sok : OutcomeKind.Malformed,
                                    false, "scan.substep", subErrDetail ?? "failed to resolve sub-step target", 0,
                                    Diagnosis: subDiag);
                            }
                            else
                            {
                                using var subStepCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                if (effectiveSub.Timeout is > 0) subStepCts.CancelAfter(effectiveSub.Timeout.Value);
                                var actx = new ActionContext(subStepCts.Token, null, _s.SessionId, null, null);
                                foreach (var act in built)
                                {
                                    internalActions++;
                                    subOutcome = await Task.Run(() => _s.Resolver.Execute(act, octx, actx), subStepCts.Token);
                                    if (act.ElementId != null) { lastPostId = act.ElementId; state.LastElementId = act.ElementId; }
                                    if (!subOutcome.Success) break;
                                }
                            }
                        }

                        subStepsExecuted++;

                        if (subOutcome?.Success != true)
                        {
                            var errName = subOutcome?.Kind.ToString() ?? "Error";
                            bool shouldStop = stopOn == null
                                || stopOn.Contains("error")
                                || stopOn.Contains(errName)
                                || (errName == "AmbiguousTarget" && stopOn.Contains("ambiguous_target"))
                                || (errName == "UnexpectedModalOpened" && stopOn.Contains("unexpected_dialog"));

                            if (shouldStop)
                            {
                                return new ScanResult(
                                    subOutcome ?? new StepOutcome(OutcomeKind.Failed, false, "scan", "sub-step failed", (int)sw.ElapsedMilliseconds),
                                    sw.ElapsedMilliseconds, subStepsExecuted, internalActions, lastPostId);
                            }
                            break;
                        }
                    }
                }

                processedCount++;
                if (processedCount >= maxItems) break;
            }

            if (processedCount >= maxItems) break;

            if (page < maxPages && container != null)
            {
                var scrollAction = new AgentAction(AgentActionKind.Scroll, ElementId: container.Id, Delta: -120);
                var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                await Task.Run(() => _s.Resolver.Execute(scrollAction, octx, actx), ct);
                internalActions++;
                await Task.Delay(150, ct);
            }
        }

        return new ScanResult(
            new StepOutcome(OutcomeKind.Verified, true, "scan",
                $"scanned {processedCount} matching items, collected {state.Collected.Count} records", (int)sw.ElapsedMilliseconds),
            sw.ElapsedMilliseconds, subStepsExecuted, internalActions, lastPostId);
    }

    private async Task<(bool Healed, string Detail)> TrySelfHeal(
        RunStep s, RunState state, string? errKind, string? errDetail,
        ObsContext octx, CancellationToken ct)
    {
        // 1. AutoScroll: target offscreen
        var isOffscreen = errKind == "TargetOffscreen" || (errDetail?.Contains("OFFSCREEN", StringComparison.OrdinalIgnoreCase) == true);
        if (isOffscreen && s.AutoScroll != false)
        {
            var targetName = s.Target?.Name ?? s.Target?.NameContains;
            var targetRole = s.Target?.Role != null && Enum.TryParse<Inbrisk.Core.Role>(s.Target.Role, true, out var r) ? r : (Inbrisk.Core.Role?)null;
            var offMatches = _s.Rt.Find(new FindSpec(
                Role: targetRole,
                Name: targetName,
                AutomationId: s.Target?.AutomationId,
                IncludeOffscreen: true,
                MaxResults: 5));
            if (offMatches.Count > 0)
            {
                var offEl = offMatches[0];
                var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                // Attempt 1: native ScrollIntoView
                var scrollAction = new AgentAction(AgentActionKind.ScrollIntoView, ElementId: offEl.Id);
                var res = await Task.Run(() => _s.Resolver.Execute(scrollAction, octx, actx), ct);
                if (res.Success)
                {
                    await Task.Delay(150, ct);
                    return (true, $"[auto_scroll: native ScrollIntoView on '{offEl.Name ?? offEl.Id}']");
                }
                // Attempt 2: container/viewport wheel scroll
                var win = _s.Rt.ForegroundWindow();
                int cx = win != null ? win.Bounds.X + win.Bounds.Width / 2 : 500;
                int cy = win != null ? win.Bounds.Y + win.Bounds.Height / 2 : 500;
                int delta = (offEl.Bounds.Y < (win?.Bounds.Y ?? 0)) ? 240 : -240;
                var wheelAction = new AgentAction(AgentActionKind.Scroll, Point: new ImagePoint(cx, cy, 0, 0), Delta: delta);
                var wheelRes = await Task.Run(() => _s.Resolver.Execute(wheelAction, octx, actx), ct);
                if (wheelRes.Success)
                {
                    await Task.Delay(200, ct);
                    return (true, $"[auto_scroll: wheel scroll {delta} towards '{offEl.Name ?? targetName}']");
                }
            }
        }

        // 2. AutoNavigate: target on different screen
        var isDiffScreen = errKind == "TargetOnDifferentScreen" || (errDetail?.Contains("previously seen on screen", StringComparison.OrdinalIgnoreCase) == true);
        if (isDiffScreen && s.AutoNavigate == true)
        {
            var app = _s.Rt.ForegroundWindow()?.ProcessName ?? s.Target?.Process ?? "";
            var curScreen = _s.Memory.GetCurrentScreen(app);
            var targetName = s.Target?.Name ?? s.Target?.NameContains;
            var sug = _s.Memory.SuggestRecovery(targetName, s.Target?.Role, app, curScreen);
            if (sug != null)
            {
                var path = _s.Memory.FindNavigationPath(app, curScreen ?? "", sug.TargetScreen);
                if (path is { Count: > 0 })
                {
                    var actx = new ActionContext(ct, null, _s.SessionId, null, null);
                    var navDetails = new List<string>();
                    bool allOk = true;
                    foreach (var step in path)
                    {
                        var navTarget = new TargetSpec(Name: step.TargetName, Role: step.TargetRole);
                        var navEl = ResolveTargetElement(null, navTarget, out var navErr, out _);
                        if (navEl == null) { allOk = false; break; }
                        var navAction = new AgentAction(AgentActionKind.Click, ElementId: navEl.Id);
                        var navRes = await Task.Run(() => _s.Resolver.Execute(navAction, octx, actx), ct);
                        if (!navRes.Success) { allOk = false; break; }
                        navDetails.Add($"{step.FromScreenId}→{step.ToScreenId} via '{step.ActionDescription}'");
                        await Task.Delay(250, ct);
                    }
                    if (allOk)
                    {
                        await Task.Delay(200, ct);
                        return (true, $"[auto_navigate: {string.Join(", ", navDetails)}]");
                    }
                }
            }
        }

        return (false, "");
    }

    private static bool MatchesItemFilter(UiElement el, ItemFilter? filter)
    {
        if (filter == null) return true;
        if (filter.StartsWith != null && !(el.Name?.StartsWith(filter.StartsWith, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.Contains != null &&
            !(el.Name?.Contains(filter.Contains, StringComparison.OrdinalIgnoreCase) == true ||
              Prop(el, "value")?.ToString()?.Contains(filter.Contains, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.NotContains != null &&
            (el.Name?.Contains(filter.NotContains, StringComparison.OrdinalIgnoreCase) == true ||
             Prop(el, "value")?.ToString()?.Contains(filter.NotContains, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        if (filter.Exact != null &&
            !string.Equals(el.Name, filter.Exact, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Prop(el, "value")?.ToString(), filter.Exact, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.Role != null && !el.Role.ToString().Equals(filter.Role, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.State != null && !(Prop(el, "state")?.ToString()?.Contains(filter.State, StringComparison.OrdinalIgnoreCase) == true))
            return false;
        return true;
    }

    /// <summary>RunStep → LaunchSpec. Friendly `app` is canonical; explicit
    /// executable/path/aumid/uri are the advanced overrides. `text` /
    /// `target.process` remain accepted as the app's name for backwards
    /// compatibility with earlier plans.</summary>
    private static LaunchSpec? StepLaunchSpec(RunStep s, out string? err)
    {
        err = null;
        var app = s.App ?? s.Search ?? s.Target?.Process ?? s.Text ?? s.Value;
        var ids = new[]
        {
            app != null, s.Executable != null, s.Path != null,
            s.Aumid != null, s.Uri != null,
        }.Count(b => b);
        if (ids == 0)
        {
            err = "launch requires app (or executable/path/aumid/uri; " +
                  "text/target.process still accepted as the app name)";
            return null;
        }
        if (ids > 1)
        {
            err = "launch takes exactly one identifier: " +
                  "app|executable|path|aumid|uri";
            return null;
        }
        if (s.Arguments is { Length: > 0 } &&
            (s.Aumid != null || s.Uri != null))
        {
            err = "launch arguments only apply to executable targets";
            return null;
        }
        return new LaunchSpec(app, s.Executable, s.Path, s.Aumid, s.Uri,
            s.Arguments, s.NewInstance ?? false, s.WaitFor ?? "window",
            s.Timeout ?? s.Ms ?? 10000, s.DebugPort);
    }

    /// <summary>wait_for scope: derive the window/process subtree to watch
    /// from the step's target — WITHOUT element scoring (scope is "which
    /// window", never a single element). Null scope → resolver falls back
    /// to the observed window, then UIA root.</summary>
    private (long? Hwnd, int? Pid, string? ScopeElementId, string? Err)
        WaitScope(TargetSpec? t, RunState state)
    {
        if (t == null) return (null, null, null, null);
        var wref = t.Within ?? t.ElementId;
        if (wref != null)
        {
            var bound = BoundId(wref, state);
            if (bound == null) return (null, null, null, $"unbound $ref '{wref}'");
            if (ParseHwnd(bound) is { } bh) return (bh, null, null, null);
            var el = _s.Rt.Parts.Registry.EnsureAlive(bound);
            if (el == null)
                return (null, null, null, $"within reference '{wref}' not found");
            // element binding → its subtree is the narrowest scope; the
            // owner hwnd stays as the fallback if the element goes stale
            return (el.Handle.Recipe.Hwnd ?? el.Hwnd, el.Pid,
                el.Handle.BackendRef, null);
        }
        if ((ParseHwnd(t.Hwnd) ?? ParseHwnd(t.Window)) is { } hw)
            return (hw, null, null, null);
        if (t.Window != null)
        {
            var w = _s.Rt.Windows().FirstOrDefault(w =>
                w.Title.Contains(t.Window, StringComparison.OrdinalIgnoreCase));
            return w != null ? (w.Hwnd, null, null, null)
                : (null, null, null, $"no window titled '{t.Window}'");
        }
        if (t.Process != null)
        {
            var w = _s.Rt.Windows().FirstOrDefault(w =>
                (w.ProcessName ?? "").Contains(t.Process,
                    StringComparison.OrdinalIgnoreCase));
            return w != null ? (null, w.Pid, null, null)
                : (null, null, null, $"no window for process '{t.Process}'");
        }
        return (null, null, null, null);
    }

    private List<AgentAction>? BuildStepActions(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out string? postId)
        => BuildStepActions(s, state, out errKind, out errDetail, out postId, out _);

    /// <summary>Resolve a plan step's target and expand it into canonical
    /// actions. Null + errKind set → step failed before executing.</summary>
    private List<AgentAction>? BuildStepActions(RunStep s, RunState state,
        out string? errKind, out string? errDetail, out string? postId,
        out TargetDiagnosis? diagnosis)
    {
        errKind = null; errDetail = null; postId = null; diagnosis = null;
        var action = (s.Action ?? "").ToLowerInvariant();
        if (action is "find" or "assert" or "checkpoint" or "launch")
            return []; // handled by PseudoOutcome / main loop

        // wait_for watches a SCOPE (window/process subtree), not a scored
        // element — resolving its target through candidate ranking would
        // drown in the window's children and always report AmbiguousTarget
        if (action is "wait_for" or "wait_for_gone")
        {
            var (scopeHwnd, scopePid, scopeEl, scopeErr) = WaitScope(s.Target, state);
            if (scopeErr != null)
            { errKind = "TargetNotFound"; errDetail = scopeErr; return null; }
            var targetQuery = s.Query ?? s.Target?.Name ?? s.Target?.NameContains;
            var targetEl = BoundId(s.ElementId, state) ?? scopeEl;
            return [new AgentAction(AgentActionKind.WaitFor, Query: targetQuery,
                ElementId: targetEl,
                Ms: s.Ms, Hwnd: scopeHwnd, Pid: scopePid,
                ScopeElementId: scopeEl,
                ExpectedState: s.ExpectedState,
                ExpectedValue: s.ExpectedValue,
                Gone: action == "wait_for_gone" || s.Gone,
                Count: s.MinCount,
                StopOnUnexpectedDialog: s.StopOnDialog ?? true)];
        }

        // focus/focus_window name a WINDOW, not an element — a window-shaped
        // target ({process|window} only, no role/name/element fields) must
        // resolve through the window list; element ranking would drown in
        // the window's many child elements and always report AmbiguousTarget
        var windowOnly = s.ElementId == null && s.Target?.ElementId == null &&
            !IsElementTarget(s.Target);
        if (windowOnly && (action is "focus_window" ||
            (action == "focus" && s.Hwnd == null)))
        {
            var wh = ParseHwnd(s.Hwnd) ?? ParseHwnd(s.Target?.Window) ??
                ParseHwnd(s.Target?.Hwnd) ??
                WindowHwndFor(s.Target, out errKind, out errDetail);
            if (wh == null)
            {
                if (action == "focus")
                { errKind = "Malformed"; errDetail = "focus requires elementId, target, or hwnd"; }
                else
                { errKind ??= "TargetNotFound"; errDetail ??= "no window matched the target"; }
                return null;
            }
            return [new AgentAction(AgentActionKind.FocusWindow, Hwnd: wh)];
        }

        var el = ResolveRunTarget(s, state, out errKind, out errDetail, out _, out diagnosis);
        postId = el?.Id;
        // a resolution error (TargetNotFound, AmbiguousTarget, Stale) must
        // surface as itself — never build a target-less action that fails
        // later with a meaningless "requires elementId" resolver error
        if (errKind != null) return null;
        AgentActionKind? Kind(string name) => name switch
        {
            "click" => AgentActionKind.Click,
            "rightclick" or "right_click" => AgentActionKind.RightClick,
            "doubleclick" or "double_click" => AgentActionKind.DoubleClick,
            "invoke" => AgentActionKind.Invoke,
            "toggle" => AgentActionKind.Toggle,
            "select" => AgentActionKind.Select,
            "hover" => AgentActionKind.Hover,
            "set_value" or "setvalue" => AgentActionKind.SetValue,
            "key" => AgentActionKind.Key,
            "hotkey" => AgentActionKind.Hotkey,
            "scroll" => AgentActionKind.Scroll,
            "scroll_into_view" => AgentActionKind.ScrollIntoView,
            "drag" => AgentActionKind.Drag,
            "wait" => AgentActionKind.Wait,
            "wait_for" or "wait_for_gone" => AgentActionKind.WaitFor,
            "wait_for_change" => AgentActionKind.WaitForChange,
            "wait_for_stable" => AgentActionKind.WaitForStable,
            "focus_window" => AgentActionKind.FocusWindow,
            _ => null,
        };

        switch (action)
        {
            case "scroll_into_view":
            {
                CallToolResult? resErr = null;
                var offEl = el ?? ResolveTargetElement(s.ElementId, s.Target, out resErr, "invoke", includeOffscreen: true);
                if (resErr != null)
                {
                    (errKind, errDetail) = ErrParts(resErr);
                    return null;
                }
                if (offEl == null) return null;
                postId = offEl.Id;
                return [new AgentAction(AgentActionKind.ScrollIntoView, ElementId: offEl.Id)];
            }
            case "type":
            {
                var modeN = (s.Mode ?? "insert").ToLowerInvariant();
                var posN = (s.Position ?? (modeN == "append" ? "end" : "current"))
                    .ToLowerInvariant();
                if (modeN is not ("replace" or "append" or "insert") ||
                    posN is not ("current" or "start" or "end"))
                { errKind = "Malformed"; errDetail = "bad mode/position"; return null; }
                if (s.Text == null && s.Value == null)
                { errKind = "Malformed"; errDetail = "type requires text"; return null; }
                return BuildTypeSteps(el, s.Text ?? s.Value!, modeN, posN, s.Submit);
            }
            case "focus":
            {
                // element → UIA SetFocus; window-only target → foreground it
                if (el == null && s.Hwnd == null && s.Target?.Window == null)
                { errKind = "Malformed"; errDetail = "focus requires a target"; return null; }
                if (s.Hwnd != null || el == null || el.Role == Core.Role.Window)
                {
                    var h = ParseHwnd(s.Hwnd) ?? el?.Hwnd
                        ?? WindowHwndFor(s.Target, out errKind, out errDetail);
                    if (h == null)
                    { errKind ??= "TargetNotFound"; errDetail ??= "no window for focus"; return null; }
                    return [new AgentAction(AgentActionKind.FocusWindow, Hwnd: h)];
                }
                return [new AgentAction(AgentActionKind.FocusElement, ElementId: el.Id)];
            }
        }

        var kind = Kind(action);
        if (kind == null)
        { errKind = "Malformed"; errDetail = $"unknown plan action '{s.Action}'"; return null; }
        if (kind.Value == AgentActionKind.FocusWindow)
        {
            var h = ParseHwnd(s.Hwnd) ?? el?.Hwnd
                ?? WindowHwndFor(s.Target, out errKind, out errDetail);
            if (h == null)
            { errKind ??= "Malformed"; errDetail ??= "focus_window requires hwnd or window target"; return null; }
            return [new AgentAction(kind.Value, Hwnd: h)];
        }
        var key = s.Key; var mods = s.Modifiers;
        if (key == null && s.Keys is { } combo)
        {
            var parts = combo.Split(['+', ',', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            key = parts.LastOrDefault();
            mods = parts.Take(parts.Length - 1).ToArray();
        }
        return [new AgentAction(kind.Value,
            ElementId: el?.Id,
            Point: MakePoint(s.FrameId, s.ObservationId, s.X, s.Y),
            To: s.ToX != null && s.ToY != null
                ? new ImagePoint(s.ToX.Value, s.ToY.Value,
                    s.ToFrameId ?? 0, s.ToObservationId ?? 0)
                : null,
            Text: s.Text ?? s.Value,
            Key: key, Modifiers: mods, Count: s.Count,
            Delta: s.Delta, Ms: s.Ms, Query: s.Query)];
    }

    /// <summary>Does the target describe an element (role/name/relationships)
    /// rather than just a window (process/title)?</summary>
    private static bool IsElementTarget(TargetSpec? t)
        => t != null && (t.Role != null || t.Name != null ||
            t.AutomationId != null || t.LabelledBy != null ||
            t.NearText != null || t.Within != null || t.Ancestor != null);

    /// <summary>First window matching a target's window/process filter —
    /// for focus steps that name a window rather than an element.</summary>
    private long? WindowHwndFor(TargetSpec? t, out string? errKind, out string? errDetail)
    {
        errKind = null; errDetail = null;
        if (t == null) return null;
        var hw = ParseHwnd(t.Window);
        if (hw != null) return hw;
        var w = _s.Rt.Windows().FirstOrDefault(w =>
            (t.Window != null && w.Title.Contains(t.Window, StringComparison.OrdinalIgnoreCase)) ||
            (t.Process != null && (w.ProcessName ?? "").Contains(t.Process,
                StringComparison.OrdinalIgnoreCase)));
        return w?.Hwnd;
    }

    private bool BadPoint(AgentAction a, out string? error)
    {
        error = null;
        foreach (var p in new[] { a.Point, a.To }.OfType<ImagePoint>())
            if (p.ObservationId == 0 ||
                !_s.FrameObservations.TryGetValue(p.FrameId, out var obsId) ||
                obsId != p.ObservationId)
            {
                error = "frameId and observationId must identify the same session frame";
                return true;
            }
        return false;
    }

    // ------------------------------------------------------------ run state

    /// <summary>Scope-scoped element snapshot — the baseline a checkpoint or
    /// pause diffs against. Ids come from the shared registry so returned
    /// refs are immediately usable as targets.</summary>
    private List<UiElement> Snapshot(long? hwnd)
    {
        try
        {
            using (PerfTrace.Stage("snapshot.uiaFind"))
                return hwnd != null
                    ? _s.Rt.Find(new FindSpec(Hwnd: hwnd)).ToList() : [];
        }
        catch { return []; }
    }

    private static string Describe(UiElement e)
        => $"[{e.Id}] {e.Role} \"{Trunc(e.Name, 60)}\" " +
           $"bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})";

    /// <summary>Step actions that cannot mutate the UI — a plan made only of
    /// these skips the pre-run baseline snapshot.</summary>
    private static readonly HashSet<string> ReadOnlyStepActions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "find", "assert", "checkpoint", "wait", "wait_for",
            "wait_for_change", "wait_for_stable",
        };

    /// <summary>Events touching <paramref name="hwnd"/> since
    /// <paramref name="since"/>. Element-level events carry no hwnd (controls
    /// return 0) — those count as relevant for any scope.</summary>
    private List<ObservedEvent> RelevantEvents(long? hwnd, DateTimeOffset since)
        => _s.Rt.Parts.EventBuffer.Snapshot(200).Where(e =>
            e.At >= since && (hwnd == null || e.Hwnd is null or 0 ||
            e.Hwnd == hwnd)).ToList();

    /// <summary>Foreground-scoped semantic delta vs the run's last baseline;
    /// refreshes the baseline so consecutive pauses report only new change.
    /// Event-gated: no relevant events → no post snapshot; a deferred
    /// baseline + observed events → event-derived delta (bounded fallback —
    /// the real diff still runs whenever a baseline exists or events fired).</summary>
    private List<string> Delta(RunState state, bool actionsRan, int cap = 30)
    {
        using var _d = PerfTrace.Stage("report.delta");
        var hwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        var since = state.BaselineDeferred ? state.RunStartedAt : state.BaselineAt;
        var fresh = RelevantEvents(hwnd, since);

        if (state.BaselineDeferred)
        {
            if (fresh.Count == 0)
            {
                PerfTrace.Count("delta.eventGated");
                return [];
            }
            var evDelta = fresh.Take(cap)
                .Select(e => $"~ {e.Kind} {Trunc(e.Detail ?? "", 80)}").ToList();
            state.Baseline = Snapshot(hwnd);
            state.BaselineHwnd = hwnd;
            state.BaselineAt = DateTimeOffset.Now;
            state.BaselineDeferred = false;
            PerfTrace.Count("delta.eventDerived");
            return evDelta;
        }
        // baseline exists, window unchanged, nothing executed and nothing
        // observed → the tree is identical to the baseline; skip the
        // redundant post-run snapshot. Providers that under-report events
        // still get a real diff whenever an action ran (bounded fallback).
        if (state.BaselineHwnd == hwnd && fresh.Count == 0 && !actionsRan)
        {
            PerfTrace.Count("delta.eventGated");
            return [];
        }
        var cur = Snapshot(hwnd);
        var delta = new List<string>();
        if (state.BaselineHwnd != hwnd)
            delta.Add($"window: 0x{state.BaselineHwnd ?? 0:X} → 0x{hwnd ?? 0:X}");
        var oldByKey = state.BaselineHwnd == hwnd
            ? state.Baseline.GroupBy(e => ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId))
                            .ToDictionary(g => g.Key, g => g.First())
            : new Dictionary<string, UiElement>();
        var curByKey = cur.GroupBy(e => ObservationBuilder.StableKey(e, e.Handle?.Recipe?.AutomationId))
                          .ToDictionary(g => g.Key, g => g.First());
        foreach (var (k, e) in curByKey)
        {
            if (!oldByKey.TryGetValue(k, out var o))
            { delta.Add($"+ {Describe(e)}"); continue; }
            var ch = new List<string>();
            if (o.Name != e.Name)
                ch.Add($"name \"{Trunc(o.Name, 40)}\"→\"{Trunc(e.Name, 40)}\"");
            foreach (var p in new[] { "value", "state", "focused", "enabled" })
            {
                var b = Prop(o, p)?.ToString();
                var a = Prop(e, p)?.ToString();
                if (a != b) ch.Add($"{p} {Trunc(b, 40)}→{Trunc(a, 40)}");
            }
            if (ch.Count > 0) delta.Add($"~ {Describe(e)}: {string.Join("; ", ch)}");
        }
        foreach (var (k, e) in oldByKey)
            if (!curByKey.ContainsKey(k)) delta.Add($"- {Describe(e)}");
        state.Baseline = cur;
        state.BaselineHwnd = hwnd;
        state.BaselineAt = DateTimeOffset.Now;
        return delta.Take(cap).ToList();
    }

    private List<string> AvailableElements(RunState state, int cap = 15)
    {
        using var _a = PerfTrace.Stage("report.available");
        var hwnd = state.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
        // Delta() refreshes state.Baseline with a fresh snapshot of this hwnd —
        // reuse it instead of paying a second identical subtree enumeration.
        var els = state.BaselineHwnd == hwnd && state.Baseline.Count > 0
            ? state.Baseline
            : Snapshot(hwnd);
        var tags = DialogTags(els);
        return els.Where(e => e.Actions.Count > 0).Take(cap)
            .Select(e => Describe(e) +
                (e.Props.TryGetValue("labelledBy", out var lb) && lb != null
                    ? $" labelledBy=\"{lb}\"" : "") +
                (tags.TryGetValue(e.Id, out var tag) ? $" dialogRole={tag}" : ""))
            .ToList();
    }

    /// <summary>Canonical affordances for the common Windows file dialogs.
    /// Generic semantic normalization over label dictionaries — tags which
    /// control is the filename field, which button saves, etc., so a model
    /// doesn't need to guess from raw UIA names. Returns an empty map when
    /// the element set doesn't look like a file dialog.</summary>
    private static Dictionary<string, string> DialogTags(IReadOnlyList<UiElement> els)
    {
        static bool Named(UiElement e, params string[] pats) =>
            e.Name != null && pats.Any(p =>
                e.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
        var tags = new Dictionary<string, string>();
        var filename = els.Where(e =>
            e.Role is Core.Role.Edit or Core.Role.ComboBox &&
            Named(e, "dosya ad", "file name", "dateiname", "nom du fichier")).ToList();
        var accept = els.Where(e => e.Role == Core.Role.Button &&
            Named(e, "kaydet", "save", "aç", "open", "speichern", "enregistrer")).ToList();
        if (filename.Count == 0 || accept.Count == 0) return tags; // not a file dialog
        foreach (var f in filename) tags[f.Id] = "filename";
        foreach (var b in accept)
            tags[b.Id] = Named(b, "kaydet", "save", "speichern", "enregistrer")
                ? "saveButton" : "openButton";
        foreach (var c in els.Where(e => e.Role == Core.Role.ComboBox &&
            Named(e, "kayıt tür", "file type", "save as type",
                "files of type", "dateityp")))
            tags[c.Id] = "filetype";
        foreach (var c in els.Where(e => e.Role == Core.Role.Button &&
            Named(e, "iptal", "cancel", "abbrechen", "annuler")))
            tags[c.Id] = "cancelButton";
        foreach (var t in els.Where(e => e.Role == Core.Role.Tree &&
            Named(e, "gezinti", "navigation", "navigationsbereich")))
            tags[t.Id] = "location";
        foreach (var t in els.Where(e => e.Role == Core.Role.Toolbar &&
            Named(e, "adres", "address")))
            tags[t.Id] = "addressBar";
        foreach (var t in els.Where(e => e.Role == Core.Role.Edit &&
            Named(e, "arama", "search", "suche")))
            tags[t.Id] = "searchBox";
        return tags;
    }

    /// <summary>Cheap Win32 gate before paying a subtree snapshot for dialog
    /// tagging: file dialogs are the #32770 class; DirectUI task dialogs are
    /// small owned windows. Pure user32 calls — no UIA/COM cost.</summary>
    private static bool LooksLikeDialog(long hwnd)
    {
        var cls = WindowClass(hwnd);
        if (cls == "#32770") return true;
        if (cls is not ("DirectUIHWND" or "NativeHWNDHost")) return false;
        if (GetWindow((IntPtr)hwnd, 4 /*GW_OWNER*/) == IntPtr.Zero) return false;
        return GetWindowRect((IntPtr)hwnd, out var r) &&
            (long)(r.Right - r.Left) * (r.Bottom - r.Top) < 1_200_000;
    }

    /// <summary>Did the foreground window change underneath us? A small
    /// button-bearing window that appeared right after an action is almost
    /// certainly a dialog — report it prominently instead of letting the
    /// plan walk past it. Returns null when nothing changed or the new
    /// window is exempt (an intended focus target).</summary>
    private (long hwnd, string title, bool dialogLikely, List<string> elements)?
        DetectNewWindow(long? beforeHwnd, long? exemptHwnd)
    {
        var fg = _s.Rt.ForegroundWindow();
        if (fg == null || fg.Hwnd == beforeHwnd || fg.Hwnd == exemptHwnd)
            return null;
        var els = Snapshot(fg.Hwnd);
        var area = (long)fg.Bounds.Width * fg.Bounds.Height;
        var buttons = els.Count(e =>
            e.Role is Core.Role.Button or Core.Role.MenuItem);
        // #32770 is THE Windows dialog class — deterministic. Fallback:
        // small owned window with buttons (DirectUI/custom dialogs).
        var cls = WindowClass(fg.Hwnd);
        var owner = GetWindow((IntPtr)fg.Hwnd, 4 /*GW_OWNER*/) != IntPtr.Zero;
        var likely = cls == "#32770" ||
            (els.Count is > 0 and <= 80 && buttons >= 1 &&
             area < 1_200_000 && owner);
        return (fg.Hwnd, fg.Title ?? "", likely,
            els.Where(e => e.Actions.Count > 0).Take(12).Select(Describe).ToList());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT32 lpRect);
    private struct RECT32 { public int Left, Top, Right, Bottom; }

    private static string WindowClass(long hwnd)
    {
        var sb = new StringBuilder(64);
        return GetClassName((IntPtr)hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static Dictionary<string, object?> StepEntry(int i, RunStep s,
        string status, string? method = null, int? ms = null,
        string? detail = null, int? actions = null)
        => new()
        {
            ["step"] = i,
            ["action"] = s.Action,
            ["status"] = status,
            ["method"] = method,
            ["ms"] = ms,
            ["detail"] = detail,
            ["internalActions"] = actions,
        };

    private CallToolResult RunReport(RunState state, string status,
        Stopwatch sw, List<Dictionary<string, object?>> steps,
        int internalActions = 0, int executed = 0, int skipped = 0,
        int pauseStep = -1, string? pauseStatus = null, string? pauseError = null,
        bool includeDelta = false, bool includeAvailable = false,
        UiElement? pre = null, UiElement? post = null,
        TargetDiagnosis? pauseDiagnosis = null)
    {
        string? curScreen = null;
        string app = "";
        try
        {
            var fg = _s?.Rt?.ForegroundWindow();
            app = fg?.ProcessName ?? "";
            curScreen = _s?.Memory?.GetCurrentScreen(app);
        }
        catch { }

        var payload = new Dictionary<string, object?>
        {
            ["runId"] = state.RunId,
            ["status"] = status,
            ["screen"] = curScreen,
            ["executed"] = executed,
            ["skipped"] = skipped,
            ["internalActions"] = internalActions,
            ["steps"] = steps,
            ["durationMs"] = sw.ElapsedMilliseconds,
        };
        if (status is "Completed")
            payload["verificationHint"] = "plan execution completed successfully; follow-up computer_observe is NOT needed";
        if (state.Bindings.Count > 0) payload["bindings"] = state.Bindings;
        if (state.Collected.Count > 0) payload["collected"] = state.Collected;
        if (state.HumanChanges != null) payload["humanChanges"] = state.HumanChanges;
        if (state.IsPausedForHuman)
        {
            payload["isPausedForHuman"] = true;
            payload["humanTakeoverHint"] = "Human user has control of the desktop. Perform manual actions, then call computer_resume_run to continue.";
        }
        if (pauseStep >= 0)
        {
            var pause = new Dictionary<string, object?>
            {
                ["step"] = pauseStep,
                ["status"] = pauseStatus,
                ["error"] = pauseError,
                ["screen"] = curScreen,
            };
            if (pauseDiagnosis != null)
            {
                pause["diagnosis"] = pauseDiagnosis;
            }
            if (pauseStatus != null && Enum.TryParse<OutcomeKind>(pauseStatus, out var okind))
            {
                pause["recoveryHint"] = RecoveryPolicy.For(okind).ToString();
            }
            var sug = _s?.Memory?.SuggestRecovery(state.LastElementId, null, app, curScreen);
            if (sug != null)
            {
                pause["navigationSuggestion"] = new
                {
                    targetScreen = sug.TargetScreen,
                    hint = sug.NavigationHint,
                    path = sug.Path,
                };
            }
            if (includeDelta) pause["observationDelta"] =
                Delta(state, executed + internalActions > 0, state.Slim ? 12 : 30);
            if (includeAvailable) pause["availableElements"] =
                AvailableElements(state, state.Slim ? 8 : 15);
            payload["pause"] = pause;
            payload["resume"] = $"computer_resume_run({{runId:\"{state.RunId}\"}})";
        }
        else if (includeDelta)
        {
            payload["delta"] = Delta(state, executed + internalActions > 0,
                state.Slim ? 12 : 30);
        }
        if (post != null)
        {
            payload["post"] = new Dictionary<string, object?>
            {
                ["id"] = post.Id,
                ["role"] = post.Role.ToString(),
                ["name"] = post.Name,
                ["value"] = post.Props.TryGetValue("value", out var pv)
                    ? TruncEdges(pv?.ToString(), 300) : null,
                ["state"] = post.Props.TryGetValue("state", out var ps) ? ps : null,
                ["focused"] = post.Props.TryGetValue("focused", out var pf) ? pf : null,
                ["bounds"] = $"({post.Bounds.X},{post.Bounds.Y} {post.Bounds.Width}x{post.Bounds.Height})",
            };
            var changes = new Dictionary<string, object?>();
            if (!Equals(Prop(pre, "value"), Prop(post, "value")))
                changes["valueChanged"] = true;
            if (!Equals(Prop(pre, "state"), Prop(post, "state")))
                changes["stateChanged"] = true;
            if (changes.Count > 0) payload["changes"] = changes;
        }
        var failed = status is "Paused" or "EmergencyStopped";
        return new CallToolResult
        {
            IsError = failed,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, J) }],
        };
    }

    private static (string kind, string detail, TargetDiagnosis? diagnosis) ErrPartsWithDiag(CallToolResult err)
    {
        try
        {
            var text = (err.Content[0] as TextContentBlock)?.Text ?? "";
            using var doc = JsonDocument.Parse(text);
            var kind = doc.RootElement.TryGetProperty("error", out var k)
                ? k.GetString() ?? "Failed" : "Failed";
            var detail = doc.RootElement.TryGetProperty("detail", out var d)
                ? d.GetString() ?? "" : "";
            // AmbiguousTarget payloads carry the candidates the model needs
            // to refine — don't strip them when the error is repackaged
            if (doc.RootElement.TryGetProperty("candidates", out var cs) &&
                cs.ValueKind == JsonValueKind.Array)
            {
                var names = cs.EnumerateArray().Take(4).Select(c =>
                    $"{c.GetProperty("id").GetString()} " +
                    $"{c.GetProperty("role").GetString()} " +
                    $"\"{c.GetProperty("name").GetString()}\" " +
                    $"score={c.GetProperty("score").GetInt32()}");
                detail += " — candidates: " + string.Join("; ", names);
            }
            TargetDiagnosis? diag = null;
            if (doc.RootElement.TryGetProperty("diagnosis", out var diagEl))
            {
                try { diag = JsonSerializer.Deserialize<TargetDiagnosis>(diagEl.GetRawText(), J); } catch { }
            }
            return (kind, detail, diag);
        }
        catch { return ("Failed", "", null); }
    }

    private static (string kind, string detail) ErrParts(CallToolResult err)
    {
        var (k, d, _) = ErrPartsWithDiag(err);
        return (k, d);
    }

    [McpServerTool(Name = "computer_key"), Description(
        "Press a single key (enter, tab, escape, arrows, f1-f12...) — goes " +
        "to the FOCUSED control of the foreground window. To press a key in " +
        "a specific window, focus it first (computer_focus_window or a " +
        "computer_run focus step). count repeats the press.")]
    public Task<CallToolResult> Key(
        [Description("key name")] string key,
        [Description("press count")] int count = 1,
        CancellationToken ct = default)
        => Act(new AgentAction(AgentActionKind.Key, Key: key, Count: count), ct);

    [McpServerTool(Name = "computer_hotkey"), Description(
        "Press a key combination — key=\"s\" modifiers=[\"ctrl\"] or the " +
        "shorthand keys=\"ctrl+s\" sends Ctrl+S to the FOREGROUND window. " +
        "Focus the target window first (computer_focus_window) — SendInput " +
        "cannot route keys to a background window.")]
    public Task<CallToolResult> Hotkey(
        [Description("key name")] string? key = null,
        [Description("modifier names: ctrl,shift,alt,win")] string[]? modifiers = null,
        [Description("combo shorthand like \"ctrl+s\" — alternative to key+modifiers")] string? keys = null,
        CancellationToken ct = default)
    {
        if (key == null && keys is { } combo)
        {
            var parts = combo.Split(['+', ',', ' '],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            key = parts.LastOrDefault();
            modifiers ??= parts.Take(parts.Length - 1).ToArray();
        }
        if (key == null)
            return Task.FromResult(Error(OutcomeKind.Malformed,
                "hotkey requires key+modifiers, or keys shorthand like \"ctrl+s\""));
        return Act(new AgentAction(AgentActionKind.Hotkey, Key: key,
            Modifiers: modifiers), ct);
    }

    [McpServerTool(Name = "computer_scroll"), Description(
        "Scroll the mouse wheel — at an elementId, a semantic target, an " +
        "image point, or the current pointer position when nothing is " +
        "given. delta: negative=down, positive=up.")]
    public Task<CallToolResult> Scroll(
        [Description("wheel delta, negative=down")] int delta = -120,
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("frameId")] long? frameId = null,
        [Description("observationId")] long? observationId = null,
        [Description("image-space x")] int? x = null,
        [Description("image-space y")] int? y = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _);
        if (err != null) return Task.FromResult(err);
        return Act(new AgentAction(AgentActionKind.Scroll, ElementId: el?.Id,
            Point: MakePoint(frameId, observationId, x, y), Delta: delta), ct, observe);
    }

    [McpServerTool(Name = "computer_scroll_into_view"), Description(
        "Scroll an offscreen element into view using native UIA ScrollItemPattern. " +
        "Direct and instant — brings virtualized or out-of-viewport elements " +
        "into the viewport without blind wheel scrolling.")]
    public async Task<CallToolResult> ScrollIntoView(
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window?, process?, role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "invoke", includeOffscreen: true);
        if (err != null) return err;
        if (el == null) return Error(OutcomeKind.Malformed, "elementId or target required");
        return await Act(new AgentAction(AgentActionKind.ScrollIntoView, ElementId: el.Id), ct, observe);
    }

    [McpServerTool(Name = "computer_drag"), Description(
        "Drag from an elementId/semantic target/image point to a destination " +
        "image point.")]
    public Task<CallToolResult> Drag(
        [Description("elementId to drag")] string? elementId = null,
        [Description("semantic target for the source element")] TargetSpec? target = null,
        [Description("source frameId")] long? frameId = null,
        [Description("source observationId")] long? observationId = null,
        [Description("source image x")] int? x = null,
        [Description("source image y")] int? y = null,
        [Description("destination frameId")] long? toFrameId = null,
        [Description("destination observationId")] long? toObservationId = null,
        [Description("destination image x")] int toX = 0,
        [Description("destination image y")] int toY = 0,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _);
        if (err != null) return Task.FromResult(err);
        return Act(new AgentAction(AgentActionKind.Drag, ElementId: el?.Id,
            Point: MakePoint(frameId, observationId, x, y),
            To: new ImagePoint(toX, toY, toFrameId ?? 0, toObservationId ?? 0)), ct);
    }

    [McpServerTool(Name = "computer_focus_window"), Description(
        "Bring a window to the foreground — verified against " +
        "GetForegroundWindow. Usually NOT needed: action tools " +
        "auto-foreground their target's window. Use this to choose which " +
        "window receives subsequent computer_key/computer_hotkey input.")]
    public Task<CallToolResult> FocusWindow(
        [Description("window handle (hex or decimal)")] string hwnd,
        CancellationToken ct = default)
    {
        var h = ParseHwnd(hwnd);
        if (h == null) return Task.FromResult(Error(OutcomeKind.Malformed, "bad hwnd"));
        return Act(new AgentAction(AgentActionKind.FocusWindow, Hwnd: h), ct);
    }

    // ------------------------------------------------------------ waits

    [McpServerTool(Name = "computer_wait"), Description(
        "Sleep ms — cancellable; returns immediately on cancel.")]
    public Task<CallToolResult> Wait(
        [Description("milliseconds")] int ms, CancellationToken ct = default)
        => Act(new AgentAction(AgentActionKind.Wait, Ms: ms), ct);

    [McpServerTool(Name = "computer_wait_for"), Description(
        "Wait until an element matching query/target appears in the active window, or until a condition (gone, state, value) is met.")]
    public Task<CallToolResult> WaitFor(
        [Description("element name or query to wait for")] string? query = null,
        [Description("structured target specification")] TargetSpec? target = null,
        [Description("expected element state (e.g. 'selected', 'checked', 'enabled', 'disabled')")] string? expectedState = null,
        [Description("expected element value")] string? expectedValue = null,
        [Description("if true, wait until the element is gone/disappears")] bool gone = false,
        [Description("minimum number of matching elements")] int? minCount = null,
        [Description("abort wait immediately if an unexpected modal dialog or error appears (default true)")] bool stopOnDialog = true,
        [Description("timeout ms (default 5000)")] int ms = 5000,
        [Description("alias of ms — models often write timeoutMs")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        var targetQuery = query ?? target?.Name ?? target?.NameContains;
        return Act(new AgentAction(AgentActionKind.WaitFor,
            Query: targetQuery,
            ElementId: target?.ElementId,
            ScopeElementId: target?.Within,
            ExpectedState: expectedState,
            ExpectedValue: expectedValue,
            Gone: gone,
            Count: minCount,
            StopOnUnexpectedDialog: stopOnDialog,
            Ms: timeoutMs ?? ms), ct);
    }

    [McpServerTool(Name = "computer_wait_for_change"), Description(
        "Wait until the scoped window's pixels or semantics change.")]
    public Task<CallToolResult> WaitForChange(
        [Description("timeout ms")] int ms = 5000,
        CancellationToken ct = default)
        => Act(new AgentAction(AgentActionKind.WaitForChange, Ms: ms), ct);

    [McpServerTool(Name = "computer_wait_for_stable"), Description(
        "Wait until the scoped window has been quiet for its configured period.")]
    public Task<CallToolResult> WaitForStable(
        [Description("timeout ms")] int ms = 10000,
        CancellationToken ct = default)
        => Act(new AgentAction(AgentActionKind.WaitForStable, Ms: ms), ct);

    [McpServerTool(Name = "computer_capabilities"), Description(
        "Machine-readable Inbrisk reference: every tool's contract, the " +
        "computer_run step schema with per-action fields and aliases, the " +
        "target spec (including labelledBy/nearText/within/ancestor), " +
        "coordinate rules, error kinds, and current control state. Call " +
        "once when unsure about syntax — not needed every task. " +
        "detail:\"slim\" returns a compact cheat-sheet (~1.5KB) instead.")]
    public CallToolResult Capabilities(
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        return Text(Slim(detail)
            ? InbriskResources.CapabilitiesJsonSlim(_s.Rt.Activity)
            : InbriskResources.CapabilitiesJson(_s.Rt.Activity));
    }

    // ------------------------------------------------------------ Desktop Shell & UI

    [McpServerTool(Name = "computer_ui_status"), Description(
        "Read-only query of the Inbrisk Desktop Shell presentation state: " +
        "HUD enabled/visible, perimeter indicator, current activity, and active sessions.")]
    public CallToolResult UiStatus(CancellationToken ct = default)
    {
        var settings = UserSettings.Load();
        var hud = _s.Rt.Hud;
        var perimeter = _s.Rt.Indicator;
        var activity = _s.Rt.Activity;

        var status = new
        {
            hudEnabled = settings.HudEnabled,
            perimeterEnabled = settings.PerimeterEnabled,
            hudVisible = hud.State != HudState.Hidden && settings.HudEnabled,
            currentHudState = hud.State.ToString(),
            currentActivity = hud.CurrentText,
            activeMcpSessions = activity.ActiveClients
        };

        return Text(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    [McpServerTool(Name = "computer_ui_set"), Description(
        "Idempotently update user presentation preferences (HUD / perimeter smoke indicator). " +
        "Does not interrupt computer control, emergency hotkeys, or MCP sessions.")]
    public CallToolResult UiSet(
        [Description("Enable or disable the Floating Activity Pill HUD")] bool? hud = null,
        [Description("Enable or disable the perimeter smoke indicator")] bool? perimeter = null,
        CancellationToken ct = default)
    {
        var settings = UserSettings.Load();
        if (hud.HasValue)
        {
            settings.HudEnabled = hud.Value;
            _s.Rt.SetHudEnabled(hud.Value);
        }
        if (perimeter.HasValue)
        {
            settings.PerimeterEnabled = perimeter.Value;
        }
        settings.Save();

        var res = new
        {
            success = true,
            hudEnabled = settings.HudEnabled,
            perimeterEnabled = settings.PerimeterEnabled
        };
        return Text(JsonSerializer.Serialize(res));
    }

    [McpServerTool(Name = "computer_ui_open_settings"), Description(
        "Open the Inbrisk Settings & Tools window for the user. Interactive, non-blocking.")]
    public CallToolResult UiOpenSettings(CancellationToken ct = default)
    {
        try
        {
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";

            Process.Start(new ProcessStartInfo(exe, "settings") { UseShellExecute = true });
            return Text(JsonSerializer.Serialize(new { success = true, opened = true }));
        }
        catch (Exception ex)
        {
            return Error(OutcomeKind.Failed, $"failed to open settings window: {ex.Message}");
        }
    }

    // ------------------------------------------------------------ Application Lifecycle

    [McpServerTool(Name = "computer_app_status"), Description(
        "Read-only status of the Inbrisk SERVER itself (this tool takes no " +
        "target — it does NOT report on user apps like notepad): version, " +
        "active MCP sessions, emergency state, UI processes, install mode. " +
        "For user apps use computer_windows or computer_apps.")]
    public CallToolResult AppStatus(CancellationToken ct = default)
    {
        var status = new
        {
            running = true,
            version = Inbrisk.Setup.InstallLayout.Version,
            mcpSessions = _s.Rt.Activity.ActiveClients,
            emergencyState = _s.Control.State.ToString(),
            uiProcess = "in-process",
            installMode = Inbrisk.Setup.InstallLayout.PathsEqual(Inbrisk.Setup.InstallLayout.ProcessDir, Inbrisk.Setup.InstallLayout.UserInstallDir)
                ? "user"
                : Inbrisk.Setup.InstallLayout.PathsEqual(Inbrisk.Setup.InstallLayout.ProcessDir, Inbrisk.Setup.InstallLayout.MachineInstallDir)
                    ? "machine"
                    : "portable"
        };
        return Text(JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    [McpServerTool(Name = "computer_app_restart"), Description(
        "Restart the Inbrisk SERVER itself — not a user application. " +
        "WARNING: intentionally terminates the current MCP session; the " +
        "MCP host may need to reconnect or respawn Inbrisk.")]
    public CallToolResult AppRestart(CancellationToken ct = default)
    {
        Task.Run(async () =>
        {
            await Task.Delay(250);
            var exe = Inbrisk.Setup.InstallLayout.CanonicalExePath;
            if (!File.Exists(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName ?? "inbrisk.exe";
            Process.Start(new ProcessStartInfo(exe, "mcp") { UseShellExecute = true });
            Environment.Exit(0);
        });

        var res = new
        {
            accepted = true,
            restarting = true,
            message = "This operation intentionally terminates the current MCP session. The MCP host may need to reconnect or respawn Inbrisk."
        };
        return Text(JsonSerializer.Serialize(res));
    }

    [McpServerTool(Name = "computer_app_shutdown"), Description(
        "Gracefully shut down the Inbrisk SERVER and terminate this MCP " +
        "session — this does NOT close user applications. To close a user " +
        "app's window, invoke its Close control or send alt+F4 via " +
        "computer_hotkey while it's focused.")]
    public CallToolResult AppShutdown(CancellationToken ct = default)
    {
        Task.Run(async () =>
        {
            await Task.Delay(250);
            Environment.Exit(0);
        });

        var res = new
        {
            accepted = true,
            shuttingDown = true,
            message = "Inbrisk is shutting down gracefully."
        };
        return Text(JsonSerializer.Serialize(res));
    }

    // ------------------------------------------------------------ screenshot

    [McpServerTool(Name = "computer_screenshot"), Description(
        "Capture an image: desktop|monitor|window|region|element. Returns an " +
        "image block plus frameId/observationId — pass those back when acting " +
        "on image coordinates. EXPENSIVE: every image costs vision tokens — " +
        "do NOT poll screenshots to track progress; wait with " +
        "computer_wait_for or observe with computer_observe instead, and " +
        "screenshot only when pixels are genuinely needed. For legibility " +
        "prefer the tightest region/element crop — hosts downscale large " +
        "images, so a small crop keeps far more readable detail.")]
    public CallToolResult Screenshot(
        [Description("desktop|monitor|window|region|element")] string target = "window",
        [Description("window handle for window target")] string? hwnd = null,
        [Description("region x,y,w,h (desktop space)")] int x = 0, int y = 0, int w = 0, int h = 0,
        [Description("elementId for element target")] string? elementId = null,
        [Description("monitor index")] int monitorIndex = 0,
        CancellationToken ct = default)
    {
        try
        {
            if (_s.Control.ActionToken() == null)
                return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
            ct.ThrowIfCancellationRequested();
            CaptureTarget ct2;
            long? shotHwnd = null;
            switch (target.ToLowerInvariant())
            {
                case "desktop": ct2 = new CaptureTarget.FullDesktop(); break;
                case "monitor":
                    var mons = _s.Rt.Monitors();
                    if (monitorIndex < 0 || monitorIndex >= mons.Count)
                        return Error(OutcomeKind.Malformed, $"monitor index {monitorIndex} out of range");
                    ct2 = new CaptureTarget.Region(mons[monitorIndex].Bounds); break;
                case "region": ct2 = new CaptureTarget.Region(new RectPx(x, y, w, h)); break;
                case "element":
                    var el = elementId != null ? _s.Rt.Parts.Registry.EnsureAlive(elementId) : null;
                    if (el == null) return Error(OutcomeKind.TargetNotFound, $"unknown element '{elementId}'");
                    ct2 = new CaptureTarget.Region(el.Bounds);
                    shotHwnd = el.Hwnd;
                    break;
                default:
                    var wh = ParseHwnd(hwnd) ?? _s.Rt.ForegroundWindow()?.Hwnd;
                    if (wh == null) return Error(OutcomeKind.Malformed, "no hwnd and no active window");
                    ct2 = new CaptureTarget.Window(wh.Value); shotHwnd = wh; break;
            }
            var raw = _s.Rt.CaptureRaw(ct2);
            if ((long)raw.Width * raw.Height > ObservationBudget.Default.MaxScreenshotPixels ||
                raw.ToPng().LongLength > ObservationBudget.Default.MaxScreenshotBytes)
                return Error(OutcomeKind.CaptureUnavailable,
                    "screenshot exceeds the observation image budget; use a smaller region");
            if (_s.Control.ActionToken() == null)
                return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
            var (fref, obsId) = _s.MintFrame(raw, shotHwnd);
            var m = fref.Meta;
            var text = $"frameId={m.FrameId} observationId={obsId} " +
                $"size={m.Width}x{m.Height} source=({m.SourceRect.X},{m.SourceRect.Y} " +
                $"{m.SourceRect.Width}x{m.SourceRect.Height}) hwnd={(m.Hwnd is { } hh ? $"0x{hh:X}" : "desktop")}\n" +
                "image-space coordinates within this frame may be passed back to " +
                "computer_click/scroll/drag together with frameId + observationId";
            // anti-poll nudge — the 3rd+ image inside a minute means the
            // model is screenshot-tracking a load/progress; steer it to
            // cheap waits instead
            var shots = _s.NoteScreenshot();
            if (shots >= 3)
                text += "\nnote: " + shots + " screenshots in " +
                    "the last minute — tracking progress? prefer " +
                    "computer_wait_for/computer_observe; images are the " +
                    "heaviest context item";
            return new CallToolResult
            {
                IsError = false,
                Content =
                [
                    new TextContentBlock { Text = text },
                    ImageContentBlock.FromBytes(m.Png!, "image/png"),
                ],
            };
        }
        catch (OperationCanceledException)
        {
            return Error(OutcomeKind.Cancelled, "request cancelled");
        }
        catch (Exception e)
        {
            return Error(OutcomeKind.CaptureUnavailable, e.Message);
        }
    }

    // ------------------------------------------------------------ plumbing

    /// <summary>Semantic target for mutating tools — an alternative to a
    /// literal elementId. The runtime resolves it internally
    /// (find → best match → re-resolve → act), so a client with a known
    /// target never has to chain find → focus → act calls itself.</summary>
    public sealed record TargetSpec(
        string? ElementId = null,
        string? Window = null,       // title substring or hwnd (hex/decimal)
        string? Hwnd = null,         // explicit window-handle alias of window
        string? Process = null,      // process name substring, e.g. "notepad"
        string? Role = null,         // button/edit/document/...
        string? Name = null,         // case-insensitive substring
        string? AutomationId = null,
        // -------- property selectors (all case-insensitive, AND'd) --------
        string? NameContains = null,     // alias of name — substring
        string? NameNotContains = null,  // reject when name contains this
        string? Value = null,            // exact value-property match
        string? ValueContains = null,    // value-property substring (e.g. "/artist/" in a hyperlink URL)
        string? ValueNotContains = null, // reject when value contains this
        string? ClassName = null,        // exact className-property match
        // -------- relationships (refine ambiguous name matches) --------
        [Description("element's label text — UIA LabeledBy, else a nearby Text element spatially left/above")] string? LabelledBy = null,
        [Description("a Text element containing this must be within ~200px of the target")] string? NearText = null,
        [Description("elementId or hwnd — target bounds must be inside this element/window")] string? Within = null,
        [Description("role name or text an ancestor in the UIA path must match")] string? Ancestor = null)
    {
        public string Summary()
        {
            var parts = new List<string>();
            if (Role != null) parts.Add($"role: {Role}");
            if (Name != null) parts.Add($"name: \"{Name}\"");
            if (NameContains != null) parts.Add($"nameContains: \"{NameContains}\"");
            if (AutomationId != null) parts.Add($"id: #{AutomationId}");
            if (Within != null) parts.Add($"within: ${Within}");
            if (Value != null) parts.Add($"value: \"{Value}\"");
            if (ValueContains != null) parts.Add($"valueContains: \"{ValueContains}\"");
            return parts.Count > 0 ? $"{{{string.Join(", ", parts)}}}" : "{empty target}";
        }

        /// <summary>Unknown JSON fields land here instead of being dropped —
        /// rejected upstream so a typo never silently degrades a target.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>One declarative plan step for computer_run. Bounded UI
    /// primitives and local behaviors (scan/for_each). Conditions skip the
    /// step; they never fail it.</summary>
    public sealed record RunStep(
        [Description("find|scan|for_each|focus|click|rightclick|doubleclick|invoke|toggle|select|hover|set_value|type|key|hotkey|scroll|drag|wait|wait_for|wait_for_change|wait_for_stable|assert|checkpoint|launch|human|human_takeover|pause_for_human")] string? Action = null,
        [Description("semantic target {window,process,role,name,automationId,elementId}")] TargetSpec? Target = null,
        [Description("elementId or \"$name\" bound by an earlier find")] string? ElementId = null,
        [Description("find: bind the result under this name")] string? As = null,
        [Description("deterministic pick when several candidates match: first|last|nth — without it, ambiguity returns AmbiguousTarget")] string? Select = null,
        [Description("0-based index for select:\"nth\"")] int? Index = null,
        [Description("candidate ordering for select: visual (top→bottom then left→right bounds) | tree (UIA enumeration order) | score (relevance, default)")] string? OrderBy = null,
        [Description("type/set_value text")] string? Text = null,
        [Description("set_value value / assert expected exact value")] string? Value = null,
        [Description("type: replace|append|insert")] string? Mode = null,
        [Description("type: current|start|end")] string? Position = null,
        [Description("type: press Enter afterwards")] bool Submit = false,
        [Description("key/hotkey key name")] string? Key = null,
        [Description("hotkey shorthand like \"ctrl+s\" — alternative to key+modifiers")] string? Keys = null,
        [Description("hotkey modifiers: ctrl,shift,alt,win")] string[]? Modifiers = null,
        [Description("key press count")] int? Count = null,
        [Description("scroll wheel delta")] int? Delta = null,
        [Description("wait ms / wait_for* timeout")] int? Ms = null,
        [Description("wait_for element-name query")] string? Query = null,
        [Description("focus_window hwnd (hex/decimal)")] string? Hwnd = null,
        [Description("image point (needs frameId+observationId)")] int? X = null, int? Y = null,
        long? FrameId = null, long? ObservationId = null,
        int? ToX = null, int? ToY = null, long? ToFrameId = null, long? ToObservationId = null,
        [Description("checkpoint reason / assert note")] string? Note = null,
        [Description("human takeover reason or explanation")] string? Reason = null,
        [Description("assert: value-or-name must contain this")] string? Contains = null,
        [Description("assert: value-or-name must NOT contain this")] string? NotContains = null,
        [Description("assert: value-or-name must equal this")] string? Exact = null,
        [Description("assert: state must contain this")] string? State = null,
        [Description("assert: enabled must equal this")] bool? Enabled = null,
        [Description("skip step unless this target exists")] TargetSpec? IfExists = null,
        [Description("skip step unless this elementId/$ref exists")] string? IfExistsId = null,
        [Description("skip step when this target exists")] TargetSpec? IfNotExists = null,
        string? IfNotExistsId = null,
        [Description("skip step unless this target is enabled")] TargetSpec? IfEnabled = null,
        string? IfEnabledId = null,
        [Description("skip step unless target value/name matches")] ValueCheck? IfValue = null,
        [Description("extra attempts on failure (0-19)")] int? Retry = null,
        [Description("ms between retry attempts — default 400, clamped 50..5000. Applies to transient failures only (TargetNotFound, Stale, assert mismatch); Malformed/AmbiguousTarget/PolicyDenied/EmergencyStopped never retry")] int? RetryInterval = null,
        [Description("per-step timeout ms")] int? Timeout = null,
        [Description("launch: friendly app name resolved via Windows app registration (\"Spotify\", \"Notepad\", \"Calculator\")")] string? App = null,
        [Description("launch: search installed apps and open best match — alias of app")] string? Search = null,
        [Description("launch: executable name/path (\"notepad.exe\")")] string? Executable = null,
        [Description("launch: explicit exe path")] string? Path = null,
        [Description("launch: packaged-app AUMID (PackageFamilyName!AppId)")] string? Aumid = null,
        [Description("launch: URI with a registered protocol handler (\"spotify:\")")] string? Uri = null,
        [Description("launch: structured arguments — each element one verbatim argument; never a command line")] string[]? Arguments = null,
        [Description("launch: force a new instance instead of reusing a running window — default false")] bool? NewInstance = null,
        [Description("launch readiness: window (default — usable top-level window+UIA) | process | none")] string? WaitFor = null,
        [Description("launch: optional remote debugging port for Chrome DevTools Protocol / CDP (e.g. 9222)")] int? DebugPort = null,
        [Description("scan/for_each: filter criteria for matching items (startsWith, contains, role, etc.)")] ItemFilter? Where = null,
        [Description("scan/for_each: sub-steps executed for each matching item (binds $item or $as)")] RunStep[]? Steps = null,
        [Description("scan/for_each: maximum items to process (default 20, max 200)")] int? MaxItems = null,
        [Description("scan/for_each: maximum pages/scrolls to paginate for virtualized lists (default 0)")] int? MaxPages = null,
        [Description("scan/for_each: abort conditions, e.g. [\"ambiguous_target\", \"unexpected_dialog\", \"error\"]")] string[]? StopOn = null,
        [Description("scan/for_each: properties to collect from matching items, e.g. [\"name\", \"value\", \"id\"]")] string[]? Collect = null,
        [Description("wait_for: expected element state (e.g. 'selected', 'checked', 'enabled')")] string? ExpectedState = null,
        [Description("wait_for: expected element value")] string? ExpectedValue = null,
        [Description("wait_for: wait until element disappears")] bool Gone = false,
        [Description("wait_for: minimum number of matching elements")] int? MinCount = null,
        [Description("wait_for: stop immediately if an unexpected modal dialog appears (default true)")] bool? StopOnDialog = null,
        [Description("auto-heal offscreen elements: scroll container or invoke native ScrollIntoView when element is offscreen (default true)")] bool? AutoScroll = null,
        [Description("auto-navigate across known screens if target is detected on another screen via screen memory (default false)")] bool? AutoNavigate = null,
        [Description("piggyback scoped observation after this step executes (zero-turn feedback)")] bool? Observe = null,
        [Description("specialist application adapter (e.g. \"media\", \"testapp\")")] string? Adapter = null,
        [Description("adapter command or arguments dictionary")] Dictionary<string, object?>? Args = null)
    {
        /// <summary>Unknown JSON fields land here instead of being dropped —
        /// ValidateStep rejects them so a misspelled field can't fake success.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>ItemFilter for scan/for_each — client-side predicate on candidate items.</summary>
    public sealed record ItemFilter(
        [Description("item name must start with this (case-insensitive)")] string? StartsWith = null,
        [Description("item name or value must contain this")] string? Contains = null,
        [Description("item name or value must NOT contain this")] string? NotContains = null,
        [Description("item name or value must exactly equal this")] string? Exact = null,
        [Description("item role (e.g. ListItem, Button, CheckBox)")] string? Role = null,
        [Description("item state must contain this (e.g. checked, focused)")] string? State = null);

    /// <summary>ifValue condition payload — target plus expected content.</summary>
    public sealed record ValueCheck(
        TargetSpec? Target = null, string? ElementId = null,
        string? Contains = null, string? Exact = null, string? State = null);

    /// <summary>Resolve elementId or a TargetSpec to a live element. The
    /// registry mints ids during Find, so the returned element's Id is
    /// immediately usable by the resolver pipeline. `purpose` ("edit",
    /// "invoke", "toggle", "select") makes candidate ranking action-aware:
    /// an element that can't perform the requested action never beats one
    /// that can, and static labels lose to real controls unless explicitly
    /// requested by role. Ties → AmbiguousTarget, never a blind first pick.</summary>
    /// <summary>Deterministic multi-candidate selection — an explicit
    /// select/orderBy pair is the ONLY sanctioned way to pick among equally
    /// strong matches; without it, ties still produce AmbiguousTarget.</summary>
    private sealed record Selection(string Select, int? Index, string? OrderBy);

    /// <summary>Selector-resolution telemetry wrapper — internal perf data
    /// (find-perf.jsonl), never serialized into tool responses.</summary>
    /// <summary>True when every constraint on the spec is pushed into the
    /// native UIA condition/scope — FindFirst may then answer existence.
    /// Negations, relations (labelledBy/nearText/ancestor) and a window
    /// TITLE are client-side post-filters and still need full enumeration.</summary>
    private static bool PureNativeSpec(TargetSpec t) =>
        t.NameNotContains == null && t.ValueNotContains == null &&
        t.LabelledBy == null && t.NearText == null && t.Ancestor == null &&
        // within scopes the search AND re-checks containment client-side in
        // ApplyRelations — a subtree element can fail that check, so the
        // first native match is not provably the answer
        t.Within == null &&
        (t.Window == null || ParseHwnd(t.Window) != null);

    private UiElement? ResolveTargetElement(string? elementId,
        TargetSpec? target, out CallToolResult? error, out int matchCount,
        string? purpose = null, bool anyMatch = false, Selection? sel = null,
        bool firstOnly = false, bool includeOffscreen = false)
    {
        var sw = Stopwatch.StartNew();
        var el = ResolveTargetElementCore(elementId, target, out error,
            out matchCount, out var nativeCount, out var filteredCount,
            purpose, anyMatch, sel, firstOnly, includeOffscreen);
        UiaPerf.Write(new
        {
            kind = "selector",
            at = DateTimeOffset.Now,
            role = target?.Role,
            name = target?.Name ?? target?.NameContains,
            within = target?.Within,
            process = target?.Process,
            candidatesEnumerated = nativeCount,
            candidatesAfterFilters = filteredCount,
            matchCount,
            selected = el?.Id,
            error = error == null ? null : "see-result",
            totalMs = sw.ElapsedMilliseconds,
        });
        return el;
    }

    private UiElement? ResolveTargetElement(string? elementId,
        TargetSpec? target, out CallToolResult? error,
        string? purpose = null, bool anyMatch = false, Selection? sel = null,
        bool firstOnly = false, bool includeOffscreen = false)
        => ResolveTargetElement(elementId, target, out error, out _, purpose, anyMatch, sel, firstOnly, includeOffscreen);

    private UiElement? ResolveTargetElementCore(string? elementId,
        TargetSpec? target, out CallToolResult? error, out int matchCount,
        out int nativeCount, out int filteredCount,
        string? purpose = null, bool anyMatch = false, Selection? sel = null,
        bool firstOnly = false, bool includeOffscreen = false)
    {
        error = null; matchCount = 0; nativeCount = -1; filteredCount = -1;
        var id = elementId ?? target?.ElementId;
        if (id != null)
        {
            var el = _s.Rt.Parts.Registry.EnsureAlive(id);
            if (el == null)
                error = Error(OutcomeKind.Stale,
                    $"element '{id}' is stale and could not be re-resolved");
            return el;
        }
        if (target == null) return null;
        if (target.Extra is { Count: > 0 } extra)
        {
            error = Error(OutcomeKind.Malformed,
                $"unknown target field(s): {string.Join(", ", extra.Keys)} — " +
                "valid: elementId,window,hwnd,process,role,name,automationId," +
                "nameContains,nameNotContains,value,valueContains,valueNotContains," +
                "className,labelledBy,nearText,within,ancestor");
            return null;
        }

        long? hwnd = ParseHwnd(target.Hwnd) ?? ParseHwnd(target.Window);
        string? title = hwnd == null ? target.Window : null;
        // within → the UIA search itself runs inside the container's subtree
        // (element binding → subtree root, hwnd → window), not a global
        // enumeration + post-filter. nameContains merges into the native
        // substring Name condition (identical semantics); pushable property
        // selectors (valueContains/value/className) shrink the native
        // candidate set; ApplyRelations/ApplyPropFilters below still verify
        // every condition on the reduced set — belt and suspenders.
        var els = FindElements(target.Role, target.Name ?? target.NameContains,
            target.AutomationId, hwnd?.ToString(), target.Process,
            out var ferr, within: target.Within,
            valueContains: target.ValueContains, valueEquals: target.Value,
            className: target.ClassName, firstOnly: firstOnly, includeOffscreen: includeOffscreen);
        nativeCount = els.Count;
        if (ferr != null) { error = ferr; return null; }
        if (title != null)
        {
            var wins = _s.Rt.Windows().Where(w =>
                w.Title.Contains(title, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (wins.Count == 0)
            {
                error = Error(OutcomeKind.TargetNotFound,
                    $"no window titled '{title}'");
                return null;
            }
            var winHwnds = wins.Select(w => w.Hwnd).ToHashSet();
            els = els.Where(e =>
            {
                // e.Hwnd is the control's own hwnd (often a child like a
                // RichEdit); the owning top-level window is the recipe's
                var owner = e.Handle.Recipe.Hwnd ?? e.Hwnd;
                if (owner is { } oh && winHwnds.Contains(oh)) return true;
                // child-hwnd fallback: same process + element geometrically
                // inside a matching top-level window
                var c = e.Center;
                return e.Pid != null && wins.Any(w => w.Pid == e.Pid &&
                    c.X >= w.Bounds.X && c.X < w.Bounds.X + w.Bounds.Width &&
                    c.Y >= w.Bounds.Y && c.Y < w.Bounds.Y + w.Bounds.Height);
            }).ToList();
        }
        var baseCandidates = els.ToList();
        els = ApplyRelations(els, target, out var relErr);
        if (relErr != null) { error = relErr; return null; }
        els = ApplyPropFilters(els, target);
        filteredCount = els.Count;
        if (els.Count == 0)
        {
            var diag = DiagnoseNotFound(target, nativeCount, relErr, baseCandidates);
            var outcome = diag.Reason switch
            {
                "TargetOffscreen" => OutcomeKind.TargetOffscreen,
                "TargetOnDifferentScreen" => OutcomeKind.TargetOnDifferentScreen,
                _ => OutcomeKind.TargetNotFound
            };
            error = Error(outcome, diag.ToString(), sw: null, diagnosis: diag);
            return null;
        }

        var scored = els.Select((e, i) => (el: e, ord: i,
                score: ScoreFor(e, target, purpose)))
            .OrderByDescending(x => x.score).ToList();
        // UIA can surface the same physical control twice (popup menu item
        // + peer, merged backends). Candidates indistinguishable to the model
        // — same role, name and bounds — are ONE candidate, not ambiguity.
        scored = scored.GroupBy(x => (
                x.el.Role, x.el.Name ?? "",
                x.el.Bounds.X / 4, x.el.Bounds.Y / 4,
                x.el.Bounds.Width / 4, x.el.Bounds.Height / 4))
            .Select(g => g.OrderByDescending(x => x.score).ThenBy(x => x.ord).First())
            .OrderByDescending(x => x.score).ThenBy(x => x.ord).ToList();
        matchCount = scored.Count;

        if (sel != null)
        {
            IEnumerable<(UiElement el, int ord, int score)> ordered =
                sel.OrderBy switch
                {
                    // top→bottom, then left→right on the same row — matches
                    // how a human reads the screen; fully deterministic
                    "visual" => scored.OrderBy(x => x.el.Bounds.Y)
                        .ThenBy(x => x.el.Bounds.X)
                        .ThenBy(x => x.el.Bounds.Width)
                        .ThenBy(x => x.el.Bounds.Height)
                        .ThenBy(x => x.el.Name).ThenBy(x => x.el.Id),
                    "tree" => scored.OrderBy(x => x.ord),
                    _ => scored.OrderByDescending(x => x.score).ThenBy(x => x.ord),
                };
            var list = ordered.ToList();
            var idx = sel.Select switch
            {
                "first" => 0,
                "last" => list.Count - 1,
                _ => sel.Index ?? 0, // "nth" (validated) — missing index → 0
            };
            if (idx < 0 || idx >= list.Count)
            {
                error = Error(OutcomeKind.TargetNotFound,
                    $"select \"{sel.Select}\" index {idx} out of range — " +
                    $"{list.Count} candidate(s)");
                return null;
            }
            return list[idx].el;
        }

        var top = scored[0].score;
        var tied = scored.Where(x => x.score == top).ToList();
        if (tied.Count > 1 && !anyMatch)
        {
            error = AmbiguousError(tied, target, purpose);
            return null;
        }
        return tied[0].el;
    }

    private TargetDiagnosis DiagnoseNotFound(TargetSpec target, int nativeCount, CallToolResult? relErr, List<UiElement>? baseCandidates = null)
    {
        var targetDesc = target.Summary();
        var fg = _s?.Rt?.ForegroundWindow();
        // When the caller scoped the target to a process, every diagnosis
        // below must look at that process's window — otherwise we report
        // ambient foreground facts (and close matches) from an unrelated
        // window, which actively misleads the model.
        var procWin = target.Process == null ? null
            : _s?.Rt?.Windows().FirstOrDefault(w =>
                (w.ProcessName ?? "").Contains(target.Process,
                    StringComparison.OrdinalIgnoreCase));
        var scopeDesc = target.Hwnd != null ? $"Window 0x{ParseHwnd(target.Hwnd):X}"
            : target.Window != null ? $"Window titled '{target.Window}'"
            : procWin != null ? $"Process '{target.Process}' window '{procWin.Title}' (0x{procWin.Hwnd:X})"
            : fg != null ? $"Foreground window '{fg.Title}' (0x{fg.Hwnd:X})"
            : "Desktop";
        var scopeWin = procWin ?? fg;

        if (relErr != null)
        {
            var (_, detail, _) = ErrPartsWithDiag(relErr);
            return new TargetDiagnosis(
                Reason: "RelationFilterError",
                Summary: $"Target {targetDesc} could not be resolved due to relation error: {detail}",
                SearchedScope: scopeDesc,
                EliminatedBy: detail,
                SuggestedAction: "verify container reference in 'within' or adjacent label in 'labelledBy'/'nearText'");
        }

        if (nativeCount > 0)
        {
            string? eliminatedBy = null;
            string? suggested = null;
            if (target.Within != null)
            {
                eliminatedBy = $"within: '{target.Within}'";
                suggested = $"check if target is inside container '{target.Within}' or remove 'within' to search the full window";
            }
            else if (target.Value != null || target.ValueContains != null)
            {
                var expectedVal = target.Value ?? target.ValueContains;
                var sampleVal = baseCandidates != null && baseCandidates.Count > 0 ? Val(baseCandidates[0]) : null;
                eliminatedBy = $"value filter: expected '{expectedVal}'" + (sampleVal != null ? $", candidate had '{Trunc(sampleVal, 40)}'" : "");
                suggested = "verify the element's actual value or relax value filter";
            }
            else if (target.ClassName != null)
            {
                var sampleCls = baseCandidates != null && baseCandidates.Count > 0 ? Cls(baseCandidates[0]) : null;
                eliminatedBy = $"className filter: expected '{target.ClassName}'" + (sampleCls != null ? $", candidate had '{sampleCls}'" : "");
                suggested = "verify className or omit className selector";
            }
            else if (target.NearText != null || target.LabelledBy != null)
            {
                eliminatedBy = $"relational proximity: '{target.NearText ?? target.LabelledBy}'";
                suggested = "check spatial positioning of adjacent text";
            }
            else
            {
                eliminatedBy = "post-filters (nameNotContains, valueNotContains)";
                suggested = "relax negative or strict property filters";
            }

            return new TargetDiagnosis(
                Reason: "FilteredByPropertyOrRelation",
                Summary: $"{nativeCount} candidate(s) matched base name/role {targetDesc}, but were eliminated by post-filters ({eliminatedBy})",
                SearchedScope: scopeDesc,
                CandidatesMatchedBase: nativeCount,
                EliminatedBy: eliminatedBy,
                SuggestedAction: suggested);
        }

        // Check if the scoped application is unresponsive
        if (scopeWin?.Pid is { } pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                if (!p.Responding)
                {
                    return new TargetDiagnosis(
                        Reason: "AppNotResponding",
                        Summary: $"Target application '{scopeWin.ProcessName}' (PID {pid}) is not responding to Windows messages",
                        SearchedScope: scopeDesc,
                        SuggestedAction: "wait for application to finish its busy state before retrying");
                }
            }
            catch { }
        }

        // Check if a modal dialog appeared and is blocking the target window
        if (scopeWin != null && (scopeWin.Title.Contains("Dialog", StringComparison.OrdinalIgnoreCase) || LooksLikeDialog(scopeWin.Hwnd)))
        {
            return new TargetDiagnosis(
                Reason: "ModalDialogBlocking",
                Summary: $"Scoped window is modal dialog '{scopeWin.Title}' (0x{scopeWin.Hwnd:X}) which may be blocking the target",
                SearchedScope: scopeDesc,
                SuggestedAction: "dismiss or inspect the modal dialog elements first");
        }

        // Check if element exists in UIA tree but is offscreen
        var targetName = target.Name ?? target.NameContains;
        if (!string.IsNullOrEmpty(targetName) || target.Role != null)
        {
            try
            {
                var offscreenSpec = new FindSpec(
                    Hwnd: target.Hwnd != null ? ParseHwnd(target.Hwnd) : procWin?.Hwnd,
                    Pid: procWin?.Pid,
                    Role: target.Role != null && Enum.TryParse<Inbrisk.Core.Role>(target.Role, true, out var r) ? r : null,
                    Name: targetName,
                    IncludeOffscreen: true,
                    MaxResults: 5);
                var offscreenMatches = _s?.Rt?.Find(offscreenSpec);
                if (offscreenMatches is { Count: > 0 })
                {
                    var firstOff = offscreenMatches[0];
                    var isOff = firstOff.Bounds.IsEmpty || (firstOff.Props.TryGetValue("offscreen", out var v) && v is true or 1);
                    if (isOff)
                    {
                        return new TargetDiagnosis(
                            Reason: "TargetOffscreen",
                            Summary: $"Target {targetDesc} exists in UI tree but is currently OFFSCREEN (bounds: {firstOff.Bounds})",
                            SearchedScope: scopeDesc,
                            SuggestedAction: "scroll the parent container or use scroll_into_view to bring it into the viewport");
                    }
                }
            }
            catch { }
        }

        // Check DesktopStateMemory for navigation suggestions across screens
        var app = fg?.ProcessName ?? "";
        var curScreen = _s?.Memory?.GetCurrentScreen(app);
        var sug = _s?.Memory?.SuggestRecovery(target.Name ?? target.NameContains, target.Role, app, curScreen);
        if (sug != null)
        {
            return new TargetDiagnosis(
                Reason: "TargetOnDifferentScreen",
                Summary: $"Target {targetDesc} not found on active screen '{sug.CurrentScreen}', but was previously seen on screen '{sug.TargetScreen}' ({sug.TimeAgo})",
                SearchedScope: scopeDesc,
                SuggestedAction: sug.NavigationHint);
        }

        // Check for close matches / typos inside the searched scope —
        // the target's own process window when scoped, else foreground.
        var closeMatches = FindCloseMatches(target,
            target.Hwnd != null ? ParseHwnd(target.Hwnd) : procWin?.Hwnd ?? fg?.Hwnd);
        if (closeMatches.Count > 0)
        {
            return new TargetDiagnosis(
                Reason: "CloseMatchesFound",
                Summary: $"No exact match for {targetDesc}, but {closeMatches.Count} similar element(s) were found in {scopeDesc}",
                SearchedScope: scopeDesc,
                SuggestedAction: $"did you mean '{closeMatches[0]}'? Try target: {{ name: \"{closeMatches[0]}\" }} or nameContains",
                CloseMatches: closeMatches);
        }

        return new TargetDiagnosis(
            Reason: "NoElementMatched",
            Summary: $"No element matched target spec {targetDesc}",
            SearchedScope: scopeDesc,
            SuggestedAction: "call computer_observe or computer_find with broader criteria (e.g. role only or partial name)");
    }

    private List<string> FindCloseMatches(TargetSpec target, long? hwnd)
    {
        var targetText = target.Name ?? target.NameContains;
        if (string.IsNullOrWhiteSpace(targetText) || _s?.Rt == null) return [];
        try
        {
            var candidates = _s.Rt.Find(new FindSpec(Hwnd: hwnd, MaxResults: 60));
            var matches = new List<(string name, int score)>();
            foreach (var el in candidates)
            {
                if (string.IsNullOrWhiteSpace(el.Name)) continue;
                var elName = el.Name.Trim();
                var score = 0;
                if (elName.Contains(targetText, StringComparison.OrdinalIgnoreCase))
                    score += 50;
                else if (targetText.Contains(elName, StringComparison.OrdinalIgnoreCase))
                    score += 30;
                else
                {
                    var targetWords = targetText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var elWords = elName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var common = targetWords.Count(tw => elWords.Any(ew => ew.Equals(tw, StringComparison.OrdinalIgnoreCase)));
                    if (common > 0) score += common * 25;
                }
                if (target.Role != null && el.Role.ToString().Equals(target.Role, StringComparison.OrdinalIgnoreCase))
                    score += 20;

                if (score >= 20)
                    matches.Add((elName, score));
            }
            return matches.OrderByDescending(m => m.score)
                .Select(m => m.name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();
        }
        catch { return []; }
    }

    // -------- relationship filters (post name/role match) --------

    private List<UiElement> ApplyRelations(List<UiElement> els, TargetSpec t,
        out CallToolResult? error)
    {
        error = null;
        if (t.Within != null)
        {
            var container = ParseHwnd(t.Within) is { } wh
                ? null // hwnd containment handled below
                : _s.Rt.Parts.Registry.EnsureAlive(t.Within);
            if (ParseHwnd(t.Within) == null && container == null)
            {
                error = Error(OutcomeKind.TargetNotFound,
                    $"within reference '{t.Within}' not found");
                return [];
            }
            els = els.Where(e =>
            {
                if (ParseHwnd(t.Within) is { } hw)
                    return (e.Handle.Recipe.Hwnd ?? e.Hwnd) == hw;
                var c = e.Center;
                var b = container!.Bounds;
                return c.X >= b.X && c.X < b.X + b.Width &&
                       c.Y >= b.Y && c.Y < b.Y + b.Height;
            }).ToList();
        }
        if (t.Ancestor != null)
            els = els.Where(e => e.Handle.Recipe.AncestryPath.Any(st =>
                st.Role.ToString().Equals(t.Ancestor, StringComparison.OrdinalIgnoreCase) ||
                st.Name?.Contains(t.Ancestor, StringComparison.OrdinalIgnoreCase) == true))
                .ToList();
        if (t.LabelledBy != null || t.NearText != null)
        {
            // Text elements per owner-window, fetched once — the spatial
            // fallback when UIA LabeledBy is absent (common on Win32 dialogs)
            var labelsByHwnd = new Dictionary<long, List<UiElement>>();
            List<UiElement> Labels(UiElement e)
            {
                var owner = e.Handle.Recipe.Hwnd ?? e.Hwnd ?? 0;
                if (!labelsByHwnd.TryGetValue(owner, out var l))
                {
                    try
                    {
                        l = _s.Rt.Find(new FindSpec(Hwnd: owner,
                            Role: Core.Role.Text)).ToList();
                    }
                    catch { l = []; }
                    labelsByHwnd[owner] = l;
                }
                return l;
            }
            if (t.LabelledBy != null)
                els = els.Where(e =>
                {
                    var prop = Prop(e, "labelledBy")?.ToString();
                    if (prop != null)
                        return prop.Contains(t.LabelledBy,
                            StringComparison.OrdinalIgnoreCase);
                    return Labels(e).Any(l =>
                        l.Name?.Contains(t.LabelledBy,
                            StringComparison.OrdinalIgnoreCase) == true &&
                        LabelAdjacent(l, e));
                }).ToList();
            if (t.NearText != null)
                els = els.Where(e => Labels(e).Any(l =>
                    l.Name?.Contains(t.NearText,
                        StringComparison.OrdinalIgnoreCase) == true &&
                    Near(l.Bounds, e.Bounds, 200))).ToList();
        }
        return els;
    }

    /// <summary>Server-side property filters — all matching is
    /// case-insensitive; name/nameContains are substring, value/className are
    /// exact, valueContains/valueNotContains are substring. Every condition
    /// is AND'd; nothing is serialized to the model before this runs.</summary>
    private static List<UiElement> ApplyPropFilters(List<UiElement> els, TargetSpec t)
    {
        const StringComparison CI = StringComparison.OrdinalIgnoreCase;
        if (t.NameContains != null)
            els = els.Where(e => e.Name?.Contains(t.NameContains, CI) == true).ToList();
        if (t.NameNotContains != null)
            els = els.Where(e => e.Name?.Contains(t.NameNotContains, CI) != true).ToList();
        if (t.Value != null)
            els = els.Where(e => string.Equals(Val(e), t.Value, CI)).ToList();
        if (t.ValueContains != null)
            els = els.Where(e => Val(e)?.Contains(t.ValueContains, CI) == true).ToList();
        if (t.ValueNotContains != null)
            els = els.Where(e => Val(e)?.Contains(t.ValueNotContains, CI) != true).ToList();
        if (t.ClassName != null)
            els = els.Where(e => string.Equals(Cls(e), t.ClassName, CI)).ToList();
        return els;
    }

    /// <summary>Label sits left of the control (same row band) or directly
    /// above it — the classic Win32 label/edit pairing.</summary>
    private static bool LabelAdjacent(UiElement label, UiElement e)
    {
        var lb = label.Bounds; var eb = e.Bounds;
        var lCenterY = lb.Y + lb.Height / 2;
        var sameRow = lCenterY >= eb.Y - 20 && lCenterY <= eb.Y + eb.Height + 20;
        var left = lb.X + lb.Width <= eb.X + 10 && eb.X - (lb.X + lb.Width) < 160;
        var above = lb.Y + lb.Height <= eb.Y + 10 && eb.Y - (lb.Y + lb.Height) < 90 &&
            lb.X < eb.X + eb.Width && lb.X + lb.Width > eb.X;
        return (sameRow && left) || above;
    }

    private static bool Near(RectPx a, RectPx b, int px)
        => a.X < b.X + b.Width + px && b.X < a.X + a.Width + px &&
           a.Y < b.Y + b.Height + px && b.Y < a.Y + a.Height + px;

    // -------- action-aware scoring --------

    private static readonly Core.Role[] EditRoles =
        [Core.Role.Edit, Core.Role.Document, Core.Role.ComboBox, Core.Role.Spinner];
    private static readonly Core.Role[] InvokeRoles =
        [Core.Role.Button, Core.Role.MenuItem, Core.Role.Hyperlink,
         Core.Role.CheckBox, Core.Role.RadioButton, Core.Role.TabItem,
         Core.Role.ListItem, Core.Role.TreeItem];
    private static readonly Core.Role[] StaticRoles =
        [Core.Role.Text, Core.Role.Image, Core.Role.StatusBar,
         Core.Role.TitleBar, Core.Role.Separator, Core.Role.Group, Core.Role.Pane];

    private static int ScoreFor(UiElement e, TargetSpec t, string? purpose)
    {
        var score = 0;
        var enabled = e.Props.TryGetValue("enabled", out var en) && en is true;
        var offscreen = e.Props.TryGetValue("offscreen", out var os) && os is true;
        if (enabled && !offscreen) score += 100;
        if (t.Role != null &&
            e.Role.ToString().Equals(t.Role, StringComparison.OrdinalIgnoreCase))
            score += 20; // explicitly asked-for role always counts
        if (t.Name != null && e.Name != null)
            score += e.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase) ? 30
                : e.Name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase) ? 15
                : 5;
        else if (t.Name == null) score += 10;
        switch (purpose)
        {
            case "edit":
                if (e.Actions.Contains("setvalue")) score += 50;
                if (EditRoles.Contains(e.Role)) score += 30;
                if (StaticRoles.Contains(e.Role)) score -= 80;
                if (e.Props.TryGetValue("focused", out var f) && f is true) score += 5;
                break;
            case "invoke":
                if (e.Actions.Contains("invoke")) score += 40;
                if (InvokeRoles.Contains(e.Role)) score += 25;
                if (StaticRoles.Contains(e.Role)) score -= 40;
                break;
            case "toggle":
                if (e.Actions.Contains("toggle")) score += 50;
                if (e.Role is Core.Role.CheckBox or Core.Role.Toggle) score += 30;
                break;
            case "select":
                if (e.Actions.Contains("select")) score += 50;
                if (e.Role is Core.Role.ListItem or Core.Role.TreeItem
                    or Core.Role.TabItem or Core.Role.RadioButton) score += 30;
                break;
            default:
                if (e.Actions.Count > 0) score += 10;
                break;
        }
        // deeper UIA path = more specific control (Edit inside ComboBox wins)
        score += Math.Min(e.Handle.Recipe.AncestryPath.Count, 10);
        return score;
    }

    private CallToolResult AmbiguousError(
        List<(UiElement el, int ord, int score)> tied, TargetSpec target,
        string? purpose)
    {
        var payload = new
        {
            error = OutcomeKind.AmbiguousTarget.ToString(),
            detail = $"{tied.Count} strong candidates matched — refine the target " +
                "(add role/name/labelledBy/within/ancestor, or pass an elementId " +
                "from computer_find)",
            candidates = tied.Take(4).Select(x => new
            {
                id = x.el.Id,
                role = x.el.Role.ToString(),
                name = x.el.Name,
                score = x.score,
                bounds = $"({x.el.Bounds.X},{x.el.Bounds.Y} {x.el.Bounds.Width}x{x.el.Bounds.Height})",
                actions = x.el.Actions,
            }).ToList(),
        };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, J) }],
        };
    }

    /// <summary>Single action = a one-step chain.</summary>
    private Task<CallToolResult> Act(AgentAction a, CancellationToken ct, bool observe = false)
        => ActChain([a], a.ElementId, ct, observe);

    /// <summary>Every mutating tool goes through here: gates once (resume
    /// chord, emergency epoch, frame-identity), then runs each step through
    /// the canonical resolver pipeline — resolve/focus/act/verify are
    /// internal steps, not extra MCP calls. Stops the chain on the first
    /// failed step; attaches post-action element state so clients need no
    /// verification observe.</summary>
    private async Task<CallToolResult> ActChain(IReadOnlyList<AgentAction> steps,
        string? postElementId, CancellationToken ct, bool observe = false)
    {
        var sw = Stopwatch.StartNew();
        using var trace = PerfTrace.Begin("tool.act",
            string.Join(",", steps.Select(a => a.Kind)));
        try
        {
            // the local resume chord is human-only — check this before the
            // panic gate so an AI can never replay it through MCP even while
            // stopped
            foreach (var a in steps)
                if (a.Kind is AgentActionKind.Hotkey or AgentActionKind.Key &&
                    _s.Control.IsLocalResumeKey(a.Key,
                        a.Kind == AgentActionKind.Hotkey ? a.Modifiers : null))
                    return Error(OutcomeKind.PolicyDenied,
                        "local resume hotkey is reserved for the human", sw);
            var epoch = _s.Control.ActionToken();
            if (epoch == null)
                return Error(OutcomeKind.EmergencyStopped,
                    StoppedDetail, sw);
            foreach (var point in steps.SelectMany(a =>
                    new[] { a.Point, a.To }).OfType<ImagePoint>())
                if (point.ObservationId == 0 ||
                    !_s.FrameObservations.TryGetValue(point.FrameId, out var obsId) ||
                    obsId != point.ObservationId)
                    return Error(OutcomeKind.StaleFrame,
                        "frameId and observationId must identify the same session frame", sw);
            var scope = _s?.ScopeHwnd ?? _s?.Rt?.ForegroundWindow()?.Hwnd;
            var octx = new ObsContext(
                _s?.LastObservation?.ObservationId ?? 0, scope, _s?.Frames ?? new Dictionary<long, FrameRef>(),
                () => _s?.MonitorFor(scope ?? 0)!, MaxFrameAgeMs: 15_000,
                _s?.ValidObsIds ?? new HashSet<long>());
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                ct, _s?.SessionCts?.Token ?? CancellationToken.None, epoch.Value);
            using var cancelReg = linked.Token.CanBeCanceled
                ? linked.Token.Register(() => { try { _s?.Rt?.PurgePendingWork(); } catch { } })
                : default;
            var actx = new ActionContext(linked.Token, null,
                _s?.SessionId ?? "session", null, null);

            UiElement? pre;
            using (PerfTrace.Stage("verify.targeted"))
                pre = ReadElementState(postElementId);
            var fgBefore = _s?.Rt?.ForegroundWindow()?.Hwnd;

            IInputLease? physicalLease = null;
            var requiresPhysical = steps.Any(a => a.Kind is AgentActionKind.Click or AgentActionKind.RightClick or AgentActionKind.DoubleClick or AgentActionKind.Drag or AgentActionKind.Scroll or AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey or AgentActionKind.FocusWindow or AgentActionKind.FocusElement);
            if (requiresPhysical)
            {
                try
                {
                    var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
                    var leaseTimeout = steps.FirstOrDefault(s => s.Ms.HasValue)?.Ms is int ms ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(5);
                    physicalLease = await arbiter.AcquireAsync(
                        ownerId: _s?.SessionId ?? "session",
                        kind: LeaseKind.PhysicalInput,
                        description: $"ActChain: {string.Join(",", steps.Select(s => s.Kind))}",
                        timeout: leaseTimeout,
                        ct: linked.Token).ConfigureAwait(false);
                }
                catch (TimeoutException tex)
                {
                    return Error(OutcomeKind.ConcurrencyConflict, tex.Message, sw);
                }
            }

            try
            {
                StepOutcome? outcome = null;
                AgentAction last = steps[0];
                foreach (var step in steps)
                {
                    if (linked.Token.IsCancellationRequested)
                    {
                        _s.Rt.PurgePendingWork();
                        if (epoch.Value.IsCancellationRequested)
                            return Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw);
                        return Error(OutcomeKind.Cancelled, "action chain cancelled", sw);
                    }
                    last = step;
                    using (PerfTrace.Stage("step.execute"))
                        outcome = await Task.Run(
                            () => _s.Resolver.Execute(step, octx, actx), linked.Token);
                    if (epoch.Value.IsCancellationRequested)
                        return Error(OutcomeKind.EmergencyStopped,
                            StoppedDetail, sw);
                    if (!outcome.Success) break; // first failure ends the chain
                }
                var o = outcome!;
                _s.PrevOutcome = o;
                _s.NoteElementRef(postElementId ?? last.ElementId);
                (long hwnd, string title, bool dialogLikely, List<string> elements)? nw;
                using (PerfTrace.Stage("step.modalCheck"))
                    nw = DetectNewWindow(fgBefore,
                        steps.LastOrDefault(s => s.Kind == AgentActionKind.FocusWindow)?.Hwnd);
                using (PerfTrace.Stage("verify.targeted"))
                {
                    var post2 = ReadElementState(postElementId);
                    var contextual = InspectContextualPostView(steps, post2 ?? pre);
                    using (PerfTrace.Stage("report.serialize"))
                        return ActionResult(steps, o, sw, pre, post2, nw, contextual, observe);
                }
            }
            finally
            {
                physicalLease?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            return _s.Control.State == ComputerControlState.EmergencyStopped
                ? Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw)
                : Error(OutcomeKind.Cancelled, "request cancelled", sw);
        }
        catch (Exception e)
        {
            return Error(OutcomeKind.Failed, e.Message, sw);
        }
    }

    private sealed record ContextualElementSummary(
        string Id,
        string Role,
        string? Name,
        string? Value,
        IReadOnlyList<string> Actions);

    private sealed record ContextualPostView(
        string Kind,
        string Summary,
        IReadOnlyList<ContextualElementSummary> Elements);

    /// <summary>
    /// For actions that expose new content (tab switch, menu open, expand, scroll),
    /// inspects the newly exposed subtree or container and produces a concise contextual view
    /// so the model does not need a follow-up computer_observe call.
    /// </summary>
    private ContextualPostView? InspectContextualPostView(IReadOnlyList<AgentAction> steps, UiElement? post)
    {
        if (steps.Count == 0 || post == null) return null;
        var last = steps[^1];

        try
        {
            var scopeHwnd = post.Hwnd ?? _s.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
            if (scopeHwnd == null) return null;

            // 1. TabItem / Tab / Select
            if (post.Role is Inbrisk.Core.Role.TabItem or Inbrisk.Core.Role.Tab || last.Kind == AgentActionKind.Select)
            {
                var items = _s.Rt.Find(new FindSpec(Hwnd: scopeHwnd, MaxResults: 16));
                List<ContextualElementSummary> interactive = items
                    .Where(e => e.Id != post.Id && (e.Actions.Count > 0 || e.Role is Inbrisk.Core.Role.Button or Inbrisk.Core.Role.Edit or Inbrisk.Core.Role.ListItem or Inbrisk.Core.Role.TabItem or Inbrisk.Core.Role.MenuItem or Inbrisk.Core.Role.CheckBox or Inbrisk.Core.Role.RadioButton))
                    .Take(12)
                    .Select(e => new ContextualElementSummary(e.Id, e.Role.ToString(), e.Name, e.Props.GetValueOrDefault("value")?.ToString(), e.Actions))
                    .ToList();

                if (interactive.Count > 0)
                    return new ContextualPostView("tab_content", $"Controls exposed after selecting tab '{post.Name}'", interactive);
            }

            // 2. Menu / ComboBox
            if (post.Role is Inbrisk.Core.Role.ComboBox or Inbrisk.Core.Role.MenuItem || post.Props.GetValueOrDefault("expandCollapseState")?.ToString() == "Expanded")
            {
                var menuItems = _s.Rt.Find(new FindSpec(Hwnd: scopeHwnd, Role: Inbrisk.Core.Role.MenuItem, MaxResults: 12));
                if (menuItems.Count == 0)
                    menuItems = _s.Rt.Find(new FindSpec(Hwnd: scopeHwnd, Role: Inbrisk.Core.Role.ListItem, MaxResults: 12));

                List<ContextualElementSummary> summaries = menuItems
                    .Select(e => new ContextualElementSummary(e.Id, e.Role.ToString(), e.Name, null, e.Actions))
                    .ToList();

                if (summaries.Count > 0)
                    return new ContextualPostView("menu_items", "Opened menu or dropdown options", summaries);
            }

            // 3. Scroll
            if (last.Kind == AgentActionKind.Scroll)
            {
                var items = _s.Rt.Find(new FindSpec(Hwnd: scopeHwnd, Role: Inbrisk.Core.Role.ListItem, MaxResults: 12));
                List<ContextualElementSummary> summaries = items
                    .Select(e => new ContextualElementSummary(e.Id, e.Role.ToString(), e.Name, null, e.Actions))
                    .ToList();

                if (summaries.Count > 0)
                    return new ContextualPostView("scrolled_content", "Visible items after scroll", summaries);
            }
        }
        catch { }

        return null;
    }

    /// <summary>Fresh post-action snapshot of the target element — re-resolves
    /// through the backend so the caller sees real state, not registry cache.</summary>
    private UiElement? ReadElementState(string? elementId)
    {
        if (elementId == null) return null;
        try
        {
            var parts = _s.Rt.Parts;
            var registered = parts.Registry.Get(elementId);
            if (registered == null) return null;
            var el = parts.Backends.FirstOrDefault(b =>
                b.Id == registered.Handle.Backend)?.ReResolve(registered.Handle);
            if (el != null)
            {
                el = el with { Id = elementId }; // keep caller-facing id
                parts.Registry.Register([el]);
            }
            return el ?? registered;
        }
        catch { return null; }
    }

    private CallToolResult ActionResult(IReadOnlyList<AgentAction> steps,
        StepOutcome o, Stopwatch sw, UiElement? pre, UiElement? post,
        (long hwnd, string title, bool dialogLikely, List<string> elements)? newWindow = null,
        ContextualPostView? contextualView = null,
        bool observe = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["action"] = steps.Count == 1
                ? steps[0].Summary()
                : string.Join(" → ", steps.Select(s => s.Summary())),
            ["status"] = o.Kind.ToString(),
            ["success"] = o.Success,
            ["method"] = o.Method,
            ["detail"] = o.Detail,
            ["durationMs"] = o.DurationMs,
            ["session"] = _s.SessionId,
        };
        string? verificationHint = o.Kind switch
        {
            OutcomeKind.Verified => "outcome is verified with concrete evidence; follow-up computer_observe is NOT needed",
            OutcomeKind.ObservedChange => "a window event was observed, but the target element's exact semantic outcome was not independently verified; call computer_find/computer_observe if confirmation is needed",
            OutcomeKind.Unverified => "action was dispatched to OS/UIA, but no verifiable state change was observed; re-observe if confirmation is needed",
            _ => null
        };
        if (verificationHint != null)
            payload["verificationHint"] = verificationHint;
        if (o.Evidence != null)
            payload["evidence"] = new
            {
                method = o.Evidence.Method,
                expected = TruncEdges(o.Evidence.Expected?.ToString(), 240),
                actual = TruncEdges(o.Evidence.Actual?.ToString(), 240),
                detail = o.Evidence.Detail,
            };
        if (o.Diagnosis != null)
            payload["diagnosis"] = o.Diagnosis;
        if (post != null)
        {
            payload["post"] = new Dictionary<string, object?>
            {
                ["id"] = post.Id,
                ["role"] = post.Role.ToString(),
                ["name"] = post.Name,
                ["value"] = post.Props.TryGetValue("value", out var pv)
                    ? TruncEdges(pv?.ToString(), 300) : null,
                ["state"] = post.Props.TryGetValue("state", out var ps) ? ps : null,
                ["focused"] = post.Props.TryGetValue("focused", out var pf) ? pf : null,
                ["bounds"] = $"({post.Bounds.X},{post.Bounds.Y} {post.Bounds.Width}x{post.Bounds.Height})",
            };
            var changes = new Dictionary<string, object?>();
            if (!Equals(Prop(pre, "value"), Prop(post, "value")))
                changes["valueChanged"] = true;
            if (!Equals(Prop(pre, "state"), Prop(post, "state")))
                changes["stateChanged"] = true;
            if (changes.Count > 0) payload["changes"] = changes;
        }
        if (newWindow is { } w)
            payload["newWindow"] = new Dictionary<string, object?>
            {
                ["hwnd"] = $"0x{w.hwnd:X}",
                ["title"] = w.title,
                ["unexpectedModalOpened"] = w.dialogLikely,
                ["hint"] = w.dialogLikely
                    ? "a dialog appeared — use its elements below before continuing"
                    : null,
                ["elements"] = w.elements,
            };
        if (contextualView != null)
            payload["contextualView"] = new Dictionary<string, object?>
            {
                ["type"] = contextualView.Kind,
                ["summary"] = contextualView.Summary,
                ["elements"] = contextualView.Elements.Select(e => new Dictionary<string, object?>
                {
                    ["id"] = e.Id,
                    ["role"] = e.Role,
                    ["name"] = e.Name,
                    ["value"] = e.Value,
                    ["actions"] = e.Actions,
                }).ToList(),
            };
        if (observe)
        {
            try
            {
                var scopeHwnd = post?.Hwnd ?? _s.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;
                var obs = _s.Observe(scopeHwnd, ObservationBudget.Default, VisualAttachPolicy.Never);
                var oObs = obs.Observation;
                payload["observation"] = new Dictionary<string, object?>
                {
                    ["observationId"] = oObs.ObservationId,
                    ["activeWindow"] = oObs.ActiveWindow != null ? new
                    {
                        hwnd = $"0x{oObs.ActiveWindow.Hwnd:X}",
                        title = oObs.ActiveWindow.Title,
                        process = oObs.ActiveWindow.Process,
                    } : null,
                    ["elements"] = oObs.Elements.Take(40).Select(e => new
                    {
                        id = e.Id,
                        role = e.Role,
                        name = e.Name,
                        value = e.Value,
                        state = e.State,
                        actions = e.Actions,
                    }).ToList(),
                    ["delta"] = oObs.Delta != null && !oObs.Delta.IsEmpty ? new
                    {
                        added = oObs.Delta.Added.Select(e => e.Name ?? e.Role).Take(10).ToList(),
                        removed = oObs.Delta.Removed.Select(e => e.Name ?? e.Role).Take(10).ToList(),
                        changed = oObs.Delta.Changed.Select(e => e.Name ?? e.Role).Take(10).ToList(),
                    } : null,
                };
            }
            catch { }
        }
        var json = JsonSerializer.Serialize(payload, J);
        return new CallToolResult
        {
            IsError = !o.Success,
            Content = [new TextContentBlock { Text = json }],
        };
    }

    private static object? Prop(UiElement? e, string key)
        => e != null && e.Props.TryGetValue(key, out var v) ? v : null;

    private static string? Val(UiElement? e) => Prop(e, "value")?.ToString();
    private static string? Cls(UiElement? e) => Prop(e, "className")?.ToString();

    private static string? Trunc(string? s, int n)
        => s != null && s.Length > n ? s[..n] + "…" : s;

    /// <summary>Head+tail truncation — appended/inserted text lands at the
    /// end, so long values keep both edges for readback verification.</summary>
    private static string? TruncEdges(string? s, int n)
        => s != null && s.Length > n
            ? s[..(n / 2)] + "…" + s[^(n - n / 2)..]
            : s;

    private static CallToolResult Error(OutcomeKind kind, string detail,
        Stopwatch? sw = null, TargetDiagnosis? diagnosis = null) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(
            diagnosis != null
                ? (object)new { error = kind.ToString(), detail, diagnosis, durationMs = sw?.ElapsedMilliseconds ?? 0 }
                : new { error = kind.ToString(), detail, durationMs = sw?.ElapsedMilliseconds ?? 0 }, J) }],
    };

    private static CallToolResult Text(string s) => new()
        { IsError = false, Content = [new TextContentBlock { Text = s }] };

    private static CallToolResult Json(object obj) =>
        Text(JsonSerializer.Serialize(obj, J));

    private static ImagePoint? MakePoint(long? frameId, long? obsId, int? x, int? y)
        => frameId is { } f && x is { } px && y is { } py
            ? new ImagePoint(px, py, f, obsId ?? 0)
            : null;

    private static long? ParseHwnd(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], System.Globalization.NumberStyles.HexNumber,
                null, out var h) ? h : null;
        return long.TryParse(s, out var h2) ? h2 : null;
    }
}
