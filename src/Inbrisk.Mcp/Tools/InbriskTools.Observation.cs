using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Apps;
using Inbrisk.Platform.Windows.Topology;
using Inbrisk.Platform.Windows.Uia;
using Inbrisk.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

public sealed partial class InbriskTools
{
    // ------------------------------------------------------------ perception

    [McpServerTool(Name = "computer_observe"), Description(
        "Semantic desktop snapshot — exploration and recovery tool. " +
        "Do NOT call it after actions with verificationHint indicating success " +
        "(action results already carry post-state + changes). Only call computer_observe " +
        "for initial screen discovery, unexpected dialogs, or when action status is " +
        "ObservedChange or Unverified and concrete confirmation is needed. Returns active " +
        "window, elements with ids/actions/labelledBy, recent events, delta vs previous " +
        "observation, and optionally frames (mode=visual|both).")]
    public async Task<CallToolResult> Observe(
        [Description("auto|semantic|visual|both — auto picks pixels only when semantics are thin")] string? mode = null,
        [Description("window handle to scope the snapshot (hex or decimal)")] string? hwnd = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("max elements listed — default 60 (slim) / all (full)")] int? maxElements = null,
        [Description("return only delta vs base snapshot instead of full element tree")] bool? deltaOnly = null,
        [Description("snapshot ID to compute delta against (defaults to previous observation)")] long? baseSnapshotId = null,
        [Description("cap attached image width in px — wider frames are downscaled; frameId coordinate mapping stays correct")] int? maxWidth = null,
        [Description("draw numbered marks on attached frames at clickable element centers; mark i = the i-th element in the printed list")] bool? marks = null,
        [Description("collapse passive UIA nodes (unnamed, non-actionable panes/groups/images) — default on for slim, off for full")] bool? prune = null,
        [Description("append an OCR word overlay for the observed window: ocr:<hwnd>:<idx> ids usable as elementIds in find/click — for UIA-less windows (games, canvas apps)")] bool? ocr = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.ObserveAsync(mode, hwnd, detail, maxElements, ct).ConfigureAwait(false);
        }

        if (BadDetail(detail) is { } bd) return bd;
        if (_s.PrevOutcome?.Kind == OutcomeKind.Verified)
        {
            _s.Telemetry.IncPostVerifiedObservation();
            PerfTrace.Count("postVerifiedObservation");
        }
        if (_s.LastLaunchedHwnd != 0 && (DateTimeOffset.UtcNow - _s.LastLaunchTimestamp).TotalSeconds < 5)
        {
            _s.Telemetry.IncLaunchFollowupDiscovery();
            PerfTrace.Count("launchFollowupDiscovery");
        }

        var normArgs = $"{mode}|{hwnd}|{detail}|{maxElements}|{deltaOnly}|{baseSnapshotId}|{maxWidth}|{marks}|{prune}|{ocr}";
        var scopeH = ParseHwnd(hwnd) ?? _s.ScopeHwnd ?? 0;
        if (RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode, autoScreenshot: false, hasCropOrRegion: false))
        {
            if (_s.Deduplicator.TryDeduplicateRead("computer_observe", normArgs, scopeH, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedObs)
            {
                if (RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode, autoScreenshot: false, hasCropOrRegion: false, cachedObs))
                    return cachedObs;
            }
        }

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
            built = await _s.ObserveAsync(hint,
                ObservationBudget.Default with { PrunePassive = prune ?? Slim(detail) },
                policy, baseSnapshotId, ct,
                maxWidth, marks == true).ConfigureAwait(false);
        if (_s.Control.State == ComputerControlState.EmergencyStopped)
            return Text($"controlState: EmergencyStopped\nemergencyHotkey: {_s.Control.PanicHotkey}");
        var o = built.Observation;

        var slim = Slim(detail);
        var sb = new StringBuilder(UntrustedHeader("uia") + "\n");
        if (!slim || _s.Control.State != ComputerControlState.Active || _s.Control.InterventionMode != HumanInterventionMode.Off)
        {
            sb.AppendLine($"controlState: {_s.Control.State}");
            sb.AppendLine($"humanInterventionMode: {_s.Control.InterventionMode}");
            sb.AppendLine($"pausedByHumanIntervention: {_s.Control.PausedByHumanIntervention.ToString().ToLowerInvariant()}");
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
        {
            var blocker = _s.Rt.WindowService.GetActiveBlockingPopup(o.ActiveWindow.Hwnd);
            if (blocker != null && blocker.Hwnd != o.ActiveWindow.Hwnd)
            {
                sb.AppendLine($"⚠️ MODAL / SYSTEM POPUP ACTIVE: 0x{blocker.Hwnd:X} \"{blocker.Title}\" ({blocker.ProcessName})");
                sb.AppendLine($"   The desktop is currently BLOCKED by this popup/flyout! Dismiss it first (e.g. press Escape via computer_hotkey 'Escape' or click its controls) before interacting with background windows.");
            }
            // orientation header is always emitted — clients (and the
            // end-to-end tests) key on the active window's identity, and a
            // modal blocker line above still reads correctly with it present
            sb.AppendLine($"active window: 0x{o.ActiveWindow.Hwnd:X} \"{o.ActiveWindow.Title}\" ({o.ActiveWindow.Process})");
        }
        else sb.AppendLine("active window: (none)");

        var sysDialogs = _s.Rt.WindowService.FindSystemDialogs();
        if (sysDialogs.Count > 0)
        {
            sb.AppendLine($"systemDialogs: {sysDialogs.Count} — active popup, warning, or error windows:");
            foreach (var d in sysDialogs)
                sb.AppendLine($"  0x{d.Hwnd:X} \"{d.Title}\" {d.ProcessName} " +
                    (d.OwnerHwnd.HasValue ? $"[owner=0x{d.OwnerHwnd.Value:X}]" : "[top-level dialog]"));
        }

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
            sb.AppendLine($"elements ({Math.Min(o.Elements.Count, elCap)} of {o.Elements.Count} shown" +
                (built.Prune is { PassiveDropped: > 0 } pp
                    ? $", {pp.PassiveDropped} passive hidden" : "") + "):");
            foreach (var e in o.Elements.Take(elCap))
            {
                var dis = e.State?.Contains("disabled", StringComparison.OrdinalIgnoreCase) == true ? " [DISABLED]" : "";
                sb.AppendLine(slim
                    ? "  " + SlimObsEl(e)
                    : $"  [{e.Id}] {e.Role}{dis}" +
                        (e.Name != null ? $" \"{e.Name}\"" : "") +
                        (e.Value != null ? $" value=\"{e.Value}\"" : "") +
                        (e.State != null ? $" state={e.State}" : "") +
                        (e.Actions.Count > 0 ? $" actions=[{string.Join(",", e.Actions)}]" : "") +
                        $" bounds=({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})");
            }
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
        // OCR word overlay — the same word list find/click's ocr:<hwnd>:<idx>
        // fallback resolves against (shared per-hwnd cache, ~2s). Screen-
        // supplied text → same untrusted-provenance marking as the header.
        if (ocr == true)
        {
            var ocrHwnd = hint ?? _s.ScopeHwnd ?? o.ActiveWindow?.Hwnd ?? 0;
            sb.AppendLine(UntrustedHeader("ocr"));
            if (ocrHwnd == 0)
            {
                sb.AppendLine("ocr words: (none — no window to OCR)");
            }
            else if (!_s.Rt.OcrAvailable)
            {
                sb.AppendLine("ocr words: (OCR engine unavailable)");
            }
            else
            {
                var words = _s.Rt.OcrWindow(ocrHwnd, forceRefresh: false);
                var wordCap = slim ? 20 : 60;
                sb.AppendLine($"ocr words ({Math.Min(words.Count, wordCap)} of {words.Count} shown; ocr:<hwnd>:<idx> ids are clickable targets):");
                for (var wi = 0; wi < Math.Min(words.Count, wordCap); wi++)
                {
                    var wb = words[wi].Bounds;
                    sb.AppendLine($"  ocr:0x{ocrHwnd:X}:{wi} \"{TruncEdges(words[wi].Text, 80)}\" ({wb.X},{wb.Y} {wb.Width}x{wb.Height})");
                }
                if (words.Count > wordCap)
                    sb.AppendLine($"  …{words.Count - wordCap} more — ocr:<hwnd>:<idx> ids index the full list; narrow the window or use computer_find");
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
        var finalResult = new CallToolResult { Content = content, IsError = false };
        if (RequestDeduplicator.IsSafeForReadDeduplication("computer_observe", mode, autoScreenshot: false, hasCropOrRegion: false, finalResult, o.Frames.Count))
            _s.Deduplicator.RecordRead("computer_observe", normArgs, scopeH, _s.Rt.MutationVersion, finalResult);
        return finalResult;
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
        "role:\"document\"} → the Notepad text area. ocr:true scans the " +
        "window's rendered text when UIA finds nothing (null = auto-fallback " +
        "on zero matches); OCR hits print as id ocr:<hwnd>:<n> role text " +
        "and are clickable via elementId.")]
    public async Task<CallToolResult> Find(
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
        [Description("batch list of search queries to execute in one turn (max 16)")] FindQuery[]? queries = null,
        [Description("collapse passive UIA nodes (unnamed, non-actionable containers) in the printed list — default on for slim, off for full; skipped when a role filter is given")] bool? prune = null,
        [Description("OCR text scan of the target window: null = auto (runs only when the UIA search yields zero matches), true = always run and merge hits into the results, false = never")] bool? ocr = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        if (_s.PrevOutcome?.Kind == OutcomeKind.Verified)
        {
            _s.Telemetry.IncPostVerifiedObservation();
            PerfTrace.Count("postVerifiedObservation");
        }
        if (_s.LastLaunchedHwnd != 0 && (DateTimeOffset.UtcNow - _s.LastLaunchTimestamp).TotalSeconds < 5)
        {
            _s.Telemetry.IncLaunchFollowupDiscovery();
            PerfTrace.Count("launchFollowupDiscovery");
        }

        var scopeH = ParseHwnd(hwnd) ?? _s.ScopeHwnd ?? 0;

        if (queries != null)
        {
            if (role != null || name != null || automationId != null || hwnd != null || process != null || enabled != null || nameNotContains != null || value != null || valueContains != null || className != null || within != null || query != null || target != null || ocr != null)
                return Error(OutcomeKind.Malformed, "InvalidArgument: use either legacy single-query fields or queries[], not both (ocr is single-query only)");
            if (queries.Length == 0)
                return Error(OutcomeKind.Malformed, "queries array requires at least 1 query");
            if (queries.Length > 16)
                return Error(OutcomeKind.Malformed, "queries array exceeds maximum limit of 16");

            var qNorm = $"batch_find:{queries.Length}:" + string.Join(";", queries.Select(q => q.Summary()));
            if (_s.Deduplicator.TryDeduplicateRead("computer_find", qNorm, scopeH, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedBatch)
                return cachedBatch;

            using var bTrace = PerfTrace.Begin("tool", "computer_find.batch");
            ct.ThrowIfCancellationRequested();
            using var bCancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;

            var results = new BatchFindItemResult[queries.Length];
            using var sem = new SemaphoreSlim(Math.Min(8, Environment.ProcessorCount));
            var tasks = new Task[queries.Length];
            for (var i = 0; i < queries.Length; i++)
            {
                var idx = i;
                var q = queries[i];
                tasks[i] = Task.Run(async () =>
                {
                    await sem.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var qRole = q.Role ?? role;
                        var qName = q.Name;
                        var qAutoId = q.AutomationId;
                        var qHwnd = q.Hwnd ?? hwnd;
                        var qProc = q.Process ?? process;
                        var qWithin = q.Within ?? within;
                        var qVal = q.Value;
                        var qClass = q.ClassName ?? className;

                        var (els, err) = await FindElementsAsync(qRole, qName, qAutoId, qHwnd, qProc,
                            within: qWithin, valueEquals: qVal,
                            className: qClass, enabled: enabled == true ? true : null, ct: ct).ConfigureAwait(false);

                        if (err != null)
                        {
                            results[idx] = new BatchFindItemResult(idx, "error", Error: ExtractErrorDetail(err));
                            return;
                        }

                        if (!string.IsNullOrWhiteSpace(qName))
                            els = els.Where(e => e.Name?.Contains(qName, StringComparison.OrdinalIgnoreCase) == true).ToList();
                        els = ApplyPropFilters(els, new TargetSpec(NameNotContains: nameNotContains, Value: qVal, ValueContains: valueContains, ClassName: qClass));
                        if (enabled == true)
                            els = els.Where(e => e.Props.TryGetValue("enabled", out var en) && en is true).ToList();

                        var cap = limit ?? (Slim(detail) ? 20 : 60);
                        var count = els.Count;
                        var matches = els.Take(cap).Select(e => new BatchElementMatch(
                            e.Id,
                            e.Role.ToString(),
                            e.Name ?? "",
                            e.Hwnd != null ? $"0x{e.Hwnd:X}" : null,
                            e.Pid,
                            $"({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})",
                            Prop(e, "enabled")?.ToString() == "false" ? false : null,
                            e.Props.TryGetValue("value", out var v) && v != null ? TruncEdges(v.ToString(), 160) : null
                        )).ToList();

                        results[idx] = new BatchFindItemResult(idx, "ok", Count: count, Truncated: count > cap ? count - cap : null, Elements: matches.Count > 0 ? matches : null);
                    }
                    catch (OperationCanceledException)
                    {
                        results[idx] = new BatchFindItemResult(idx, "cancelled", Error: "query cancelled");
                    }
                    catch (Exception ex)
                    {
                        results[idx] = new BatchFindItemResult(idx, "error", Error: ex.Message);
                    }
                    finally
                    {
                        sem.Release();
                    }
                }, ct);
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }

            var okCount = results.Count(r => r?.Status == "ok");
            var failCount = results.Length - okCount;
            var overallStatus = okCount == results.Length ? "ok" : (okCount > 0 ? "partial" : "error");
            var resObj = new
            {
                status = overallStatus,
                total = results.Length,
                successful = okCount,
                failed = failCount,
                results = results,
                provenance = Provenance("uia")
            };
            var bResult = Text(JsonSerializer.Serialize(resObj, J));
            _s.Deduplicator.RecordRead("computer_find", qNorm, scopeH, _s.Rt.MutationVersion, bResult);
            return bResult;
        }

        // OCR-CONTRACT: until TargetSpec.OcrText/Ocr/OcrLang land, those
        // fields arrive via JsonExtensionData — include them in the dedup key.
        var targetExtraKey = target?.Extra is { Count: > 0 } te
            ? string.Join(",", te.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        var normArgs = $"{role}|{name}|{automationId}|{hwnd}|{process}|{enabled}|{nameNotContains}|{value}|{valueContains}|{className}|{within}|{limit}|{detail}|{query}|{prune}|{ocr}|{targetExtraKey}";
        if (_s.Deduplicator.TryDeduplicateRead("computer_find", normArgs, scopeH, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedFind)
            return cachedFind;

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
            (els, err) = await FindElementsAsync(role, name, automationId, hwnd, process,
                within: within, valueContains: valueContains,
                valueEquals: value, className: className,
                enabled: enabled == true ? true : null, ct: ct).ConfigureAwait(false);
        if (err != null) return err;
        if (!string.IsNullOrWhiteSpace(name))
            els = els.Where(e => e.Name?.Contains(name, StringComparison.OrdinalIgnoreCase) == true).ToList();
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
        // ---- OCR text fallback ------------------------------------------
        // `ocr` semantics: null = auto (run only when the filtered UIA
        // search above yielded zero matches), true = always scan and merge
        // hits into the printed results, false = never.
        // OCR-CONTRACT: the resolver-side OCR path
        // (ActionResolver.ResolveOcr(TargetSpec, CancellationToken) ->
        // OcrHit? — {point, confidence, matchedText}) and the
        // TargetSpec.OcrText/Ocr/OcrLang fields are being added in parallel.
        // Until they merge, those fields arrive via TargetSpec.Extra
        // (JsonExtensionData) and this drives the runtime's OcrService
        // directly (Rt.CaptureRaw + Rt.Ocr — the same engine ResolveOcr
        // will wrap). Swap to _s.Resolver.ResolveOcr(spec, ct) once it lands.
        string? ocrText = null; bool? ocrTarget = null; string? ocrLang = null;
        if (target?.Extra is { Count: > 0 } ocrExtra)
            foreach (var kv in ocrExtra)
            {
                if (kv.Key.Equals("ocrText", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.ValueKind == JsonValueKind.String)
                    ocrText = kv.Value.GetString();
                else if (kv.Key.Equals("ocr", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ocrTarget = kv.Value.GetBoolean();
                else if (kv.Key.Equals("ocrLang", StringComparison.OrdinalIgnoreCase)
                    && kv.Value.ValueKind == JsonValueKind.String)
                    ocrLang = kv.Value.GetString();
            }
        var ocrMode = ocr ?? ocrTarget; // flat arg wins over target:{ocr:…}
        var wantOcr = ocrMode == true || (ocrMode == null && els.Count == 0);
        var ocrQuery = ocrText ?? name;
        List<UiElement>? ocrEls = null;
        string? ocrNote = null;
        var ocrWords = 0;
        if (wantOcr)
        {
            // scope: explicit hwnd/window title → within container → the
            // process' best window → session scope → foreground window.
            long? ocrHwnd = ParseHwnd(hwnd);
            if (ocrHwnd == null && !string.IsNullOrWhiteSpace(hwnd))
            {
                try { ocrHwnd = _s.Rt.ResolveWindow(hwnd); }
                catch { /* title ambiguous/not-found → fall through */ }
            }
            if (ocrHwnd == null && within is { } wref)
            {
                if (ParseHwnd(wref) is { } wh)
                    ocrHwnd = wh;
                else
                    try
                    {
                        var c = _s.Rt.Parts.Registry.EnsureAlive(wref);
                        ocrHwnd = c?.Handle.Recipe.Hwnd ?? c?.Hwnd;
                    }
                    catch { /* stale within → other scopes */ }
            }
            ocrHwnd ??= process != null
                ? FindBestWindowForProcess(process)?.Hwnd : null;
            ocrHwnd ??= _s.ScopeHwnd ?? _s.Rt.ForegroundWindow()?.Hwnd;

            if (ocrHwnd is not { } oHw)
                ocrNote = "skipped — no target window (pass hwnd/process or focus a window)";
            else if (!_s.Rt.OcrAvailable)
                ocrNote = $"skipped — OCR engine unavailable (lang={_s.Rt.OcrLanguage})";
            else
            {
                var oSw = Stopwatch.StartNew();
                IReadOnlyList<TextSpan> spans = [];
                try
                {
                    var frame = await Task.Run(
                        () => _s.Rt.CaptureRaw(new CaptureTarget.Window(oHw)),
                        ct).ConfigureAwait(false);
                    spans = await Task.Run(() => _s.Rt.Ocr(frame), ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception oex)
                {
                    ocrNote = $"capture/recognize failed: {oex.Message}";
                }
                ocrWords = spans.Count;
                if (ocrNote == null)
                {
                    if (ocrLang != null && !string.Equals(ocrLang,
                            _s.Rt.OcrLanguage, StringComparison.OrdinalIgnoreCase))
                        ocrNote = $"ocrLang \"{ocrLang}\" requested; engine is " +
                            $"fixed to {_s.Rt.OcrLanguage}";
                    // OCR emits words — a multi-word query matches on any of
                    // its tokens so "Render Pipeline" still finds "Render".
                    var tokens = (ocrQuery ?? "").Split(' ',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries);
                    var hits = spans.Where(sp => tokens.Length == 0 ||
                        tokens.Any(t => sp.Text.Contains(t,
                            StringComparison.OrdinalIgnoreCase))).ToList();
                    // drop words that merely restate a UIA match (same
                    // coverage + text) — OCR adds what UIA cannot see
                    if (els.Count > 0)
                        hits = hits.Where(sp => !els.Any(e =>
                            SceneMerger.Covers(e.Bounds, sp.Bounds) &&
                            (e.Name?.Contains(sp.Text,
                                StringComparison.OrdinalIgnoreCase) == true ||
                             (e.Name is { Length: > 0 } en && sp.Text.Contains(
                                 en, StringComparison.OrdinalIgnoreCase)))))
                            .ToList();
                    var oPid = _s.Rt.Window(oHw)?.Pid;
                    // synthetic elements: id ocr:<hwnd>:<idx>, role text,
                    // [click] action, registered so elementId works in
                    // computer_click (Ocr backend → coordinate click).
                    ocrEls = hits.Select((sp, i) => new UiElement(
                        $"ocr:{oHw:X}:{i}", BackendId.Ocr, Inbrisk.Core.Role.Text, sp.Text,
                        sp.Bounds, ["click"],
                        new Dictionary<string, object?>
                        {
                            ["source"] = "ocr",
                            ["confidence"] = sp.Confidence,
                        },
                        new ElementHandle(BackendId.Ocr, sp.Text,
                            new ReResolveRecipe(oPid, oHw, null, Inbrisk.Core.Role.Text,
                                sp.Text, null, [], sp.Bounds)),
                        oPid, oHw, sp.Confidence)).ToList();
                    if (ocrEls.Count > 0)
                        _s.Rt.Parts.Registry.Register(ocrEls);
                }
                UiaPerf.Write(new
                {
                    kind = "ocr.find", at = DateTimeOffset.Now,
                    traceId = PerfTrace.CurrentId,
                    ms = oSw.ElapsedMilliseconds,
                    hwnd = $"0x{oHw:X}", query = ocrQuery,
                    words = ocrWords, hits = ocrEls?.Count ?? 0,
                });
            }
        }

        var sb = new StringBuilder(UntrustedHeader(
            ocrEls is { Count: > 0 } ? (els.Count > 0 ? "uia+ocr" : "ocr")
            : "uia") + "\n");
        var slim = Slim(detail);
        var cap = limit ?? (slim ? 20 : 60);
        if (els.Count == 0 && ocrEls is not { Count: > 0 })
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
            if (wantOcr)
                sb.AppendLine($"  ocr: {ocrNote ?? $"{ocrWords} word(s) scanned, 0 matching hits" +
                    (ocrQuery != null ? $" (query \"{ocrQuery}\")" : "")}");
            var missResult = Text(sb.ToString());
            _s.Deduplicator.RecordRead("computer_find", normArgs, scopeH, _s.Rt.MutationVersion, missResult);
            return missResult;
        }
        // Passive pruning for unfiltered dumps — when the caller asked for a
        // specific role, every match is intentional, so leave the list alone.
        var effPrune = (prune ?? slim) && role == null;
        var shown = effPrune
            ? els.Where(e => !ObservationBuilder.IsPassiveNoise(e)).ToList()
            : els;
        if (effPrune && shown.Count < els.Count)
            UiaPerf.Write(new
            {
                kind = "uia.find.prune", at = DateTimeOffset.Now,
                traceId = PerfTrace.CurrentId,
                matched = els.Count, shown = shown.Count,
                dropped = els.Count - shown.Count, cap,
            });
        // OCR hits are appended after UIA pruning — a word is never passive
        // noise — and share the same printed shape + cap.
        var printed = ocrEls is { Count: > 0 } oc
            ? shown.Concat(oc).ToList() : shown;
        sb.AppendLine($"found {els.Count + (ocrEls?.Count ?? 0)} element(s)" +
            (ocrEls is { Count: > 0 } oc2 ? $" ({oc2.Count} via ocr)" : "") +
            (shown.Count < els.Count
                ? $" ({els.Count - shown.Count} passive hidden — prune:false to show)" : "") +
            (printed.Count > cap ? $" — showing {cap}:" : ":"));
        foreach (var e in printed.Take(cap))
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
        if (printed.Count > cap)
            sb.AppendLine($"  …{printed.Count - cap} more — refine target or pass limit");
        if (wantOcr && ocrEls is not { Count: > 0 })
            sb.AppendLine($"  ocr: {ocrNote ?? $"{ocrWords} word(s) scanned, 0 matching hits"}");
        var finalResult = Text(sb.ToString());
        _s.Deduplicator.RecordRead("computer_find", normArgs, scopeH, _s.Rt.MutationVersion, finalResult);
        return finalResult;
    }

    /// <summary>Shared deterministic find — used by computer_find and by
    /// semantic-target resolution inside action tools. `within` narrows the
    /// UIA search itself to the container's subtree (elementId → that
    /// element's subtree, hwnd → that window); pushable property selectors
    /// (value/valueContains/className/enabled) go into the native UIA
    /// condition so providers never enumerate non-matching subtrees.</summary>
    private async Task<(List<UiElement> Elements, CallToolResult? Error)> FindElementsAsync(string? role, string? name,
        string? automationId, string? hwnd, string? process,
        string? within = null,
        string? valueContains = null, string? valueEquals = null,
        string? className = null, bool? enabled = null,
        bool firstOnly = false, bool includeOffscreen = false,
        CancellationToken ct = default)
    {
        Core.Role? r = role != null && Enum.TryParse<Core.Role>(role, true,
            out var rr) ? rr : null;
        if (role != null && r == null)
        {
            return ([], Error(OutcomeKind.Malformed, $"unknown role '{role}'"));
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
                    return ([], Error(OutcomeKind.TargetNotFound,
                        $"within reference '{wref}' not found"));
                }
                scopeId = container.Handle.BackendRef;
                scopeHwnd = container.Handle.Recipe.Hwnd ?? container.Hwnd;
            }
        }
        int? pid = null;
        if (process != null)
        {
            var w = FindBestWindowForProcess(process);
            if (w == null)
            {
                return ([], Error(OutcomeKind.TargetNotFound,
                    $"no window for process '{process}'"));
            }
            pid = w.Pid;
        }
        // An explicit hwnd (arg or hwnd-shaped within) that names no live
        // window is a hard error — letting it reach UIA either throws a raw
        // COM exception or silently reports "0 elements".
        var explicitFindHwnd = ParseHwnd(hwnd) ?? (within != null ? ParseHwnd(within) : null);
        if (explicitFindHwnd is { } efh && !WindowExists(efh))
        {
            return ([], Error(OutcomeKind.TargetNotFound,
                $"window not found — invalid hwnd '{hwnd ?? within}' (no live window with that handle)"));
        }
        var spec = new FindSpec(Hwnd: scopeHwnd ?? ParseHwnd(hwnd), Pid: pid,
            Role: r, Name: name, AutomationId: automationId,
            ScopeElementId: scopeId, ValueContains: valueContains,
            ValueEquals: valueEquals, ClassName: className, Enabled: enabled,
            FirstOnly: firstOnly, IncludeOffscreen: includeOffscreen);
        var fKey = new FindCacheKey(
            ScopeHwnd: scopeHwnd,
            Hwnd: ParseHwnd(hwnd),
            Pid: pid,
            Role: r,
            Name: name,
            AutomationId: automationId,
            ScopeElementId: scopeId,
            ValueContains: valueContains,
            ValueEquals: valueEquals,
            ClassName: className,
            Enabled: enabled,
            FirstOnly: firstOnly,
            IncludeOffscreen: includeOffscreen);

        var beforeHits = _s.FindCache.Telemetry.HitCount;
        var result = await _s.FindCache.GetOrComputeAsync(fKey, async innerCt =>
        {
            using (PerfTrace.Stage("find:cache=miss"))
            {
                var res = await _s.Rt.FindAsync(spec, innerCt).ConfigureAwait(false);
                return res.ToList();
            }
        }, ct).ConfigureAwait(false);

        if (_s.FindCache.Telemetry.HitCount > beforeHits)
        {
            using (PerfTrace.Stage("find:cache=hit")) { }
            PerfTrace.Count("find:cache=hit");
        }
        else
        {
            PerfTrace.Count("find:cache=miss");
        }

        return (result.ToList(), null);
    }

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
            var w = FindBestWindowForProcess(process);
            if (w == null)
            {
                error = Error(OutcomeKind.TargetNotFound,
                    $"no window for process '{process}'");
                return [];
            }
            pid = w.Pid;
        }
        var explicitHwnd = ParseHwnd(hwnd) ?? (within != null ? ParseHwnd(within) : null);
        if (explicitHwnd is { } eh && !WindowExists(eh))
        {
            error = Error(OutcomeKind.TargetNotFound,
                $"window not found — invalid hwnd '{hwnd ?? within}' (no live window with that handle)");
            return [];
        }
        var spec = new FindSpec(Hwnd: scopeHwnd ?? ParseHwnd(hwnd), Pid: pid,
            Role: r, Name: name, AutomationId: automationId,
            ScopeElementId: scopeId, ValueContains: valueContains,
            ValueEquals: valueEquals, ClassName: className, Enabled: enabled,
            FirstOnly: firstOnly, IncludeOffscreen: includeOffscreen);
        var fKey = new FindCacheKey(
            ScopeHwnd: scopeHwnd,
            Hwnd: ParseHwnd(hwnd),
            Pid: pid,
            Role: r,
            Name: name,
            AutomationId: automationId,
            ScopeElementId: scopeId,
            ValueContains: valueContains,
            ValueEquals: valueEquals,
            ClassName: className,
            Enabled: enabled,
            FirstOnly: firstOnly,
            IncludeOffscreen: includeOffscreen);

        var result = _s.FindCache.GetOrCompute(fKey, () => _s.Rt.Find(spec).ToList());
        return result.ToList();
    }

    [McpServerTool(Name = "computer_inspect"), Description(
        "Deep view of one window's element tree (with dialogRole " +
        "annotations for file dialogs) or one element's full property set. " +
        "Pass relational=true to group list/table rows with their child actions and labels. " +
        "Accepts hwnd, elementId, or target object {hwnd, elementId}. " +
        "Automatically detects and surfaces blocking modal popups and error dialogs.")]
    public async Task<CallToolResult> Inspect(
        [Description("window handle to inspect (hex or decimal); default = active")] string? hwnd = null,
        [Description("elementId for single-element detail")] string? elementId = null,
        [Description("target specification: accepts {hwnd: '...'}, {elementId: '...'}, or string")] JsonElement? target = null,
        [Description("max elements to list — default 40 (slim) / 80 (full)")] int? maxElements = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("group rows/items relationally with their child actions and labels — ideal for lists/tables")] bool relational = false,
        [Description("batch list of window handles to inspect in one call (max 16)")] string[]? hwnds = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.InspectAsync(hwnd, elementId, detail, maxElements, ct).ConfigureAwait(false);
        }

        if (target.HasValue)
        {
            if (target.Value.ValueKind == JsonValueKind.String)
            {
                var ts = target.Value.GetString();
                if (!string.IsNullOrWhiteSpace(ts))
                {
                    if (ts.StartsWith("uia_", StringComparison.OrdinalIgnoreCase))
                        elementId ??= ts;
                    else
                        hwnd ??= ts;
                }
            }
            else if (target.Value.ValueKind == JsonValueKind.Object)
            {
                if (target.Value.TryGetProperty("hwnd", out var jh) && jh.GetString() is { } jhs)
                    hwnd ??= jhs;
                if (target.Value.TryGetProperty("elementId", out var je) && je.GetString() is { } jes)
                    elementId ??= jes;
            }
        }

        if (BadDetail(detail) is { } bd) return bd;
        if (_s.PrevOutcome?.Kind == OutcomeKind.Verified)
        {
            _s.Telemetry.IncPostVerifiedObservation();
            PerfTrace.Count("postVerifiedObservation");
        }
        if (_s.LastLaunchedHwnd != 0 && (DateTimeOffset.UtcNow - _s.LastLaunchTimestamp).TotalSeconds < 5)
        {
            _s.Telemetry.IncLaunchFollowupDiscovery();
            PerfTrace.Count("launchFollowupDiscovery");
        }
        if (_s.PrevOutcome?.Kind == OutcomeKind.ObservedChange)
        {
            _s.Telemetry.IncObservedChangeFollowup();
            PerfTrace.Count("observedChangeFollowup");
        }

        if (hwnds != null)
        {
            if (hwnd != null || elementId != null || target.HasValue)
                return Error(OutcomeKind.Malformed, "InvalidArgument: use either hwnd or hwnds[], not both");
            if (hwnds.Length == 0)
                return Error(OutcomeKind.Malformed, "hwnds array requires at least 1 window handle");
            if (hwnds.Length > 16)
                return Error(OutcomeKind.Malformed, "hwnds array exceeds maximum limit of 16");

            var hNorm = $"batch_inspect:{hwnds.Length}:" + string.Join(";", hwnds);
            if (_s.Deduplicator.TryDeduplicateRead("computer_inspect", hNorm, 0, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedBatchInsp)
                return cachedBatchInsp;

            using var bTrace = PerfTrace.Begin("tool", "computer_inspect.batch");
            ct.ThrowIfCancellationRequested();
            using var bCancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;

            var inspResults = new BatchInspectItemResult[hwnds.Length];
            using var semInsp = new SemaphoreSlim(Math.Min(8, Environment.ProcessorCount));
            var inspTasks = new Task[hwnds.Length];
            var slimInsp = Slim(detail);
            var capInsp = maxElements ?? (slimInsp ? 40 : 80);

            for (var i = 0; i < hwnds.Length; i++)
            {
                var idx = i;
                var rawHwnd = hwnds[i];
                inspTasks[i] = Task.Run(async () =>
                {
                    await semInsp.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        var parsed = ParseHwnd(rawHwnd);
                        if (parsed == null)
                        {
                            inspResults[idx] = new BatchInspectItemResult(idx, rawHwnd, "error", Error: $"Invalid window handle '{rawHwnd}'");
                            return;
                        }
                        if (!WindowExists(parsed.Value))
                        {
                            inspResults[idx] = new BatchInspectItemResult(idx, rawHwnd, "error", Error: $"window not found — invalid hwnd '{rawHwnd}' (no live window with that handle)");
                            return;
                        }

                        var rootH = WindowService.GetRootHwnd(parsed.Value);
                        var win = _s.Rt.Window(rootH);
                        var app = win?.ProcessName ?? "";
                        var title = win?.Title ?? "";

                        var els = await _s.Rt.InspectAsync(rootH, ct: ct).ConfigureAwait(false);
                        var count = els.Count;
                        var matches = els.Take(capInsp).Select(e => new BatchElementMatch(
                            e.Id,
                            e.Role.ToString(),
                            e.Name ?? "",
                            e.Hwnd != null ? $"0x{e.Hwnd:X}" : null,
                            e.Pid,
                            $"({e.Bounds.X},{e.Bounds.Y} {e.Bounds.Width}x{e.Bounds.Height})",
                            Prop(e, "enabled")?.ToString() == "false" ? false : null,
                            e.Props.TryGetValue("value", out var v) && v != null ? TruncEdges(v.ToString(), 160) : null
                        )).ToList();

                        inspResults[idx] = new BatchInspectItemResult(idx, $"0x{rootH:X}", "ok", Title: title, Process: app, Count: count, Truncated: count > capInsp ? count - capInsp : null, Elements: matches);
                    }
                    catch (OperationCanceledException)
                    {
                        inspResults[idx] = new BatchInspectItemResult(idx, rawHwnd, "cancelled", Error: "inspect cancelled");
                    }
                    catch (Exception ex)
                    {
                        inspResults[idx] = new BatchInspectItemResult(idx, rawHwnd, "error", Error: ex.Message);
                    }
                    finally
                    {
                        semInsp.Release();
                    }
                }, ct);
            }

            try
            {
                await Task.WhenAll(inspTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ct.ThrowIfCancellationRequested();
            }

            var inspOkCount = inspResults.Count(r => r?.Status == "ok");
            var inspFailCount = inspResults.Length - inspOkCount;
            var inspOverallStatus = inspOkCount == inspResults.Length ? "ok" : (inspOkCount > 0 ? "partial" : "error");
            var inspResObj = new
            {
                status = inspOverallStatus,
                total = inspResults.Length,
                successful = inspOkCount,
                failed = inspFailCount,
                results = inspResults,
                provenance = Provenance("uia")
            };
            var inspFinalRes = Text(JsonSerializer.Serialize(inspResObj, J));
            _s.Deduplicator.RecordRead("computer_inspect", hNorm, 0, _s.Rt.MutationVersion, inspFinalRes);
            return inspFinalRes;
        }

        var normArgs = $"{hwnd}|{elementId}|{maxElements}|{detail}|{relational}";
        var scopeH = ParseHwnd(hwnd) ?? _s.ScopeHwnd ?? 0;
        var snapshotKey = new SnapshotCacheKey(
            Hwnd: ParseHwnd(hwnd) ?? _s.Rt.ForegroundWindow()?.Hwnd,
            ElementId: elementId,
            MaxElements: maxElements,
            Detail: detail,
            Relational: relational
        );

        var beforeSnapHits = _s.SnapshotCache.Telemetry.HitCount;
        var snapResult = await _s.SnapshotCache.GetOrComputeAsync(snapshotKey, async innerCt =>
        {
            using (PerfTrace.Stage("inspect:cache=miss"))
            {
                using var trace = PerfTrace.Begin("tool", "computer_inspect");
                innerCt.ThrowIfCancellationRequested();
                using var cancelReg = innerCt.CanBeCanceled ? innerCt.Register(() => _s.Rt.PurgePendingWork()) : default;
            if (elementId != null)
            {
                var parts = _s.Rt.Parts;
                var registered = parts.Registry.Get(elementId);
                if (registered == null)
                    return Error(OutcomeKind.TargetNotFound, $"unknown element '{elementId}'");
                var backend = parts.Backends.FirstOrDefault(b => b.Id == registered.Handle.Backend);
                UiElement? el;
                using (PerfTrace.Stage("reResolve"))
                    el = backend != null ? await backend.ReResolveAsync(registered.Handle, innerCt).ConfigureAwait(false) : null;
                if (el == null)
                    return Error(OutcomeKind.Stale, $"element '{elementId}' could not be re-resolved");
                el = el with { Id = elementId };
                parts.Registry.Register([el]);
                var sb = new StringBuilder(UntrustedHeader("uia") + "\n");
                sb.AppendLine($"[{el.Id}] {el.Role} \"{el.Name}\" stale={el.IsStale}");
                sb.AppendLine($"bounds=({el.Bounds.X},{el.Bounds.Y} {el.Bounds.Width}x{el.Bounds.Height}) hwnd=0x{el.Hwnd ?? 0:X} pid={el.Pid}");
                sb.AppendLine($"actions=[{string.Join(",", el.Actions)}]");
                foreach (var kv in el.Props) sb.AppendLine($"  {kv.Key} = {kv.Value}");
                var elResult = Text(sb.ToString());
                return elResult;
            }
            var h = ParseHwnd(hwnd) ?? _s.Rt.ForegroundWindow()?.Hwnd;
            if (h == null) return Error(OutcomeKind.Malformed, "no hwnd and no active window");
            if (ParseHwnd(hwnd) is { } requestedHwnd && !WindowExists(requestedHwnd))
                return Error(OutcomeKind.TargetNotFound,
                    $"window not found — invalid hwnd '{hwnd}' (no live window with that handle)");

            var rootH = WindowService.GetRootHwnd(h.Value);
            var modal = _s.Rt.WindowService.GetActiveBlockingPopup(rootH);
            var isModalBlocked = modal != null && modal.Hwnd != rootH;
            var inspectHwnd = isModalBlocked ? modal!.Hwnd : rootH;

            IReadOnlyList<UiElement> els;
            using (PerfTrace.Stage("inspect.uia"))
                els = await _s.Rt.InspectAsync(inspectHwnd, ct: innerCt).ConfigureAwait(false);
            var win = _s.Rt.Window(inspectHwnd);
            var app = win?.ProcessName ?? "";
            var screen = _s.Memory.RecordObservation(inspectHwnd, app, win?.Title ?? "", els);
            var tags = DialogTags(els);
            var slim = Slim(detail);
            var cap = maxElements ?? (slim ? 40 : 80);

            var modalWarning = "";
            if (isModalBlocked)
            {
                modalWarning = $"⚠️ [MODAL-OR-POPUP-ACTIVE: 0x{modal!.Hwnd:X} \"{modal.Title}\" ({modal.ProcessName})]\n" +
                    $"WARNING: Window 0x{h.Value:X} is currently BLOCKED by foreground popup/dialog 0x{modal.Hwnd:X}.\n" +
                    $"Actions sent to 0x{h.Value:X} will fail. You must dismiss or interact with this modal dialog or flyout first (e.g. press Escape).\n" +
                    $"Inspecting controls of active popup 0x{modal.Hwnd:X}:\n\n";
            }

            if (relational)
            {
                var rows = RelationalInspector.ExtractRows(els);
                if (rows.Count > 0)
                {
                    var rb = new StringBuilder(UntrustedHeader("uia") + "\n" + modalWarning);
                    rb.AppendLine($"window 0x{inspectHwnd:X} [screen: {screen}]: {rows.Count} relational rows:");
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
                    var rowResult = Text(rb.ToString().TrimEnd());
                    return rowResult;
                }
            }
            var b = new StringBuilder(UntrustedHeader("uia") + "\n" + modalWarning);
            b.AppendLine($"window 0x{inspectHwnd:X} [screen: {screen}]: {els.Count} elements " +
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
            var finalResult = Text(b.ToString());
                return finalResult;
            }
        }, ct).ConfigureAwait(false);

        if (_s.SnapshotCache.Telemetry.HitCount > beforeSnapHits)
        {
            using (PerfTrace.Stage("inspect:cache=hit")) { }
            PerfTrace.Count("inspect:cache=hit");
        }
        else
        {
            PerfTrace.Count("inspect:cache=miss");
        }
        return snapResult;
    }

    private static readonly HashSet<string> SupportedReadProps = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "text", "role", "value", "val", "enabled", "isenabled",
        "checked", "ischecked", "selected", "isselected", "bounds",
        "automationid", "classname", "id", "hwnd", "pid"
    };

    [McpServerTool(Name = "computer_read"), Description(
        "Read specific properties from multiple targets (element IDs or target selectors) in a single turn without full UI tree dumps. " +
        "Fast, compact, and read-only. " +
        "Target use case: computer_read(targets: ['uia_1', 'uia_2'], props: ['name', 'value', 'enabled']).")]
    public async Task<CallToolResult> Read(
        [Description("ordered list of element IDs (e.g. 'uia_...') or element names to read (max 32)")] string[] targets,
        [Description("properties to extract (e.g. 'name', 'value', 'enabled', 'checked', 'role', 'bounds'); default ['name', 'value', 'enabled']")] string[]? props = null,
        CancellationToken ct = default)
    {
        if (targets is not { Length: > 0 })
            return Error(OutcomeKind.Malformed, "targets array requires at least 1 target");
        if (targets.Length > 32)
            return Error(OutcomeKind.Malformed, "targets array exceeds maximum limit of 32");

        if (props != null)
        {
            if (props.Length == 0)
                return Error(OutcomeKind.Malformed, "props array cannot be empty when specified");
            if (props.Length > 16)
                return Error(OutcomeKind.Malformed, "props array exceeds maximum limit of 16");
            foreach (var p in props)
            {
                if (string.IsNullOrWhiteSpace(p) || !SupportedReadProps.Contains(p.Trim()))
                    return Error(OutcomeKind.Malformed, $"InvalidArgument: unsupported property '{p}'. Supported properties: name, role, value, enabled, checked, selected, bounds, automationId, className, id, hwnd, pid");
            }
        }

        var normArgs = $"read:{targets.Length}:{string.Join(";", targets)}:{string.Join(",", props ?? [])}";
        if (_s.Deduplicator.TryDeduplicateRead("computer_read", normArgs, 0, _s.Rt.MutationVersion, _s.Telemetry) is { } cachedRead)
            return cachedRead;

        using var trace = PerfTrace.Begin("tool", "computer_read");
        ct.ThrowIfCancellationRequested();
        using var cancelReg = ct.CanBeCanceled ? ct.Register(() => _s.Rt.PurgePendingWork()) : default;

        var results = new BatchReadItemResult[targets.Length];
        using var sem = new SemaphoreSlim(Math.Min(8, Environment.ProcessorCount));
        var tasks = new Task[targets.Length];
        var reqProps = props is { Length: > 0 }
            ? props.Select(p => p.Trim().ToLowerInvariant()).Distinct().ToArray()
            : ["name", "value", "enabled"];

        for (var i = 0; i < targets.Length; i++)
        {
            var idx = i;
            var t = targets[i];
            tasks[i] = Task.Run(async () =>
            {
                await sem.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    UiElement? el = null;
                    if (t.StartsWith("uia_", StringComparison.OrdinalIgnoreCase) || _s.Rt.Parts.Registry.Get(t) != null)
                    {
                        var registered = _s.Rt.Parts.Registry.Get(t);
                        if (registered != null)
                        {
                            var backend = _s.Rt.Parts.Backends.FirstOrDefault(b => b.Id == registered.Handle.Backend);
                            if (backend != null)
                                el = await backend.ReResolveAsync(registered.Handle, ct).ConfigureAwait(false);
                            el ??= registered;
                        }
                    }

                    if (el == null && !t.StartsWith("uia_", StringComparison.OrdinalIgnoreCase))
                    {
                        var (foundEls, _) = await FindElementsAsync(null, name: t, null, null, null, firstOnly: true, ct: ct).ConfigureAwait(false);
                        if (foundEls.Count > 0)
                            el = foundEls[0];
                    }

                    if (el == null)
                    {
                        results[idx] = new BatchReadItemResult(idx, t, "error", Error: "TargetNotFound");
                        return;
                    }

                    var extracted = new Dictionary<string, object?>();
                    foreach (var p in reqProps)
                    {
                        var pl = p.ToLowerInvariant();
                        if (pl is "name" or "text") extracted["name"] = el.Name;
                        else if (pl is "value" or "val") extracted["value"] = el.Props.GetValueOrDefault("value");
                        else if (pl is "enabled" or "isenabled") extracted["enabled"] = el.Props.TryGetValue("enabled", out var en) ? en : true;
                        else if (pl is "checked" or "ischecked" or "selected" or "isselected") extracted["checked"] = el.Props.GetValueOrDefault("selected") ?? el.Props.GetValueOrDefault("checked");
                        else if (pl is "role") extracted["role"] = el.Role.ToString();
                        else if (pl is "bounds") extracted["bounds"] = new { x = el.Bounds.X, y = el.Bounds.Y, width = el.Bounds.Width, height = el.Bounds.Height };
                        else if (pl is "automationid") extracted["automationId"] = el.Props.GetValueOrDefault("automationId")?.ToString() ?? "";
                        else if (pl is "classname") extracted["className"] = el.Props.GetValueOrDefault("className") ?? "";
                        else if (pl is "id") extracted["id"] = el.Id;
                        else if (pl is "hwnd") extracted["hwnd"] = el.Hwnd != null ? $"0x{el.Hwnd:X}" : null;
                        else if (pl is "pid") extracted["pid"] = el.Pid;
                    }
                    results[idx] = new BatchReadItemResult(idx, t, "ok", extracted);
                }

                catch (OperationCanceledException)
                {
                    results[idx] = new BatchReadItemResult(idx, t, "cancelled", Error: "read cancelled");
                }
                catch (Exception ex)
                {
                    results[idx] = new BatchReadItemResult(idx, t, "error", Error: ex.Message);
                }
                finally
                {
                    sem.Release();
                }
            }, ct);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
        }

        var okCount = results.Count(r => r?.Status == "ok");
        var failCount = results.Length - okCount;
        var overallStatus = okCount == results.Length ? "ok" : (okCount > 0 ? "partial" : "error");
        var resObj = new
        {
            status = overallStatus,
            total = results.Length,
            successful = okCount,
            failed = failCount,
            results = results,
            provenance = Provenance("uia")
        };
        var res = Text(JsonSerializer.Serialize(resObj, J));
        _s.Deduplicator.RecordRead("computer_read", normArgs, 0, _s.Rt.MutationVersion, res);
        return res;
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
        [Description("cap image width in px — wider captures are downscaled (bilinear); image-space coordinates still map back via frameId")] int? maxWidth = null,
        [Description("snake_case alias of maxWidth")] int? max_width = null,
        [Description("draw numbered marks at clickable element centers (window/element targets only); marks are listed in the result text")] bool marks = false,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return _s.GhostCoordinator.ScreenshotAsync(ct).GetAwaiter().GetResult();
        }

        try
        {
            if (_s.Control.ActionToken() == null)
                return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
            ct.ThrowIfCancellationRequested();
            using var trace = PerfTrace.Begin("tool", "computer_screenshot");
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
            RawFrame raw;
            using (PerfTrace.Stage("capture"))
                raw = _s.Rt.CaptureRaw(ct2);
            if ((maxWidth ?? max_width) is { } mw && mw > 0 && raw.Width > mw)
                using (PerfTrace.Stage("downscale"))
                    raw = raw.ScaledToMaxWidth(mw);
            List<(int Index, UiElement El)>? marked = null;
            if (marks && shotHwnd is { } markHwnd)
            {
                using (PerfTrace.Stage("marks"))
                {
                    try
                    {
                        var els = _s.Rt.Inspect(markHwnd);
                        var tags = new List<(int Index, RectPx Bounds)>(60);
                        marked = new List<(int, UiElement)>(60);
                        for (var i = 0; i < els.Count && tags.Count < 60; i++)
                        {
                            var e = els[i];
                            if (e.Actions.Count == 0 || e.Bounds.IsEmpty) continue;
                            var (ecx, ecy) = e.Bounds.Center;
                            if (!raw.Transform.SourceRect.Contains(ecx, ecy)) continue;
                            var idx = marked.Count + 1;
                            tags.Add((idx, e.Bounds));
                            marked.Add((idx, e));
                        }
                        if (tags.Count > 0) raw.DrawMarks(tags);
                    }
                    catch { marked = null; }
                }
            }
            // repeated captures of the same target: diff against the previous
            // frame — identical pixels reuse the prior PNG (encoder is
            // deterministic), otherwise report the changed bbox
            var shotKey = $"{target.ToLowerInvariant()}:{shotHwnd ?? 0}:" +
                $"{raw.Transform.SourceRect}:{raw.Width}x{raw.Height}:{marks}";
            var unchanged = false;
            long? prevFrameId = null;
            RectPx? changedBounds = null;
            byte[]? png = null;
            using (PerfTrace.Stage("diff"))
            {
                var cmp = _s.CompareScreenshot(shotKey, raw);
                unchanged = cmp.Unchanged;
                prevFrameId = cmp.PrevFrameId;
                changedBounds = cmp.ChangedBounds;
                if (cmp.Unchanged) png = cmp.ReusablePng;
            }
            using (PerfTrace.Stage("encode"))
                png ??= raw.ToPng();
            if ((long)raw.Width * raw.Height > ObservationBudget.Default.MaxScreenshotPixels ||
                png.LongLength > ObservationBudget.Default.MaxScreenshotBytes)
                return Error(OutcomeKind.CaptureUnavailable,
                    "screenshot exceeds the observation image budget; use a smaller region or maxWidth");
            if (_s.Control.ActionToken() == null)
                return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
            var (fref, obsId) = _s.MintFrame(raw, shotHwnd, png);
            _s.NoteScreenshotFrame(shotKey, raw, png!, fref.Meta.FrameId);
            var m = fref.Meta;
            var text = UntrustedHeader("screenshot") + "\n" +
                $"frameId={m.FrameId} observationId={obsId} " +
                $"size={m.Width}x{m.Height} source=({m.SourceRect.X},{m.SourceRect.Y} " +
                $"{m.SourceRect.Width}x{m.SourceRect.Height}) hwnd={(m.Hwnd is { } hh ? $"0x{hh:X}" : "desktop")}\n" +
                "image-space coordinates within this frame may be passed back to " +
                "computer_click/scroll/drag together with frameId + observationId";
            if (m.Width != m.SourceRect.Width || m.Height != m.SourceRect.Height)
                text += $"\ndownscaled: {m.Width}x{m.Height} image covers the " +
                    $"{m.SourceRect.Width}x{m.SourceRect.Height} source region";
            if (unchanged && prevFrameId is { } pf)
                text += $"\nunchanged: pixels identical to frameId={pf} — the " +
                    "previous image still describes the screen";
            else if (changedBounds is { } cb)
                text += $"\nchangedSince frameId={prevFrameId}: bbox=({cb.X},{cb.Y} " +
                    $"{cb.Width}x{cb.Height}) image space";
            if (marks && marked == null && shotHwnd == null)
                text += "\nmarks: skipped (needs a window/element target)";
            if (marked is { Count: > 0 } mk)
            {
                var mb = new StringBuilder("\nmarks (index → element at marked center):");
                foreach (var (idx, el) in mk.Take(40))
                    mb.Append($"\n  {idx} → {el.Id}" +
                        (el.Name != null ? $" \"{el.Name}\"" : "") +
                        $" {el.Role} bounds=({el.Bounds.X},{el.Bounds.Y} " +
                        $"{el.Bounds.Width}x{el.Bounds.Height})");
                text += mb.ToString();
            }
            trace.Set("pngBytes", png.LongLength);
            trace.Set("w", raw.Width); trace.Set("h", raw.Height);
            trace.Set("sourceRect", m.SourceRect.ToString());
            trace.Set("unchanged", unchanged);
            if (changedBounds is { } cbx) trace.Set("changedBBox", cbx.ToString());
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
}
