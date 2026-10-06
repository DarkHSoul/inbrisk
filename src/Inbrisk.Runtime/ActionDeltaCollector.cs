using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Measures what one action changed on the desktop so the result payload
/// carries the delta and the model can skip a mandatory re-observe.
///
/// Mechanism (preferred → fallback):
///   1. Semantic events — the shared RecentEventBuffer already merges
///      WinEvent (foreground/create/destroy/show/focus/state/name/value)
///      and UIA (WindowOpened/Closed, StructureChanged, notifications)
///      streams. A generation baseline + WaitForNextEvent gives a settle
///      window that returns EARLY on the first event instead of sleeping.
///   2. Snapshot diff — the window set is enumerated before the action and
///      again after the settle window; hwnd set arithmetic yields
///      opened/closed lists even when the event source is not running.
///
/// The collector is deliberately cheap: Begin() is one EnumWindows +
/// GetForegroundWindow; Complete() pays at most `settleMs` and a single
/// element ReResolve.
/// </summary>
public sealed class ActionDeltaCollector
{
    /// <summary>Post-action settle: how long to wait for the first
    /// semantic event / async window creation. Early-out on first event.</summary>
    private readonly int _settleMs;
    /// <summary>After the first event arrives, a short drain catches the
    /// rest of the burst (coalesced kinds flush on a ~120ms timer).</summary>
    private readonly int _drainMs;
    /// <summary>Snapsho-only fallback pause when no event stream runs.</summary>
    private readonly int _fallbackSettleMs;

    private readonly IWindowService _windows;
    private readonly IEventWaiter? _events;
    private readonly IReadOnlyList<IElementBackend>? _backends;

    public ActionDeltaCollector(IWindowService windows, IEventWaiter? events = null,
        IReadOnlyList<IElementBackend>? backends = null,
        int settleMs = 200, int drainMs = 40, int fallbackSettleMs = 60)
    {
        _windows = windows;
        _events = events;
        _backends = backends;
        _settleMs = Math.Clamp(settleMs, 0, 2000);
        _drainMs = Math.Clamp(drainMs, 0, 500);
        _fallbackSettleMs = Math.Clamp(fallbackSettleMs, 0, 2000);
    }

    /// <summary>Snapshot the desktop before the action runs.</summary>
    public Scope Begin()
    {
        IReadOnlyList<WindowInfo> before;
        WindowInfo? fg;
        try { before = _windows.ListWindows() ?? []; }
        catch { before = Array.Empty<WindowInfo>(); }
        try { fg = _windows.GetForegroundWindow(); }
        catch { fg = null; }
        return new Scope(this, before, fg, _events?.CurrentGeneration);
    }

    public sealed class Scope
    {
        private readonly ActionDeltaCollector _c;
        private readonly DateTimeOffset _at = DateTimeOffset.Now;
        private readonly Dictionary<long, WindowInfo> _before;
        private readonly WindowInfo? _fgBefore;
        private readonly long? _genBefore;

        internal Scope(ActionDeltaCollector c, IReadOnlyList<WindowInfo> before,
            WindowInfo? fg, long? gen)
        {
            _c = c;
            _fgBefore = fg;
            _genBefore = gen;
            _before = new Dictionary<long, WindowInfo>(before.Count);
            foreach (var w in before) _before[w.Hwnd] = w;
        }

        /// <summary>Finish collecting. <paramref name="settle"/> waits the
        /// configured post-action window for async effects (dialog opens,
        /// focus hops); pass false for pre-act guard rejections where no
        /// mutation could have occurred. <paramref name="target"/> is the
        /// resolved pre-action element — when the action succeeded it is
        /// re-resolved once to capture enabled/bounds/name/value changes.</summary>
        public ActionDelta Complete(ActionResult? result, UiElement? target, bool settle)
        {
            var c = _c;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // --- settle ---
            int waited = 0;
            try
            {
                if (settle && c._events != null && _genBefore.HasValue)
                {
                    // wakes as soon as ANY semantic event lands — quiet actions
                    // pay the full window, busy ones return almost immediately
                    c._events.WaitForNextEvent(_genBefore.Value, c._settleMs);
                    waited = (int)sw.ElapsedMilliseconds;
                    if (waited < c._settleMs + c._drainMs)
                    {
                        Thread.Sleep(c._drainMs);
                        waited = (int)sw.ElapsedMilliseconds;
                    }
                }
                else if (settle)
                {
                    Thread.Sleep(c._fallbackSettleMs);
                    waited = (int)sw.ElapsedMilliseconds;
                }
            }
            catch { /* delta collection must never fault the action result */ }

            // --- event slice since Begin ---
            List<ObservedEvent>? evs = null;
            if (c._events != null)
            {
                try
                {
                    evs = c._events.Snapshot(200)
                        .Where(e => e.At >= _at.AddMilliseconds(-50)).ToList();
                }
                catch { }
            }

            // --- window snapshot diff ---
            Dictionary<long, WindowInfo> after;
            try { after = (c._windows.ListWindows() ?? [])
                    .ToDictionary(w => w.Hwnd); }
            catch { after = new(); }

            var opened = new List<WindowDelta>();
            var closed = new List<WindowDelta>();
            var dialogs = new List<WindowDelta>();

            foreach (var (hwnd, w) in after)
            {
                if (_before.ContainsKey(hwnd)) continue;
                var d = ToDelta(w, dialogLikely: w.IsModalPopup);
                opened.Add(d);
                if (d.DialogLikely) dialogs.Add(d);
            }
            foreach (var (hwnd, w) in _before)
            {
                if (after.ContainsKey(hwnd)) continue;
                closed.Add(new WindowDelta(hwnd, w.Pid, w.Title,
                    w.ProcessName, w.IsModalPopup));
            }

            // event-observed hwnds that opened AND closed inside the window
            // (transient popups invisible to a two-point snapshot)
            if (evs is { Count: > 0 })
            {
                var openedEv = evs.Where(e => (e.Kind is EventKind.WindowOpened
                        or EventKind.WindowShown) && e.Hwnd is { })
                    .Select(e => e.Hwnd!.Value).ToHashSet();
                var closedEv = evs.Where(e => e.Kind == EventKind.WindowClosed
                        && e.Hwnd is { })
                    .Select(e => e.Hwnd!.Value).ToHashSet();
                foreach (var h in openedEv.Intersect(closedEv))
                {
                    if (opened.Any(o => o.Hwnd == h)) continue;
                    var d = new WindowDelta(h, null, null, null,
                        DialogLikely: false, Transient: true);
                    opened.Add(d);
                    closed.Add(d);
                }
                // a hwnd the snapshot missed entirely but events saw open
                foreach (var h in openedEv)
                {
                    if (_before.ContainsKey(h) || after.ContainsKey(h) ||
                        opened.Any(o => o.Hwnd == h)) continue;
                    opened.Add(new WindowDelta(h, null, null, null));
                }
            }

            // --- focus / foreground ---
            FocusDelta? focus = null;
            WindowInfo? fgAfter;
            try { fgAfter = c._windows.GetForegroundWindow(); }
            catch { fgAfter = null; }
            var fgHops = evs?.Where(e => e.Kind == EventKind.ForegroundChanged
                    && e.Hwnd is { })
                .Select(e => e.Hwnd!.Value).Distinct().ToList();
            if (_fgBefore?.Hwnd != fgAfter?.Hwnd ||
                (fgHops is { Count: > 1 }))
            {
                focus = new FocusDelta(
                    _fgBefore?.Hwnd, _fgBefore?.Title,
                    fgAfter?.Hwnd, fgAfter?.Title,
                    fgHops is { Count: > 0 } ? fgHops : null);
            }

            // --- target element re-read ---
            ElementDelta? el = null;
            if (target != null && result is { Success: true } &&
                c._backends is { } backends)
            {
                try
                {
                    var backend = backends.FirstOrDefault(
                        b => b.Id == target.Handle.Backend);
                    var fresh = backend?.ReResolve(target.Handle);
                    el = fresh == null
                        ? new ElementDelta(target.Id, Disappeared: true)
                        : new ElementDelta(target.Id,
                            EnabledBefore: Prop(target, "enabled"),
                            EnabledAfter: Prop(fresh, "enabled"),
                            NameBefore: target.Name,
                            NameAfter: fresh.Name,
                            ValueBefore: Prop(target, "value"),
                            ValueAfter: Prop(fresh, "value"),
                            StateBefore: Prop(target, "state"),
                            StateAfter: Prop(fresh, "state"),
                            BoundsBefore: target.Bounds,
                            BoundsAfter: fresh.Bounds);
                    if (el is { Changed: false }) el = null; // nothing to report
                }
                catch { }
            }

            // --- compact event summary ---
            var summary = Summarize(evs, opened, closed, focus);

            var delta = new ActionDelta(
                opened, closed, dialogs, focus, el, summary,
                c._events != null ? "events+snapshot" : "snapshot",
                waited);

            PerfTrace.Count("actionDelta.payloads");
            if (!delta.IsEmpty)
            {
                PerfTrace.Count("actionDelta.withChanges");
                PerfTrace.Count("actionDelta.opened", opened.Count);
                PerfTrace.Count("actionDelta.closed", closed.Count);
                if (focus != null) PerfTrace.Count("actionDelta.focusChanged");
                PerfLog.Write(new
                {
                    kind = "actionDelta",
                    at = DateTimeOffset.Now,
                    opened = opened.Count,
                    closed = closed.Count,
                    dialogs = dialogs.Count,
                    focusChanged = focus != null,
                    targetChanged = el != null,
                    events = evs?.Count ?? 0,
                    settleMs = waited,
                    source = delta.Source,
                });
            }
            return delta;
        }

        private static string? Prop(UiElement e, string name) =>
            e.Props.GetValueOrDefault(name)?.ToString();

        private static WindowDelta ToDelta(WindowInfo w, bool dialogLikely) =>
            new(w.Hwnd, w.Pid, w.Title, w.ProcessName, dialogLikely);

        private static List<string> Summarize(List<ObservedEvent>? evs,
            List<WindowDelta> opened, List<WindowDelta> closed, FocusDelta? focus)
        {
            var s = new List<string>(12);
            foreach (var o in opened.Take(6))
                s.Add($"window opened: '{o.Title ?? "?"}' 0x{o.Hwnd:X}" +
                      (o.DialogLikely ? " [dialog]" : "") +
                      (o.Transient ? " [transient]" : ""));
            foreach (var c in closed.Take(4))
                s.Add($"window closed: '{c.Title ?? "?"}' 0x{c.Hwnd:X}");
            if (focus != null)
                s.Add($"foreground: '{focus.FromTitle ?? "-"}' → '{focus.ToTitle ?? "-"}'");
            if (evs is { Count: > 0 })
            {
                foreach (var g in evs.GroupBy(e => e.Kind)
                             .OrderByDescending(g => g.Count()).Take(4))
                {
                    var kind = g.Key;
                    if (kind is EventKind.WindowOpened or EventKind.WindowShown
                        or EventKind.WindowClosed or EventKind.ForegroundChanged)
                        continue; // already covered by the lists above
                    s.Add($"{kind} x{g.Count()}");
                }
            }
            return s;
        }
    }
}
