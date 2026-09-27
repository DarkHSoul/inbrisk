using System.Runtime.InteropServices;
using System.Text;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>Win32 truth about windows, monitors, virtual desktops, foreground.</summary>
public sealed class WindowService : IWindowService
{
    private static readonly int SelfPid = Environment.ProcessId;
    private static readonly HashSet<int> ProtectedPids = GetSelfAndAncestorPids();

    public WindowService() => Dpi.EnsurePerMonitorV2();

    public IReadOnlyList<WindowInfo> ListWindows() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var monitors = GetMonitors().ToList();
        var fg = NativeMethods.GetForegroundWindow();
        var vdm = TryCreateVdm();
        var list = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            if (IsCloaked(hwnd)) return true;
            var title = GetTitle(hwnd);
            if (string.IsNullOrEmpty(title)) return true; // skip untitled tool windows

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == SelfPid) return true;

            NativeMethods.GetWindowRect(hwnd, out var r);
            var bounds = new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

            var mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var monIdx = monitors.FindIndex(m => m.Bounds == RectOf(mon));

            var onVd = true;
            if (vdm != null)
            {
                try { onVd = vdm.IsWindowOnCurrentVirtualDesktop(hwnd) != 0; }
                catch { /* treat as on current */ }
            }

            var popup = NativeMethods.GetLastActivePopup(hwnd);
            var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
            long? popupHwnd = (popup != IntPtr.Zero && popup != hwnd && popup != owner && NativeMethods.IsWindow(popup) && NativeMethods.IsWindowVisible(popup))
                ? popup.ToInt64() : null;
            long? ownerHwnd = owner != IntPtr.Zero ? owner.ToInt64() : null;
            var isEnabled = NativeMethods.IsWindowEnabled(hwnd);
            var cls = GetClassName(hwnd);
            var isMenuOrPopup = cls is "PopupHost" or "Popup" or "#32768" or "ComboLBox" or "Windows.UI.Core.CoreComponentInputSource";
            var isModal = !isMenuOrPopup && (cls == "#32770" || ownerHwnd.HasValue);

            list.Add(new WindowInfo(
                Hwnd: hwnd.ToInt64(),
                Pid: pid,
                Title: title,
                ProcessName: GetProcessName(pid),
                Bounds: bounds,
                State: NativeMethods.IsIconic(hwnd) ? WindowState.Minimized
                     : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized
                     : WindowState.Normal,
                IsForeground: hwnd == fg,
                IsElevated: IntegrityService.IsProcessElevated(pid),
                OnCurrentVirtualDesktop: onVd,
                MonitorIndex: monIdx,
                IsModalPopup: isModal,
                OwnerHwnd: ownerHwnd,
                ModalPopupHwnd: popupHwnd,
                IsEnabled: isEnabled));
            return true;
        }, IntPtr.Zero);

        return list.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).ToList();
    });

    public WindowInfo? GetWindow(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return null;
        var fg = NativeMethods.GetForegroundWindow();
        var mon = NativeMethods.MonitorFromWindow(h, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var monitors = GetMonitors().ToList();
        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        NativeMethods.GetWindowRect(h, out var r);
        var onVd = true;
        var vdm = TryCreateVdm();
        if (vdm != null) try { onVd = vdm.IsWindowOnCurrentVirtualDesktop(h) != 0; } catch { }

        var popup = NativeMethods.GetLastActivePopup(h);
        long? popupHwnd = (popup != IntPtr.Zero && popup != h && NativeMethods.IsWindow(popup) && NativeMethods.IsWindowVisible(popup))
            ? popup.ToInt64() : null;
        var owner = NativeMethods.GetWindow(h, NativeMethods.GW_OWNER);
        long? ownerHwnd = owner != IntPtr.Zero ? owner.ToInt64() : null;
        var isEnabled = NativeMethods.IsWindowEnabled(h);
        var cls = GetClassName(h);
        var isModal = cls == "#32770" || ownerHwnd.HasValue;

        return new WindowInfo(
            hwnd, pid, GetTitle(h), GetProcessName(pid),
            new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            NativeMethods.IsIconic(h) ? WindowState.Minimized
                : NativeMethods.IsZoomed(h) ? WindowState.Maximized
                : WindowState.Normal,
            h == fg, IntegrityService.IsProcessElevated(pid), onVd,
            monitors.FindIndex(m => m.Bounds == RectOf(mon)),
            IsModalPopup: isModal,
            OwnerHwnd: ownerHwnd,
            ModalPopupHwnd: popupHwnd,
            IsEnabled: isEnabled);
    });

    public WindowInfo? GetModalPopup(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return null;
        var popup = NativeMethods.GetLastActivePopup(h);
        if (popup == IntPtr.Zero || popup == h || !NativeMethods.IsWindow(popup) || !NativeMethods.IsWindowVisible(popup))
            return null;
        var owner = NativeMethods.GetWindow(h, NativeMethods.GW_OWNER);
        if (popup == owner) return null; // An owner window is never a modal popup blocking its owned child
        var popupCls = GetClassName(popup);
        if (popupCls is "PopupHost" or "Popup" or "#32768" or "ComboLBox" or "Windows.UI.Core.CoreComponentInputSource")
            return null;
        return GetWindow(popup.ToInt64());
    });

    public bool IsWindowEnabled(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        return NativeMethods.IsWindow(h) && NativeMethods.IsWindowEnabled(h);
    });

    public IReadOnlyList<WindowInfo> FindSystemDialogs() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var list = new List<WindowInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || IsCloaked(hwnd) || NativeMethods.IsIconic(hwnd)) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == SelfPid) return true;

            var cls = GetClassName(hwnd);
            var title = GetTitle(hwnd);
            var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
            var proc = GetProcessName(pid) ?? "";

            bool isDialog = IsSystemDialogOrFlyout(proc, cls, title, owner);

            if (isDialog)
            {
                if (GetWindow(hwnd.ToInt64()) is { } win)
                {
                    var ownerHwnd = owner != IntPtr.Zero ? (long?)owner.ToInt64() : null;
                    list.Add(win with { IsModalPopup = true, OwnerHwnd = ownerHwnd });
                }
            }
            return true;
        }, IntPtr.Zero);
        return list;
    });

    public static long GetRootHwnd(long hwnd)
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return hwnd;
        var root = NativeMethods.GetAncestor(h, NativeMethods.GA_ROOT);
        return root != IntPtr.Zero ? root.ToInt64() : hwnd;
    }

    public static bool IsSystemDialogOrFlyout(string proc, string cls, string title, IntPtr owner)
    {
        // Menus, submenus, dropdowns, combo boxes are lightweight popups, not blocking system dialogs
        if (cls is "PopupHost" or "Popup" or "#32768" or "ComboLBox" or "Windows.UI.Core.CoreComponentInputSource")
            return false;

        var p = proc.ToLowerInvariant();
        if (p is "werfault.exe" or "pickerhost.exe" or "openwith.exe" or "credentialuibroker.exe"
            or "smartscreen.exe" or "consent.exe" or "useraccountcontrol.exe")
            return true;

        if (p is "shellexperiencehost.exe")
        {
            if (title.Contains("Share", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Paylaş", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Open with", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Birlikte Aç", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Cast", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Project", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Connect", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Nearby", StringComparison.OrdinalIgnoreCase) ||
                (cls == "Windows.UI.Core.CoreWindow" && !string.IsNullOrWhiteSpace(title)
                 && !title.Equals("Windows Shell Experience Host", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Owned dialog: has an owner window (e.g. Save As, File Open, font dialog, confirmation prompt, message box)
        if (owner != IntPtr.Zero && (cls == "#32770" || !string.IsNullOrWhiteSpace(title))) return true;

        var t = title.Trim();
        if (t.Equals("Share", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Paylaş", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Share ", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Paylaş ", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Hata", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Uyarı", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Alert", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Dikkat", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Yanıt Vermiyor", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Not Responding", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Problem", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Sorun", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("Çökme", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public WindowInfo? GetActiveBlockingPopup(long? targetHwnd = null) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        long? targetRoot = targetHwnd.HasValue ? GetRootHwnd(targetHwnd.Value) : null;

        // 1. If targetRoot is specified, check classic Win32 modal popup first
        if (targetRoot.HasValue)
        {
            var modal = GetModalPopup(targetRoot.Value);
            if (modal != null && modal.Hwnd != targetRoot.Value)
                return modal;
        }

        // 2. Query system dialogs and flyouts
        var dialogs = FindSystemDialogs();
        if (dialogs.Count == 0) return null;

        // If targetRoot is specified, check if any dialog is owned by targetRoot
        if (targetRoot.HasValue)
        {
            var targetH = new IntPtr(targetHwnd!.Value);
            var targetOwner = NativeMethods.GetWindow(targetH, NativeMethods.GW_OWNER).ToInt64();

            // A dialog owned by targetRoot blocks targetRoot, UNLESS targetRoot/targetH is itself the dialog or its owner
            var owned = dialogs.FirstOrDefault(d => d.OwnerHwnd == targetRoot.Value && d.Hwnd != targetRoot.Value && d.Hwnd != targetHwnd.Value && d.OwnerHwnd != targetHwnd.Value && d.Hwnd != targetOwner);
            if (owned != null) return owned;
        }

        // 3. Check if the foreground window itself is a system dialog or flyout
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var fgHwnd = fg.ToInt64();
            if (targetRoot.HasValue && targetRoot.Value == fgHwnd)
                return null;
            if (targetHwnd.HasValue && targetHwnd.Value == fgHwnd)
                return null;

            var fgDialog = dialogs.FirstOrDefault(d => d.Hwnd == fgHwnd);
            if (fgDialog != null && (!targetRoot.HasValue || fgDialog.Hwnd != targetRoot.Value))
            {
                // If the target is owned by fgDialog or fgDialog is owned by target, do not block each other
                if (targetHwnd.HasValue)
                {
                    var targetOwner = NativeMethods.GetWindow(new IntPtr(targetHwnd.Value), NativeMethods.GW_OWNER).ToInt64();
                    if (targetOwner == fgDialog.Hwnd || fgDialog.OwnerHwnd == targetHwnd.Value || fgDialog.OwnerHwnd == targetRoot)
                        return null;
                }
                return fgDialog;
            }
        }

        return null;
    });

    public bool IsWindowProtected(long hwnd, out string? reason)
    {
        reason = null;
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return false;

        var cls = GetClassName(h);
        var title = GetTitle(h);

        // Real Windows Shell windows that MUST be protected from closure:
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" || title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"protected Windows desktop or taskbar shell ('{cls}')";
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(h, out var pid);
        var proc = (GetProcessName(pid) ?? "").ToLowerInvariant();

        // File Explorer folder windows (CabinetWClass or ExploreWClass, or explorer.exe with "File Explorer" / "Dosya Gezgini")
        // are document/folder browsing windows. They share the explorer.exe PID (which is often an ancestor of the terminal),
        // but closing them (via WM_CLOSE or Ctrl+W) closes ONLY that folder window and NEVER terminates the Windows desktop or shell.
        bool isFileExplorerFolder = cls is "CabinetWClass" or "ExploreWClass"
            || (proc == "explorer.exe" && (title.Contains("File Explorer", StringComparison.OrdinalIgnoreCase) || title.Contains("Dosya Gezgini", StringComparison.OrdinalIgnoreCase)));

        if (!isFileExplorerFolder && ProtectedPids.Contains(pid))
        {
            reason = $"agent host process or terminal ancestor (pid={pid})";
            return true;
        }

        if (!isFileExplorerFolder)
        {
            var settings = UserSettings.Load();
            if (settings.ProtectedProcesses != null)
            {
                foreach (var p in settings.ProtectedProcesses)
                {
                    var norm = p.Trim().ToLowerInvariant();
                    if (norm.EndsWith(".exe") ? proc == norm : proc == norm + ".exe" || proc.StartsWith(norm))
                    {
                        reason = $"protected by user policy ('{proc}')";
                        return true;
                    }
                }
            }

            if (proc is "windowsterminal.exe" or "conhost.exe" or "powershell.exe" or "pwsh.exe"
                or "cmd.exe" or "code.exe" or "antigravity.exe" or "cursor.exe" or "devenv.exe" or "explorer.exe" or "dwm.exe" or "inbrisk.exe" or "devin.exe")
            {
                reason = $"protected host terminal, IDE, or system shell ('{proc}')";
                return true;
            }
        }

        if (title.Contains("Google Colab", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Colab", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Inbrisk", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Claude", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Jupyter", StringComparison.OrdinalIgnoreCase) ||
            title.Equals("Program Manager", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"critical agent, notebook, or IDE session window ('{title}')";
            return true;
        }

        return false;
    }

    private static HashSet<int> GetSelfAndAncestorPids()
    {
        var pids = new HashSet<int> { SelfPid };
        try
        {
            var snap = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return pids;
            try
            {
                var entry = new NativeMethods.PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<NativeMethods.PROCESSENTRY32W>() };
                var parentMap = new Dictionary<int, int>();
                if (NativeMethods.Process32FirstW(snap, ref entry))
                {
                    do
                    {
                        parentMap[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                    } while (NativeMethods.Process32NextW(snap, ref entry));
                }

                var cur = SelfPid;
                while (parentMap.TryGetValue(cur, out var parent) && parent > 0 && !pids.Contains(parent))
                {
                    pids.Add(parent);
                    cur = parent;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(snap);
            }
        }
        catch { }
        return pids;
    }

    internal static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Visible window bounds (DWM extended frame bounds) — excludes the
    /// invisible resize/shadow margins GetWindowRect includes. Matches what WGC
    /// captures for a window item.</summary>
    public RectPx? VisibleBounds(long hwnd) => DesktopBridge.RunOnDefaultDesktop<RectPx?>(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return null;
        var hr = NativeMethods.DwmGetWindowAttributeRect(h,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>());
        if (hr != 0) NativeMethods.GetWindowRect(h, out r);
        return new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    });

    public WindowInfo? GetForegroundWindow() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var fg = NativeMethods.GetForegroundWindow();
        return fg == IntPtr.Zero ? null : GetWindow(fg.ToInt64());
    });

    public IReadOnlyList<MonitorInfo> GetMonitors() => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var list = new List<MonitorInfo>();
        NativeMethods.MonitorEnumProc proc = (IntPtr hMon, IntPtr hdcMon, ref RECT r, IntPtr dwData) =>
        {
            var info = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
            NativeMethods.GetMonitorInfoW(hMon, ref info);
            NativeMethods.GetDpiForMonitor(hMon, NativeMethods.MDT_EFFECTIVE_DPI, out var dx, out var dy);
            list.Add(new MonitorInfo(
                Index: list.Count,
                Bounds: new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                DpiX: (int)(dx == 0 ? 96 : dx),
                DpiY: (int)(dy == 0 ? 96 : dy),
                IsPrimary: (info.DwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                DeviceName: info.SzDevice));
            return true;
        };
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        return list;
    });

    public RectPx GetVirtualDesktopBounds() => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// Foreground-lock aware activation: AttachThreadInput lets us borrow the
    /// foreground thread's right to call SetForegroundWindow, supplemented with
    /// a VK_MENU pulse and SwitchToThisWindow fallback to break OS foreground lock.
    /// </summary>
    public bool FocusWindow(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return false;
        if (NativeMethods.IsIconic(h)) NativeMethods.ShowWindow(h, NativeMethods.SW_RESTORE);

        var fg = NativeMethods.GetForegroundWindow();
        if (fg == h) return true;

        var cur = NativeMethods.GetCurrentThreadId();
        var fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(h, out _);

        var attachedFg = fgThread != 0 && fgThread != cur &&
            NativeMethods.AttachThreadInput(cur, fgThread, true);
        var attachedTarget = targetThread != 0 && targetThread != cur && targetThread != fgThread &&
            NativeMethods.AttachThreadInput(cur, targetThread, true);

        // Break Windows foreground lock by pulsing Alt (VK_MENU)
        try
        {
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }

        NativeMethods.BringWindowToTop(h);
        var ok = NativeMethods.SetForegroundWindow(h);
        if (!ok || NativeMethods.GetForegroundWindow() != h)
        {
            NativeMethods.SwitchToThisWindow(h, true);
        }

        if (attachedTarget) NativeMethods.AttachThreadInput(cur, targetThread, false);
        if (attachedFg) NativeMethods.AttachThreadInput(cur, fgThread, false);

        for (var i = 0; i < 10; i++)
        {
            if (NativeMethods.GetForegroundWindow() == h) return true;
            Thread.Sleep(20);
        }

        return NativeMethods.GetForegroundWindow() == h;
    });

    /// <summary>
    /// WM_CLOSE is posted, not sent — the owning thread processes it, so
    /// unsaved-work prompts appear normally and hung apps don't hang us.
    /// Returns true once the window is actually gone or hidden (e.g. minimized to tray).
    /// </summary>
    public bool CloseWindow(long hwnd) => DesktopBridge.RunOnDefaultDesktop(() =>
    {
        var h = new IntPtr(hwnd);
        if (!NativeMethods.IsWindow(h)) return false;
        NativeMethods.PostMessageW(h, 0x0010 /*WM_CLOSE*/, IntPtr.Zero, IntPtr.Zero);
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h)) return true;
            Thread.Sleep(100);
        }
        return !NativeMethods.IsWindow(h) || !NativeMethods.IsWindowVisible(h);
    });

    internal static string GetTitle(IntPtr hwnd)
    {
        var len = NativeMethods.GetWindowTextLengthW(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        NativeMethods.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    internal static string? GetProcessName(int pid)
    {
        var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(512);
            var size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size)
                ? Path.GetFileName(sb.ToString()) : null;
        }
        finally { NativeMethods.CloseHandle(h); }
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        var hr = NativeMethods.DwmGetWindowAttribute(
            hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static RectPx RectOf(IntPtr monitor)
    {
        var info = new MONITORINFOEXW { CbSize = Marshal.SizeOf<MONITORINFOEXW>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info);
        return new RectPx(info.RcMonitor.Left, info.RcMonitor.Top,
            info.RcMonitor.Right - info.RcMonitor.Left,
            info.RcMonitor.Bottom - info.RcMonitor.Top);
    }

    private static IVirtualDesktopManager? TryCreateVdm()
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerCom(); }
        catch { return null; }
    }
}
