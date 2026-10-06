using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;
using Inbrisk.Platform.Windows.Topology;

namespace Inbrisk.Platform.Windows.Events;

/// <summary>
/// A dialog-shaped top-level window that appeared while a run was armed on
/// <c>mainHwnd</c>. This layer is detection-only: cheap Win32 facts gathered
/// at event time, no UIA. Callers re-verify real modality (e.g.
/// IWindowService.GetModalPopup / GetActiveBlockingPopup) in the engine.
/// <paramref name="Detection"/> records which predicate matched.
/// </summary>
public sealed record ModalInterrupt(
    long Hwnd, string? Title, string? ClassName,
    int Pid, long? OwnerHwnd, DateTimeOffset At, string Detection);

/// <summary>
/// Subscribes to the existing <see cref="IEventSource"/> stream (the shared
/// WinEventService already hooks EVENT_OBJECT_CREATE/SHOW system-wide,
/// out-of-context — no second pump) and raises <see cref="Interrupted"/>
/// within ms when a foreign modal/dialog candidate appears while armed.
///
/// Hot path stays under ~2ms: the order of checks rejects the OBJECT_CREATE
/// object-churn storm (which fires per child HWND and non-window object)
/// using only IsWindowVisible/GetAncestor/class/style/owner reads — the
/// expensive steps (process-name resolution, IsWindowProtected) run only
/// after a window already looks dialog-shaped and run-relevant.
/// </summary>
public sealed class ModalInterruptInterceptor : IDisposable
{
    private static readonly int SelfPid = Environment.ProcessId;

    /// <summary>Menu/tooltip/lightweight-popup classes — never modal dialogs.</summary>
    private static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal)
    {
        "PopupHost", "Popup", "#32768", "ComboLBox",
        "Windows.UI.Core.CoreComponentInputSource",
        "tooltips_class32", "SysShadow", "MSCTFIME UI", "IME",
        // WinUI 3 popup host (titled "PopupHost") — hosts flyouts, tooltips,
        // autocomplete and IME candidates inside app windows. Same-pid
        // relevance would admit it and the engine would fail-safe abort a
        // benign run; it is a content popup, not a blocking dialog.
        "Microsoft.UI.Content.PopupWindowSiteBridge",
    };

    /// <summary>System processes whose #32770 windows count as interrupt
    /// candidates even without an owner link to the armed window. Mirrors the
    /// explicit system-dialog list in WindowService.IsSystemDialogOrFlyout.</summary>
    private static readonly HashSet<string> SystemDialogProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "werfault.exe", "pickerhost.exe", "openwith.exe", "credentialuibroker.exe",
        "smartscreen.exe", "consent.exe", "useraccountcontrol.exe",
        "shellexperiencehost.exe",
    };

    private readonly IEventSource _events;
    private readonly IWindowService? _windows;
    private readonly object _gate = new();
    private readonly HashSet<long> _reported = new();

    private volatile bool _armed;
    private long _mainHwnd;
    private int _mainPid;
    private bool _subscribed;
    private bool _disposed;

    public event Action<ModalInterrupt>? Interrupted;

    /// <param name="events">The shared event source (WinEventService or the
    /// coalescing/composite wrapper — WindowOpened/WindowShown pass through
    /// immediately). Must already be started by the host; this class does not
    /// own its lifetime.</param>
    /// <param name="windows">Optional window service for the protection veto
    /// (IsWindowProtected). When null, only pid-based protection applies.</param>
    public ModalInterruptInterceptor(IEventSource events, IWindowService? windows = null)
    {
        _events = events;
        _windows = windows;
    }

    public bool Armed => _armed;

    /// <summary>Arm: watch for modals that are NOT <paramref name="mainHwnd"/>.
    /// Re-arming resets the per-hwnd debounce. <paramref name="mainPid"/> may
    /// be null when unknown — owner-chain and system-dialog detection still work.</summary>
    public void Start(long mainHwnd, int? mainPid)
    {
        lock (_gate)
        {
            if (_disposed) return;
            // Normalize to the top-level root so a child hwnd still works.
            var h = new IntPtr(mainHwnd);
            var root = mainHwnd != 0 && NativeMethods.IsWindow(h)
                ? NativeMethods.GetAncestor(h, NativeMethods.GA_ROOT)
                : h;
            _mainHwnd = root != IntPtr.Zero ? root.ToInt64() : mainHwnd;
            _mainPid = mainPid ?? 0;
            _reported.Clear();
            _armed = true;
            if (!_subscribed)
            {
                _events.Event += OnEvent;
                _subscribed = true;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _armed = false;
            _reported.Clear();
        }
    }

    private void OnEvent(ObservedEvent e)
    {
        // Boundary: invoked on the WinEvent pump thread — a fault here rides
        // back into the hook dispatch. Keep it cheap and never throw.
        try
        {
            if (!_armed) return;
            if (e.Kind is not (EventKind.WindowOpened or EventKind.WindowShown)) return;
            if (e.Hwnd is not > 0) return;

            var hwnd = e.Hwnd.Value;
            if (hwnd == _mainHwnd) return;

            lock (_gate)
            {
                if (_reported.Contains(hwnd)) return;
            }

            var h = new IntPtr(hwnd);

            // Cheap structural rejects first: real window, visible, top-level.
            if (!NativeMethods.IsWindow(h)) return;
            if (!NativeMethods.IsWindowVisible(h)) return; // hidden now — a later SHOW event re-enters
            if (NativeMethods.GetAncestor(h, NativeMethods.GA_ROOT) != h) return;

            // pid: prefer the event's (WinEventService already paid for it).
            var pid = e.Pid ?? 0;
            if (pid <= 0)
                NativeMethods.GetWindowThreadProcessId(h, out pid);
            if (pid == SelfPid) return; // our HUD/indicator windows
            if (WindowService.IsAgentHostOrAncestorPid(pid, out _)) return; // host/terminal/IDE ancestors

            var cls = WindowService.GetClassName(h);
            if (cls.Length == 0 || SkipClasses.Contains(cls)) return;

            var owner = NativeMethods.GetWindow(h, NativeMethods.GW_OWNER);
            var style = (uint)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE).ToInt64();
            var exStyle = (int)NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();

            var isDialogClass = cls == "#32770";
            var hasModalFrame = (exStyle & NativeMethods.WS_EX_DLGMODALFRAME) != 0;
            var isOwnedPopup = (style & NativeMethods.WS_POPUP) != 0 && owner != IntPtr.Zero;
            if (!isDialogClass && !hasModalFrame && !isOwnedPopup) return;

            // Relevance: owner chain reaches the armed window, same process,
            // or a #32770 raised by a known system-dialog host while armed.
            var ownerHwnd = owner != IntPtr.Zero ? (long?)owner.ToInt64() : null;
            string? detection = null;
            if (OwnerChainReaches(h, _mainHwnd))
            {
                detection = $"owner-chain->main shape={(isDialogClass ? "#32770" : hasModalFrame ? "dlgmodalframe" : "popup+owner")}";
            }
            else if (_mainPid != 0 && pid == _mainPid)
            {
                detection = $"same-pid shape={(isDialogClass ? "#32770" : hasModalFrame ? "dlgmodalframe" : "popup+owner")}";
            }
            else if (isDialogClass && IsSystemDialogProc(pid, out var proc))
            {
                detection = $"system-dialog proc={proc}";
            }
            if (detection == null) return;

            if (_windows != null && _windows.IsWindowProtected(hwnd, out _)) return;

            var title = WindowService.GetTitle(h);
            lock (_gate)
            {
                if (_reported.Count > 4096) _reported.Clear(); // hwnd reuse guard
                if (!_reported.Add(hwnd)) return;
            }
            Raise(new ModalInterrupt(hwnd, title.Length == 0 ? null : title, cls,
                pid, ownerHwnd, e.At, detection));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"inbrisk modal-interceptor fault: {ex}");
        }
    }

    /// <summary>Walk the GW_OWNER chain (max 8 hops) looking for targetHwnd.</summary>
    private static bool OwnerChainReaches(IntPtr hwnd, long targetHwnd)
    {
        if (targetHwnd == 0) return false;
        var target = new IntPtr(targetHwnd);
        var cur = hwnd;
        for (var i = 0; i < 8; i++)
        {
            var owner = NativeMethods.GetWindow(cur, NativeMethods.GW_OWNER);
            if (owner == IntPtr.Zero || owner == cur) return false;
            if (owner == target) return true;
            cur = owner;
        }
        return false;
    }

    private static bool IsSystemDialogProc(int pid, out string? proc)
    {
        proc = null;
        if (pid <= 0) return false;
        proc = WindowService.GetProcessName(pid);
        return proc != null && SystemDialogProcs.Contains(proc);
    }

    // Per-subscriber delivery on the event thread — one bad handler must
    // neither abort the rest nor escape back into the WinEvent pump.
    private void Raise(ModalInterrupt m)
    {
        var subs = Interrupted;
        if (subs == null) return;
        foreach (Action<ModalInterrupt> sub in subs.GetInvocationList())
        {
            try { sub(m); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"inbrisk modal-interceptor subscriber fault: {ex}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _reported.Clear();
            if (_subscribed)
            {
                _events.Event -= OnEvent;
                _subscribed = false;
            }
        }
    }
}
