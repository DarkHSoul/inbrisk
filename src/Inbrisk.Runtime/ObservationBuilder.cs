using System.Security.Cryptography;
using System.Text;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Result of building one observation: the canonical record plus the
/// attached frames keyed by FrameId — the grounding/transform source for
/// coordinate validation. Empty when the observation is semantic-only.</summary>
public sealed record BuiltObservation(AgentObservation Observation,
    IReadOnlyDictionary<long, FrameRef> Frames);

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

    private readonly IWindowService _windows;
    private readonly Func<long, IReadOnlyList<UiElement>> _scene;
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
        ITelemetrySink? telemetry = null)
    {
        _windows = windows;
        _scene = scene;
        _captureRaw = captureRaw;
        _events = events;
        _telemetry = telemetry;
    }

    /// <summary>Build the next observation for the agent loop.
    /// <paramref name="recentElementRefs"/> — element ids acted on in recent
    /// steps; they rank higher so the model keeps seeing its own context.
    /// <paramref name="baseSnapshot"/> — optional base observation to diff against (defaults to previous observation).</summary>
    public BuiltObservation Build(long? hwndHint, ObservationBudget budget,
        VisualAttachPolicy policy, ChangeMonitor? monitor, StepOutcome? prevOutcome,
        IReadOnlyCollection<string>? recentElementRefs = null,
        AgentObservation? baseSnapshot = null)
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
        var obsElements = Prune(elements, budget, changedKeys, recent, regions, target);

        var (frames, raws) = AttachPixels(policy, target, obsElements, regions, budget);

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
        return new BuiltObservation(obs, raws);
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
        if (Interactive.Contains(e.Role) || e.Actions.Count > 0) s += 100;
        if (e.Props.GetValueOrDefault("focused") is true) s += 40;
        if (recentRefs.Contains(e.Id)) s += 50;
        if (changedKeys.Contains(stableKey)) s += 30;
        if (e.Name != null) s += 10;
        if (e.Props.GetValueOrDefault("enabled") is not false) s += 5;
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

    private static IReadOnlyList<ObsElement> Prune(IReadOnlyList<UiElement> elements,
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
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.El.Bounds.Y).ThenBy(x => x.El.Bounds.X);

        var list = new List<ObsElement>(b.MaxElements);
        var chars = 0;
        foreach (var x in scored)
        {
            var o = ToObs(x.El);
            var c = Chars(o);
            if (list.Count >= b.MaxElements || chars + c > b.MaxChars) break;
            chars += c;
            list.Add(o);
        }
        return list;
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
        IReadOnlyList<RectPx> regions, ObservationBudget b)
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

    private static RectPx Clamp(RectPx r, RectPx? within, int maxW, int maxH)
    {
        var rr = within is { } w ? r.Intersect(w) : r;
        return rr with { Width = Math.Min(rr.Width, maxW), Height = Math.Min(rr.Height, maxH) };
    }
}
