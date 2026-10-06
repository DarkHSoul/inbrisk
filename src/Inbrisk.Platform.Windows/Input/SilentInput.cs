using System.Diagnostics;
using System.Text;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>
/// Message-based ("silent") element actuation: driving a target by posting
/// window messages to its hwnd instead of synthesizing pointer/keyboard
/// input through SendInput. Works on occluded, background and (often)
/// minimized windows without stealing focus.
///
/// What messages can honestly do is narrow — this class refuses to fake the
/// rest. Supported: WM_SETTEXT / EM_REPLACESEL text entry, BM_CLICK /
/// BM_SETCHECK for real Win32 button-class controls, WM_COMMAND for menu
/// items with a resolvable command id, and WM_CLOSE for windows. Hotkeys,
/// right-click, double-click, drag and scroll have no reliable message-only
/// equivalent and report <see cref="SilentStatus.Unsupported"/> so the
/// dispatcher can fall back to SendInput instead of lying.
///
/// Blocking safety: every cross-window send goes through SendMessageTimeoutW
/// (SMTO_ABORTIFHUNG, ≤ <see cref="DefaultTimeoutMs"/>). A hung target can
/// never park the dispatcher on a synchronous SendMessage. WM_CLOSE is
/// *posted* (same as WindowService's graceful path) so it never blocks at
/// all. Verification read-backs (WM_GETTEXT, BM_GETCHECK) are timeout-guarded
/// too — GetWindowTextW would route through a bare SendMessage cross-process
/// and can hang.
/// </summary>
public static class SilentInput
{
    /// <summary>Per-send timeout for SendMessageTimeoutW.</summary>
    public const int DefaultTimeoutMs = 3_000;

    /// <summary>Shorter timeout for state read-backs and child-scan probes;
    /// a scan touches many hwnds, so each one gets a small budget.</summary>
    private const int ReadTimeoutMs = 1_000;
    private const int ProbeTimeoutMs = 250;

    /// <summary>Bounded effort for child-control scans and menu walks.</summary>
    private const int MaxChildrenScanned = 400;
    private const int MaxChildScanMs = 4_000;
    private const int MaxMenuDepth = 6;
    private const int MaxMenuItemsVisited = 200;

    /// <summary>Cap on WM_GETTEXT read-back length — a hostile/buggy control
    /// must not make us allocate unbounded buffers.</summary>
    private const int MaxTextChars = 64 * 1024;

    /// <summary>How long TryClose waits for the window to actually disappear
    /// before reporting "posted but not verified".</summary>
    private const int CloseVerifyMs = 1_500;

    // ------------------------------------------------------------------
    // result model
    // ------------------------------------------------------------------

    public enum SilentStatus
    {
        /// <summary>Message was delivered (and verified when a read-back
        /// exists — see <see cref="SilentResult.Verified"/>).</summary>
        Ok,
        /// <summary>Operation attempted but the target rejected it, or the
        /// post-condition did not hold.</summary>
        Failed,
        /// <summary>SendMessageTimeoutW timed out — target thread is hung
        /// or not pumping messages.</summary>
        Timeout,
        /// <summary>No reliable message-only path exists for this request;
        /// the dispatcher should fall back to SendInput or report
        /// NotSupported.</summary>
        Unsupported,
    }

    /// <summary>Outcome of one silent operation. <paramref name="Method"/>
    /// is the mechanism used ("wm_settext" | "em_replacesel" | "bm_click" |
    /// "bm_setcheck" | "wm_command" | "wm_close" | "none") so dispatch can
    /// log exactly what was tried.</summary>
    public sealed record SilentResult(
        bool Ok,
        SilentStatus Status,
        string Method,
        bool Verified,
        string? Detail = null);

    /// <summary>
    /// How a caller identifies the element to act on. Supply either
    /// <see cref="ElementHwnd"/> (the UIA element's NativeWindowHandle —
    /// zero for HWND-less UIA elements, which cannot be driven this way)
    /// or <see cref="WindowHwnd"/> plus enough of AutomationId / Name /
    /// Bounds to locate the child control via a bounded EnumChildWindows
    /// scan. <see cref="CommandId"/> addresses a menu item for WM_COMMAND.
    /// </summary>
    public sealed record SilentTarget(
        IntPtr? ElementHwnd = null,
        IntPtr? WindowHwnd = null,
        string? AutomationId = null,
        string? Name = null,
        RectPx? Bounds = null,
        int? CommandId = null);

    // ------------------------------------------------------------------
    // support matrix — dispatch consults this before attempting
    // ------------------------------------------------------------------

    public enum SilentSupport
    {
        /// <summary>Reliable message-only path exists.</summary>
        Supported,
        /// <summary>Works only when the target resolves to a compatible
        /// Win32 control (button-class hwnd, edit control, menu command
        /// id). HWND-less UIA elements are never eligible.</summary>
        Conditional,
        /// <summary>No honest message-only equivalent — do not attempt.</summary>
        Unsupported,
    }

    /// <summary>Whether an ActionKind has any silent path. "Conditional"
    /// kinds still need a compatible hwnd at call time.</summary>
    public static SilentSupport Capability(ActionKind kind) => kind switch
    {
        // SetValue→WM_SETTEXT/EM_REPLACESEL (edit-class hwnds);
        // Click/Invoke→BM_CLICK (button-class hwnds) or WM_COMMAND (menu
        // items); Toggle→BM_CLICK/BM_SETCHECK (checkable buttons).
        ActionKind.SetValue or ActionKind.Invoke or ActionKind.Click
            or ActionKind.Toggle => SilentSupport.Conditional,
        _ => SilentSupport.Unsupported,
    };

    /// <summary>Human-readable reason a kind can't be silent — surfaced in
    /// the dispatch NotSupported detail instead of a generic refusal.</summary>
    public static string UnsupportedReason(ActionKind kind) => kind switch
    {
        ActionKind.RightClick or ActionKind.DoubleClick or ActionKind.MiddleClick =>
            "no reliable message-only equivalent — synthesized WM_xBUTTON messages " +
            "are ignored by most apps and can desync button state",
        ActionKind.Drag =>
            "drag requires an ordered move/down/up stream with capture — " +
            "message injection cannot reproduce drag state reliably",
        ActionKind.Scroll =>
            "WM_MOUSEWHEEL/WM_VSCROLL routing depends on hover/focus state " +
            "that cannot be established silently",
        ActionKind.MouseMove =>
            "pure cursor motion is inherently non-silent",
        ActionKind.KeyPress or ActionKind.Hotkey or ActionKind.TypeText =>
            "keyboard input goes to the focused window — WM_KEYDOWN injection " +
            "cannot emulate focus, IME state or accelerator tables reliably",
        ActionKind.Select or ActionKind.Expand or ActionKind.Collapse
            or ActionKind.ScrollIntoView =>
            "control-pattern operation with no Win32 message equivalent — " +
            "use UIA patterns or pointer input",
        ActionKind.FocusWindow or ActionKind.FocusElement =>
            "activation/focus cannot be faked by messages; " +
            "use SetForegroundWindow path",
        ActionKind.ClipboardRead or ActionKind.ClipboardWrite =>
            "clipboard is not an element action and needs no silent path",
        _ => "no message-only path for this action kind",
    };

    /// <summary>Dispatch gate: false means return NotSupported to the caller
    /// rather than attempting (and misreporting) a silent action.</summary>
    public static bool IsSupported(ActionKind kind, out string detail)
    {
        var cap = Capability(kind);
        detail = cap == SilentSupport.Unsupported
            ? UnsupportedReason(kind)
            : "message-based path available when the element resolves to a " +
              "compatible Win32 control hwnd";
        return cap != SilentSupport.Unsupported;
    }

    // ------------------------------------------------------------------
    // set text
    // ------------------------------------------------------------------

    /// <summary>Set an edit-capable control's text. Primary: WM_SETTEXT
    /// (replaces whole content). Fallback for edit-class controls:
    /// EM_SETSEL(0,-1) + EM_REPLACESEL, which preserves the app's undo
    /// stack. Verified by WM_GETTEXT read-back.</summary>
    public static SilentResult TrySetText(IntPtr hwnd, string text)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return Fail("none", "hwnd is not a live window");
        if (!NativeMethods.IsWindowEnabled(hwnd))
            return Fail("none", "control is disabled");

        // Primary: WM_SETTEXT. lResult TRUE = text set.
        if (TrySendText(hwnd, NativeMethods.WM_SETTEXT, IntPtr.Zero, text,
                out var status))
        {
            if (ReadBackTextMatches(hwnd, text))
                return Ok("wm_settext", verified: true);
            // Delivered but read-back disagrees — apps that override
            // WM_SETTEXT (masked fields, filtered edits) do this.
            if (IsEditClass(hwnd))
                return SetTextViaReplaceSel(hwnd, text,
                    priorNote: "wm_settext read-back mismatch");
            return new SilentResult(false, SilentStatus.Failed, "wm_settext",
                Verified: false,
                "WM_SETTEXT accepted but WM_GETTEXT read-back does not match " +
                "(control may filter/transform its value)");
        }
        if (status == SilentStatus.Timeout)
            return new SilentResult(false, SilentStatus.Timeout, "wm_settext",
                Verified: false, "target did not answer WM_SETTEXT within timeout");

        // Fallback for edit controls: select-all + EM_REPLACESEL keeps the
        // control's undo buffer coherent.
        if (IsEditClass(hwnd))
            return SetTextViaReplaceSel(hwnd, text, priorNote: "wm_settext rejected");

        return Fail("wm_settext",
            $"WM_SETTEXT refused (class '{ClassOf(hwnd)}') — control likely " +
            "does not implement text storage via window text");
    }

    /// <summary>Resolve the target to a control hwnd, then set text.</summary>
    public static SilentResult TrySetText(SilentTarget target, string text)
    {
        if (!TryResolve(target, out var hwnd, out var why))
            return Unresolved("none", why);
        return TrySetText(hwnd, text);
    }

    private static SilentResult SetTextViaReplaceSel(IntPtr hwnd, string text,
        string priorNote)
    {
        // EM_SETSEL(0,-1): select entire contents so REPLACESEL overwrites.
        if (!TrySend(hwnd, NativeMethods.EM_SETSEL, IntPtr.Zero, new IntPtr(-1),
                out _))
            return new SilentResult(false, SilentStatus.Timeout, "em_replacesel",
                Verified: false, $"{priorNote}; EM_SETSEL timed out");

        if (!TrySendText(hwnd, NativeMethods.EM_REPLACESEL,
                new IntPtr(1) /* fCanUndo */, text, out var status))
            return new SilentResult(false, status, "em_replacesel",
                Verified: false, $"{priorNote}; EM_REPLACESEL not delivered");

        if (ReadBackTextMatches(hwnd, text))
            return new SilentResult(true, SilentStatus.Ok, "em_replacesel",
                Verified: true, priorNote);

        return new SilentResult(false, SilentStatus.Failed, "em_replacesel",
            Verified: false,
            $"{priorNote}; EM_REPLACESEL accepted but read-back mismatch");
    }

    // ------------------------------------------------------------------
    // click / toggle
    // ------------------------------------------------------------------

    /// <summary>
    /// BM_CLICK on a real button-class control. Refuses (Unsupported) on
    /// anything else — spraying WM_LBUTTONDOWN/UP at arbitrary hwnds is not
    /// reliable and is deliberately not attempted. Checkable buttons are
    /// verified via BM_GETCHECK; push buttons have no read-back state, so
    /// the result is delivered-but-unverified.
    /// </summary>
    public static SilentResult TryClick(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return Fail("none", "hwnd is not a live window");
        if (!IsButtonClass(hwnd))
            return new SilentResult(false, SilentStatus.Unsupported, "none",
                Verified: false,
                $"class '{ClassOf(hwnd)}' is not a Win32 button — BM_CLICK " +
                "would be ignored; refusing to synthesize mouse messages");
        if (!NativeMethods.IsWindowEnabled(hwnd))
            return Fail("bm_click", "button is disabled");

        if (!TrySend(hwnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero,
                out var status))
            return new SilentResult(false, status, "bm_click", Verified: false,
                status == SilentStatus.Timeout
                    ? "target did not answer BM_CLICK within timeout"
                    : "BM_CLICK not delivered");

        // Checkable buttons expose post-click state via BM_GETCHECK — a
        // genuine read-back. Push buttons don't; honest unverified Ok.
        if (IsCheckable(hwnd) && TryReadCheck(hwnd, out var checkState))
            return new SilentResult(true, SilentStatus.Ok, "bm_click",
                Verified: true,
                $"post-click check state = {checkState}");

        return new SilentResult(true, SilentStatus.Ok, "bm_click",
            Verified: false,
            "BM_CLICK delivered; push buttons expose no state read-back — " +
            "verify the effect via observation");
    }

    /// <summary>
    /// Click a resolved control (BM_CLICK) or — when the target names a menu
    /// item (CommandId, or Name matched through the window's menu tree) —
    /// invoke it via WM_COMMAND on the owning window.
    /// </summary>
    public static SilentResult TryClick(SilentTarget target)
    {
        if (TryResolve(target, out var hwnd, out _))
            return TryClick(hwnd);

        // No control hwnd — try the WM_COMMAND/menu path.
        if (target.WindowHwnd is { } win && win != IntPtr.Zero)
        {
            if (target.CommandId is { } id)
                return TryInvokeCommand(win, id);
            if (!string.IsNullOrEmpty(target.Name))
                return TryInvokeMenuItem(win, target.Name);
        }

        return Unresolved("none",
            "no element hwnd, no resolvable child control, and no menu " +
            "command id/name to address via WM_COMMAND");
    }

    /// <summary>
    /// Deterministic check-state set for checkboxes/radio buttons. Reads
    /// BM_GETCHECK first; if the state already matches, reports verified
    /// without side effects. Otherwise BM_CLICK (which fires the app's
    /// BN_CLICKED handler so business logic runs), verified by read-back;
    /// BM_SETCHECK is a last resort (sets state without notification).
    /// </summary>
    public static SilentResult TrySetCheck(IntPtr hwnd, bool check)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return Fail("none", "hwnd is not a live window");
        if (!IsButtonClass(hwnd) || !IsCheckable(hwnd))
            return new SilentResult(false, SilentStatus.Unsupported, "none",
                Verified: false,
                $"hwnd class '{ClassOf(hwnd)}' is not a checkable Win32 button");
        if (!NativeMethods.IsWindowEnabled(hwnd))
            return Fail("bm_click", "control is disabled");

        var want = check ? NativeMethods.BST_CHECKED : NativeMethods.BST_UNCHECKED;
        if (TryReadCheck(hwnd, out var cur) && cur == want)
            return new SilentResult(true, SilentStatus.Ok, "bm_getcheck",
                Verified: true, "already in desired check state");

        // BM_CLICK preserves notification semantics — the app sees a real
        // toggle, not a silent state write.
        if (!TrySend(hwnd, NativeMethods.BM_CLICK, IntPtr.Zero, IntPtr.Zero,
                out var status))
            return new SilentResult(false, status, "bm_click", Verified: false,
                "BM_CLICK toggle not delivered");

        if (TryReadCheck(hwnd, out var after) && after == want)
            return new SilentResult(true, SilentStatus.Ok, "bm_click",
                Verified: true);

        // Fallback: write state directly (no BN_CLICKED reaches the parent —
        // reported in detail so dispatch knows semantics differed).
        if (!TrySend(hwnd, NativeMethods.BM_SETCHECK, new IntPtr(want),
                IntPtr.Zero, out var status2))
            return new SilentResult(false, status2, "bm_setcheck",
                Verified: false,
                "BM_CLICK toggle did not land; BM_SETCHECK not delivered");

        if (TryReadCheck(hwnd, out var after2) && after2 == want)
            return new SilentResult(true, SilentStatus.Ok, "bm_setcheck",
                Verified: true,
                "state written via BM_SETCHECK — no BN_CLICKED notification " +
                "was sent to the parent");

        return Fail("bm_setcheck",
            "check state still does not match after BM_CLICK + BM_SETCHECK");
    }

    public static SilentResult TrySetCheck(SilentTarget target, bool check)
    {
        if (!TryResolve(target, out var hwnd, out var why))
            return Unresolved("none", why);
        return TrySetCheck(hwnd, check);
    }

    // ------------------------------------------------------------------
    // command / menu
    // ------------------------------------------------------------------

    /// <summary>
    /// Invoke a menu item by command id: WM_COMMAND, LOWORD=commandId,
    /// HIWORD=0 (menu source), lParam=0. This is the documented menu-item
    /// notification path — the window cannot distinguish it from a real
    /// menu pick. No state read-back exists; delivery is the success bar.
    /// </summary>
    public static SilentResult TryInvokeCommand(IntPtr windowHwnd, int commandId)
    {
        if (windowHwnd == IntPtr.Zero || !NativeMethods.IsWindow(windowHwnd))
            return Fail("none", "hwnd is not a live window");
        if (commandId <= 0 || commandId > 0xFFFF)
            return Fail("wm_command", $"command id {commandId} out of range");

        if (!TrySend(windowHwnd, NativeMethods.WM_COMMAND,
                new IntPtr(commandId), IntPtr.Zero, out var status))
            return new SilentResult(false, status, "wm_command",
                Verified: false,
                status == SilentStatus.Timeout
                    ? "target did not answer WM_COMMAND within timeout"
                    : "WM_COMMAND not delivered");

        return new SilentResult(true, SilentStatus.Ok, "wm_command",
            Verified: false,
            $"WM_COMMAND id={commandId} delivered; menu commands have no " +
            "state read-back — verify the effect via observation");
    }

    /// <summary>
    /// Find a menu item by caption in the window's menu tree (bounded walk:
    /// depth ≤ 6, ≤ 200 items) and invoke it via WM_COMMAND. Captions are
    /// matched with accelerator markers ('&amp;') and trailing ellipses
    /// stripped, case-insensitive.
    /// </summary>
    public static SilentResult TryInvokeMenuItem(IntPtr windowHwnd, string itemName)
    {
        if (windowHwnd == IntPtr.Zero || !NativeMethods.IsWindow(windowHwnd))
            return Fail("none", "hwnd is not a live window");
        if (string.IsNullOrWhiteSpace(itemName))
            return Fail("wm_command", "menu item name is empty");

        var hMenu = NativeMethods.GetMenu(windowHwnd);
        if (hMenu == IntPtr.Zero)
            return new SilentResult(false, SilentStatus.Unsupported,
                "wm_command", Verified: false,
                "window exposes no menu — nothing to invoke");

        var want = NormalizeCaption(itemName);
        var visited = 0;
        if (TryFindMenuCommand(hMenu, want, depth: 0, ref visited,
                out var commandId))
            return TryInvokeCommand(windowHwnd, commandId);

        return Unresolved("wm_command",
            $"no menu item matching '{itemName}' found " +
            $"(visited {visited} items, depth ≤ {MaxMenuDepth})");
    }

    // ------------------------------------------------------------------
    // close
    // ------------------------------------------------------------------

    /// <summary>
    /// Background close: posts WM_CLOSE — identical semantics to the
    /// graceful foreground path (apps may surface save prompts; a save
    /// prompt appearing IS correct polite behavior). Posted rather than
    /// sent so a hung queue can't block us. Verified by polling IsWindow
    /// for up to <see cref="CloseVerifyMs"/>.
    /// </summary>
    public static SilentResult TryClose(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return Fail("none", "hwnd is not a live window");

        if (!NativeMethods.PostMessageW(hwnd, NativeMethods.WM_CLOSE,
                IntPtr.Zero, IntPtr.Zero))
            return Fail("wm_close", "PostMessage(WM_CLOSE) failed");

        var deadline = Environment.TickCount64 + CloseVerifyMs;
        while (Environment.TickCount64 < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
                return new SilentResult(true, SilentStatus.Ok, "wm_close",
                    Verified: true, "window destroyed");
            Thread.Sleep(75);
        }

        // Still alive: save prompt, refusal, or slow teardown — the polite
        // close was delivered; the window's own logic decided to stay.
        return new SilentResult(true, SilentStatus.Ok, "wm_close",
            Verified: false,
            "WM_CLOSE posted but window still exists — likely a save " +
            "prompt, a modal blocker, or an app that ignored it");
    }

    // ------------------------------------------------------------------
    // target resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolve a <see cref="SilentTarget"/> to a concrete control hwnd.
    /// Direct path: <see cref="SilentTarget.ElementHwnd"/> when it is a live
    /// window. Indirect path: bounded EnumChildWindows scan of
    /// <see cref="SilentTarget.WindowHwnd"/> matching, in priority order,
    /// (a) AutomationId parsed as a dialog control id via GetDlgCtrlID,
    /// (b) control caption vs Name (accelerator-stripped), (c) screen-rect
    /// overlap vs Bounds. Returns false with <paramref name="detail"/>
    /// explaining why when nothing matches — callers must then report
    /// Unsupported rather than picking an arbitrary child.
    /// </summary>
    public static bool TryResolve(SilentTarget target, out IntPtr hwnd,
        out string? detail)
    {
        hwnd = IntPtr.Zero;
        detail = null;

        if (target.ElementHwnd is { } el && el != IntPtr.Zero)
        {
            if (NativeMethods.IsWindow(el))
            {
                hwnd = el;
                return true;
            }
            detail = $"element hwnd 0x{el:X} is not a live window (stale?)";
            // fall through — the window path may still locate a replacement
        }

        if (target.WindowHwnd is not { } win || win == IntPtr.Zero)
        {
            detail ??= "no element hwnd and no window hwnd supplied — " +
                "HWND-less UIA elements cannot be driven by window messages";
            return false;
        }
        if (!NativeMethods.IsWindow(win))
        {
            detail = $"window hwnd 0x{win:X} is not a live window";
            return false;
        }

        if (target.AutomationId == null && target.Name == null &&
            target.Bounds == null)
        {
            detail = "window hwnd supplied without automationId, name or " +
                "bounds — refusing to guess a child control";
            return false;
        }

        var scan = new ChildScan(target);
        NativeMethods.EnumChildWindows(win, scan.Callback, IntPtr.Zero);
        var best = scan.BestOrBounds;
        if (best != IntPtr.Zero)
        {
            hwnd = best;
            return true;
        }

        detail = $"no child control matched after scanning " +
            $"{scan.Scanned} children (bounded at {MaxChildrenScanned} / " +
            $"{MaxChildScanMs}ms)";
        return false;
    }

    /// <summary>EnumChildWindows scoring pass. Exact control-id or caption
    /// matches win immediately; the first bounds-overlap match is kept as
    /// a fallback. The whole pass is bounded by child count and wall time —
    /// a pathological window tree can never stall the dispatcher.</summary>
    private sealed class ChildScan
    {
        private readonly SilentTarget _t;
        private readonly string? _wantName;
        private readonly int? _wantCtrlId;
        private readonly string? _wantIdText;
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private IntPtr _boundsMatch = IntPtr.Zero;

        public IntPtr Best { get; private set; } = IntPtr.Zero;
        public int Scanned { get; private set; }

        public ChildScan(SilentTarget t)
        {
            _t = t;
            _wantName = t.Name is { } n ? NormalizeCaption(n) : null;
            // UIA's Win32-provider automationId is usually the numeric
            // dialog resource id; non-numeric ids are matched against the
            // control's caption as a secondary heuristic.
            if (t.AutomationId is { } aid)
            {
                var trimmed = aid.Trim();
                if (int.TryParse(trimmed, out var id) && id > 0)
                    _wantCtrlId = id;
                else
                    _wantIdText = NormalizeCaption(trimmed);
            }
        }

        public bool Callback(IntPtr child, IntPtr _)
        {
            Scanned++;
            if (Scanned > MaxChildrenScanned ||
                _elapsed.ElapsedMilliseconds > MaxChildScanMs)
                return false;

            // Control id: cheap, no message round-trip.
            if (_wantCtrlId is { } cid &&
                NativeMethods.GetDlgCtrlID(child) == cid)
            {
                Best = child;
                return false;
            }

            // Caption: one guarded WM_GETTEXT, only when text matching is
            // actually requested.
            if (_wantName != null || _wantIdText != null)
            {
                if (TryReadText(child, ProbeTimeoutMs, out var caption))
                {
                    var norm = NormalizeCaption(caption);
                    if (MatchesCaption(norm, _wantName) ||
                        MatchesCaption(norm, _wantIdText))
                    {
                        Best = child;
                        return false;
                    }
                }
            }

            // Bounds overlap: first plausible geometric match is remembered
            // but exact id/caption hits may still overrule it later.
            if (_boundsMatch == IntPtr.Zero && _t.Bounds is { } b &&
                !b.IsEmpty && NativeMethods.GetWindowRect(child, out var rc))
            {
                var childRect = new RectPx(rc.Left, rc.Top,
                    rc.Right - rc.Left, rc.Bottom - rc.Top);
                var (cx, cy) = childRect.Center;
                var (tx, ty) = b.Center;
                if (b.Contains(cx, cy) || childRect.Contains(tx, ty) ||
                    b.Intersect(childRect) is { IsEmpty: false } overlap &&
                    overlap.Width * overlap.Height * 2 >=
                    Math.Min(b.Width * b.Height,
                        childRect.Width * childRect.Height))
                    _boundsMatch = child;
            }

            return true;
        }

        /// <summary>Exact id/caption hit if found, else the first
        /// bounds-overlap match.</summary>
        public IntPtr BestOrBounds =>
            Best != IntPtr.Zero ? Best : _boundsMatch;
    }

    // ------------------------------------------------------------------
    // low-level send/read helpers — all timeout-guarded
    // ------------------------------------------------------------------

    /// <summary>Send with IntPtr payload. Returns false on failure and sets
    /// <paramref name="status"/> (Timeout vs Failed).</summary>
    private static bool TrySend(IntPtr hwnd, uint msg, IntPtr wParam,
        IntPtr lParam, out SilentStatus status,
        int timeoutMs = DefaultTimeoutMs)
    {
        var ret = NativeMethods.SendMessageTimeoutW(hwnd, msg, wParam, lParam,
            NativeMethods.SMTO_ABORTIFHUNG, (uint)timeoutMs, out _);
        if (ret != IntPtr.Zero)
        {
            status = SilentStatus.Ok;
            return true;
        }
        status = TimeoutOrFailed();
        return false;
    }

    /// <summary>Send with LPWStr payload (WM_SETTEXT, EM_REPLACESEL).</summary>
    private static bool TrySendText(IntPtr hwnd, uint msg, IntPtr wParam,
        string text, out SilentStatus status,
        int timeoutMs = DefaultTimeoutMs)
    {
        var ret = NativeMethods.SendMessageTimeoutText(hwnd, msg, wParam, text,
            NativeMethods.SMTO_ABORTIFHUNG, (uint)timeoutMs, out var lres);
        if (ret != IntPtr.Zero)
        {
            // WM_SETTEXT signals refusal through lResult FALSE too.
            status = lres != UIntPtr.Zero || msg != NativeMethods.WM_SETTEXT
                ? SilentStatus.Ok
                : SilentStatus.Failed;
            return status == SilentStatus.Ok;
        }
        status = TimeoutOrFailed();
        return false;
    }

    private static SilentStatus TimeoutOrFailed()
    {
        // SendMessageTimeoutW fails on timeout (ERROR_TIMEOUT) and on a dead
        // hwnd — distinguish so the result honestly says which.
        var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        // ERROR_TIMEOUT (or no code — SendMessageTimeoutW may return 0
        // without setting one when the window is already gone) both read as
        // "not delivered"; only a live-but-unresponsive queue is a timeout.
        return err == 1460 /* ERROR_TIMEOUT */
            ? SilentStatus.Timeout
            : SilentStatus.Failed;
    }

    /// <summary>WM_GETTEXT read-back via SendMessageTimeoutW — never bare
    /// GetWindowTextW, which can hang on an unresponsive target.</summary>
    private static bool TryReadText(IntPtr hwnd, int timeoutMs,
        out string? text)
    {
        text = null;
        var ret = NativeMethods.SendMessageTimeoutW(hwnd,
            NativeMethods.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, (uint)timeoutMs, out var lenRes);
        if (ret == IntPtr.Zero)
            return false;

        var len = (int)Math.Min((ulong)lenRes, MaxTextChars);
        var sb = new StringBuilder(len + 8);
        ret = NativeMethods.SendMessageTimeoutBuffer(hwnd,
            NativeMethods.WM_GETTEXT, (IntPtr)sb.Capacity, sb,
            NativeMethods.SMTO_ABORTIFHUNG, (uint)timeoutMs, out _);
        if (ret == IntPtr.Zero)
            return false;

        text = sb.ToString();
        return true;
    }

    private static bool ReadBackTextMatches(IntPtr hwnd, string expected)
    {
        // Give the target's message queue a beat to finish applying the
        // text before the read-back; still bounded by ReadTimeoutMs.
        Thread.Sleep(30);
        return TryReadText(hwnd, ReadTimeoutMs, out var got) &&
            string.Equals(got, expected, StringComparison.Ordinal);
    }

    private static bool TryReadCheck(IntPtr hwnd, out int state)
    {
        state = -1;
        var ret = NativeMethods.SendMessageTimeoutW(hwnd,
            NativeMethods.BM_GETCHECK, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG, ReadTimeoutMs, out var res);
        if (ret == IntPtr.Zero)
            return false;
        state = (int)res;
        return state is >= NativeMethods.BST_UNCHECKED
            and <= NativeMethods.BST_INDETERMINATE;
    }

    // ------------------------------------------------------------------
    // class / style helpers
    // ------------------------------------------------------------------

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        return NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity) > 0
            ? sb.ToString()
            : "";
    }

    /// <summary>Real Win32 buttons: the standard "Button" class plus
    /// WinForms' versioned ".BUTTON." classes. Toolbar/listview items are
    /// NOT hwnds and are excluded by construction.</summary>
    private static bool IsButtonClass(IntPtr hwnd)
    {
        var cls = ClassOf(hwnd);
        return cls.Equals("Button", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains(".button.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEditClass(IntPtr hwnd)
    {
        var cls = ClassOf(hwnd);
        return cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
            cls.Contains(".edit.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checkbox/radio detection from the window style so we know
    /// when BM_GETCHECK is a meaningful read-back.</summary>
    private static bool IsCheckable(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd,
            NativeMethods.GWL_STYLE).ToInt64();
        var type = style & NativeMethods.BS_TYPEMASK;
        return type is NativeMethods.BS_CHECKBOX or NativeMethods.BS_AUTOCHECKBOX
            or NativeMethods.BS_3STATE or NativeMethods.BS_AUTO3STATE
            or NativeMethods.BS_RADIOBUTTON or NativeMethods.BS_AUTORADIOBUTTON;
    }

    // ------------------------------------------------------------------
    // menu tree walk
    // ------------------------------------------------------------------

    private static bool TryFindMenuCommand(IntPtr hMenu, string wantNorm,
        int depth, ref int visited, out int commandId)
    {
        commandId = 0;
        if (hMenu == IntPtr.Zero || depth > MaxMenuDepth)
            return false;

        var count = NativeMethods.GetMenuItemCount(hMenu);
        var sb = new StringBuilder(256);
        for (var i = 0; i < count; i++)
        {
            if (++visited > MaxMenuItemsVisited)
                return false;

            var id = NativeMethods.GetMenuItemID(hMenu, i);
            if (id == NativeMethods.MENU_ITEM_SUBMENU)
            {
                var sub = NativeMethods.GetSubMenu(hMenu, i);
                if (TryFindMenuCommand(sub, wantNorm, depth + 1, ref visited,
                        out commandId))
                    return true;
                continue;
            }
            if (id == 0) continue; // separator

            sb.Clear();
            NativeMethods.GetMenuStringW(hMenu, (uint)i, sb, sb.Capacity,
                NativeMethods.MF_BYPOSITION);
            if (MatchesCaption(NormalizeCaption(sb.ToString()), wantNorm))
            {
                commandId = (int)id;
                return true;
            }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // small utilities
    // ------------------------------------------------------------------

    /// <summary>Strip accelerator '&amp;' markers and trailing ellipsis so
    /// "&amp;Save As…" matches "Save As".</summary>
    private static string NormalizeCaption(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var t = s.Replace("&", "").Replace("…", "").Trim();
        while (t.EndsWith("...", StringComparison.Ordinal))
            t = t[..^3].TrimEnd();
        return t.Trim().TrimEnd('.', '…').Trim();
    }

    private static bool MatchesCaption(string? actual, string? want)
    {
        if (actual == null || want == null || actual.Length == 0)
            return false;
        return actual.Equals(want, StringComparison.OrdinalIgnoreCase) ||
            actual.StartsWith(want, StringComparison.OrdinalIgnoreCase);
    }

    private static SilentResult Fail(string method, string detail) =>
        new(false, SilentStatus.Failed, method, Verified: false, detail);

    private static SilentResult Unresolved(string method, string? detail) =>
        new(false, SilentStatus.Unsupported, method, Verified: false,
            detail ?? "target did not resolve to a usable hwnd");

    private static SilentResult Ok(string method, bool verified,
        string? detail = null) =>
        new(true, SilentStatus.Ok, method, Verified: verified, detail);
}
