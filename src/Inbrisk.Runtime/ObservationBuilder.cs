using System.Security.Cryptography;
using System.Text;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Result of building one observation: the canonical record plus the
/// attached frames keyed by FrameId — the grounding/transform source for
/// coordinate validation. Empty when the observation is semantic-only.
/// <paramref name="Prune"/> — element-count/byte metrics for the passive-node
/// pruner so tool layers can report how much noise was dropped.</summary>
public sealed record BuiltObservation(AgentObservation Observation,
    IReadOnlyDictionary<long, FrameRef> Frames)
{
    /// <summary>Element-count/byte metrics for the passive-node pruner —
    /// init property (not positional) so existing 2-arg deconstructions in
    /// AgentOrchestrator keep compiling.</summary>
    public PruneStats? Prune { get; init; }
}

/// <summary>Size accounting for one pass of <see cref="ObservationBuilder"/>'s
/// element pruning — emitted to perf-trace.jsonl and carried on
/// BuiltObservation for tool-layer reporting.</summary>
public sealed record PruneStats(
    /// <summary>Scene size before any filtering.</summary>
    int RawElements,
    /// <summary>Survived the geometry + relevance gates (pre-prune).</summary>
    int CandidateElements,
    /// <summary>Dropped by the passive-noise filter (unnamed, actionless,
    /// valueless, non-structural role).</summary>
    int PassiveDropped,
    /// <summary>Dropped by the MaxElements/MaxChars caps.</summary>
    int BudgetDropped,
    /// <summary>Final printed element count.</summary>
    int KeptElements,
    /// <summary>Serialized-size estimate (chars) of the kept elements.</summary>
    int ApproxChars);

/// <summary>
/// Turns the live desktop into a canonical AgentObservation:
/// merged scene → deterministic relevance ranking under byte/element/image
/// budgets → screenshot-attachment decision → delta against the previous
/// observation. The model never sees the raw 10k-element UIA tree.
/// </summary>
public sealed class ObservationBuilder
{
    private static readonly HashSet<Role> Interactive = new()
    {
        Role.Button, Role.Edit, Role.Document, Role.CheckBox, Role.ComboBox,
        Role.RadioButton, Role.MenuItem, Role.ListItem, Role.TreeItem,
        Role.TabItem, Role.Hyperlink, Role.Slider, Role.Spinner, Role.DataItem,
        Role.Menu, Role.Tab,
    };

    /// <summary>Control-type allowlist for the pruned view — roles that
    /// survive passive pruning even when unnamed/actionless. On top of the
    /// interactive set it keeps Toggle (toggle-button-ish) and the item
    /// containers List/Tree/Table so the flat list retains minimal structural
    /// context for the actionable children they group. Everything else
    /// (Pane, Group, Custom, Image, …) collapses unless it carries a name,
    /// a value, or an action.</summary>
    private static readonly HashSet<Role> PrunedViewKeep = new(Interactive)
    {
        Role.Toggle, Role.List, Role.Tree, Role.Table,
    };

    private readonly IWindowService _windows;
    private readonly Func<long, IReadOnlyList<UiElement>> _scene;
    private readonly Func<long, CancellationToken, Task<IReadOnlyList<UiElement>>>? _sceneAsync;
    private readonly Func<CaptureTarget, RawFrame> _captureRaw;
    private readonly RecentEventBuffer _events;
    private readonly ITelemetrySink? _telemetry;
    private long _obsSeq;
    // process-wide: frame ids must be unique across builders so a stale-frame
    // guard can never collide with a fresh builder's ids
    private static long _frameSeq;
    private AgentObservation? _prev;

    public ObservationBuilder(
        IWindowService windows,
        Func<long, IReadOnlyList<UiElement>> scene,
        Func<CaptureTarget, RawFrame> captureRaw,
        RecentEventBuffer events,
        ITelemetrySink? telemetry = null,
        Func<long, CancellationToken, Task<IReadOnlyList<UiElement>>>? sceneAsync = null)
    {
        _windows = windows;
        _scene = scene;
        _sceneAsync = sceneAsync;
        _captureRaw = captureRaw;
        _events = events;
        _telemetry = telemetry;
    }

    /// <summary>Build the next observation for the agent loop.
    /// <paramref name="recentElementRefs"/> — element ids acted on in recent
    /// steps; they rank higher so the model keeps seeing its own context.
    /// <paramref name="baseSnapshot"/> — optional base observation to diff against (defaults to previous observation).</summary>
    /// <param name="maxImageWidth">Optional cap on attached frame width —
    /// wider captures are bilinear-downscaled; the FrameTransform keeps the
    /// same desktop source rect so coordinate mapping stays correct.</param>
    /// <param name="markElements">Draw numbered marks at interactive element
    /// centers on attached frames; mark i = the (i+1)-th element in the
    /// observation's printed element list.</param>
    public BuiltObservation Build(long? hwndHint, ObservationBudget budget,
        VisualAttachPolicy policy, ChangeMonitor? monitor, StepOutcome? prevOutcome,
        IReadOnlyCollection<string>? recentElementRefs = null,
        AgentObservation? baseSnapshot = null,
        int? maxImageWidth = null, bool markElements = false)
    {
        var fg = _windows.GetForegroundWindow();
        var target = hwndHint ?? fg?.Hwnd;

        var wins = _windows.ListWindows();
        var active = wins.FirstOrDefault(w => w.Hwnd == fg?.Hwnd);
        var obsWins = wins
            .OrderByDescending(w => w.Hwnd == fg?.Hwnd)
            .ThenByDescending(w => w.Hwnd == target)
            .Take(budget.MaxWindows)
            .Select(w => new ObsWindow(w.Hwnd,
                Trunc(w.Title, budget.MaxNameLength) ?? "",
                w.ProcessName ?? "", w.Bounds, w.Hwnd == fg?.Hwnd))
            .ToList();
        var activeObs = obsWins.FirstOrDefault(w => w.Active);

        var elements = target is { } t ? _scene(t) : (IReadOnlyList<UiElement>)[];
        var regions = monitor?.LastChangedDesktopRegions ?? (IReadOnlyList<RectPx>)[];
        var changedKeys = _prev?.Delta is { } d
            ? d.Added.Concat(d.Changed).Select(e => e.StableKey).ToHashSet()
            : new HashSet<string>();
        var recent = recentElementRefs ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        var (obsElements, pruneStats) = Prune(elements, budget, changedKeys, recent, regions, target);

        var (frames, raws) = AttachPixels(policy, target, obsElements, regions, budget,
            maxImageWidth, markElements);

        var backend = monitor?.Session.Backend.ToString()
            ?? (frames.Count > 0 ? "oneshot" : "none");

        var baseObs = baseSnapshot ?? _prev;
        var stats = ComputeStats(obsElements, obsWins, frames);
        var obs = new AgentObservation(
            Interlocked.Increment(ref _obsSeq), DateTimeOffset.Now,
            activeObs, obsWins, obsElements,
            _events.Snapshot(budget.MaxEvents), frames, regions,
            backend, prevOutcome, Delta: null, Stats: stats,
            BaseObservationId: baseObs?.ObservationId);
        obs = obs with { Delta = baseObs == null ? null : DeltaBuilder.Compute(baseObs, obs, elements) };
        _prev = obs;
        return new BuiltObservation(obs, raws) { Prune = pruneStats };
    }

    /// <summary>Build the next observation asynchronously without blocking thread pool threads.</summary>
    public async Task<BuiltObservation> BuildAsync(long? hwndHint, ObservationBudget budget,
        VisualAttachPolicy policy, ChangeMonitor? monitor, StepOutcome? prevOutcome,
        IReadOnlyCollection<string>? recentElementRefs = null,
        AgentObservation? baseSnapshot = null,
        CancellationToken ct = default,
        int? maxImageWidth = null, bool markElements = false)
    {
        var fg = _windows.GetForegroundWindow();
        var target = hwndHint ?? fg?.Hwnd;

        var wins = _windows.ListWindows();
        var active = wins.FirstOrDefault(w => w.Hwnd == fg?.Hwnd);
        var obsWins = wins
            .OrderByDescending(w => w.Hwnd == fg?.Hwnd)
            .ThenByDescending(w => w.Hwnd == target)
            .Take(budget.MaxWindows)
            .Select(w => new ObsWindow(w.Hwnd,
                Trunc(w.Title, budget.MaxNameLength) ?? "",
                w.ProcessName ?? "", w.Bounds, w.Hwnd == fg?.Hwnd))
            .ToList();
        var activeObs = obsWins.FirstOrDefault(w => w.Active);

        IReadOnlyList<UiElement> elements = (IReadOnlyList<UiElement>)[];
        if (target is { } t)
        {
            if (_sceneAsync != null)
                elements = await _sceneAsync(t, ct).ConfigureAwait(false);
            else
                elements = _scene(t);
        }
        var regions = monitor?.LastChangedDesktopRegions ?? (IReadOnlyList<RectPx>)[];
        var changedKeys = _prev?.Delta is { } d
            ? d.Added.Concat(d.Changed).Select(e => e.StableKey).ToHashSet()
            : new HashSet<string>();
        var recent = recentElementRefs ?? (IReadOnlyCollection<string>)Array.Empty<string>();
        var (obsElements, pruneStats) = Prune(elements, budget, changedKeys, recent, regions, target);

        var (frames, raws) = AttachPixels(policy, target, obsElements, regions, budget,
            maxImageWidth, markElements);

        var backend = monitor?.Session.Backend.ToString()
            ?? (frames.Count > 0 ? "oneshot" : "none");

        var baseObs = baseSnapshot ?? _prev;
        var stats = ComputeStats(obsElements, obsWins, frames);
        var obs = new AgentObservation(
            Interlocked.Increment(ref _obsSeq), DateTimeOffset.Now,
            activeObs, obsWins, obsElements,
            _events.Snapshot(budget.MaxEvents), frames, regions,
            backend, prevOutcome, Delta: null, Stats: stats,
            BaseObservationId: baseObs?.ObservationId);
        obs = obs with { Delta = baseObs == null ? null : DeltaBuilder.Compute(baseObs, obs, elements) };
        _prev = obs;
        return new BuiltObservation(obs, raws) { Prune = pruneStats };
    }

    /// <summary>Reset delta baseline (e.g. a new run).</summary>
    public void ResetDelta() => _prev = null;

    // --------------------------- internals -----------------------------------

    /// <summary>Deterministic relevance scoring — no LLM, only observable
    /// signals. Higher score survives the element/char budgets.</summary>
    private static int Relevance(UiElement e, string stableKey,
        HashSet<string> changedKeys, IReadOnlyCollection<string> recentRefs,
        IReadOnlyList<RectPx> regions, long? targetHwnd)
    {
        var s = 0;
        var isEnabled = e.Props.GetValueOrDefault("enabled") is not false;
        if (isEnabled && (Interactive.Contains(e.Role) || e.Actions.Count > 0)) s += 100;
        else if (!isEnabled && Interactive.Contains(e.Role)) s += 5;
        if (e.Props.GetValueOrDefault("focused") is true) s += 40;
        if (recentRefs.Contains(e.Id)) s += 50;
        if (changedKeys.Contains(stableKey)) s += 30;
        if (e.Name != null) s += 10;
        if (isEnabled) s += 5;
        if (e.Hwnd == targetHwnd) s += 20;
        foreach (var r in regions)
            if (e.Bounds.Intersects(r)) { s += 15; break; }
        return s;
    }

    /// <summary>Approximate serialized size of one element in the model-facing
    /// payload — enforces the char budget deterministically.</summary>
    private static int Chars(ObsElement e) =>
        e.Id.Length + e.Role.Length + (e.Name?.Length ?? 0) + (e.Value?.Length ?? 0)
        + (e.State?.Length ?? 0) + e.Source.Length + e.StableKey.Length
        + e.Actions.Sum(a => a.Length) + 64 /* bounds + separators */;

    /// <summary>Passive-noise predicate — shared by the observation pruner
    /// and the MCP find text builder. An element is droppable when its role
    /// isn't on the pruned-view allowlist AND it has no actionable verbs AND
    /// no name AND no text value: the classic unnamed Pane/Group/Image
    /// filler that floods the context without giving the model anything it
    /// can reference or act on. Named Text keeps its content via the name
    /// check; named containers survive as minimal structural context.</summary>
    public static bool IsPassiveNoise(UiElement e) =>
        !PrunedViewKeep.Contains(e.Role)
        && e.Actions.Count == 0
        && string.IsNullOrWhiteSpace(e.Name)
        && !(e.Props.TryGetValue("value", out var v)
            && v?.ToString() is { Length: > 0 });

    private static (List<ObsElement> List, PruneStats Stats) Prune(
        IReadOnlyList<UiElement> elements,
        ObservationBudget b, HashSet<string> changedKeys,
        IReadOnlyCollection<string> recentRefs, IReadOnlyList<RectPx> regions,
        long? targetHwnd)
    {
        var scored = elements
            .Where(e => e.Bounds.Width > 0 && e.Bounds.Height > 0
                && e.Role != Role.Separator)
            .Select(e => (El: e, Key: StableKey(e, e.Handle.Recipe.AutomationId)))
            .Select(x => (x.El, x.Key,
                Score: Relevance(x.El, x.Key, changedKeys, recentRefs, regions, targetHwnd)))
            .Where(x => x.Score > 0 || x.El.Name != null)
            .ToList();

        // Passive pruning — collapsed nodes carry no identity the model could
        // reference anyway. Elements the loop recently acted on or that just
        // changed stay regardless of how inert they look.
        var kept = b.PrunePassive
            ? scored.Where(x => !IsPassiveNoise(x.El)
                    || recentRefs.Contains(x.El.Id)
                    || changedKeys.Contains(x.Key))
                .ToList()
            : scored;

        var list = new List<ObsElement>(b.MaxElements);
        var chars = 0;
        foreach (var x in kept
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.El.Bounds.Y).ThenBy(x => x.El.Bounds.X))
        {
            var o = ToObs(x.El);
            var c = Chars(o);
            if (list.Count >= b.MaxElements || chars + c > b.MaxChars) break;
            chars += c;
            list.Add(o);
        }

        var stats = new PruneStats(elements.Count, scored.Count,
            scored.Count - kept.Count, kept.Count - list.Count,
            list.Count, chars);
        PerfLog.Write(new
        {
            kind = "observation.prune",
            at = DateTimeOffset.Now,
            traceId = PerfTrace.CurrentId,
            hwnd = targetHwnd,
            enabled = b.PrunePassive,
            raw = stats.RawElements,
            candidates = stats.CandidateElements,
            passiveDropped = stats.PassiveDropped,
            budgetDropped = stats.BudgetDropped,
            kept = stats.KeptElements,
            approxChars = stats.ApproxChars,
            approxBytes = stats.ApproxChars * 2, // ContextStats convention
        });
        return (list, stats);
    }

    private static ObsElement ToObs(UiElement e)
    {
        var sb = new StringBuilder();
        if (e.Props.TryGetValue("enabled", out var en) && en is false) sb.Append("disabled;");
        if (e.Props.TryGetValue("toggleState", out var tg)) sb.Append($"toggle:{tg};");
        if (e.Props.TryGetValue("expandCollapseState", out var ex)) sb.Append($"expand:{ex};");
        if (e.Props.TryGetValue("selected", out var se) && se is true) sb.Append("selected;");
        if (e.Props.TryGetValue("focused", out var fo) && fo is true) sb.Append("focused;");
        var state = sb.Length > 0 ? sb.ToString().TrimEnd(';') : null;
        var value = e.Props.TryGetValue("value", out var vv) ? vv?.ToString() : null;
        var aid = e.Handle.Recipe.AutomationId;
        return new ObsElement(e.Id, e.Role.ToString(),
            Trunc(e.Name, 80), Trunc(value, 120), e.Bounds, e.Actions,
            e.Source.ToString(), state, StableKey(e, aid));
    }

    public static string StableKey(UiElement e, string? aid = null)
    {
        var recipe = e.Handle?.Recipe;
        var autoId = aid ?? recipe?.AutomationId;
        string basis;
        if (!string.IsNullOrEmpty(autoId))
        {
            basis = $"{e.Hwnd}:{e.Role}:{autoId}";
        }
        else if (recipe?.AncestryPath is { Count: > 0 } path)
        {
            var pStr = string.Join("/", path.Select(s => $"{s.Role}_{s.AutomationId ?? s.Name}_{s.IndexAmongSiblings}"));
            basis = $"{e.Hwnd}:{pStr}:{e.Role}:{e.Name}";
        }
        else if (!string.IsNullOrEmpty(e.Name))
        {
            basis = $"{e.Hwnd}:{e.Role}:{e.Name}";
        }
        else
        {
            basis = $"{e.Hwnd}:{e.Role}:{e.Id}";
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..12];
    }

    private static string? Trunc(string? s, int n) =>
        s == null ? null : s.Length <= n ? s : s[..n] + "…";

    /// <summary>Attach frames under pixel/byte budgets. Window captures carry
    /// the window's hwnd + capture time so coordinate actions can re-validate
    /// identity and geometry before clicking.</summary>
    private (List<ObsFrameRef>, Dictionary<long, FrameRef>) AttachPixels(
        VisualAttachPolicy policy, long? target, IReadOnlyList<ObsElement> elements,
        IReadOnlyList<RectPx> regions, ObservationBudget b,
        int? maxImageWidth = null, bool markElements = false)
    {
        var frames = new List<ObsFrameRef>();
        var raws = new Dictionary<long, FrameRef>();
        long imgBytes = 0, imgPixels = 0;

        var interactive = elements.Count(e => e.Actions.Count > 0
            || InteractiveRole(e.Role));
        var attach = policy switch
        {
            VisualAttachPolicy.Never => VisualAttach.None,
            VisualAttachPolicy.Always => target != null ? VisualAttach.WindowFrame
                : VisualAttach.FullFrame,
            _ => DecideAuto(target, interactive, regions),
        };

        if (attach == VisualAttach.None || target == null && attach != VisualAttach.FullFrame)
            return (frames, raws);

        bool UnderBudget(RawFrame raw, byte[] png) =>
            imgBytes + png.LongLength <= b.MaxScreenshotBytes
            && imgPixels + (long)raw.Width * raw.Height <= b.MaxScreenshotPixels;

        void Capture(CaptureTarget ct, VisualAttach kind, long? hwnd)
        {
            var raw = _captureRaw(ct);
            if (maxImageWidth is { } mw && mw > 0 && raw.Width > mw)
                raw = raw.ScaledToMaxWidth(mw);
            if (markElements)
            {
                var marks = CollectMarks(raw.Transform, elements);
                if (marks.Count > 0) raw.DrawMarks(marks);
            }
            var png = raw.ToPng();
            if (!UnderBudget(raw, png)) return; // budget exhausted — skip frame
            var id = Interlocked.Increment(ref _frameSeq);
            var meta = new ObsFrameRef(id, raw.Width, raw.Height,
                raw.Transform.SourceRect, kind, png, hwnd, DateTimeOffset.Now,
                hwnd is { } h ? _windows.GetWindow(h)?.Bounds : null);
            frames.Add(meta);
            raws[id] = new FrameRef(raw, meta);
            imgBytes += png.LongLength;
            imgPixels += (long)raw.Width * raw.Height;
        }

        try
        {
            if (attach == VisualAttach.ChangedRegions && regions.Count > 0)
            {
                var win = target is { } t ? _windows.GetWindow(t)?.Bounds : null;
                foreach (var r in regions.Take(b.MaxCropRegions))
                {
                    var clamped = Clamp(r, win, b.CropMaxWidth, b.CropMaxHeight);
                    Capture(new CaptureTarget.Region(clamped), VisualAttach.ChangedRegions, target);
                }
            }
            else
            {
                var capTarget = attach switch
                {
                    VisualAttach.WindowFrame when target != null =>
                        (CaptureTarget)new CaptureTarget.Window(target.Value),
                    _ => new CaptureTarget.FullDesktop(),
                };
                Capture(capTarget, attach,
                    attach == VisualAttach.WindowFrame ? target : null);
            }
        }
        catch (Exception e)
        {
            _telemetry?.EmitPipeline(new PipelineTelemetry(DateTimeOffset.Now,
                "observation", Target: $"capture error: {e.Message}"));
        }
        return (frames, raws);
    }

    /// <summary>Size accounting the adapter can use for provider-specific
    /// trimming/billing — Inbrisk enforces budgets first.</summary>
    private static ContextStats ComputeStats(IReadOnlyList<ObsElement> elements,
        IReadOnlyList<ObsWindow> windows, IReadOnlyList<ObsFrameRef> frames)
    {
        var chars = elements.Sum(Chars)
            + windows.Sum(w => w.Title.Length + w.Process.Length + 48);
        long imgBytes = 0, imgPixels = 0;
        foreach (var f in frames)
        {
            imgBytes += f.Png?.LongLength ?? 0;
            imgPixels += (long)f.Width * f.Height;
        }
        return new ContextStats(chars, chars * 2L + imgBytes,
            frames.Count, imgPixels, imgBytes);
    }

    private static VisualAttach DecideAuto(long? target, int interactive,
        IReadOnlyList<RectPx> regions)
    {
        if (target == null) return VisualAttach.None;
        // rich semantics → no pixels; sparse semantics → visual evidence
        if (interactive >= 4 && regions.Count == 0) return VisualAttach.None;
        if (regions.Count > 0) return VisualAttach.ChangedRegions;
        return VisualAttach.WindowFrame;
    }

    private static bool InteractiveRole(string role) =>
        Enum.TryParse<Role>(role, out var r) && Interactive.Contains(r);

    /// <summary>Set-of-mark overlay list for a frame: interactive elements
    /// whose desktop-space center lands inside the captured rect. The index is
    /// 1-based into the observation's printed element order.</summary>
    private static List<(int Index, RectPx Bounds)> CollectMarks(
        FrameTransform t, IReadOnlyList<ObsElement> elements)
    {
        var marks = new List<(int, RectPx)>(60);
        for (var i = 0; i < elements.Count && marks.Count < 60; i++)
        {
            var e = elements[i];
            if (e.Actions.Count == 0 && !InteractiveRole(e.Role)) continue;
            var (cx, cy) = e.Bounds.Center;
            if (!t.SourceRect.Contains(cx, cy)) continue;
            marks.Add((i + 1, e.Bounds));
        }
        return marks;
    }

    private static RectPx Clamp(RectPx r, RectPx? within, int maxW, int maxH)
    {
        var rr = within is { } w ? r.Intersect(w) : r;
        return rr with { Width = Math.Min(rr.Width, maxW), Height = Math.Min(rr.Height, maxH) };
    }
}
