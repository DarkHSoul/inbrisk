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
    [McpServerTool(Name = "computer_run"), Description(
        "Run a declarative UI plan as ONE call — the preferred tool for deterministic multi-step work. " +
        "Steps: launch{target:{process}|text,as,ms}, find{target,as,select,orderBy,index}, " +
        "assert{elementId,contains|notContains|exact|state|enabled|value}, checkpoint{note}, " +
        "focus/focus_window{elementId|target|hwnd}, click|invoke|toggle|select|hover{elementId|target}, " +
        "set_value{elementId|target,text|value}, type{elementId|target,text,mode,position,submit}, " +
        "key{key,count,ms=hold the key down for ms; keys:\"w+space\"+ms holds several keys together}, hotkey{key,modifiers OR keys:\"ctrl+s\"}, look{target,dx,dy: relative mouse look for games}, mouse{target,button,ms[,keys]: hold a mouse button, optionally with keys held together, e.g. mining or space+right-click to pillar up}, scroll{delta,target}, " +
        "scroll_into_view{elementId|target}, drag{target,toX,toY}, wait{ms}, " +
        "wait_for{query,ms,target-scope:{within|process|window}}, wait_for_gone{query|target|elementId,ms}, wait_for_change/wait_for_stable{ms}, " +
        "scan/for_each{target,as,steps,where,collect,maxItems,maxPages,stopOn}, " +
        "adapter{adapter,args}/media actions (play,pause,next,previous,volume_up,volume_down,mute), human|pause_for_human{reason}. " +
        "Dynamic variables ($var, {{var}}, $item.name, $item.value, $item.role, $item.id) interpolate automatically " +
        "inside target names, text, values, and element IDs. " +
        "Targets resolve LAZILY at each step, so later steps can act on UI created by earlier ones. " +
        "find{as:\"x\"} binds an element for later steps via elementId:\"$x\" (also inside target.within). " +
        "scan iterates server-side over matched items without LLM roundtrips, binding $as and $as.name/.value/.role/.id per item. " +
        "When several candidates match, selection stays server-side: select:\"first|last|nth\" + index + " +
        "orderBy:\"visual\" | \"tree\" | \"score\". " +
        "ifExists/ifNotExists/ifEnabled/ifValue skip steps; retry/timeout bound them. " +
        "STRICT: unknown or wrong-action fields are Malformed, never ignored. " +
        "First failure pauses with step/error/observationDelta/availableElements — resume with the same runId. " +
        "checkpoint pauses deliberately for model reasoning. An unexpected dialog pauses with UnexpectedModalOpened. " +
        "A proven run becomes a reusable parameterized recipe via computer_save_recipe{fromRunId} — replay later with computer_run_recipe. " +
        "save_as_recipe:\"name\" auto-saves a successful run as a recipe in the same call.")]
    public async Task<CallToolResult> RunPlan(
        [Description("ordered plan steps")] RunStep[] steps,
        [Description("resume an existing run — keeps its element bindings")] string? runId = null,
        [Description("output verbosity: slim|full — default from INBRISK_DETAIL or settings.json outputDetail")] string? detail = null,
        [Description("on success, generalize the executed steps and persist as task-recipes/<name>.json — replay later via computer_run_recipe")] string? saveAsRecipe = null,
        [Description("snake_case alias of saveAsRecipe")] string? save_as_recipe = null,
        [Description("intercept unexpected modal dialogs between steps via the reflex engine (default true)")] bool? enableReflex = null,
        [Description("snake_case alias of enableReflex")] bool? enable_reflex = null,
        [Description("auto-dismiss policy for save-confirmation modals: off|save|discard|closeOnly (default closeOnly — cancels the prompt rather than committing or discarding work)")] string? autoDismissModals = null,
        [Description("snake_case alias of autoDismissModals")] string? auto_dismiss_modals = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        _s.Telemetry.ResetSequentialSingleAction();
        if (BadDetail(detail) is { } bd) return bd;

        var recipeName = saveAsRecipe ?? save_as_recipe;
        var reflexOn = enableReflex ?? enable_reflex;
        var dismissModals = autoDismissModals ?? auto_dismiss_modals;
        var normArgs = $"{runId}|{detail}|{recipeName}|{reflexOn}|{dismissModals}|" + JsonSerializer.Serialize(steps, J);

        return await ExecuteWithIdempotencyAsync(operationId, "computer_run", normArgs, async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var res = await RunPlanCore(steps, runId, sw, ct, detail,
                    enableReflex: reflexOn, autoDismissModals: dismissModals);
                return AttachSavedRecipe(res, recipeName, steps,
                    steps.FirstOrDefault(s => string.Equals(s.Action, "launch",
                        StringComparison.OrdinalIgnoreCase))?.App,
                    "computer_run");
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
        });
    }

    [McpServerTool(Name = "computer_do"), Description(
        "Execute a composite fast UI action in a single turn without LLM roundtrips. " +
        "Can launch/focus an app, click a target, type text, and/or send a hotkey. " +
        "Examples: computer_do(app: \"notepad\", type: \"hello world\", hotkey: \"ctrl+s\"), " +
        "computer_do(click: \"Save\", type: \"test.txt\", submit: true). " +
        "For anything beyond launch+click+type+hotkey — conditions, loops over matched items, reads, reusable flows — use computer_run (or a saved computer_run_recipe). " +
        "save_as_recipe:\"name\" persists a successful flow as a replayable recipe.")]
    public async Task<CallToolResult> ComputerDo(
        [Description("Application to launch or focus (e.g. 'Notepad', 'Calculator')")] string? app = null,
        [Description("Target element name or text to click")] string? click = null,
        [Description("Text to type into the target or focused window")] string? type = null,
        [Description("Hotkey to press (e.g. 'ctrl+s', 'enter', 'alt+f4')")] string? hotkey = null,
        [Description("Whether to press Enter after typing (default false)")] bool submit = false,
        [Description("Semantic target or elementId to click")] TargetSpec? target = null,
        [Description("Wait in milliseconds between steps (default 100ms)")] int waitMs = 100,
        [Description("after the action completes, reap session-spawned processes that are still running (same agent-owned-only safety policy as computer_cleanup; default false)")] bool cleanup = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("optional specialist adapter for the launch step — \"blender\" force-enables the bpy socket bridge injection; \"none\" disables auto-injection")] string? adapter = null,
        [Description("auto-inject the adapter bridge script when a supported app is detected (default true for Blender); false launches plain")] bool? bridge = null,
        [Description("on success, generalize the executed steps and persist as task-recipes/<name>.json — replay later via computer_run_recipe")] string? saveAsRecipe = null,
        [Description("snake_case alias of saveAsRecipe")] string? save_as_recipe = null,
        [Description("intercept unexpected modal dialogs between steps via the reflex engine (default true)")] bool? enableReflex = null,
        [Description("snake_case alias of enableReflex")] bool? enable_reflex = null,
        [Description("auto-dismiss policy for save-confirmation modals: off|save|discard|closeOnly (default closeOnly)")] string? autoDismissModals = null,
        [Description("snake_case alias of autoDismissModals")] string? auto_dismiss_modals = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.ComputerDoAsync(
                app, click, type, hotkey, submit, target, waitMs, ct).ConfigureAwait(false);
        }

        _s.Telemetry.ResetSequentialSingleAction();
        var recipeName = saveAsRecipe ?? save_as_recipe;
        var reflexOn = enableReflex ?? enable_reflex;
        var dismissModals = autoDismissModals ?? auto_dismiss_modals;
        var normArgs = $"{app}|{click}|{type}|{hotkey}|{submit}|{target?.Name}|{target?.Role}|{waitMs}|{cleanup}|{adapter}|{bridge}|{recipeName}|{reflexOn}|{dismissModals}";

        return await ExecuteWithIdempotencyAsync(operationId, "computer_do", normArgs, async () =>
        {
            var steps = new List<RunStep>();

            if (!string.IsNullOrWhiteSpace(app))
            {
                steps.Add(new RunStep { Action = "launch", App = app, WaitFor = "window",
                    Adapter = bridge == false ? "none" : adapter });
                if (waitMs > 0 && (!string.IsNullOrWhiteSpace(click) || target != null || !string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(hotkey)))
                    steps.Add(new RunStep { Action = "wait", Ms = waitMs });
            }

            if (!string.IsNullOrWhiteSpace(click) || target != null)
            {
                var clickTarget = target ?? new TargetSpec { Name = click };
                steps.Add(new RunStep { Action = "click", Target = clickTarget });
                if (waitMs > 0 && (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(hotkey)))
                    steps.Add(new RunStep { Action = "wait", Ms = waitMs });
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                steps.Add(new RunStep { Action = "type", Text = type, Submit = submit });
                if (waitMs > 0 && !string.IsNullOrWhiteSpace(hotkey))
                    steps.Add(new RunStep { Action = "wait", Ms = waitMs });
            }

            if (!string.IsNullOrWhiteSpace(hotkey))
            {
                steps.Add(new RunStep { Action = "hotkey", Keys = hotkey });
            }

            if (steps.Count == 0)
            {
                return Text(JsonSerializer.Serialize(new
                {
                    error = "Malformed",
                    detail = "pass at least one action: app, click, type, or hotkey"
                }, J));
            }

            var sw = Stopwatch.StartNew();
            try
            {
                var planSteps = steps.ToArray();
                var res = await RunPlanCore(planSteps, null, sw, ct,
                    detail: "slim", enableReflex: reflexOn,
                    autoDismissModals: dismissModals);
                res = AttachSavedRecipe(res, recipeName, planSteps, app, "computer_do");
                return cleanup ? WithCleanupReap(res) : res;
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
        });
    }

    [McpServerTool(Name = "computer_batch"), Description(
        "Execute a fast, declarative sequence of UI actions in a single turn without LLM roundtrips. " +
        "When 2+ deterministic actions are known and intermediate observation is not needed, ALWAYS prefer computer_batch. " +
        "Supports sequential 'steps' (click, invoke, type, set_value, hotkey, key, wait, launch, focus, toggle, select, scroll), " +
        "OR a compact 'set' list for multiple form fields (mutually exclusive with 'steps' to avoid ambiguous ordering). " +
        "Step fields accept aliases: action→do, target→t (string, or object {elementId,name,role,hwnd,window,process,automationId}), " +
        "text/value→v, waitFor→ms (number; for do:launch also accepts readiness 'window|process|none'). " +
        "Unknown step fields are REJECTED as Malformed — never silently dropped. " +
        "failFast (default true) stops at the first failing step; failFast:false or a per-step continueOnError:true " +
        "records the failure and keeps executing the rest. " +
        "Optional 'until' waits (appears|disappears|windowAppears|stable; value/state refine appears) are event-driven — " +
        "WinEvent/UIA events wake them early, a poll cadence remains as fallback; timeoutMs unchanged. " +
        "Result is compact: {success, stepsExecuted, totalSteps, totalDurationMs, results:[{step,do,t,ok,ms,error?}]}. " +
        "Do NOT batch across an unpredicted reasoning boundary. " +
        "For conditions (ifExists/ifValue), iteration over matched items (scan/for_each), or reusable parameterized flows, prefer computer_run / computer_run_recipe. " +
        "save_as_recipe:\"name\" auto-saves a fully successful batch as a replayable recipe.")]
    public async Task<CallToolResult> Batch(
        [Description("ordered list of simple action steps to execute sequentially (mutually exclusive with 'set')")] BatchStep[]? steps = null,
        [Description("compact list of form fields to set in one turn (target, value, role?); mutually exclusive with 'steps'")] FormFieldSpec[]? set = null,
        [Description("optional condition to wait for after steps execute (appears/disappears/windowAppears/stable; value/state refine appears)")] BatchUntilSpec? until = null,
        [Description("optional list of element targets to read/extract properties from upon completion")] BatchReadSpec[]? read = null,
        [Description("output verbosity: slim|full — default slim")] string? detail = null,
        [Description("stop the batch at the first failing step (default true); false records each failure in results[] and keeps executing")] bool? failFast = null,
        [Description("snake_case alias of failFast")] bool? fail_fast = null,
        [Description("on success, generalize the executed steps and persist as task-recipes/<name>.json — replay later via computer_run_recipe")] string? saveAsRecipe = null,
        [Description("snake_case alias of saveAsRecipe")] string? save_as_recipe = null,
        [Description("intercept unexpected modal dialogs between steps via the reflex engine (default true)")] bool? enableReflex = null,
        [Description("snake_case alias of enableReflex")] bool? enable_reflex = null,
        [Description("auto-dismiss policy for save-confirmation modals: off|save|discard|closeOnly (default closeOnly)")] string? autoDismissModals = null,
        [Description("snake_case alias of autoDismissModals")] string? auto_dismiss_modals = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.BatchAsync(
                steps, set, until, read, detail, failFast ?? fail_fast,
                autoDismissModals ?? auto_dismiss_modals,
                timeoutMs: null, operationId, ct).ConfigureAwait(false);
        }

        _s.Telemetry.ResetSequentialSingleAction();
        var reflexOn = enableReflex ?? enable_reflex;
        var dismissModals = autoDismissModals ?? auto_dismiss_modals;
        if (steps is { Length: > 0 } && set is { Length: > 0 })
            return Error(OutcomeKind.Malformed, "Ambiguous execution order: specify either 'set' (for compact form fill) or 'steps' (for sequenced actions including set_value), not both simultaneously.");

        if (set != null && set.Length > 32)
            return Error(OutcomeKind.Malformed, "set array exceeds maximum limit of 32");

        var failFastE = failFast ?? fail_fast ?? true;

        // Strict schema: batch specs capture unknown JSON fields into Extra —
        // a misspelled field must fail loudly, never be silently dropped.
        if (steps != null)
        {
            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i].Extra is { Count: > 0 } ex)
                    return Error(OutcomeKind.Malformed,
                        $"batch step[{i}] unknown field(s): {string.Join(", ", ex.Keys)} — " +
                        "valid: do/action, t/target, v/text/value, role, hwnd, ms/waitFor, submit, keys, continueOnError");
            }
        }
        if (set != null)
        {
            for (var i = 0; i < set.Length; i++)
            {
                if (set[i].Extra is { Count: > 0 } sx)
                    return Error(OutcomeKind.Malformed,
                        $"batch set[{i}] unknown field(s): {string.Join(", ", sx.Keys)} — valid: target, value, role");
                if (string.IsNullOrWhiteSpace(set[i].Target))
                    return Error(OutcomeKind.Malformed, $"batch set[{i}] requires 'target'");
            }
        }
        if (until != null)
        {
            if (until.Extra is { Count: > 0 } ux)
                return Error(OutcomeKind.Malformed,
                    $"batch 'until' unknown field(s): {string.Join(", ", ux.Keys)} — " +
                    "valid: appears, disappears, windowAppears, stable, value, state, timeoutMs, stableMs");
            if (string.IsNullOrWhiteSpace(until.Appears) &&
                string.IsNullOrWhiteSpace(until.Disappears) &&
                string.IsNullOrWhiteSpace(until.WindowAppears) &&
                until.Stable != true)
                return Error(OutcomeKind.Malformed,
                    "until requires a condition: appears|disappears|windowAppears|stable — " +
                    "value/state refine 'appears' and are not standalone conditions");
        }
        if (read != null)
        {
            for (var i = 0; i < read.Length; i++)
            {
                if (read[i].Extra is { Count: > 0 } rx)
                    return Error(OutcomeKind.Malformed,
                        $"batch read[{i}] unknown field(s): {string.Join(", ", rx.Keys)} — valid: target, props, role");
                if (string.IsNullOrWhiteSpace(read[i].Target))
                    return Error(OutcomeKind.Malformed, $"batch read[{i}] requires 'target'");
            }
        }

        if (BadDetail(detail) is { } bd) return bd;

        var stepCount = (steps?.Length ?? 0) + (set?.Length ?? 0);
        if (stepCount == 0 && (read == null || read.Length == 0))
            return Error(OutcomeKind.Malformed, "either steps, set, or read array is required");

        // Translate the batch surface into canonical plan steps. meta[i] maps
        // plan-step index → the caller-facing do/t for the compact result.
        var runSteps = new List<RunStep>();
        var meta = new List<(string Do, string? T)>();
        void AddStep(RunStep rs, string doName, string? t, bool tolerated)
        {
            runSteps.Add(tolerated ? rs with { ContinueOnError = true } : rs);
            meta.Add((doName, t));
        }

        if (steps != null)
        {
            for (var i = 0; i < steps.Length; i++)
            {
                var s = steps[i];
                var act = (s.Do ?? s.Action ?? "click").Trim().ToLowerInvariant();
                var tolerated = s.ContinueOnError || !failFastE;

                // friendly aliases: target→t (string or object), text/value→v, waitFor→ms
                string? tStr = s.T;
                string? roleStr = s.Role;
                string? hwndStr = s.Hwnd;
                TargetSpec? aliasTarget = null;
                if (s.Target is { } te)
                {
                    if (te.ValueKind == JsonValueKind.String)
                    {
                        tStr ??= te.GetString();
                    }
                    else if (te.ValueKind == JsonValueKind.Object)
                    {
                        try { aliasTarget = te.Deserialize<TargetSpec>(J); }
                        catch
                        {
                            return Error(OutcomeKind.Malformed,
                                $"batch step[{i}] 'target' is not a string or a valid target object");
                        }
                        if (aliasTarget != null)
                        {
                            tStr ??= aliasTarget.ElementId ?? aliasTarget.Name;
                            roleStr ??= aliasTarget.Role;
                            hwndStr ??= aliasTarget.Hwnd ?? aliasTarget.Window;
                        }
                    }
                    else
                    {
                        return Error(OutcomeKind.Malformed,
                            $"batch step[{i}] 'target' must be a string or an object");
                    }
                }
                var val = s.V ?? BatchAliasStr(s.Text) ?? BatchAliasStr(s.Value);
                var msV = s.Ms;
                string? launchReadiness = null;
                if (s.WaitFor is { } we)
                {
                    if (we.ValueKind == JsonValueKind.Number && we.TryGetInt32(out var wn))
                    {
                        msV ??= wn;
                    }
                    else if (we.ValueKind == JsonValueKind.String)
                    {
                        var ws = we.GetString();
                        if (int.TryParse(ws, out var wm)) msV ??= wm;
                        else launchReadiness = ws;
                    }
                    else
                    {
                        return Error(OutcomeKind.Malformed,
                            $"batch step[{i}] 'waitFor' must be a ms number (wait/scroll) " +
                            "or a launch readiness (window|process|none)");
                    }
                }

                var isElId = tStr?.StartsWith("uia_", StringComparison.OrdinalIgnoreCase) == true;
                var elId = isElId ? tStr : aliasTarget?.ElementId;
                TargetSpec? target =
                    !string.IsNullOrWhiteSpace(tStr) || !string.IsNullOrWhiteSpace(roleStr) ||
                    !string.IsNullOrWhiteSpace(hwndStr) || aliasTarget != null
                        ? (aliasTarget ?? new TargetSpec()) with
                        {
                            Name = isElId ? null : tStr ?? aliasTarget?.Name,
                            ElementId = isElId ? tStr : aliasTarget?.ElementId,
                            Role = roleStr ?? aliasTarget?.Role,
                            Window = hwndStr ?? aliasTarget?.Window,
                        }
                        : null;

                switch (act)
                {
                    case "click":
                        AddStep(new RunStep { Action = "click", Target = target, ElementId = elId }, act, tStr, tolerated);
                        break;
                    case "invoke":
                        AddStep(new RunStep { Action = "invoke", Target = target, ElementId = elId }, act, tStr, tolerated);
                        break;
                    case "type":
                        AddStep(new RunStep { Action = "type", Target = target, ElementId = elId, Text = val, Submit = s.Submit }, act, tStr, tolerated);
                        break;
                    case "set_value" or "setvalue":
                        AddStep(new RunStep { Action = "set_value", Target = target, ElementId = elId, Value = val }, act, tStr, tolerated);
                        break;
                    case "hotkey":
                        AddStep(new RunStep { Action = "hotkey", Keys = s.Keys ?? val }, act, s.Keys ?? val, tolerated);
                        break;
                    case "key":
                        AddStep(new RunStep { Action = "key", Key = s.Keys ?? val }, act, s.Keys ?? val, tolerated);
                        break;
                    case "wait":
                        AddStep(new RunStep { Action = "wait", Ms = msV ?? (int.TryParse(val, out var m) ? m : 500) }, act, tStr, tolerated);
                        break;
                    case "launch":
                        AddStep(new RunStep { Action = "launch", App = tStr ?? val, WaitFor = launchReadiness ?? "window" }, act, tStr ?? val, tolerated);
                        break;
                    case "focus":
                    {
                        // focus names a WINDOW or an ELEMENT: an hwnd-like t
                        // routes to hwnd, an element name stays a target (its
                        // owning window follows the element). A pure hwnd spec
                        // needs no element target at all.
                        var tIsHwnd = ParseHwnd(tStr) != null;
                        var fTarget = (tStr != null && !tIsHwnd) || roleStr != null || aliasTarget != null
                            ? target : null;
                        AddStep(new RunStep
                        {
                            Action = "focus",
                            Hwnd = s.Hwnd ?? (tIsHwnd ? tStr : null),
                            Target = fTarget,
                            ElementId = elId
                        }, act, tStr ?? s.Hwnd, tolerated);
                        break;
                    }
                    case "toggle":
                        AddStep(new RunStep { Action = "toggle", Target = target, ElementId = elId }, act, tStr, tolerated);
                        break;
                    case "select":
                        AddStep(new RunStep { Action = "select", Target = target, ElementId = elId }, act, tStr, tolerated);
                        break;
                    case "scroll":
                        AddStep(new RunStep { Action = "scroll", Delta = msV ?? (int.TryParse(val, out var d) ? d : -120) }, act, tStr, tolerated);
                        break;
                    default:
                        AddStep(new RunStep { Action = act, Target = target, ElementId = elId, Text = val, Keys = s.Keys }, act, tStr, tolerated);
                        break;
                }
            }
        }

        if (set != null)
        {
            for (var i = 0; i < set.Length; i++)
            {
                var f = set[i];
                var isElId = f.Target.StartsWith("uia_", StringComparison.OrdinalIgnoreCase);
                AddStep(new RunStep
                {
                    Action = "set_value",
                    ElementId = isElId ? f.Target : null,
                    Target = new TargetSpec
                    {
                        Name = isElId ? null : f.Target,
                        ElementId = isElId ? f.Target : null,
                        Role = f.Role ?? "Edit"
                    },
                    Value = f.Value
                }, "set_value", f.Target, !failFastE);
            }
        }

        // until → a trailing wait step inside the same run. The runtime wait
        // service is event-driven: scoped WinEvent/UIA events wake it early
        // (~200ms poll slices remain the fallback cadence). disappears goes
        // to wait_for_gone and stable to wait_for_stable — the previous
        // expectedState:"disappeared"/"stable" encodings could never match a
        // live element's props and always timed out.
        if (until != null && stepCount > 0)
        {
            var timeout = until.TimeoutMs ?? 5000;
            var uTolerated = !failFastE;
            if (!string.IsNullOrWhiteSpace(until.Appears))
                AddStep(new RunStep { Action = "wait_for", Query = until.Appears, ExpectedValue = until.Value, ExpectedState = until.State, Ms = timeout }, "until.appears", until.Appears, uTolerated);
            else if (!string.IsNullOrWhiteSpace(until.Disappears))
                AddStep(new RunStep { Action = "wait_for_gone", Query = until.Disappears, Ms = timeout }, "until.disappears", until.Disappears, uTolerated);
            else if (!string.IsNullOrWhiteSpace(until.WindowAppears))
                AddStep(new RunStep { Action = "wait_for", Query = until.WindowAppears, Ms = timeout }, "until.windowAppears", until.WindowAppears, uTolerated);
            else
                AddStep(new RunStep { Action = "wait_for_stable", Ms = timeout }, "until.stable", null, uTolerated);
        }

        var normArgs = JsonSerializer.Serialize(new
        {
            steps = runSteps,
            until = until == null ? null : new
            {
                appears = until.Appears,
                disappears = until.Disappears,
                windowAppears = until.WindowAppears,
                stable = until.Stable,
                value = until.Value,
                state = until.State,
                timeoutMs = until.TimeoutMs,
                stableMs = until.StableMs
            },
            read = read?.Select(r => new
            {
                target = r.Target,
                role = r.Role,
                props = r.Props
            }),
            detail,
            failFast = failFastE,
            saveAsRecipe = saveAsRecipe ?? save_as_recipe,
            enableReflex = reflexOn,
            autoDismissModals = dismissModals
        }, J);
        if (_s.Deduplicator.TryDeduplicateMutation(operationId, "computer_batch", normArgs, _s.Telemetry, out var conflictError) is { } deduped)
            return deduped;
        if (conflictError != null)
            return conflictError;

        var sw = Stopwatch.StartNew();
        CallToolResult res;

        if (runSteps.Count == 0)
        {
            res = Text(JsonSerializer.Serialize(new
            {
                success = true, status = "Ok", stepsExecuted = 0, totalSteps = 0,
                totalDurationMs = 0, results = Array.Empty<object>(),
                detail = "read-only batch completed"
            }, J));
        }
        else
        {
            // wait-telemetry baseline — the until step's wake source is the
            // delta of the shared WaitService counters across this run
            var wt = until != null ? _s.Rt.Parts.Waits.Telemetry : null;
            var ev0 = wt?.EventWakeCount ?? 0;
            var poll0 = wt?.FallbackPollCount ?? 0;

            try
            {
                var raw = await RunPlanCore(runSteps.ToArray(), null, sw, ct,
                    detail: detail ?? "slim", enableReflex: reflexOn,
                    autoDismissModals: dismissModals);
                (long EventWakes, long PollWakes)? waitDelta = wt != null
                    ? (wt.EventWakeCount - ev0, wt.FallbackPollCount - poll0)
                    : null;
                res = CompactBatchResult(raw, meta, failFastE, until, waitDelta, sw);
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

        if (read is { Length: > 0 })
        {
            var readResults = new Dictionary<string, object?>();
            foreach (var r in read)
            {
                UiElement? el = _s.Rt.Parts.Registry.Get(r.Target);
                if (el == null && r.Role != null && Enum.TryParse<Inbrisk.Core.Role>(r.Role, true, out var parsedRole))
                    el = _s.Rt.Parts.Registry.FindByName(r.Target, parsedRole);
                else if (el == null)
                    el = _s.Rt.Parts.Registry.FindByName(r.Target);

                if (el == null)
                {
                    var els = FindElements(r.Role, r.Target, null, null, null, out _, firstOnly: true);
                    if (els.Count > 0) el = els[0];
                }
                if (el != null)
                {
                    var props = new Dictionary<string, object?>();
                    var reqProps = r.Props ?? ["value", "name", "role", "isEnabled"];
                    foreach (var p in reqProps)
                    {
                        var pl = p.ToLowerInvariant();
                        if (pl is "value" or "val") props["value"] = el.Props.GetValueOrDefault("value");
                        else if (pl is "name" or "text") props["name"] = el.Name ?? el.Props.GetValueOrDefault("name");
                        else if (pl is "role") props["role"] = el.Role.ToString();
                        else if (pl is "isenabled" or "enabled") props["isEnabled"] = el.Props.TryGetValue("enabled", out var en) ? en : true;
                        else if (pl is "isselected" or "selected") props["isSelected"] = el.Props.GetValueOrDefault("selected");
                        else if (pl is "bounds") props["bounds"] = new { x = el.Bounds.X, y = el.Bounds.Y, w = el.Bounds.Width, h = el.Bounds.Height };
                        else if (pl is "id") props["id"] = el.Id;
                    }
                    readResults[r.Target] = props;
                }
                else
                {
                    readResults[r.Target] = new { error = "NotFound" };
                }
            }

            if (res.Content is [TextContentBlock { Text: { } rawText }])
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<Dictionary<string, object?>>(rawText, J);
                    if (doc != null)
                    {
                        doc["read"] = readResults;
                        doc["provenance"] = Provenance("uia");
                        res = new CallToolResult
                        {
                            IsError = res.IsError,
                            Content = [new TextContentBlock
                                { Text = JsonSerializer.Serialize(doc, J) }],
                        };
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"computer_batch read-merge failed: {ex.Message}");
                }
            }
        }

        res = AttachSavedRecipe(res, saveAsRecipe ?? save_as_recipe, runSteps,
            runSteps.FirstOrDefault(s => string.Equals(s.Action, "launch",
                StringComparison.OrdinalIgnoreCase))?.App,
            "computer_batch");
        _s.Deduplicator.RecordMutation(operationId, "computer_batch", normArgs, res);
        return res;
    }

    /// <summary>JsonElement → string for batch field aliases (target/text/
    /// value/waitFor) — strings pass through, numbers/bools stringify, anything
    /// else (null/object/array) is ignored.</summary>
    private static string? BatchAliasStr(JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } v
            => v.ToString(),
        _ => null,
    };

    /// <summary>Wrap the plan report into the compact batch contract:
    /// {success, stepsExecuted, totalSteps, totalDurationMs,
    /// results:[{step,do,t,ok,ms,error?}]} — plus runId/resume affordance on
    /// failure and an until{} block carrying the wait's wake source
    /// (event vs poll vs timeout vs immediate). Emits batch.summary and
    /// batch.until UiaPerf records.</summary>
    private CallToolResult CompactBatchResult(CallToolResult raw,
        IReadOnlyList<(string Do, string? T)> meta, bool failFast,
        BatchUntilSpec? until,
        (long EventWakes, long PollWakes)? waitDelta, Stopwatch sw)
    {
        var results = new List<Dictionary<string, object?>>();
        var status = "Failed";
        string? runId = null, pauseStatus = null, pauseError = null;
        string? reflexJson = null, modalJson = null;
        int? failedStep = null;
        try
        {
            var text = raw.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("status", out var st))
                    status = st.GetString() ?? status;
                if (root.TryGetProperty("runId", out var rid))
                    runId = rid.GetString();
                if (root.TryGetProperty("steps", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var idx = e.TryGetProperty("step", out var si) &&
                            si.ValueKind == JsonValueKind.Number
                                ? si.GetInt32() : results.Count;
                        var stt = e.TryGetProperty("status", out var ss)
                            ? ss.GetString() ?? "" : "";
                        var ok = stt.Equals("Ok", StringComparison.OrdinalIgnoreCase)
                            || stt.Equals("Verified", StringComparison.OrdinalIgnoreCase)
                            || stt.Equals("Unverified", StringComparison.OrdinalIgnoreCase)
                            || stt.Equals("ObservedChange", StringComparison.OrdinalIgnoreCase)
                            || stt.Equals("Skipped", StringComparison.OrdinalIgnoreCase);
                        var entry = new Dictionary<string, object?>
                        {
                            ["step"] = idx,
                            ["do"] = idx >= 0 && idx < meta.Count
                                ? meta[idx].Do
                                : (e.TryGetProperty("action", out var ac) ? ac.GetString() : null),
                            ["t"] = idx >= 0 && idx < meta.Count ? meta[idx].T : null,
                            ["ok"] = ok,
                        };
                        if (e.TryGetProperty("ms", out var msEl) &&
                            msEl.ValueKind == JsonValueKind.Number)
                            entry["ms"] = msEl.GetInt64();
                        if (!ok)
                        {
                            var det = e.TryGetProperty("detail", out var dEl)
                                ? dEl.GetString() : null;
                            entry["error"] = string.IsNullOrEmpty(det)
                                ? stt : $"{stt}: {det}";
                            failedStep ??= idx;
                        }
                        results.Add(entry);
                    }
                }
                if (root.TryGetProperty("pause", out var pz))
                {
                    pauseStatus = pz.TryGetProperty("status", out var ps)
                        ? ps.GetString() : null;
                    pauseError = pz.TryGetProperty("error", out var pe)
                        ? pe.GetString() : null;
                    if (pz.TryGetProperty("step", out var pst) &&
                        pst.ValueKind == JsonValueKind.Number)
                        failedStep ??= pst.GetInt32();
                }
                // Error() payloads carry {error, detail} instead of status
                if (root.TryGetProperty("error", out var eTop))
                {
                    status = eTop.GetString() ?? status;
                    pauseError ??= root.TryGetProperty("detail", out var d2)
                        ? d2.GetString() : null;
                }
                // reflex abort payloads surface their modal + interception
                // log on the compact result too — raw text so the parsed
                // doc can die with this scope.
                if (root.TryGetProperty("reflex", out var rfx) &&
                    rfx.ValueKind == JsonValueKind.Object)
                    reflexJson = rfx.GetRawText();
                if (root.TryGetProperty("modal", out var mdl) &&
                    mdl.ValueKind == JsonValueKind.Object)
                    modalJson = mdl.GetRawText();
                if (root.TryGetProperty("failedStep", out var fse) &&
                    fse.ValueKind == JsonValueKind.Number)
                    failedStep ??= fse.GetInt32();
            }
        }
        catch { /* emit whatever parsed — compact must never throw */ }

        var allOk = results.All(r => r["ok"] is true);
        var success = allOk && status == "Completed";
        var finalStatus = success ? "Completed"
            : status == "Completed" ? "Partial"   // completed w/ tolerated failures
            : pauseStatus ?? (status is "Paused" ? "Failed" : status);

        var outDoc = new Dictionary<string, object?>
        {
            ["success"] = success,
            ["status"] = finalStatus,
            ["stepsExecuted"] = results.Count,
            ["totalSteps"] = meta.Count,
            ["totalDurationMs"] = sw.ElapsedMilliseconds,
            ["results"] = results,
            ["provenance"] = Provenance("uia"),
        };
        if (runId != null) outDoc["runId"] = runId;
        if (reflexJson != null)
            try { outDoc["reflex"] = JsonSerializer.Deserialize<JsonElement>(reflexJson, J); }
            catch (Exception ex) { Debug.WriteLine($"computer_batch reflex payload parse failed: {ex.Message}"); }
        if (modalJson != null)
            try { outDoc["modal"] = JsonSerializer.Deserialize<JsonElement>(modalJson, J); }
            catch (Exception ex) { Debug.WriteLine($"computer_batch modal payload parse failed: {ex.Message}"); }
        if (!failFast) outDoc["failFast"] = false;
        if (!success)
        {
            if (failedStep != null) outDoc["failedStep"] = failedStep;
            var firstErr = results.FirstOrDefault(r => r["ok"] is false);
            outDoc["error"] = pauseError
                ?? (firstErr?.TryGetValue("error", out var ev) == true ? ev : null)
                ?? finalStatus;
            if (runId != null)
                outDoc["resume"] = $"computer_resume_run({{runId:\"{runId}\"}})";
        }

        if (until != null)
        {
            var uIdx = results.FindIndex(r =>
                (r["do"] as string)?.StartsWith("until.", StringComparison.Ordinal) == true);
            // uIdx < 0 → an earlier fail-fast stop kept the until step from
            // ever running — report "skipped", not a fake "timeout"
            var uOk = uIdx >= 0 && results[uIdx]["ok"] is true;
            var cond = !string.IsNullOrWhiteSpace(until.Appears) ? "appears"
                : !string.IsNullOrWhiteSpace(until.Disappears) ? "disappears"
                : !string.IsNullOrWhiteSpace(until.WindowAppears) ? "windowAppears"
                : "stable";
            // wake source: any event wake → "event"; satisfied before the
            // first wait slice → "immediate"; else the poll fallback cadence
            // carried the wait → "poll"; failure/timeout → "timeout"
            var (evW, pollW) = waitDelta ?? (0L, 0L);
            var wake = uIdx < 0 ? "skipped"
                : !uOk ? "timeout"
                : evW > 0 ? "event"
                : pollW == 0 ? "immediate"
                : "poll";
            var uBlock = new Dictionary<string, object?>
            {
                ["condition"] = cond,
                ["query"] = until.Appears ?? until.Disappears ?? until.WindowAppears,
                ["ok"] = uOk,
                ["wakeSource"] = wake,
            };
            if (waitDelta != null)
            {
                uBlock["eventWakes"] = evW;
                uBlock["pollWakes"] = pollW;
            }
            if (uIdx >= 0 && results[uIdx].TryGetValue("ms", out var ums))
                uBlock["ms"] = ums;
            outDoc["until"] = uBlock;

            UiaPerf.Write(new
            {
                kind = "batch.until",
                runId,
                condition = cond,
                ok = uOk,
                wakeSource = wake,
                eventWakes = waitDelta != null ? evW : (long?)null,
                pollWakes = waitDelta != null ? pollW : (long?)null,
                ms = uIdx >= 0 && results[uIdx].TryGetValue("ms", out var um2)
                    ? um2 : null,
            });
        }

        UiaPerf.Write(new
        {
            kind = "batch.summary",
            runId,
            status = finalStatus,
            success,
            failFast,
            stepsExecuted = results.Count,
            totalSteps = meta.Count,
            failedStep,
            totalMs = sw.ElapsedMilliseconds,
            stepMs = results.Select(r =>
                r.TryGetValue("ms", out var m) ? m : null).ToArray(),
            untilWakeSource = until != null &&
                outDoc["until"] is Dictionary<string, object?> ub
                    ? ub["wakeSource"] : null,
        });

        return new CallToolResult
        {
            IsError = !success,
            Content = [new TextContentBlock
                { Text = JsonSerializer.Serialize(outDoc, J) }],
        };
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
        {
            var fallbackId = !string.IsNullOrWhiteSpace(runId) ? runId : "run_" + Guid.NewGuid().ToString("N")[..8];
            state = new RunState { RunId = fallbackId };
            _s.Runs[fallbackId] = state;
        }

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
        [Description("optional replacement or appended steps for the remainder of the plan (as JSON array)")] JsonElement? remainingSteps = null,
        [Description("optional updated variable bindings after human intervention")] Dictionary<string, string>? bindings = null,
        [Description("output verbosity: slim|full")] string? detail = null,
        CancellationToken ct = default)
    {
        if (BadDetail(detail) is { } bd) return bd;
        if (_s.Control.State == ComputerControlState.EmergencyStopped)
            return Error(OutcomeKind.EmergencyStopped, StoppedDetail);
        if (_s.Control.State == ComputerControlState.Paused)
            _s.Control.ResumeFromPause("computer_resume_run");
        if (!_s.Runs.TryGetValue(runId, out var state))
        {
            state = new RunState { RunId = runId, Slim = Slim(detail) };
            if (bindings != null)
            {
                foreach (var kv in bindings)
                    state.Bindings[kv.Key] = kv.Value;
            }
            _s.Runs[runId] = state;
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
        RunStep[]? parsedRemaining = null;
        if (remainingSteps.HasValue && remainingSteps.Value.ValueKind != JsonValueKind.Null && remainingSteps.Value.ValueKind != JsonValueKind.Undefined)
        {
            try
            {
                parsedRemaining = JsonSerializer.Deserialize<RunStep[]>(remainingSteps.Value.GetRawText(), J);
            }
            catch (Exception ex)
            {
                return Error(OutcomeKind.Malformed, $"Invalid remainingSteps JSON: {ex.Message}");
            }
        }

        RunStep[] stepsToRun;
        if (parsedRemaining is { Length: > 0 })
        {
            stepsToRun = parsedRemaining;
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
        [Description("permanent UI-map key \"app.element\" (e.g. \"notepad.document\", \"calculator.equals\") — seeds role/name/automationId/className/process from resources/uimap.json; explicit fields override map defaults")] string? Map = null,
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
        [Description("role name or text an ancestor in the UIA path must match")] string? Ancestor = null,
        [Description("coordinate x (when targeting an image or screen point)")] int? X = null,
        [Description("coordinate y (when targeting an image or screen point)")] int? Y = null,
        // -------- OCR fallback (pixel-space targeting when UIA can't see it) --------
        [Description("text to locate via OCR inside the target window — exact → case-insensitive contains → fuzzy; resolves to the word's clickable center. Fallback for canvas/image/custom-drawn UI the UIA tree misses")] string? OcrText = null,
        [Description("opt this target into OCR resolution — name/nameContains is matched against the window's OCR'd text instead of the UIA tree")] bool Ocr = false,
        [Description("BCP-47 OCR recognizer language (e.g. \"en-US\", \"de-DE\"); default = system OCR language")] string? OcrLang = null)
    {
        public string Summary()
        {
            var parts = new List<string>();
            if (Map != null) parts.Add($"map: {Map}");
            if (Role != null) parts.Add($"role: {Role}");
            if (Name != null) parts.Add($"name: \"{Name}\"");
            if (NameContains != null) parts.Add($"nameContains: \"{NameContains}\"");
            if (AutomationId != null) parts.Add($"id: #{AutomationId}");
            if (Within != null) parts.Add($"within: ${Within}");
            if (Value != null) parts.Add($"value: \"{Value}\"");
            if (ValueContains != null) parts.Add($"valueContains: \"{ValueContains}\"");
            if (OcrText != null) parts.Add($"ocrText: \"{OcrText}\"");
            if (Ocr) parts.Add("ocr: true");
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
        [Description("find|scan|for_each|focus|click|rightclick|doubleclick|invoke|toggle|select|hover|set_value|type|key|hotkey|look|mouse|scroll|drag|wait|wait_for|wait_for_change|wait_for_stable|assert|checkpoint|launch|human|human_takeover|pause_for_human")] string? Action = null,
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
        [Description("wait ms / wait_for* timeout; key: hold the key down this many ms (max 10000) instead of an instant tap — games read held keys per tick")] int? Ms = null,
        [Description("wait_for element-name query — accepts \"map:app.element\" (e.g. \"map:calculator.equals\")")] string? Query = null,
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
        [Description("adapter command or arguments dictionary")] Dictionary<string, object?>? Args = null,
        [Description("record this step's failure and keep executing the rest of the plan instead of pausing (batch continueOnError / failFast:false); EmergencyStopped/Cancelled still stop the run")] bool? ContinueOnError = null,
        [Description("deliver input via background window messages instead of foreground SendInput — no focus theft, works on unfocused/occluded windows; applies to click/invoke/toggle/type/set_value/close steps — unsupported kinds return NotSupported, never a silent SendInput fallback")] bool? Silent = null,
        [Description("look: relative mouse movement in raw counts, right = positive (camera look in games that capture the cursor)")] int? Dx = null,
        [Description("look: relative mouse movement in raw counts, down = positive")] int? Dy = null,
        [Description("mouse: button to hold, left|right|middle (default left) for ms milliseconds, pointer does not move")] string? Button = null)
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

    /// <summary>Lightweight action step for computer_batch.</summary>
    public sealed record BatchStep(
        [Description("action: click|invoke|type|key|hotkey|wait|launch|focus|toggle|select|set_value|scroll")] string? Do = null,
        [Description("alias for do")] string? Action = null,
        [Description("target element name, automationId, elementId (uia_*), or selector")] string? T = null,
        [Description("alias for t — string name/id, or a semantic object {elementId,name,role,hwnd,window,process,automationId}")] JsonElement? Target = null,
        [Description("value or text to input / hotkey string")] string? V = null,
        [Description("alias for v (text to type / value to set)")] JsonElement? Text = null,
        [Description("alias for v")] JsonElement? Value = null,
        [Description("target role (e.g. Button, Edit, MenuItem, CheckBox)")] string? Role = null,
        [Description("target window HWND or title (optional)")] string? Hwnd = null,
        [Description("wait duration in ms (do:wait) / scroll delta (do:scroll)")] int? Ms = null,
        [Description("alias for ms — number or numeric string; for do:launch also accepts readiness window|process|none")] JsonElement? WaitFor = null,
        [Description("type: press enter afterwards")] bool Submit = false,
        [Description("hotkey shorthand (e.g. 'ctrl+s')")] string? Keys = null,
        [Description("record this step's failure and keep executing the rest of the batch (default false)")] bool ContinueOnError = false)
    {
        /// <summary>Unknown JSON fields land here instead of being dropped —
        /// Batch rejects them so a typo never silently degrades a step.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>Compact form field specification for computer_batch.</summary>
    public sealed record FormFieldSpec(
        [Description("target element selector, ID, or name")] string Target,
        [Description("value or text to input")] string Value,
        [Description("target role (e.g. Edit, ComboBox); default Edit")] string? Role = "Edit")
    {
        /// <summary>Unknown JSON fields land here instead of being dropped.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>Wait condition specification for computer_batch.</summary>
    public sealed record BatchUntilSpec(
        [Description("wait until element with this name/text appears")] string? Appears = null,
        [Description("wait until element with this name/text disappears")] string? Disappears = null,
        [Description("wait until window with this title appears")] string? WindowAppears = null,
        [Description("wait until screen or element is stable")] bool? Stable = null,
        [Description("refine appears: target element must match this expected value")] string? Value = null,
        [Description("refine appears: target element must match this expected state")] string? State = null,
        [Description("timeout in milliseconds (default 5000)")] int? TimeoutMs = null,
        [Description("stability duration in milliseconds (default 200)")] int? StableMs = null)
    {
        /// <summary>Unknown JSON fields land here instead of being dropped.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    /// <summary>Read/extract specification for computer_batch.</summary>
    public sealed record BatchReadSpec(
        [Description("target element name to read")] string Target,
        [Description("properties to extract: name, value, role, isEnabled, isSelected, bounds")] string[]? Props = null,
        [Description("target role filter")] string? Role = null)
    {
        /// <summary>Unknown JSON fields land here instead of being dropped.</summary>
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    public sealed record FindQuery(
        [Description("element name or text substring")] string? Name = null,
        [Description("role filter, e.g. button, edit, checkbox")] string? Role = null,
        [Description("AutomationId")] string? AutomationId = null,
        [Description("window handle or title")] string? Hwnd = null,
        [Description("process name")] string? Process = null,
        [Description("subtree container elementId or hwnd")] string? Within = null,
        [Description("exact value match")] string? Value = null,
        [Description("exact className match")] string? ClassName = null)
    {
        public string Summary()
        {
            var parts = new List<string>();
            if (Role != null) parts.Add($"role: {Role}");
            if (Name != null) parts.Add($"name: \"{Name}\"");
            if (AutomationId != null) parts.Add($"id: #{AutomationId}");
            if (Within != null) parts.Add($"within: {Within}");
            if (Value != null) parts.Add($"value: \"{Value}\"");
            return parts.Count > 0 ? $"{{{string.Join(", ", parts)}}}" : "{empty query}";
        }
    }

    public sealed record BatchFindItemResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("index")] int Index,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("count"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("truncated"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Truncated = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("elements"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<BatchElementMatch>? Elements = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("error"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

    public sealed record BatchElementMatch(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
        [property: System.Text.Json.Serialization.JsonPropertyName("hwnd"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Hwnd = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("pid"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Pid = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("bounds")] string Bounds = "",
        [property: System.Text.Json.Serialization.JsonPropertyName("enabled"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? Enabled = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("value"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Value = null);

    public sealed record BatchInspectItemResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("index")] int Index,
        [property: System.Text.Json.Serialization.JsonPropertyName("hwnd")] string Hwnd,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("title"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Title = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("process"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Process = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("count"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Count = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("truncated"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? Truncated = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("elements"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] List<BatchElementMatch>? Elements = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("error"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

    public sealed record BatchReadItemResult(
        [property: System.Text.Json.Serialization.JsonPropertyName("index")] int Index,
        [property: System.Text.Json.Serialization.JsonPropertyName("target")] string Target,
        [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("props"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, object?>? Props = null,
        [property: System.Text.Json.Serialization.JsonPropertyName("error"), System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

    private static string ExtractErrorDetail(CallToolResult err)
    {
        if (err.Content is [TextContentBlock { Text: { } t }])
            return t;
        return "Unknown error";
    }
}
