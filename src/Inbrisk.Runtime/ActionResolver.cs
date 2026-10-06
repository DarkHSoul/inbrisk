using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>A captured frame + its observation metadata — everything needed
/// to decide whether an image coordinate is still safe to act on.</summary>
public sealed record FrameRef(RawFrame Raw, ObsFrameRef Meta);

/// <summary>Per-step context the resolver validates against — the frames that
/// produced the current observation (FrameId → transform for grounding) and
/// the monitor factory for wait actions.</summary>
public sealed record ObsContext(
    long ObservationId,
    long? ActiveHwnd,
    IReadOnlyDictionary<long, FrameRef> Frames,
    Func<ChangeMonitor> MonitorFactory,
    /// <summary>Image points older than this are rejected even if the frame
    /// is still attached to the current observation.</summary>
    int MaxFrameAgeMs = 15_000,
    /// <summary>Optional set of observation ids a point may claim. Null →
    /// the point's ObservationId must equal ObservationId (agent-loop
    /// semantics). MCP sessions pass their minted-observation set.</summary>
    IReadOnlySet<long>? ValidObservationIds = null);

// --------------------- OCR fallback resolution -----------------------

/// <summary>A request to locate rendered text inside a window via OCR —
/// the semantic fallback when the UIA tree can't see the target.</summary>
public sealed record OcrResolveRequest(
    /// <summary>Text to locate — a word or short phrase (e.g. the spec's
    /// ocrText, or name/nameContains when the target opted into OCR).</summary>
    string Text,
    /// <summary>Window to capture; null → current foreground window.</summary>
    long? Hwnd = null,
    /// <summary>BCP-47 recognizer language (e.g. "en-US"); null = default.</summary>
    string? Language = null,
    /// <summary>Cap on candidates reported back on ambiguity.</summary>
    int MaxCandidates = 8,
    /// <summary>Per-recognition timeout; null = engine default.</summary>
    TimeSpan? Timeout = null);

/// <summary>One OCR'd candidate that matched the query, in desktop space.</summary>
public sealed record OcrMatch(
    string Text, RectPx Bounds, double Score, double? Confidence)
{
    public (int X, int Y) Center => Bounds.Center;
}

/// <summary>
/// Synthetic resolution produced by the OCR fallback: the matched text's
/// center point in desktop space plus its match provenance. Consumable
/// anywhere a point target is accepted.
/// </summary>
public sealed record OcrResolution(
    int X, int Y,
    string MatchedText,
    RectPx Bounds,
    double Score,
    double? Confidence,
    long Hwnd,
    int WordCount,
    int MatchCount,
    string Language)
{
    /// <summary>Executor-ready point target.</summary>
    public TargetRef ToTargetRef() => TargetRef.At(X, Y);

    /// <summary>Direct desktop point — FrameId 0 carries no frame binding,
    /// so ImageToDesktop passes the coordinates through unchanged.</summary>
    public ImagePoint ToImagePoint() => new(X, Y, FrameId: 0);
}

/// <summary>
/// Canonical action → validated execution. The chain:
///   schema → target exists → frame freshness/geometry → safety (in Executor)
///   → pre-state snapshot → Executor.Perform → auto-verification →
///   OutcomeKind classification + evidence.
/// Malformed actions never reach Windows.
/// </summary>
public sealed class ActionResolver
{
    private readonly ElementRegistry _registry;
    private readonly Executor _executor;
    private readonly WaitService _waits;
    private readonly IWindowService _windows;
    private readonly AutoVerifier _verifier;
    private readonly ICaptureService? _capture;
    private readonly IOcrService? _ocr;

    public ActionResolver(ElementRegistry registry, Executor executor,
        WaitService waits, IWindowService windows, AutoVerifier verifier,
        ICaptureService? capture = null, IOcrService? ocr = null)
    {
        _registry = registry;
        _executor = executor;
        _waits = waits;
        _windows = windows;
        _verifier = verifier;
        _capture = capture;
        _ocr = ocr;
    }

    public StepOutcome Execute(AgentAction a, ObsContext ctx, ActionContext actx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            switch (a.Kind)
            {
                // ---- waits: real runtime signals, not executor intents ----
                case AgentActionKind.Wait:
                    if (a.Ms is not > 0) return Malformed(sw, "wait requires ms>0");
                    if (actx.Ct.WaitHandle.WaitOne(Math.Min(a.Ms.Value, 30_000)))
                        return Done(sw, OutcomeKind.Cancelled, "wait", "cancelled", false);
                    return Done(sw, OutcomeKind.Verified, "wait", $"slept {a.Ms}ms");

                case AgentActionKind.WaitFor:
                {
                    if (string.IsNullOrWhiteSpace(a.Query) && string.IsNullOrWhiteSpace(a.ElementId) &&
                        string.IsNullOrWhiteSpace(a.ExpectedState) && string.IsNullOrWhiteSpace(a.ExpectedValue) && !a.Gone)
                        return Malformed(sw, "wait_for requires query or condition");

                    // For a "map:app.element" query the map's process seed is
                    // the scope — the implicit active-window fallback must not
                    // trap the wait in an unrelated focused window. An explicit
                    // hwnd/pid/element scope still wins over the map seed.
                    var isMapQuery = a.Query?.StartsWith(UiMap.Prefix,
                        StringComparison.OrdinalIgnoreCase) == true;
                    var scopeHwnd = a.Hwnd ??
                        (!isMapQuery && a.Pid == null && a.ScopeElementId == null &&
                         ctx.ActiveHwnd is > 0 ? ctx.ActiveHwnd : null);

                    var cond = new WaitCondition(
                        Name: a.Query,
                        ExpectedState: a.ExpectedState,
                        ExpectedValue: a.ExpectedValue,
                        Gone: a.Gone,
                        MinCount: a.Count,
                        ScopeElementId: a.ScopeElementId ?? a.ElementId,
                        Hwnd: scopeHwnd,
                        Pid: a.Pid,
                        StopOnUnexpectedDialog: a.StopOnUnexpectedDialog);

                    // A "map:app.element" query expands to the map's selector
                    // condition up front so a bad key is a structured resolver
                    // error, not an opaque wait timeout. ForCondition re-checks
                    // the prefix, so direct WaitService callers get it too.
                    if (cond.Name?.StartsWith(UiMap.Prefix, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (!_waits.TryExpandMapCondition(cond, out var expanded, out var mapErr))
                            return Done(sw,
                                UiMap.TryParseKey(cond.Name, out _, out _)
                                    ? OutcomeKind.TargetNotFound : OutcomeKind.Malformed,
                                "wait_for", mapErr, false);
                        cond = expanded;
                    }

                    var r = _waits.ForCondition(cond, a.Ms ?? 5000, actx.Ct);
                    if (r.InterruptedByDialog)
                        return Done(sw, OutcomeKind.InterruptedByDialog, "wait_for", r.Reason, false);

                    return r.Success
                        ? Done(sw, OutcomeKind.Verified, "wait_for", r.Reason)
                        : Done(sw, OutcomeKind.Timeout, "wait_for", r.Reason, false);
                }

                case AgentActionKind.WaitForChange:
                case AgentActionKind.WaitForStable:
                {
                    if (ctx.ActiveHwnd == null)
                        return Malformed(sw, "no active window to watch");
                    var mon = ctx.MonitorFactory(); // orchestrator-owned — do not dispose
                    var r = a.Kind == AgentActionKind.WaitForChange
                        ? _waits.ForChange(mon, a.Ms ?? 5000, actx.Ct)
                        : _waits.ForStable(mon, a.Ms ?? 10000, actx.Ct);
                    return r.Success
                        ? Done(sw, OutcomeKind.Verified, a.Kind.ToString().ToLower(), r.Reason)
                        : Done(sw, OutcomeKind.Timeout, a.Kind.ToString().ToLower(),
                            r.Reason, false);
                }

                // ---- window targeting ----
                case AgentActionKind.FocusWindow:
                {
                    if (a.Hwnd is not { } h) return Malformed(sw, "focus_window requires hwnd");
                    if (_windows.GetWindow(h) == null)
                        return Done(sw, OutcomeKind.WindowLost, "guard",
                            $"window 0x{h:X} gone", false);
                    return Run(sw, a, new ActionIntent(ActionKind.FocusWindow,
                        TargetRef.Window(h)), actx);
                }

                // ---- everything else resolves to an executor intent ----
                default:
                    return RunValidated(sw, a, ctx, actx);
            }
        }
        catch (OperationCanceledException)
        {
            return Done(sw, OutcomeKind.Cancelled, "cancelled", "run cancelled", false);
        }
        catch (Exception e)
        {
            return Done(sw, OutcomeKind.Failed, "resolver", e.Message, false);
        }
    }

    private StepOutcome RunValidated(System.Diagnostics.Stopwatch sw,
        AgentAction a, ObsContext ctx, ActionContext actx)
    {
        var intent = ToIntent(a, ctx, out var err, out var kind);
        if (intent == null) return Done(sw, kind, "validation",
            err ?? "invalid action", false);
        return Run(sw, a, intent, actx);
    }

    /// <summary>pre-state → execute → auto-verify. Verified only with
    /// evidence; otherwise the executor's honest Unverified stands.</summary>
    private StepOutcome Run(System.Diagnostics.Stopwatch sw, AgentAction a,
        ActionIntent intent, ActionContext actx)
    {
        actx.Ct.ThrowIfCancellationRequested();
        UiElement? element;
        using (PerfTrace.Stage("resolve.element"))
            element = a.ElementId != null ? _registry.EnsureAlive(a.ElementId) : null;
        var window = element?.Hwnd is { } h ? _windows.GetTopLevelWindow(h) : null;
        AutoVerifier.PreState pre;
        using (PerfTrace.Stage("verify.pre"))
            pre = _verifier.Snapshot(element);

        ActionResult r;
        using (PerfTrace.Stage("executor"))
            r = _executor.Perform(intent, actx);
        var outcome = new StepOutcome(MapOutcome(r), r.Success, r.Method,
            r.ErrorMessage, (int)sw.ElapsedMilliseconds, r.Evidence,
            Delta: r.Delta);

        // executor success but no explicit VerifySpec → try auto-verification
        if (outcome.Kind == OutcomeKind.Unverified)
        {
            StepOutcome? auto;
            using (PerfTrace.Stage("verify.auto"))
                auto = _verifier.Verify(a, pre, r, window, actx.Ct);
            if (auto != null)
                outcome = auto with
                { Method = r.Method, DurationMs = (int)sw.ElapsedMilliseconds,
                    Delta = r.Delta };
        }
        return outcome;
    }

    /// <summary>Schema + target + frame-freshness validation → ActionIntent.
    /// Returns null when rejected; <paramref name="rejectKind"/> classifies why.</summary>
    private ActionIntent? ToIntent(AgentAction a, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        error = null;
        rejectKind = OutcomeKind.Malformed;
        TargetRef? target = a.Kind switch
        {
            AgentActionKind.Click or AgentActionKind.RightClick
                or AgentActionKind.DoubleClick or AgentActionKind.Invoke
                or AgentActionKind.Toggle or AgentActionKind.Select
                or AgentActionKind.SetValue or AgentActionKind.Scroll
                or AgentActionKind.Hover
                or AgentActionKind.ScrollIntoView
                or AgentActionKind.Drag => ResolveTarget(a, ctx, out error, out rejectKind),
            AgentActionKind.FocusElement
                => a.ElementId != null ? ResolveElement(a.ElementId, out error) : null,
            AgentActionKind.Type or AgentActionKind.Key or AgentActionKind.Hotkey
                => a.ElementId != null ? ResolveElement(a.ElementId, out error) : null,
            _ => null,
        };
        if (error != null) return null;

        var kind = a.Kind switch
        {
            AgentActionKind.Click => ActionKind.Click,
            AgentActionKind.RightClick => ActionKind.RightClick,
            AgentActionKind.DoubleClick => ActionKind.DoubleClick,
            AgentActionKind.Invoke => ActionKind.Invoke,
            AgentActionKind.Toggle => ActionKind.Toggle,
            AgentActionKind.Select => ActionKind.Select,
            AgentActionKind.ScrollIntoView => ActionKind.ScrollIntoView,
            AgentActionKind.SetValue => ActionKind.SetValue,
            AgentActionKind.Scroll => ActionKind.Scroll,
            AgentActionKind.Drag => ActionKind.Drag,
            AgentActionKind.Hover => ActionKind.MouseMove,
            AgentActionKind.FocusElement => ActionKind.FocusElement,
            AgentActionKind.Type => ActionKind.TypeText,
            AgentActionKind.Key => ActionKind.KeyPress,
            AgentActionKind.Hotkey => ActionKind.Hotkey,
            _ => (ActionKind?)null,
        };
        if (kind == null) { error = $"unhandled action kind {a.Kind}"; return null; }

        if (target == null && kind is not (ActionKind.TypeText or ActionKind.KeyPress
            or ActionKind.Hotkey))
        { error = $"{a.Kind} requires elementId or image point"; return null; }

        var args = new Dictionary<string, object?>();
        switch (kind.Value)
        {
            case ActionKind.SetValue or ActionKind.TypeText:
                if (a.Text == null) { error = $"{a.Kind} requires text"; return null; }
                args["text"] = a.Text; break;
            case ActionKind.KeyPress or ActionKind.Hotkey:
                if (!Enum.TryParse<KeyCode>(a.Key, true, out var key))
                { error = $"key '{a.Key}' unknown"; return null; }
                args["key"] = key.ToString();
                if (a.Modifiers != null) args["modifiers"] = a.Modifiers;
                if (a.Count is > 1) args["count"] = a.Count.Value;
                break;
            case ActionKind.Scroll:
                args["delta"] = a.Delta ?? -120; break;
            case ActionKind.Drag:
            {
                if (a.To == null) { error = "drag requires destination"; return null; }
                var to = ImageToDesktop(a.To, ctx, out error, out rejectKind);
                if (error != null) return null;
                args["to"] = new[] { to.X, to.Y };
                break;
            }
        }
        return new ActionIntent(kind.Value, target ?? TargetRef.At(0, 0)
            with { }, args, Silent: a.Silent);
    }

    /// <summary>Element id → TargetRef; unknown id is malformed, stale is
    /// handled inside the executor's EnsureAlive path.</summary>
    private TargetRef? ResolveElement(string id, out string? error)
    {
        if (_registry.Get(id) == null)
        { error = $"unknown element id '{id}' — observe first"; return null; }
        error = null;
        return TargetRef.Element(id);
    }

    private TargetRef? ResolveTarget(AgentAction a, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        rejectKind = OutcomeKind.Malformed;
        if (a.ElementId != null) return ResolveElement(a.ElementId, out error);
        if (a.Point != null)
        {
            var p = ImageToDesktop(a.Point, ctx, out error, out rejectKind);
            return error == null ? TargetRef.At(p.X, p.Y) : null;
        }
        error = $"{a.Kind} requires elementId or image point";
        return null;
    }

    /// <summary>The race-condition guard. An image point is only valid when:
    /// the frame is attached to THIS observation, it belongs to THIS
    /// observation id, it is not too old, and the captured window still
    /// exists with materially unchanged geometry.</summary>
    private (int X, int Y) ImageToDesktop(ImagePoint p, ObsContext ctx,
        out string? error, out OutcomeKind rejectKind)
    {
        rejectKind = OutcomeKind.Malformed;
        if (p.FrameId == 0)
        {
            // Direct screen/window coordinate (e.g. from OCR bounds or explicit coordinates without a video frame)
            error = null;
            return (p.X, p.Y);
        }
        if (ctx.Frames.Count == 0)
        { error = "observation carried no frame — coordinates unusable"; return default; }
        if (!ctx.Frames.TryGetValue(p.FrameId, out var fref))
        {
            rejectKind = OutcomeKind.StaleFrame;
            error = $"frame {p.FrameId} is not part of the current observation; re-observe";
            return default;
        }
        if (p.ObservationId != 0)
        {
            var obsOk = ctx.ValidObservationIds != null
                ? ctx.ValidObservationIds.Contains(p.ObservationId)
                : p.ObservationId == ctx.ObservationId;
            if (!obsOk)
            {
                rejectKind = OutcomeKind.StaleFrame;
                error = $"point belongs to observation {p.ObservationId}, " +
                        $"which is not a current observation of this session";
                return default;
            }
        }
        if (fref.Meta.At != default &&
            (DateTimeOffset.Now - fref.Meta.At).TotalMilliseconds > ctx.MaxFrameAgeMs)
        {
            rejectKind = OutcomeKind.StaleFrame;
            error = $"frame {p.FrameId} is older than {ctx.MaxFrameAgeMs}ms; re-observe";
            return default;
        }
        if (fref.Meta.Hwnd is { } hw)
        {
            var win = _windows.GetWindow(hw);
            if (win == null)
            {
                rejectKind = OutcomeKind.WindowLost;
                error = $"frame's window 0x{hw:X} is gone";
                return default;
            }
            var d = win.Bounds;
            var s = fref.Meta.WindowBounds ?? fref.Meta.SourceRect;
            if (Math.Abs(d.X - s.X) > 4 || Math.Abs(d.Y - s.Y) > 4 ||
                Math.Abs(d.Width - s.Width) > 4 || Math.Abs(d.Height - s.Height) > 4)
            {
                rejectKind = OutcomeKind.WindowGeometryChanged;
                error = $"window moved/resized since frame {p.FrameId}: " +
                        $"{s.Width}x{s.Height}@{s.X},{s.Y} → {d.Width}x{d.Height}@{d.X},{d.Y}";
                return default;
            }
        }
        error = null;
        return fref.Raw.Transform.ImageToDesktop(p.X, p.Y);
    }

    /// <summary>
    /// OCR fallback targeting: capture the target window's frame, recognize
    /// its words, match <see cref="OcrResolveRequest.Text"/> (exact →
    /// case-insensitive contains → fuzzy) and return a synthetic point
    /// resolution — the matched text's center — that downstream click/type
    /// paths consume as a point target.
    ///
    /// Runs ONLY when asked: the caller decides when UIA resolution failed
    /// or the spec opted into OCR (ocrText / ocr:true) — nothing here
    /// triggers OCR implicitly. On multiple equally-scored matches the call
    /// refuses with <see cref="OutcomeKind.AmbiguousTarget"/> and the tied
    /// candidates (bounded by MaxCandidates); on zero matches it fails with
    /// <see cref="OutcomeKind.TargetNotFound"/> plus the OCR'd word count.
    /// </summary>
    public OcrResolution? ResolveOcrTarget(OcrResolveRequest req,
        out string? error, out OutcomeKind rejectKind,
        out IReadOnlyList<OcrMatch> candidates)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var words = 0;
        OcrResolution? res = null;
        string? err = null;
        string? warn = null;
        var kind = OutcomeKind.Malformed;
        var cands = (IReadOnlyList<OcrMatch>)Array.Empty<OcrMatch>();
        try
        {
            res = ResolveOcrCore(req, out err, out kind, out var c,
                out words, out warn);
            cands = c;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            err = e.Message;
            kind = OutcomeKind.Failed;
        }
        finally
        {
            error = err;
            rejectKind = kind;
            candidates = cands;
            // ocr.resolve — one JSONL event per call: latency, recognizer
            // yield, match spread and the winner (or reject classification).
            PerfLog.Write(new
            {
                kind = "ocr.resolve",
                run = PerfTrace.CurrentId,
                hwnd = req.Hwnd,
                text = req.Text,
                lang = req.Language ?? _ocr?.Language,
                ms = sw.ElapsedMilliseconds,
                words,
                matches = cands.Count,
                chosen = res?.MatchedText,
                outcome = res != null ? "resolved" : kind.ToString(),
                warn,
            });
        }
        return res;
    }

    private OcrResolution? ResolveOcrCore(OcrResolveRequest req,
        out string? error, out OutcomeKind rejectKind,
        out IReadOnlyList<OcrMatch> candidates, out int words,
        out string? warning)
    {
        error = null;
        rejectKind = OutcomeKind.Malformed;
        candidates = Array.Empty<OcrMatch>();
        words = 0;
        warning = null;

        var text = req.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        { error = "OCR resolution requires non-empty text"; return null; }
        if (_ocr == null)
        {
            rejectKind = OutcomeKind.Failed;
            error = "OCR resolution is not wired on this runtime " +
                    "(no OCR service)";
            return null;
        }
        if (!_ocr.Available)
        {
            rejectKind = OutcomeKind.Failed;
            error = "OCR engine unavailable — no recognizer language installed";
            return null;
        }

        var hwnd = req.Hwnd ?? _windows.GetForegroundWindow()?.Hwnd;
        if (hwnd is not { } h)
        {
            rejectKind = OutcomeKind.TargetNotFound;
            error = "no window to OCR — pass hwnd or focus a window first";
            return null;
        }
        var win = _windows.GetWindow(h);
        if (win == null)
        {
            rejectKind = OutcomeKind.WindowLost;
            error = $"window 0x{h:X} is gone";
            return null;
        }
        if (win.State == WindowState.Minimized)
        {
            rejectKind = OutcomeKind.CaptureUnavailable;
            error = $"window '{win.Title}' (0x{h:X}) is minimized — nothing to OCR";
            return null;
        }
        // clamp to the visible desktop — bounds partially offscreen capture
        // black edges, fully offscreen captures nothing
        var region = win.Bounds.Intersect(_windows.GetVirtualDesktopBounds());
        if (region.IsEmpty)
        {
            rejectKind = OutcomeKind.CaptureUnavailable;
            error = $"window '{win.Title}' (0x{h:X}) has no visible area";
            return null;
        }

        OcrResult result;
        if (_capture != null)
        {
            // self-contained path: capture the clamped window bounds and run
            // the engine on the frame — works even when the OCR service was
            // built without its own capture wiring
            RawFrame frame;
            try
            {
                using (PerfTrace.Stage("ocr.capture"))
                {
                    var raw = _capture.CaptureRaw(region);
                    // stamp the owning hwnd — image-space actions derived
                    // from this frame can raise the right window before input
                    frame = raw with
                    { Transform = raw.Transform with { SourceHwnd = h } };
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                rejectKind = OutcomeKind.CaptureUnavailable;
                error = $"window capture failed: {e.Message}";
                return null;
            }
            using (PerfTrace.Stage("ocr.recognize"))
                result = _ocr.Recognize(frame, req.Language, req.Timeout);
        }
        else
        {
            // no direct capture — the service captures the window itself
            // (throws InbriskException NotFound/CaptureFailed/Unsupported)
            try
            {
                using (PerfTrace.Stage("ocr.recognize"))
                    result = _ocr.RecognizeWindow(h, req.Language, req.Timeout);
            }
            catch (InbriskException ie)
            {
                rejectKind = ie.Code switch
                {
                    ErrorCode.NotFound => OutcomeKind.WindowLost,
                    ErrorCode.CaptureFailed => OutcomeKind.CaptureUnavailable,
                    _ => OutcomeKind.Failed,
                };
                error = $"OCR capture failed: {ie.Message}";
                return null;
            }
        }
        var spans = result.Words;
        words = spans.Count;
        warning = result.Warning;

        var scored = ScoreOcrCandidates(spans, text);
        var maxList = Math.Clamp(req.MaxCandidates, 1, 32);
        if (scored.Count == 0)
        {
            rejectKind = OutcomeKind.TargetNotFound;
            error = $"OCR matched nothing for \"{Trunc(text)}\" — " +
                    $"{words} word(s) recognized in '{win.Title}' (0x{h:X})" +
                    (warning != null ? $" [{warning}]" : "");
            return null;
        }

        var top = scored[0].Score;
        var tied = scored.Where(m => m.Score == top).ToList();
        if (tied.Count > 1)
        {
            rejectKind = OutcomeKind.AmbiguousTarget;
            candidates = tied.Take(maxList).ToList();
            var list = string.Join(", ", candidates.Select(m =>
                $"\"{m.Text}\" @{m.Bounds}"));
            var more = tied.Count > candidates.Count
                ? $" (+{tied.Count - candidates.Count} more)" : "";
            error = $"OCR target \"{Trunc(text)}\" is ambiguous — " +
                    $"{tied.Count} equal matches: {list}{more}";
            return null;
        }

        candidates = scored.Take(maxList).ToList();
        var best = scored[0];
        var c = best.Bounds.Center;
        return new OcrResolution(c.X, c.Y, best.Text, best.Bounds, best.Score,
            best.Confidence, h, words, scored.Count, result.Language);
    }

    /// <summary>Word spans → scored candidates. Multi-word queries also
    /// match consecutive same-line spans joined with spaces — the engine
    /// emits one span per rendered word, so phrases live across spans.
    /// Ordering: score desc, then visual order (top→bottom, left→right) —
    /// fully deterministic, matching the UIA resolver's tie-break.</summary>
    private static List<OcrMatch> ScoreOcrCandidates(
        IReadOnlyList<TextSpan> spans, string query)
    {
        var queryWords = query.Split(' ',
            StringSplitOptions.RemoveEmptyEntries).Length;

        var units = new List<(string Text, RectPx Bounds, double? Confidence)>(
            spans.Count);
        foreach (var s in spans)
        {
            var t = s.Text.Trim();
            if (t.Length > 0) units.Add((t, s.Bounds, s.Confidence));
        }

        if (queryWords > 1)
        {
            foreach (var line in GroupOcrLines(spans))
            {
                for (var i = 0; i < line.Count; i++)
                {
                    var maxW = Math.Min(queryWords, line.Count - i);
                    for (var n = 2; n <= maxW; n++)
                    {
                        var slice = line.GetRange(i, n);
                        units.Add((string.Join(' ',
                                slice.Select(w => w.Text.Trim())),
                            UnionOcrBounds(slice), slice[0].Confidence));
                    }
                }
            }
        }

        var scored = new List<OcrMatch>(units.Count);
        foreach (var u in units)
        {
            var s = ScoreOcrCandidate(u.Text, query);
            if (s > 0) scored.Add(new OcrMatch(u.Text, u.Bounds, s, u.Confidence));
        }
        // ~same text at ~same bounds = one candidate (identical rule to the
        // UIA resolver's indistinguishable-candidate dedupe)
        return scored
            .GroupBy(m => (m.Text, m.Bounds.X / 4, m.Bounds.Y / 4,
                m.Bounds.Width / 4, m.Bounds.Height / 4))
            .Select(g => g.OrderByDescending(m => m.Score).First())
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X)
            .ThenBy(m => m.Bounds.Width).ThenBy(m => m.Text)
            .ToList();
    }

    /// <summary>Cluster word spans into visual lines: sorted by vertical
    /// center, a word joins the current line while its center stays within
    /// ~60% of its height of the line's running center.</summary>
    private static List<List<TextSpan>> GroupOcrLines(IReadOnlyList<TextSpan> spans)
    {
        var sorted = spans
            .OrderBy(s => s.Bounds.Y + s.Bounds.Height / 2.0)
            .ThenBy(s => s.Bounds.X).ToList();
        var lines = new List<List<TextSpan>>();
        var centers = new List<double>();
        foreach (var s in sorted)
        {
            var cy = s.Bounds.Y + s.Bounds.Height / 2.0;
            var tol = Math.Max(4.0, s.Bounds.Height * 0.6);
            if (lines.Count > 0 && Math.Abs(cy - centers[^1]) <= tol)
            {
                var l = lines[^1];
                centers[^1] = (centers[^1] * l.Count + cy) / (l.Count + 1);
                l.Add(s);
            }
            else
            {
                lines.Add([s]);
                centers.Add(cy);
            }
        }
        foreach (var l in lines)
            l.Sort((a, b) => a.Bounds.X.CompareTo(b.Bounds.X));
        return lines;
    }

    private static RectPx UnionOcrBounds(IReadOnlyList<TextSpan> ws)
    {
        var x = ws.Min(w => w.Bounds.X);
        var y = ws.Min(w => w.Bounds.Y);
        var r = ws.Max(w => w.Bounds.Right);
        var b = ws.Max(w => w.Bounds.Bottom);
        return new RectPx(x, y, r - x, b - y);
    }

    /// <summary>exact → case-insensitive contains → fuzzy score. Tier gaps
    /// keep an exact hit above every contains and contains above fuzzy, so
    /// ordering alone decides the match class.</summary>
    private static double ScoreOcrCandidate(string candidate, string query)
    {
        if (string.Equals(candidate, query, StringComparison.Ordinal)) return 100;
        if (string.Equals(candidate, query, StringComparison.OrdinalIgnoreCase))
            return 95;
        if (candidate.Contains(query, StringComparison.OrdinalIgnoreCase))
            // longer candidate beyond the query → weaker containment
            return 60 + 25.0 * query.Length / candidate.Length;
        if (candidate.Length >= 3 &&
            query.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            // the word is a fragment of a multi-word query (OCR split/merge)
            return 45 + 15.0 * candidate.Length / query.Length;
        // fuzzy: normalized edit similarity. A length delta alone caps the
        // reachable similarity — skip the DP when it can't clear the floor.
        var maxLen = Math.Max(candidate.Length, query.Length);
        if (Math.Abs(candidate.Length - query.Length) > maxLen * 0.4) return 0;
        var sim = 1.0 - (double)Levenshtein(candidate, query) / maxLen;
        return sim >= 0.6 ? sim * 40 : 0;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToUpperInvariant(a[i - 1]) ==
                    char.ToUpperInvariant(b[j - 1]) ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string Trunc(string s, int max = 60) =>
        s.Length <= max ? s : s[..max] + "…";

    private static StepOutcome Malformed(System.Diagnostics.Stopwatch sw, string why) =>
        new(OutcomeKind.Malformed, false, "validation", why, (int)sw.ElapsedMilliseconds);

    private static StepOutcome Done(System.Diagnostics.Stopwatch sw, OutcomeKind k,
        string method, string? detail, bool success = true) =>
        new(k, success, method, detail, (int)sw.ElapsedMilliseconds);

    public static OutcomeKind MapOutcome(ActionResult r) => r switch
    {
        { Success: true, Verification: VerifyResult.Verified } => OutcomeKind.Verified,
        { Success: true } => OutcomeKind.Unverified,
        { Error: ErrorCode.Stale or ErrorCode.StaleUnresolvable } => OutcomeKind.Stale,
        { Error: ErrorCode.NotFound } => OutcomeKind.TargetNotFound,
        { Error: ErrorCode.TargetElevated } => OutcomeKind.TargetElevated,
        { Error: ErrorCode.SecureDesktopActive } => OutcomeKind.SecureDesktop,
        { Error: ErrorCode.Timeout } => OutcomeKind.Timeout,
        { Error: ErrorCode.InputBlocked } => OutcomeKind.InputRejected,
        { Error: ErrorCode.CaptureFailed } => OutcomeKind.CaptureUnavailable,
        { Error: ErrorCode.PolicyDenied } => OutcomeKind.PolicyDenied,
        { Error: ErrorCode.ConfirmationRequired } => OutcomeKind.ConfirmationDenied,
        { Error: ErrorCode.Busy } => OutcomeKind.ConcurrencyConflict,
        { Error: ErrorCode.Unsupported } => OutcomeKind.NotSupported,
        _ => OutcomeKind.Failed,
    };
}

/// <summary>Outcome → loop steering. Recovery is decided here, not by the model.</summary>
public static class RecoveryPolicy
{
    public static RecoveryHint For(OutcomeKind k) => k switch
    {
        OutcomeKind.Verified or OutcomeKind.ObservedChange or OutcomeKind.Unverified => RecoveryHint.ReObserve,
        OutcomeKind.Stale => RecoveryHint.ReResolve,
        OutcomeKind.StaleFrame or OutcomeKind.WindowGeometryChanged
            or OutcomeKind.TargetNotFound => RecoveryHint.ReObserve,
        OutcomeKind.WindowLost => RecoveryHint.FindWindow,
        OutcomeKind.NoProgress => RecoveryHint.ReObserve,
        OutcomeKind.TargetElevated or OutcomeKind.SecureDesktop
            or OutcomeKind.PolicyDenied => RecoveryHint.Abort,
        OutcomeKind.InterruptedByDialog => RecoveryHint.ReObserve,
        OutcomeKind.TargetOnDifferentScreen => RecoveryHint.ReObserve,
        OutcomeKind.TargetOffscreen => RecoveryHint.ReObserve,
        _ => RecoveryHint.ReportToModel,
    };

    /// <summary>Outcomes that end the run regardless of retry budget.</summary>
    public static bool IsFatal(OutcomeKind k) =>
        For(k) == RecoveryHint.Abort;
}
