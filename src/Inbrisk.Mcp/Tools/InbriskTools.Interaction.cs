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
    public Task<CallToolResult> ResetInput(
        [Description("also make detected input-blocking windows click-through")] bool? fixBlockingWindows = null,
        [Description("deprecated model parameter: cannot bypass healthy active input operations")] bool force = false,
        CancellationToken ct = default)
        => ResetInputCore(fixBlockingWindows, privilegedBypass: false, ct);

    internal Task<CallToolResult> PrivilegedResetInputAsync(
        bool? fixBlockingWindows = null,
        CancellationToken ct = default)
        => ResetInputCore(fixBlockingWindows, privilegedBypass: true, ct);

    internal async Task<CallToolResult> ResetInputCore(
        bool? fixBlockingWindows,
        bool privilegedBypass,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var arbiter = _s?.Arbiter ?? DesktopArbiter.Shared;
        var activePhysical = arbiter.GetExclusiveOwner();

        bool isEmergency = _s.Control.State == ComputerControlState.EmergencyStopped || EmergencyGate.IsStopped;
        bool isStaleOrHung = activePhysical != null && (DateTimeOffset.UtcNow >= activePhysical.ExpiresAt || !activePhysical.IsActive);

        // Round 6: Immediate bypass strictly requires EmergencyStopped, a verified stale/hung lease,
        // or explicit internal privileged authorization. Normal MCP model calls (including force:true)
        // are NEVER granted immediate bypass over healthy active physical leases.
        bool isRecoveryBypass = isEmergency || isStaleOrHung || privilegedBypass;

        IInputLease? normalLease = null;
        if (!isRecoveryBypass && activePhysical != null)
        {
            // Healthy physical input sequence in progress: do NOT corrupt active operation.
            // Wait behind the Global Desktop Gate to sweep cleanly once the healthy operation completes.
            try
            {
                normalLease = await arbiter.AcquireAsync("reset_input", LeaseKind.PhysicalInput, "clean input reset", timeout: TimeSpan.FromSeconds(5), ct: ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Do NOT cancel the owner. A lease that has not expired and is
                // still marked active is a HEALTHY operation that merely runs
                // longer than our 5s wait; cancelling it is a denial-of-service
                // lever for any caller of reset_input. Proceed with the
                // release-only sweep without holding a lease instead.
                Debug.WriteLine("reset_input timed out waiting for a healthy physical lease; proceeding without cancelling it.");
                isRecoveryBypass = true;
            }
        }
        else if (!isRecoveryBypass && activePhysical == null)
        {
            try
            {
                normalLease = await arbiter.AcquireAsync("reset_input", LeaseKind.PhysicalInput, "clean input reset", timeout: TimeSpan.FromSeconds(2), ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"reset_input lease acquire failed, proceeding without lease: {ex.Message}");
            }
        }

        try
        {
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
                ["resetReason"] = isRecoveryBypass ? (isEmergency ? "emergency stop recovery" : (isStaleOrHung ? "stale/hung lease recovery" : "privileged recovery")) : "normal input reset",
                ["recoveryBypassedGate"] = isRecoveryBypass,
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
        finally
        {
            normalLease?.Dispose();
        }
    }

    // ------------------------------------------------------------ actions
    // Mutating tools self-resolve: pass elementId (from find/observe) OR a
    // semantic `target` {window,process,role,name,automationId} — the runtime
    // does find → re-resolve → foreground → act → verify → post-state in one
    // call. No computer_observe needed when the target is already known.

    [McpServerTool(Name = "computer_click"), Description(
        "Click an element. Prefer computer_invoke for buttons/menu items " +
        "(no focus/geometry needed). Targeting styles: elementId, " +
        "semantic target {window,process,role,name,automationId,labelledBy," +
        "nearText,within,ancestor}, or coordinates x+y (desktop or OCR coordinates; " +
        "frameId/observationId optional). button:left|right|double. " +
        "For apps with no UIA elements (Blender, games, custom OpenGL) pass " +
        "rx+ry — px offsets from the target window's top-left — or " +
        "relativeTo:\"window\" to reinterpret x+y the same way; the anchor " +
        "window comes from target.hwnd/window/process, else the foreground window. " +
        "When executing multiple clicks, typing, or hotkeys in sequence, PREFER computer_batch to collapse them into a single fast roundtrip.")]
    public async Task<CallToolResult> Click(
        [Description("elementId from observe/find")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("frameId the point was derived from")] long? frameId = null,
        [Description("observationId the point was derived from")] long? observationId = null,
        [Description("image-space x (or window-relative when relativeTo:\"window\")")] int? x = null,
        [Description("image-space y (or window-relative when relativeTo:\"window\")")] int? y = null,
        [Description("left|right|double")] string? button = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("window-relative x in px from the anchor window's top-left — for element-less windows; implies relativeTo:\"window\"")] int? rx = null,
        [Description("window-relative y (see rx)")] int? ry = null,
        [Description("coordinate space for x,y: \"screen\" (default, absolute desktop px) | \"window\" (x,y are offsets from the anchor window's top-left)")] string? relativeTo = null,
        [Description("deliver via background window message instead of synthesized input — no focus theft, works on unfocused windows. Requires a control element target; coordinate/OCR-point and unsupported kinds fail NotSupported — never a SendInput fallback")] bool? silent = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            int? clickX = rx ?? x ?? target?.X;
            int? clickY = ry ?? y ?? target?.Y;
            bool isDouble = string.Equals(button, "double", StringComparison.OrdinalIgnoreCase);
            return await _s.GhostCoordinator.ClickAsync(
                clickX, clickY, button, isDouble, elementId, target, ct).ConfigureAwait(false);
        }

        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");

        // Window-relative coordinate path — the fallback for windows that
        // expose no UIA elements (Blender, games, custom OpenGL canvases).
        // rx/ry (or x/y under relativeTo:"window") are resolved against the
        // anchor window's live GetWindowRect and dispatched as a raw desktop
        // point (FrameId=0 — no captured-frame identity check; the rect is
        // sampled fresh at click time instead).
        var windowRelative = rx != null || ry != null ||
            string.Equals(relativeTo, "window", StringComparison.OrdinalIgnoreCase);
        ImagePoint? point;
        string? synthId = null;
        long anchorHwnd = 0;
        int relX = 0, relY = 0, absX = 0, absY = 0;
        if (windowRelative)
        {
            var inX = rx ?? x ?? target?.X;
            var inY = ry ?? y ?? target?.Y;
            if (inX == null || inY == null)
                return Error(OutcomeKind.Malformed,
                    "window-relative click requires rx+ry (or x+y with relativeTo:\"window\")");
            if (!TryResolveWindowAnchor(elementId, target, out var h, out var anchorErr))
                return anchorErr!;
            if (!GetWindowRect((IntPtr)h, out var rc) ||
                rc.Right - rc.Left <= 0 || rc.Bottom - rc.Top <= 0)
                return Error(OutcomeKind.WindowLost,
                    $"anchor window 0x{h:X} has no usable rectangle (closed or minimized)");
            anchorHwnd = h;
            relX = inX.Value; relY = inY.Value;
            absX = rc.Left + relX; absY = rc.Top + relY;
            // synthetic element id in the same shape the element/result
            // schema uses — lets callers correlate element-less clicks
            synthId = $"win:0x{h:X}@({relX},{relY})";
            point = new ImagePoint(absX, absY, FrameId: 0);
        }
        else
        {
            point = MakePoint(frameId, observationId, x ?? target?.X, y ?? target?.Y);
        }

        // A pure window locator (process/hwnd/title only) must not trigger a
        // UIA element search — element-less windows return zero matches and
        // would fail before the coordinate path runs. Element-identifying
        // fields still resolve normally (element wins over the point).
        UiElement? el;
        OcrTargetHit? ocrHit = null;
        // OCR targeting — elementId "ocr:<hwnd>:<idx>" (the synthetic ids
        // computer_find registers and computer_observe ocr:true prints) or
        // target:{ocrText:"X"} / {ocr:true, name:"X"}. Resolves to the
        // word's registered element or its center point, then flows through
        // the same guarded Act pipeline as any other target.
        if (TryResolveOcrTarget(elementId, target, out el, out var ocrPoint,
                out ocrHit, out var ocrErr))
        {
            if (ocrErr != null) return ocrErr;
            point ??= ocrPoint; // explicit x/y or rx/ry coordinates still win
        }
        else if (windowRelative && elementId == null && target?.ElementId == null &&
            target?.Role == null && target?.Name == null &&
            target?.AutomationId == null)
        {
            el = null;
        }
        else
        {
            el = ResolveTargetElement(elementId, target, out var err, out _, "invoke");
            if (err != null) return err;
        }

        // A bare coordinate click that lands outside the virtual screen hits
        // nothing — report Malformed with the real bounds instead of a
        // fabricated success (SendInput happily "clicks" offscreen).
        if (el == null && point is { } clickPt)
        {
            var vb = _s.Rt.WindowService.GetVirtualDesktopBounds();
            if (clickPt.X < vb.X || clickPt.Y < vb.Y ||
                clickPt.X >= vb.X + vb.Width || clickPt.Y >= vb.Y + vb.Height)
                return Error(OutcomeKind.Malformed,
                    $"click point ({clickPt.X},{clickPt.Y}) is outside the " +
                    $"virtual screen ({vb.X},{vb.Y} {vb.Width}x{vb.Height}) — " +
                    "re-observe for fresh coordinates");
        }

        var res = await Act(new AgentAction(button?.Equals("right", StringComparison.OrdinalIgnoreCase) == true
                ? AgentActionKind.RightClick
                : button?.Equals("double", StringComparison.OrdinalIgnoreCase) == true
                    ? AgentActionKind.DoubleClick
                    : AgentActionKind.Click,
            ElementId: el?.Id, Point: point, Silent: silent == true),
            ct, observe, operationId);
        return synthId != null
            ? AnnotateWindowRelativeResult(res, synthId, anchorHwnd, relX, relY, absX, absY)
            : ocrHit != null ? AnnotateOcrResult(res, ocrHit) : res;
    }

    [McpServerTool(Name = "computer_invoke"), Description(
        "Trigger an element's default action via UIA InvokePattern — the " +
        "preferred way to press buttons and menu items: works without " +
        "foreground focus or pixel hit-testing. Accepts elementId or " +
        "semantic target. Example: {process:\"notepad\", role:\"menuitem\", " +
        "name:\"Dosya\"} opens the File menu. Also accepts OCR ids " +
        "(elementId \"ocr:<hwnd>:<idx>\") and target:{ocrText|ocr:true} — " +
        "invoking a word hits its clickable center. When executing multiple actions in sequence, PREFER computer_batch.")]
    public async Task<CallToolResult> Invoke(
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?, ocrText?, ocr?, ocrLang?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("deliver via background window message (BM_CLICK) — no focus theft; targets that cannot be messaged fail NotSupported — never a SendInput fallback")] bool? silent = null,
        CancellationToken ct = default)
    {
        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");
        UiElement? el;
        OcrTargetHit? ocrHit = null;
        ImagePoint? ocrPoint = null;
        if (TryResolveOcrTarget(elementId, target, out el, out ocrPoint,
                out ocrHit, out var ocrErr))
        {
            if (ocrErr != null) return ocrErr;
        }
        else
        {
            el = ResolveTargetElement(elementId, target, out var err, out _, "invoke");
            if (err != null) return err;
        }
        if (el == null && ocrPoint == null) return
            Error(OutcomeKind.Malformed, "elementId or target required");
        // Invoking a bare OCR word = clicking its center — Invoke with a
        // point target has no executor path, so dispatch Click.
        var res = await Act(new AgentAction(
            el == null ? AgentActionKind.Click : AgentActionKind.Invoke,
            ElementId: el?.Id, Point: ocrPoint, Silent: silent == true),
            ct, observe, operationId);
        return ocrHit != null ? AnnotateOcrResult(res, ocrHit) : res;
    }

    [McpServerTool(Name = "computer_set_value"), Description(
        "Atomically set an element's whole value via UIA ValuePattern — " +
        "verified by readback. Best for text fields and combo boxes; use " +
        "computer_type for keystroke-level editing or append/insert at a " +
        "caret position. The resolver prefers editable controls — a Text " +
        "label with the same name never wins unless you ask for role:Text. " +
        "Example: {target:{process:\"notepad\", role:\"edit\", " +
        "labelledBy:\"Dosya adı:\"}, value:\"x.txt\"}. " +
        "When setting multiple fields in forms or dialogs, PREFER computer_batch(set: [...]).")]
    public Task<CallToolResult> SetValue(
        [Description("value to set")] string value,
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");
        var el = ResolveTargetElement(elementId, target, out var err, out _, "edit");
        if (err != null) return Task.FromResult(err);
        if (el == null) return Task.FromResult(
            Error(OutcomeKind.Malformed, "elementId or target required"));
        return Act(new AgentAction(AgentActionKind.SetValue, ElementId: el.Id,
            Text: value), ct, observe, operationId);
    }

    [McpServerTool(Name = "computer_type"), Description(
        "High-level text editing in ONE call — focus, foreground and caret " +
        "navigation are internal. mode=replace|append|insert, " +
        "position=current|start|end, submit=Enter afterwards. Uses " +
        "ValuePattern when the control supports it (atomic + verified), " +
        "else real keystrokes. When typing into canvas/OpenGL/game windows " +
        "without UIA controls (e.g. Blender file dialog), omit elementId and target " +
        "to type directly into the focused window. When combined with clicks/navigation, PREFER computer_batch.")]
    public async Task<CallToolResult> Type(
        [Description("text to type")] string text,
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window? (title substring, localized — prefer process), process? (exe name), role?, name?, automationId?, ocrText?, ocr?, ocrLang?}")] TargetSpec? target = null,
        [Description("replace|append|insert")] string? mode = null,
        [Description("current|start|end")] string? position = null,
        [Description("press Enter after typing")] bool submit = false,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("deliver the text via window message (WM_SETTEXT/EM_REPLACESEL) instead of keystrokes — no focus theft. Requires an element target and mode replace|append; caret-level insert and submit have no message-only equivalent and fail NotSupported")] bool? silent = null,
        [Description(GhostOverrideDoc)] bool? ghost = null,
        CancellationToken ct = default)
    {
        if (GhostOn(ghost))
        {
            return await _s.GhostCoordinator.TypeAsync(text, submit, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(text))
            return Error(OutcomeKind.Malformed,
                "computer_type requires non-empty text — an empty string types nothing");

        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");
        var normArgs = $"{text}|{elementId}|{target?.ToString()}|{mode}|{position}|{submit}|{observe}|{silent}";
        if (_s.Deduplicator.TryDeduplicateMutation(operationId, "computer_type", normArgs, _s.Telemetry, out var conflictError) is { } deduped)
            return deduped;
        if (conflictError != null)
            return conflictError;

        UiElement? el;
        OcrTargetHit? ocrHit = null;
        ImagePoint? ocrPoint = null;
        if (TryResolveOcrTarget(elementId, target, out el, out ocrPoint,
                out ocrHit, out var ocrErr))
        {
            if (ocrErr != null) return ocrErr;
        }
        else
        {
            el = ResolveTargetElement(elementId, target, out var err, out _, "edit");
            if (err != null) return err;
        }
        var modeN = (mode ?? "insert").ToLowerInvariant();
        var posN = (position ?? (modeN == "append" ? "end" : "current"))
            .ToLowerInvariant();
        if (modeN is not ("replace" or "append" or "insert") ||
            posN is not ("current" or "start" or "end"))
            return Error(OutcomeKind.Malformed,
                "mode must be replace|append|insert; position current|start|end");

        List<AgentAction> steps;
        if (silent == true)
        {
            // Message-based delivery can only set a control's whole text —
            // caret positioning, keypresses and focused-window typing have
            // no honest message equivalent. Refuse rather than degrade to
            // SendInput.
            if (submit)
                return Error(OutcomeKind.NotSupported,
                    "silent type cannot submit — Enter is a keypress with " +
                    "no message-only equivalent; omit submit or retry with " +
                    "silent:false");
            if (el == null)
                return Error(OutcomeKind.NotSupported,
                    "silent type requires an element target — text delivered " +
                    "by window message needs a control hwnd; OCR/coordinate/" +
                    "focused typing is SendInput semantics — retry with " +
                    "silent:false");
            var cur = Prop(el, "value")?.ToString() ?? "";
            var setText = modeN switch
            {
                "replace" => text,
                "append" when posN == "end" => cur + text,
                "append" when posN == "start" => text + cur,
                _ => (string?)null,
            };
            if (setText == null)
                return Error(OutcomeKind.NotSupported,
                    "silent type supports mode replace and append " +
                    "(whole-text WM_SETTEXT); insert and caret positioning " +
                    "have no message-only equivalent — retry with silent:false");
            steps = [new AgentAction(AgentActionKind.SetValue,
                ElementId: el.Id, Text: setText, Silent: true)];
        }
        else
        {
            steps = BuildTypeSteps(el, text, modeN, posN, submit);
            // OCR word target → click its center first so the field under
            // it receives focus, then the usual navigation/type steps.
            if (ocrPoint != null && el == null)
                steps.Insert(0, new AgentAction(AgentActionKind.Click,
                    Point: ocrPoint));
            // An explicit window target (target:{hwnd|window|process})
            // means the text is destined for THAT window — focus it first;
            // without this the keystrokes blind-hit whatever is foreground.
            if (el == null && ocrPoint == null)
            {
                var (winSpecified, winHwnd) = ResolveExplicitWindow(target, null);
                if (winSpecified && winHwnd == null)
                    return Error(OutcomeKind.TargetNotFound,
                        "type target window not found — the named hwnd/title/process resolves to no live window");
                if (winHwnd is { } focusHwnd)
                    steps.Insert(0, new AgentAction(AgentActionKind.FocusWindow, Hwnd: focusHwnd));
            }
        }
        var res = await ActChain(steps, el?.Id, ct, observe);
        _s.Deduplicator.RecordMutation(operationId, "computer_type", normArgs, res);
        return ocrHit != null ? AnnotateOcrResult(res, ocrHit) : res;
    }

    /// <summary>Expand a high-level type request into primitive actions:
    /// click-to-focus + caret navigation + optional select-all + type +
    /// optional submit. UIA ValuePattern is preferred when the element
    /// supports it — atomic and readback-verified.</summary>
    private static List<AgentAction> BuildTypeSteps(UiElement? el, string text,
        string modeN, string posN, bool submit)
    {
        var steps = new List<AgentAction>();
        var canSet = el?.IsActionSupported("setvalue") == true;
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

    [McpServerTool(Name = "computer_key"), Description(
        "Press a single key (enter, tab, escape, arrows, f1-f12...) — goes " +
        "to the FOCUSED control of the foreground window. To press a key in " +
        "a specific window, focus it first (computer_focus_window or a " +
        "computer_run focus step). count repeats the press. For multi-step keystrokes, PREFER computer_batch.")]
    public Task<CallToolResult> Key(
        [Description("key name")] string key,
        [Description("press count")] int count = 1,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("deliver via background window message — no focus theft. Key presses have no message-only equivalent: this fails NotSupported instead of falling back to SendInput")] bool? silent = null,
        CancellationToken ct = default)
    {
        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");
        return Act(new AgentAction(AgentActionKind.Key, Key: key, Count: count,
            Silent: silent == true), ct, operationId: operationId);
    }

    [McpServerTool(Name = "computer_hotkey"), Description(
        "Press a key combination — key=\"s\" modifiers=[\"ctrl\"] or the " +
        "shorthand keys=\"ctrl+s\" sends Ctrl+S to the FOREGROUND window, or " +
        "to a specific window via window/target (hwnd, title substring, or " +
        "process — the window is focused first). For multi-step interactions, PREFER computer_batch.")]
    public Task<CallToolResult> Hotkey(
        [Description("key name")] string? key = null,
        [Description("modifier names: ctrl,shift,alt,win")] string[]? modifiers = null,
        [Description("combo shorthand like \"ctrl+s\" — alternative to key+modifiers")] string? keys = null,
        [Description("deliver the keystroke to a specific window: hwnd (hex/decimal) or title substring — focuses it first so keys don't blind-hit whatever is foreground")] string? window = null,
        [Description("window target object: {hwnd, window, process} — alternative to window")] TargetSpec? target = null,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        [Description("deliver via background window message — no focus theft. Hotkeys have no message-only equivalent: this fails NotSupported instead of falling back to SendInput")] bool? silent = null,
        CancellationToken ct = default)
    {
        _s.Telemetry.IncSequentialSingleAction();
        Inbrisk.Core.PerfTrace.Count("sequentialSingleAction");
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

        // Explicit window routing: focus the named hwnd/title/process window
        // first so the keystroke reaches it instead of the blind foreground.
        var (winSpecified, winHwnd) = ResolveExplicitWindow(target, window);
        if (winSpecified && winHwnd == null)
            return Task.FromResult(Error(OutcomeKind.TargetNotFound,
                "hotkey target window not found — the named hwnd/title/process resolves to no live window"));

        var action = new AgentAction(AgentActionKind.Hotkey, Key: key,
            Modifiers: modifiers, Silent: silent == true);
        if (winHwnd is { } wh)
            return ActChain([new AgentAction(AgentActionKind.FocusWindow, Hwnd: wh), action],
                null, ct);
        return Act(action, ct, operationId: operationId);
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
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _);
        if (err != null) return Task.FromResult(err);
        return Act(new AgentAction(AgentActionKind.Scroll, ElementId: el?.Id,
            Point: MakePoint(frameId, observationId, x, y), Delta: delta), ct, observe, operationId: operationId);
    }

    [McpServerTool(Name = "computer_scroll_into_view"), Description(
        "Scroll an offscreen element into view using native UIA ScrollItemPattern. " +
        "Direct and instant — brings virtualized or out-of-viewport elements " +
        "into the viewport without blind wheel scrolling.")]
    public async Task<CallToolResult> ScrollIntoView(
        [Description("elementId")] string? elementId = null,
        [Description("semantic target: {elementId?, window?, process?, role?, name?, automationId?}")] TargetSpec? target = null,
        [Description("piggyback scoped observation of the resulting UI state (zero-turn feedback)")] bool observe = false,
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _, "invoke", includeOffscreen: true);
        if (err != null) return err;
        if (el == null) return Error(OutcomeKind.Malformed, "elementId or target required");
        return await Act(new AgentAction(AgentActionKind.ScrollIntoView, ElementId: el.Id), ct, observe, operationId: operationId);
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
        [Description("optional client operation ID for safe transport retry deduplication")] string? operationId = null,
        CancellationToken ct = default)
    {
        var el = ResolveTargetElement(elementId, target, out var err, out _);
        if (err != null) return Task.FromResult(err);
        return Act(new AgentAction(AgentActionKind.Drag, ElementId: el?.Id,
            Point: MakePoint(frameId, observationId, x, y),
            To: new ImagePoint(toX, toY, toFrameId ?? 0, toObservationId ?? 0)), ct, operationId: operationId);
    }
}
