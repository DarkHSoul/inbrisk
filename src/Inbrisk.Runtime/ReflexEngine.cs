using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>Classification contract the ReflexEngine decides through.
/// An interface rather than a static dependency so the engine compiles and
/// tests against any implementation — the stock <see cref="ReflexPolicy"/>
/// is static, so wiring adapts it via <see cref="DelegateReflexPolicy"/>.
/// <see cref="IsDangerous"/> must be conservative: when in doubt, true.</summary>
public interface IReflexPolicy
{
    /// <summary>Decide what to do with a verified modal under the armed
    /// mode. MUST NOT synthesize ModalButtons the engine didn't report —
    /// the engine rejects clicks on hwnds outside the modal's child tree.</summary>
    ReflexDecision Decide(ModalInfo modal, AutoDismissMode mode);

    /// <summary>Destructive/privileged classifier — checked BEFORE
    /// <see cref="Decide"/>. true → the run aborts and the window is never
    /// touched (no messages, no close).</summary>
    bool IsDangerous(ModalInfo modal);
}

/// <summary>Latched abort record set when the reflex engine fails the
/// plan: an unclassifiable or dangerous modal, or a dismiss that could
/// not be delivered by window messages. The run loop reads
/// <see cref="ReflexEngine.Abort"/> and fails the run with an
/// InterruptedByDialog-style structured error carrying this payload.</summary>
public sealed record ReflexAbort(
    long ModalHwnd,
    string? Title,
    ModalDisposition Disposition,
    string Reason,
    IReadOnlyList<string> ButtonsSeen,
    DateTimeOffset At);

/// <summary>One handled (or ignored) interception — appended to
/// <see cref="ReflexEngine.Log"/> for telemetry.</summary>
public sealed record ReflexInterception(
    DateTimeOffset At,
    long Hwnd,
    string? Title,
    int Pid,
    ModalDisposition Disposition,
    ReflexAction Action,
    bool Success,
    string Reason,
    double ElapsedMs);

/// <summary>
/// The auto-dismiss reflex: while armed, a modal window interrupt (raised by
/// the platform's ModalInterruptInterceptor via <see cref="IModalInterruptSource"/>)
/// is re-verified, inspected, classified by <see cref="IReflexPolicy"/>, and —
/// when safe — dismissed through ISilentInputService window messages
/// (BM_CLICK / WM_CLOSE) that never steal focus.
///
/// State machine:
///   Disarmed --Arm--> Armed --interrupt--> Handling (run loop paused)
///       Handling --dismissed--> Armed (focus restored to the armed window)
///       Handling --FailPlan--> Aborted (Abort latched; gate released so the
///                                run loop wakes and fails the run)
///       Armed/Aborted --Arm--> Armed (re-arm clears a latched Abort)
///       any --Disarm--> Disarmed (queue drained, pause released)
///
/// Threading: the event path only enqueues (sub-millisecond); all Win32
/// work happens on a dedicated single worker thread so modal handling is
/// serialized. The run loop consumes: <see cref="PauseGate"/> (wait while
/// the engine works) and <see cref="Abort"/> (fail the run when set).
///
/// Fail-safe default: anything unclassifiable, unsupported by message-only
/// input, or still alive after a delivered dismiss aborts the run — the
/// engine never blind-clicks and never sends SendInput.
/// </summary>
public sealed class ReflexEngine : IDisposable
{
    // ----------------------- bounded-work budgets -----------------------

    /// <summary>Max child windows enumerated during modal inspection.</summary>
    private const int MaxChildrenScanned = 100;
    /// <summary>Hard wall-clock budget for the whole child scan.</summary>
    private const int ScanBudgetMs = 300;
    /// <summary>Per-child WM_GETTEXT timeout — a hung dialog thread must not
    /// eat the scan budget one control at a time.</summary>
    private const int ChildTextTimeoutMs = 50;
    /// <summary>How long a delivered dismiss gets to actually kill the modal
    /// before it counts as a failure.</summary>
    private const int DismissWatchMs = 5_000;
    private const int DismissPollMs = 75;
    /// <summary>Event-source dedup window — the same hwnd re-signalled inside
    /// this window does not re-queue.</summary>
    private const int DedupWindowMs = 2_000;
    private const int LogCapacity = 256;
    private const int MaxDialogTextChars = 2_048;
    private const int MaxStaticTextChars = 512;

    private sealed record ArmedContext(long MainHwnd, int MainPid, AutoDismissMode Mode);

    private enum Verify { Gone, Foreign, NotModal, Modal }

    // ----------------------- dependencies / state -----------------------

    private readonly IWindowService _windows;
    private readonly ISilentInputService _silent;
    private readonly IReflexPolicy _policy;
    private readonly IModalInterruptSource? _source;
    /// <summary>Optional event buffer (e.g. RecentEventBuffer) consulted
    /// during dismiss-wait so a reported WindowClosed short-circuits the
    /// IsWindow poll.</summary>
    private readonly IEventWaiter? _events;

    private readonly BlockingCollection<ModalInterrupt> _queue = new();
    private readonly Thread _worker;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Hwnds currently queued/being handled — keeps the event path
    /// allocation-free and the queue shallow under repeat signals.</summary>
    private readonly ConcurrentDictionary<long, byte> _inflight = new();
    /// <summary>Recently handled hwnds → last-handled ticks (dedup).</summary>
    private readonly ConcurrentDictionary<long, long> _recent = new();

    private readonly List<ReflexInterception> _log = new();
    private readonly object _logGate = new();

    private ArmedContext? _armed;
    private ReflexAbort? _abort;
    private int _pauseDepth;
    private volatile bool _disposed;

    /// <summary>Pause gate the run loop waits on — reset while the engine is
    /// handling a modal, set when it releases (or aborts, so a waiting loop
    /// wakes and sees <see cref="Abort"/>). Mirrors AgentRunContext.PauseGate.</summary>
    public ManualResetEventSlim PauseGate { get; } = new(initialState: true);

    /// <summary>True while the engine is inspecting/dismissing a modal.</summary>
    public bool Paused => Volatile.Read(ref _pauseDepth) > 0;

    public bool Armed => Volatile.Read(ref _armed) != null;

    /// <summary>The main-window hwnd interception is scoped to
    /// (0 while disarmed).</summary>
    public long ArmedHwnd => Volatile.Read(ref _armed)?.MainHwnd ?? 0;

    /// <summary>The armed mode, or null while disarmed.</summary>
    public AutoDismissMode? Mode => Volatile.Read(ref _armed)?.Mode;

    /// <summary>Latched abort — non-null means the run must fail with an
    /// InterruptedByDialog-style structured error carrying this payload.
    /// Cleared by <see cref="Arm"/>.</summary>
    public ReflexAbort? Abort => Volatile.Read(ref _abort);

    /// <summary>Raised when the engine takes the pause (first in-flight
    /// modal). Marshalled on the worker thread — subscribers must be cheap.</summary>
    public event Action? PauseRequested;

    /// <summary>Raised when the last in-flight modal resolves — the run loop
    /// may proceed (and should re-check <see cref="Abort"/>).</summary>
    public event Action? ResumeRequested;

    /// <param name="windows">Window topology/actuation service.</param>
    /// <param name="silent">Message-based input — the ONLY actuation path
    /// the engine is allowed to use.</param>
    /// <param name="policy">Modal classifier. Wrap a static ReflexPolicy in
    /// <see cref="DelegateReflexPolicy"/> if it isn't an IReflexPolicy.</param>
    /// <param name="source">Optional modal-interrupt source; when supplied the
    /// engine subscribes immediately. Without one, feed
    /// <see cref="OnInterrupt"/> from any event shape.</param>
    /// <param name="events">Optional event buffer whose WindowClosed records
    /// accelerate the post-dismiss watch.</param>
    public ReflexEngine(IWindowService windows, ISilentInputService silent,
        IReflexPolicy policy, IModalInterruptSource? source = null,
        IEventWaiter? events = null)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _silent = silent ?? throw new ArgumentNullException(nameof(silent));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _source = source;
        _events = events;
        if (_source != null) _source.ModalDetected += OnInterrupt;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "inbrisk-reflex",
        };
        _worker.Start();
    }

    // ----------------------- arming -------------------------------------

    /// <summary>Arm the reflex for a run bound to
    /// <paramref name="mainHwnd"/>/<paramref name="mainPid"/>. Re-arming is
    /// legal — a per-step target-window change re-arms mid-leg — but only a
    /// FRESH arm (from the disarmed state) clears a latched <see cref="Abort"/>.
    /// Otherwise a FailPlan latched by a modal could be swallowed by a re-arm
    /// before the run loop ever reads it. Modals whose pid and owner chain
    /// do not trace back to the armed window are ignored.</summary>
    public void Arm(long mainHwnd, int mainPid, AutoDismissMode mode)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ReflexEngine));
        if (Volatile.Read(ref _armed) == null)
            Volatile.Write(ref _abort, null);
        Volatile.Write(ref _armed, new ArmedContext(mainHwnd, mainPid, mode));
        PerfLog.Write(new
        {
            kind = "reflex.arm",
            hwnd = $"0x{mainHwnd:X}",
            pid = mainPid,
            mode = mode.ToString(),
        });
    }

    /// <summary>Stand down: pending work is dropped, the pause (if held) is
    /// released, and further interrupts are ignored until the next Arm.
    /// A latched Abort stays readable for the run loop to consume.</summary>
    public void Disarm()
    {
        Volatile.Write(ref _armed, null);
        while (_queue.TryTake(out _)) { }
        _inflight.Clear();
        ReleasePauseAll();
        PerfLog.Write(new { kind = "reflex.disarm" });
    }

    /// <summary>Block while the engine is handling a modal (or until ct).
    /// The run loop calls this per-step; it should check <see cref="Abort"/>
    /// immediately after it returns.</summary>
    public void WaitWhilePaused(CancellationToken ct = default) => PauseGate.Wait(ct);

    /// <summary>Bounded interception log (newest last) for telemetry.</summary>
    public IReadOnlyList<ReflexInterception> Log
    {
        get { lock (_logGate) return _log.ToArray(); }
    }

    // ----------------------- event path (< 2ms) --------------------------

    /// <summary>Interrupt entry point — subscribed to
    /// <see cref="IModalInterruptSource.ModalDetected"/> when a source is
    /// supplied, and public so any event shape can feed it. Contract: O(1),
    /// no Win32 calls, never blocks.</summary>
    public void OnInterrupt(ModalInterrupt m)
    {
        var armed = Volatile.Read(ref _armed);
        if (armed == null || _disposed || Volatile.Read(ref _abort) != null) return;
        if (m.Hwnd == 0) return;
        if (!_inflight.TryAdd(m.Hwnd, 0)) return;

        // Same hwnd re-signalled shortly after we finished it — drop.
        var now = Environment.TickCount64;
        if (_recent.TryGetValue(m.Hwnd, out var last)
            && now - last < DedupWindowMs)
        {
            _inflight.TryRemove(m.Hwnd, out _);
            return;
        }
        if (_recent.Count > 512) _recent.Clear();

        try { _queue.Add(m); }
        catch { _inflight.TryRemove(m.Hwnd, out _); } // completed/disposed
    }

    // ----------------------- worker --------------------------------------

    private void WorkerLoop()
    {
        try
        {
            foreach (var m in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                try { Handle(m); }
                catch (Exception e)
                {
                    // The reflex must never take the process down.
                    PerfLog.Write(new
                    {
                        kind = "reflex.error",
                        hwnd = $"0x{m.Hwnd:X}",
                        error = e.Message,
                    });
                }
                finally
                {
                    _inflight.TryRemove(m.Hwnd, out _);
                    _recent[m.Hwnd] = Environment.TickCount64;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // CompleteAdding during Dispose
    }

    /// <summary>inspect → decide → dismiss → resume for one interrupt.</summary>
    private void Handle(ModalInterrupt m)
    {
        var armed = Volatile.Read(ref _armed);
        if (armed == null || Volatile.Read(ref _abort) != null) return;

        var sw = Stopwatch.StartNew();
        var hwnd = new IntPtr(m.Hwnd);

        // ---- re-verify: still there, still modal, still OURS ----
        var ver = Reverify(hwnd, m, armed, out var w, out var cls, out var ownerHwnd);
        if (ver != Verify.Modal)
        {
            var why = ver == Verify.Gone ? "window gone before inspection"
                : ver == Verify.Foreign ? "dialog belongs to a foreign process/window"
                : "re-check: no longer modal-shaped";
            RecordLog(m, ModalDisposition.Unknown, ReflexAction.Ignore, true, why, sw);
            return;
        }

        // ---- pause the run while we work ----
        BeginPause();
        try
        {
            var modal = InspectModal(hwnd, m, w, cls, ownerHwnd);
            PerfLog.Write(new
            {
                kind = "reflex.intercept",
                hwnd = $"0x{modal.Hwnd:X}",
                title = modal.Title,
                cls = modal.ClassName,
                pid = modal.Pid,
                buttons = modal.Buttons.Count,
                detection = m.Detection,
                ms = sw.ElapsedMilliseconds,
            });

            // Dangerous first — checked before Decide, and a throwing policy
            // counts as dangerous (fail-safe).
            bool dangerous;
            try { dangerous = _policy.IsDangerous(modal); }
            catch { dangerous = true; }
            if (dangerous)
            {
                FailPlan(modal, ModalDisposition.Unknown,
                    "dangerous modal — refusing to touch the window", sw, m);
                return;
            }

            ReflexDecision? decision;
            try { decision = _policy.Decide(modal, armed.Mode); }
            catch (Exception e)
            {
                FailPlan(modal, ModalDisposition.Unknown,
                    $"policy Decide threw: {e.Message}", sw, m);
                return;
            }
            if (decision == null)
            {
                FailPlan(modal, ModalDisposition.Unknown,
                    "policy returned no decision", sw, m);
                return;
            }

            // A second modal may have aborted, or the run may have
            // disarmed/re-armed, while we inspected — never act on a latched
            // abort or a stale arm context.
            if (Volatile.Read(ref _abort) != null
                || Volatile.Read(ref _armed) != armed) return;

            if (decision.Action == ReflexAction.FailPlan)
            {
                FailPlan(modal, decision.Disposition,
                    decision.Reason ?? "policy failed the plan", sw, m);
                return;
            }

            if (decision.Action == ReflexAction.Ignore)
            {
                RecordLog(m, decision.Disposition, ReflexAction.Ignore, true,
                    decision.Reason ?? "policy declined", sw);
                return;
            }

            if (!TryAct(modal, decision, out var how, out var actWhy))
            {
                FailPlan(modal, decision.Disposition,
                    actWhy ?? "dismiss could not be delivered", sw, m);
                return;
            }

            // ---- resume-on-dismiss: confirm the modal actually died ----
            if (!WaitForDismissal(modal.Hwnd))
            {
                FailPlan(modal, decision.Disposition,
                    $"{how} delivered but modal still alive after " +
                    $"{DismissWatchMs}ms — treating as failed dismiss", sw, m);
                return;
            }

            RestoreFocus(armed);
            PerfLog.Write(new
            {
                kind = "reflex.dismiss",
                hwnd = $"0x{modal.Hwnd:X}",
                title = modal.Title,
                disposition = decision.Disposition.ToString(),
                action = decision.Action.ToString(),
                method = how,
                ms = sw.ElapsedMilliseconds,
            });
            RecordLog(m, decision.Disposition, decision.Action, true,
                $"{decision.Reason ?? "dismissed"} ({how})", sw);
        }
        finally
        {
            // Releases the gate (and fires ResumeRequested) on EVERY exit —
            // including FailPlan, so a run loop parked on PauseGate wakes and
            // sees the latched Abort.
            EndPause();
        }
    }

    // ----------------------- verification --------------------------------

    /// <summary>Is the interrupt still a live modal belonging to the armed
    /// window? Order of evidence: IWindowService's WindowInfo/IsModalPopup and
    /// GetModalPopup, then Win32 owner+style heuristics (GW_OWNER /
    /// WS_POPUP / owner-disabled — a real modal disables its owner).</summary>
    private Verify Reverify(IntPtr hwnd, ModalInterrupt m, ArmedContext armed,
        out WindowInfo? w, out string cls, out long ownerHwnd)
    {
        w = null; cls = ""; ownerHwnd = 0;
        if (!Native.IsWindow(hwnd)) return Verify.Gone;
        try { w = _windows.GetWindow(m.Hwnd); } catch { w = null; }
        if (w == null) return Verify.Gone;

        cls = Native.ClassOf(hwnd);
        ownerHwnd = w.OwnerHwnd
            ?? m.OwnerHwnd
            ?? Native.GetWindow(hwnd, Native.GW_OWNER).ToInt64();

        // Scope: same pid as armed, or owned by the armed window. A foreign
        // modal is someone else's problem — we ignore rather than abort,
        // and we never message it.
        var ours = (armed.MainPid != 0 &&
                    (w.Pid == armed.MainPid || m.Pid == armed.MainPid))
                   || ownerHwnd == armed.MainHwnd;
        if (!ours && ownerHwnd != 0 && armed.MainHwnd != 0)
        {
            try
            {
                ours = _windows.GetTopLevelWindow(ownerHwnd)?.Hwnd
                       == armed.MainHwnd;
            }
            catch { }
        }
        if (!ours) return Verify.Foreign;

        // Modality. The hard signal: the owner (or the armed main window for
        // a task-modal) is disabled while this window is up.
        var modal = false;
        if (ownerHwnd != 0)
        {
            try { if (!_windows.IsWindowEnabled(ownerHwnd)) modal = true; }
            catch { }
            if (!modal)
            {
                try
                {
                    if (_windows.GetModalPopup(ownerHwnd)?.Hwnd == m.Hwnd)
                        modal = true;
                }
                catch { }
            }
        }
        if (!modal)
        {
            try
            {
                if (_windows.GetModalPopup(armed.MainHwnd)?.Hwnd == m.Hwnd)
                    modal = true;
            }
            catch { }
        }
        if (!modal && w.IsModalPopup && ownerHwnd != 0)
        {
            // WindowService marks any owned/#32770 window modal-ish — accept
            // it only with dialog shape behind it.
            if (cls == "#32770" || Native.IsPopupStyle(hwnd)) modal = true;
        }
        return modal ? Verify.Modal : Verify.NotModal;
    }

    // ----------------------- inspection ----------------------------------

    /// <summary>UIA-free Win32 inspection: bounded EnumChildWindows walk —
    /// button-class children become <see cref="ModalButton"/>s (caption via
    /// timeout-guarded WM_GETTEXT; the dialog control id — IDOK=1,
    /// IDCANCEL=2, … — rides along as AutomationId); static/edit text is
    /// joined into <see cref="ModalInfo.BodyText"/> for the policy to
    /// classify. ≤100 children, ≤300ms total, ≤50ms per caption read.</summary>
    private ModalInfo InspectModal(IntPtr hwnd, ModalInterrupt m,
        WindowInfo? w, string cls, long ownerHwnd)
    {
        var buttons = new List<ModalButton>();
        var texts = new List<string>();
        var sw = Stopwatch.StartNew();
        var visited = 0;

        Native.EnumProc cb = (child, _) =>
        {
            try
            {
                if (visited >= MaxChildrenScanned
                    || sw.ElapsedMilliseconds >= ScanBudgetMs)
                    return false;
                visited++;

                var ccls = Native.ClassOf(child);
                if (ccls.Equals("Button", StringComparison.OrdinalIgnoreCase))
                {
                    var cap = Native.TextOf(child, ChildTextTimeoutMs);
                    var id = Native.GetDlgCtrlID(child);
                    buttons.Add(new ModalButton(
                        Name: NormalizeCaption(cap),
                        AutomationId: id > 0 ? id.ToString() : null,
                        Hwnd: child.ToInt64()));
                }
                else if (ccls is "Static" or "SysLink" or "Edit"
                             or "RichEdit20W" or "RichEdit50W")
                {
                    // Static text + edit bodies — the dialog's message text
                    // the policy classifies against.
                    var cap = Native.TextOf(child, ChildTextTimeoutMs);
                    if (!string.IsNullOrWhiteSpace(cap)
                        && cap.Length <= MaxStaticTextChars)
                        texts.Add(cap.Trim());
                }
                return true;
            }
            catch { return true; }
        };
        try { Native.EnumChildWindows(hwnd, cb, IntPtr.Zero); }
        catch { }
        GC.KeepAlive(cb);

        var text = texts.Count == 0 ? null : string.Join(" | ", texts);
        if (text != null && text.Length > MaxDialogTextChars)
            text = text[..MaxDialogTextChars];

        // Owner title helps the policy classify (Haystack folds it in).
        string? ownerTitle = null;
        if (ownerHwnd != 0)
        {
            try { ownerTitle = _windows.GetWindow(ownerHwnd)?.Title; }
            catch { }
            if (string.IsNullOrEmpty(ownerTitle))
                ownerTitle = Native.TextOf(new IntPtr(ownerHwnd), 200);
            if (string.IsNullOrEmpty(ownerTitle)) ownerTitle = null;
        }

        return new ModalInfo(
            Hwnd: m.Hwnd,
            Title: w?.Title ?? m.Title,
            ClassName: cls,
            Pid: w?.Pid ?? m.Pid,
            OwnerTitle: ownerTitle,
            Buttons: buttons,
            BodyText: text);
    }

    private static string NormalizeCaption(string cap)
    {
        if (string.IsNullOrEmpty(cap)) return "";
        // Strip accelerator markers: "&Yes" → "Yes", "&&" → "&".
        var sb = new StringBuilder(cap.Length);
        for (var i = 0; i < cap.Length; i++)
        {
            if (cap[i] == '&' && i + 1 < cap.Length && cap[i + 1] == '&')
            { sb.Append('&'); i++; continue; }
            if (cap[i] == '&') continue;
            sb.Append(cap[i]);
        }
        return sb.ToString().Trim();
    }

    // ----------------------- actuation -----------------------------------

    /// <summary>Deliver the decision via window messages ONLY. ClickButton →
    /// TryClick (BM_CLICK on the button hwnd — no focus theft); Dismiss →
    /// TryClose (posted WM_CLOSE). Unsupported/timeout/failure returns false
    /// and the caller aborts — there is deliberately no SendInput fallback
    /// (foreground-restoration timing is fragile).</summary>
    private bool TryAct(ModalInfo modal, ReflexDecision d,
        out string how, out string? why)
    {
        how = "none"; why = null;
        try
        {
            // Protection recheck — the interceptor vetoed host/protected
            // windows at detection, but an owned same-process window can
            // still slip through that relevance gate. The engine never
            // messages a protected window, and never pretends it did.
            if (_windows.IsWindowProtected(modal.Hwnd, out var protWhy))
            { why = $"modal is a protected window: {protWhy}"; return false; }
            // Elevation: WM messages cannot cross UIPI — an elevated modal
            // would silently eat the click; fail honest so the plan aborts.
            try
            {
                if (_windows.GetWindow(modal.Hwnd)?.IsElevated == true)
                { why = "modal runs elevated — window messages cannot cross UIPI"; return false; }
            }
            catch { /* window info lookup failed → proceed, send is timeout-guarded */ }

            switch (d.Action)
            {
                case ReflexAction.ClickButton:
                {
                    var b = d.Button;
                    if (b?.Hwnd is not > 0)
                    { why = "decision named no button hwnd"; return false; }
                    var bh = new IntPtr(b.Hwnd.Value);
                    // Containment: the button hwnd must still live inside the
                    // modal's tree — never message a fabricated/stale hwnd.
                    if (!Native.IsWindow(bh)
                        || !Native.IsChild(new IntPtr(modal.Hwnd), bh))
                    {
                        why = $"button 0x{b.Hwnd.Value:X} no longer lives " +
                              "inside the modal — refusing to message it";
                        return false;
                    }
                    var r = _silent.TryClick(new SilentTargetRef(
                        ElementHwnd: b.Hwnd.Value));
                    if (!r.Ok)
                    {
                        why = $"silent click failed ({r.Status}): {r.Detail}";
                        return false;
                    }
                    how = r.Method; // "bm_click" — recorded honestly
                    return true;
                }
                case ReflexAction.Dismiss:
                {
                    var r = _silent.TryClose(modal.Hwnd);
                    if (!r.Ok)
                    {
                        why = $"wm_close failed ({r.Status}): {r.Detail}";
                        return false;
                    }
                    how = r.Method;
                    return true;
                }
                default:
                    why = $"action {d.Action} has no execution path";
                    return false;
            }
        }
        catch (Exception e)
        {
            why = $"actuation threw: {e.Message}";
            return false;
        }
    }

    /// <summary>Watch the modal die: a WindowClosed event short-circuits the
    /// IsWindow poll when an event buffer is wired, otherwise pure poll,
    /// ≤5s total.</summary>
    private bool WaitForDismissal(long hwnd)
    {
        var h = new IntPtr(hwnd);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < DismissWatchMs)
        {
            if (!Native.IsWindow(h)) return true;
            if (ObservedClosed(hwnd)) return true;
            Thread.Sleep(DismissPollMs);
            if (_disposed || _shutdown.IsCancellationRequested) return false;
        }
        return !Native.IsWindow(h);
    }

    private bool ObservedClosed(long hwnd)
    {
        var ev = _events;
        if (ev == null) return false;
        try
        {
            foreach (var e in ev.Snapshot(64))
                if (e.Kind == EventKind.WindowClosed && e.Hwnd == hwnd)
                    return true;
        }
        catch { }
        return false;
    }

    /// <summary>Foreground restoration: IWindowService.FocusWindow is the
    /// platform's AttachThreadInput + BringWindowToTop + SetForegroundWindow
    /// path — strictly more reliable than a bare SetForegroundWindow from a
    /// background process.</summary>
    private void RestoreFocus(ArmedContext armed)
    {
        try
        {
            if (_windows.GetWindow(armed.MainHwnd) != null)
                _windows.FocusWindow(armed.MainHwnd);
        }
        catch { /* best effort — focus loss is recoverable, a crash is not */ }
    }

    // ----------------------- pause / abort / log -------------------------

    private void BeginPause()
    {
        if (Interlocked.Increment(ref _pauseDepth) == 1)
        {
            try { PauseGate.Reset(); } catch (ObjectDisposedException) { }
            try { PauseRequested?.Invoke(); } catch { }
        }
    }

    private void EndPause()
    {
        while (true)
        {
            var d = Volatile.Read(ref _pauseDepth);
            if (d <= 0) return;
            if (Interlocked.CompareExchange(ref _pauseDepth, d - 1, d) != d)
                continue;
            if (d - 1 == 0)
            {
                try { PauseGate.Set(); } catch (ObjectDisposedException) { }
                try { ResumeRequested?.Invoke(); } catch { }
            }
            return;
        }
    }

    private void ReleasePauseAll()
    {
        if (Interlocked.Exchange(ref _pauseDepth, 0) > 0)
        {
            try { PauseGate.Set(); } catch (ObjectDisposedException) { }
            try { ResumeRequested?.Invoke(); } catch { }
        }
        else
        {
            try { PauseGate.Set(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>FailPlan: latch the abort record the run loop consumes. The
    /// gate is released by Handle's finally so a paused loop wakes, reads
    /// <see cref="Abort"/>, and fails the run.</summary>
    private void FailPlan(ModalInfo modal, ModalDisposition disp,
        string reason, Stopwatch sw, ModalInterrupt m)
    {
        var abort = new ReflexAbort(
            modal.Hwnd, modal.Title, disp, reason,
            modal.Buttons.Select(b => b.Name).ToList(),
            DateTimeOffset.Now);
        Volatile.Write(ref _abort, abort);
        RecordLog(m, disp, ReflexAction.FailPlan, false, reason, sw);
        PerfLog.Write(new
        {
            kind = "reflex.abort",
            hwnd = $"0x{modal.Hwnd:X}",
            title = modal.Title,
            disposition = disp.ToString(),
            reason,
            buttons = abort.ButtonsSeen,
            ms = sw.ElapsedMilliseconds,
        });
    }

    private void RecordLog(ModalInterrupt m, ModalDisposition disp,
        ReflexAction act, bool ok, string reason, Stopwatch sw)
    {
        var rec = new ReflexInterception(
            DateTimeOffset.Now, m.Hwnd, m.Title ?? "", m.Pid,
            disp, act, ok, reason, sw.Elapsed.TotalMilliseconds);
        lock (_logGate)
        {
            _log.Add(rec);
            if (_log.Count > LogCapacity) _log.RemoveAt(0);
        }
    }

    // ----------------------- dispose -------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _armed, null);

        if (_source != null)
            try { _source.ModalDetected -= OnInterrupt; } catch { }
        try { _queue.CompleteAdding(); } catch { }
        try { _shutdown.Cancel(); } catch { }
        try { _worker.Join(2_000); } catch { }
        ReleasePauseAll();

        try { _queue.Dispose(); } catch { }
        try { _shutdown.Dispose(); } catch { }
        try { PauseGate.Dispose(); } catch { }
    }

    // ----------------------- private Win32 --------------------------------
    // Runtime references only Core — Platform's NativeMethods is internal —
    // so the engine carries the few calls it needs. Same discipline as
    // SilentInput: cross-process reads go through SendMessageTimeoutW, never
    // bare SendMessage/GetWindowTextW which can hang on a wedged dialog.

    private static class Native
    {
        public const int GW_OWNER = 4;
        public const int GWL_STYLE = -16;
        public const uint WM_GETTEXT = 0x000D;
        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const long WS_POPUP = unchecked((long)0x80000000L);

        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLong64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutBuffer(IntPtr hWnd, uint msg,
            IntPtr wParam, StringBuilder lParam, uint fuFlags, uint uTimeout,
            out UIntPtr lpdwResult);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
            IntPtr.Size == 8
                ? GetWindowLong64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));

        public static bool IsPopupStyle(IntPtr hWnd) =>
            (GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64() & WS_POPUP) != 0;

        public static string ClassOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            try
            {
                return GetClassNameW(hWnd, sb, sb.Capacity) > 0
                    ? sb.ToString() : "";
            }
            catch { return ""; }
        }

        /// <summary>WM_GETTEXT via SendMessageTimeoutW — GetWindowTextW routes
        /// through a bare cross-process SendMessage and can hang.</summary>
        public static string TextOf(IntPtr hWnd, int timeoutMs)
        {
            var sb = new StringBuilder(512);
            try
            {
                var r = SendMessageTimeoutBuffer(hWnd, WM_GETTEXT,
                    new IntPtr(sb.Capacity), sb, SMTO_ABORTIFHUNG,
                    (uint)timeoutMs, out _);
                return r != IntPtr.Zero ? sb.ToString() : "";
            }
            catch { return ""; }
        }
    }
}

/// <summary>Adapter letting a static ReflexPolicy (or any pair of
/// delegates) satisfy <see cref="IReflexPolicy"/>:
/// <c>new DelegateReflexPolicy(ReflexPolicy.Decide, ReflexPolicy.IsDangerous)</c></summary>
public sealed class DelegateReflexPolicy : IReflexPolicy
{
    private readonly Func<ModalInfo, AutoDismissMode, ReflexDecision> _decide;
    private readonly Func<ModalInfo, bool> _isDangerous;

    public DelegateReflexPolicy(
        Func<ModalInfo, AutoDismissMode, ReflexDecision> decide,
        Func<ModalInfo, bool> isDangerous)
    {
        _decide = decide ?? throw new ArgumentNullException(nameof(decide));
        _isDangerous = isDangerous
            ?? throw new ArgumentNullException(nameof(isDangerous));
    }

    public ReflexDecision Decide(ModalInfo modal, AutoDismissMode mode)
        => _decide(modal, mode);

    public bool IsDangerous(ModalInfo modal) => _isDangerous(modal);
}
