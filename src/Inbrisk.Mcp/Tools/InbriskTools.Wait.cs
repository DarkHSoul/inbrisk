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
    // ------------------------------------------------------------ waits

    [McpServerTool(Name = "computer_wait"), Description(
        "Sleep ms — cancellable; returns immediately on cancel.")]
    public Task<CallToolResult> Wait(
        [Description("milliseconds")] int ms, CancellationToken ct = default)
        => Act(new AgentAction(AgentActionKind.Wait, Ms: ms), ct);

    [McpServerTool(Name = "computer_wait_for"), Description(
        "Wait until an element matching query/target appears, a window/process condition is met, or until a condition (gone, state, value) holds. " +
        "query accepts a string (\"Paint\" — matches element names AND window titles) or an object ({process, role, name, hwnd, window}). " +
        "process alone waits for that process to own a window; pid waits on a numeric process id. Polls live state each ~200ms — already-open windows/elements satisfy immediately.")]
    public async Task<CallToolResult> WaitFor(
        [Description("element name or query to wait for — string (\"map:app.element\" e.g. \"map:calculator.equals\") or object ({process,role,name,hwnd,window})")] JsonElement? query = null,
        [Description("element name alias for query")] string? name = null,
        [Description("element text alias for query")] string? text = null,
        [Description("structured target specification")] TargetSpec? target = null,
        [Description("window hwnd to observe in background (defaults to target.hwnd or scoped/active window)")] string? hwnd = null,
        [Description("target process name (or numeric pid) to wait on")] string? process = null,
        [Description("numeric process id to wait on")] int? pid = null,
        [Description("expected element state (e.g. 'selected', 'checked', 'enabled', 'disabled')")] string? expectedState = null,
        [Description("expected element value")] string? expectedValue = null,
        [Description("element value alias for expectedValue")] string? value = null,
        [Description("if true, wait until the element is gone/disappears")] bool gone = false,
        [Description("minimum number of matching elements")] int? minCount = null,
        [Description("abort wait immediately if an unexpected modal dialog or error appears (default true)")] bool stopOnDialog = true,
        [Description("timeout ms (default 5000)")] int ms = 5000,
        [Description("alias of ms — models often write timeoutMs")] int? timeoutMs = null,
        CancellationToken ct = default)
    {
        // query accepts a string OR a target object — object form crashed the
        // string binder ("Cannot get the value of a token type 'StartObject'").
        string? queryStr = null;
        TargetSpec? qObj = null;
        if (query is { } qe)
        {
            if (qe.ValueKind == JsonValueKind.String)
                queryStr = qe.GetString();
            else if (qe.ValueKind == JsonValueKind.Object)
            {
                try { qObj = qe.Deserialize<TargetSpec>(J); }
                catch (Exception ex)
                {
                    return Error(OutcomeKind.Malformed,
                        $"wait_for query object is not a valid target: {ex.Message}");
                }
            }
            else
            {
                return Error(OutcomeKind.Malformed,
                    "wait_for query must be a string or a target object ({process,role,name,hwnd,window})");
            }
        }

        process ??= qObj?.Process;
        hwnd ??= qObj?.Hwnd;
        var within = target?.Within ?? qObj?.Within;
        var elId = target?.ElementId ?? qObj?.ElementId;
        var autoId = target?.AutomationId ?? qObj?.AutomationId;
        var roleStr = qObj?.Role ?? target?.Role;
        Core.Role? roleF = null;
        if (roleStr != null)
        {
            if (!Enum.TryParse<Core.Role>(roleStr, true, out var rr))
                return Error(OutcomeKind.Malformed, $"unknown role '{roleStr}'");
            roleF = rr;
        }
        // a non-numeric window field is a window-TITLE condition
        var titleQ = (qObj?.Window ?? target?.Window) is { } tw && ParseHwnd(tw) == null
            ? tw : null;
        hwnd ??= ParseHwnd(qObj?.Window ?? target?.Window)?.ToString();
        // numeric process = a pid condition
        int? pidCond = pid;
        if (pidCond == null && int.TryParse(process, out var procPid)) pidCond = procPid;
        var processName = pidCond != null && !string.IsNullOrWhiteSpace(process) && int.TryParse(process, out _)
            ? null : process;

        var targetQuery = queryStr ?? name ?? text ?? qObj?.Name ?? qObj?.NameContains
            ?? target?.Name ?? target?.NameContains ?? MapQuery(target?.Map);
        var targetVal = expectedValue ?? value;
        long? explicitHwnd = ParseHwnd(hwnd ?? target?.Hwnd);

        // hwnd-shaped 'within' narrows the element scan to that window
        long? withinHwnd = null;
        string? withinScopeId = null;
        if (!string.IsNullOrWhiteSpace(within))
        {
            if (ParseHwnd(within) is { } wh2) withinHwnd = wh2;
            else if (_s.Rt.Parts.Registry.EnsureAlive(within) is { } container)
            {
                withinScopeId = container.Handle.BackendRef;
                withinHwnd = container.Handle.Recipe.Hwnd ?? container.Hwnd;
            }
        }

        var hasCondition = targetQuery != null || elId != null || titleQ != null
            || expectedState != null || targetVal != null
            || explicitHwnd != null || processName != null || pidCond != null;
        if (!hasCondition)
            return Error(OutcomeKind.Malformed,
                "wait_for requires query or condition (query/name/text, target, process, pid, hwnd, or expected state/value)");

        var timeout = timeoutMs ?? ms;
        var want = Math.Max(1, minCount ?? 1);
        var sw = Stopwatch.StartNew();
        var label = targetQuery ?? titleQ ?? processName
            ?? (pidCond != null ? $"pid:{pidCond}" : null)
            ?? (explicitHwnd != null ? $"0x{explicitHwnd.Value:X}" : null)
            ?? elId ?? "condition";

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // ---- resolve current state each poll: windows, processes,
                // elements — never event-only, so a pre-existing match
                // satisfies immediately.
                WindowInfo? winHit = null;
                if (explicitHwnd is { } eh)
                    winHit = _s.Rt.WindowService.GetTopLevelWindow(eh);
                if (winHit == null && pidCond is { } pc)
                    winHit = _s.Rt.Windows().FirstOrDefault(w => w.Pid == pc);
                if (winHit == null && !string.IsNullOrWhiteSpace(processName))
                    winHit = FindBestWindowForProcess(processName);
                var titleHit = titleQ != null
                    ? _s.Rt.Windows().FirstOrDefault(w =>
                        w.Title.Contains(titleQ, StringComparison.OrdinalIgnoreCase))
                    : null;
                // element-name queries also match already-open window titles
                // ("Paint" hits "Adsız - Paint") — a wait for a window must
                // see windows that existed before the call.
                if (titleHit == null && targetQuery != null && elId == null)
                    titleHit = _s.Rt.Windows().FirstOrDefault(w =>
                        w.Title.Contains(targetQuery, StringComparison.OrdinalIgnoreCase));

                int found = 0;
                UiElement? elMatch = null;
                var wantsElements = elId != null || targetQuery != null
                    || expectedState != null || targetVal != null;
                if (elId != null)
                {
                    elMatch = _s.Rt.Parts.Registry.EnsureAlive(elId);
                    if (elMatch != null) found++;
                }
                else if (wantsElements)
                {
                    int? pidF = pidCond ?? winHit?.Pid;
                    var spec = new FindSpec(
                        Hwnd: withinHwnd ?? explicitHwnd ?? winHit?.Hwnd ?? _s.ScopeHwnd,
                        Pid: pidF,
                        Role: roleF, Name: targetQuery, AutomationId: autoId,
                        ScopeElementId: withinScopeId,
                        FirstOnly: minCount == null);
                    var els = await _s.Rt.FindAsync(spec, ct).ConfigureAwait(false);
                    IEnumerable<UiElement> filtered = els;
                    if (!string.IsNullOrWhiteSpace(targetQuery))
                        filtered = filtered.Where(e =>
                            e.Name?.Contains(targetQuery, StringComparison.OrdinalIgnoreCase) == true);
                    if (!string.IsNullOrWhiteSpace(targetVal))
                        filtered = filtered.Where(e =>
                            (Prop(e, "value")?.ToString() ?? "").Contains(targetVal, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(expectedState))
                        filtered = filtered.Where(e =>
                            (Prop(e, "state")?.ToString() ?? "").Contains(expectedState, StringComparison.OrdinalIgnoreCase));
                    var fList = filtered.ToList();
                    found += fList.Count;
                    elMatch = fList.FirstOrDefault();
                }
                if (winHit != null || titleHit != null) found += want; // window condition counts fully

                var present = found >= want;
                var satisfied = gone ? !present : present;
                if (satisfied)
                {
                    var w = winHit ?? titleHit;
                    return Text(JsonSerializer.Serialize(new
                    {
                        action = $"wait_for(\"{label}\")",
                        status = "Ok",
                        success = true,
                        method = "wait_for",
                        durationMs = sw.ElapsedMilliseconds,
                        matched = w != null
                            ? (object)new { hwnd = $"0x{w.Hwnd:X}", title = w.Title, pid = w.Pid, process = w.ProcessName }
                            : elMatch != null
                                ? new { elementId = elMatch.Id, name = elMatch.Name, role = elMatch.Role.ToString() }
                                : null,
                        provenance = Provenance("uia"),
                    }, J));
                }

                if (sw.ElapsedMilliseconds >= timeout)
                    return Error(OutcomeKind.Timeout,
                        $"timeout ({timeout}ms) waiting for '{label}'" +
                        (gone ? " to disappear" : " to appear"), sw);

                if (stopOnDialog &&
                    _s.Rt.WindowService.GetActiveBlockingPopup() is { } dlg)
                    return Error(OutcomeKind.Failed,
                        $"unexpected dialog 0x{dlg.Hwnd:X} \"{dlg.Title}\" ({dlg.ProcessName}) appeared while waiting for '{label}'", sw);

                await Task.Delay(200, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return _s.Control.State == ComputerControlState.EmergencyStopped
                ? Error(OutcomeKind.EmergencyStopped, StoppedDetail, sw)
                : Error(OutcomeKind.Cancelled, "request cancelled", sw);
        }
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
}
