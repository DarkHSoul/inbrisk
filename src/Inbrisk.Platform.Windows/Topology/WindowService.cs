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

    private readonly HwndMetadataCache? _metadataCache;
    private readonly MonitorTopologyCache? _monitorCache;
    private readonly VirtualDesktopManagerHolder? _vdmHolder;

    public HwndMetadataCache? MetadataCache => _metadataCache;
    public MonitorTopologyCache? MonitorCache => _monitorCache;
    public VirtualDesktopManagerHolder? VdmHolder => _vdmHolder;

    public WindowService(
        HwndMetadataCache? metadataCache = null,
        MonitorTopologyCache? monitorCache = null,
        VirtualDesktopManagerHolder? vdmHolder = null)
    {
        _metadataCache = metadataCache;
        _monitorCache = monitorCache;
        _vdmHolder = vdmHolder;
        Dpi.EnsurePerMonitorV2();
    }

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

            string? procName;
            bool isElevated;
            if (_metadataCache != null)
            {
                var meta = _metadataCache.GetOrAdd(hwnd.ToInt64(), h => new HwndMetadata(
                    h,
                    pid,
                    GetProcessName(pid),
                    GetProcessStartTime(pid),
                    IntegrityService.IsProcessElevated(pid)));
                procName = meta?.ProcessName;
                isElevated = meta?.IsElevated ?? false;
            }
            else
            {
                procName = GetProcessName(pid);
                isElevated = IntegrityService.IsProcessElevated(pid);
            }

            list.Add(new WindowInfo(
                Hwnd: hwnd.ToInt64(),
                Pid: pid,
                Title: title,
                ProcessName: procName,
                Bounds: bounds,
                State: NativeMethods.IsIconic(hwnd) ? WindowState.Minimized
                     : NativeMethods.IsZoomed(hwnd) ? WindowState.Maximized
                     : WindowState.Normal,
                IsForeground: hwnd == fg,
                IsElevated: isElevated,
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

        string? procName;
        bool isElevated;
        if (_metadataCache != null)
        {
            var meta = _metadataCache.GetOrAdd(hwnd, hVal => new HwndMetadata(
                hVal,
                pid,
                GetProcessName(pid),
                GetProcessStartTime(pid),
                IntegrityService.IsProcessElevated(pid)));
            procName = meta?.ProcessName;
            isElevated = meta?.IsElevated ?? false;
        }
        else
        {
            procName = GetProcessName(pid);
            isElevated = IntegrityService.IsProcessElevated(pid);
        }

        return new WindowInfo(
            hwnd, pid, GetTitle(h), procName,
            new RectPx(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            NativeMethods.IsIconic(h) ? WindowState.Minimized
                : NativeMethods.IsZoomed(h) ? WindowState.Maximized
                : WindowState.Normal,
            h == fg, isElevated, onVd,
            monitors.FindIndex(m => m.Bounds == RectOf(mon)),
            IsModalPopup: isModal,
            OwnerHwnd: ownerHwnd,
            ModalPopupHwnd: popupHwnd,
            IsEnabled: isEnabled);
    });

    public Dictionary<long, WindowInfo> SyntheticModalPopups { get; } = new();

    public WindowInfo? GetModalPopup(long hwnd)
    {
        if (SyntheticModalPopups.TryGetValue(hwnd, out var synModal))
            return synModal;

        return DesktopBridge.RunOnDefaultDesktop(() =>
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
    }

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

        // Top-level unowned windows: NEVER treat browsers, IDEs, or general document windows as system dialogs
        if (cls is "Chrome_WidgetWin_1" or "MozillaWindowClass" or "CabinetWClass" or "ApplicationFrameWindow")
            return false;

        var t = title.Trim();

        // Standard Win32 dialog class (#32770) or TaskDialog:
        if (cls is "#32770" or "TaskDialog" or "OperationStatusWindow")
        {
            if (t.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Hata", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Uyarı", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Alert", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Dikkat", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Yanıt Vermiyor", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Not Responding", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Crash", StringComparison.OrdinalIgnoreCase) ||
                t.Contains("Çökme", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Exact match or strict suffix match for standalone unowned crash/error prompts
        if (t.Equals("Error", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Hata", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Beklenmeyen Hata", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("Crash", StringComparison.OrdinalIgnoreCase) ||
            t.EndsWith(" - Error", StringComparison.OrdinalIgnoreCase) ||
            t.EndsWith(" - Hata", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("Crash: ", StringComparison.OrdinalIgnoreCase))
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

    public static bool IsProcessProtected(string? processName, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var proc = processName.Trim().ToLowerInvariant();
        if (!proc.EndsWith(".exe")) proc += ".exe";

        var settings = UserSettings.LoadCached();
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
            or "cmd.exe" or "code.exe" or "antigravity.exe" or "cursor.exe" or "devenv.exe" or "explorer.exe" or "dwm.exe" or "inbrisk.exe" or "devin.exe" or "claude.exe")
        {
            reason = $"protected host terminal, IDE, or system shell ('{proc}')";
            return true;
        }

        return false;
    }

    /// <summary>PIDs that must never be terminated by the agent: this MCP
    /// server process plus every ancestor up the parent chain to the session
    /// root (terminal, IDE, shell). Killing any of them ends the AI host
    /// session. Snapshot taken once at startup — a process's own ancestry is
    /// fixed for its lifetime.</summary>
    public static IReadOnlyCollection<int> AgentHostAndAncestorPids => ProtectedPids;

    /// <summary>Is <paramref name="pid"/> the MCP server process itself or one
    /// of its ancestors? This is a raw pid check — it applies to processes that
    /// own no windows (console hosts, launcher shells) and is NOT weakened by
    /// the File-Explorer-folder carve-out in <see cref="IsWindowProtected"/>:
    /// closing one Explorer folder window is safe, killing explorer.exe is not.</summary>
    public static bool IsAgentHostOrAncestorPid(int pid, out string? reason)
    {
        if (pid > 0 && ProtectedPids.Contains(pid))
        {
            reason = $"agent host process or terminal/IDE ancestor (pid={pid})";
            return true;
        }
        reason = null;
        return false;
    }

    /// <summary>Known shared process hosts that must NEVER be Process.Kill'd —
    /// terminating them tears down every window/app they host (UWP frames,
    /// shell experiences). Unlike <see cref="IsProcessProtected"/> this does
    /// NOT block a graceful WM_CLOSE of an individual hosted window; it only
    /// vetoes hard termination.</summary>
    public static bool IsSharedMultiWindowProcess(string? processName, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var proc = processName.Trim().ToLowerInvariant();
        if (!proc.EndsWith(".exe")) proc += ".exe";

        if (proc is "applicationframehost.exe" or "shellexperiencehost.exe"
            or "startmenuexperiencehost.exe" or "searchhost.exe" or "searchapp.exe"
            or "sihost.exe" or "textinputhost.exe")
        {
            reason = $"shared shell/UWP host process ('{proc}') — killing it would close every window it hosts";
            return true;
        }

        return false;
    }

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
            if (IsProcessProtected(proc, out reason))
                return true;
        }

        bool isBrowserOrIde = proc is "chrome.exe" or "msedge.exe" or "firefox.exe" or "brave.exe" or "code.exe" or "cursor.exe" or "devin.exe" or "antigravity.exe";
        if (isBrowserOrIde && (
            title.Contains("Google Colab", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Colab", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Qwen", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Antigravity", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Jupyter", StringComparison.OrdinalIgnoreCase) ||
            title.EndsWith(" - Claude", StringComparison.OrdinalIgnoreCase) ||
            title.EndsWith(" | Claude", StringComparison.OrdinalIgnoreCase)))
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

    public long? SyntheticForegroundHwnd { get; set; }
    public Func<long, bool>? OnCloseWindow { get; set; }

    public WindowInfo? GetForegroundWindow()
    {
        if (SyntheticForegroundHwnd.HasValue)
        {
            return GetWindow(SyntheticForegroundHwnd.Value) ?? new WindowInfo(
                SyntheticForegroundHwnd.Value, 1000, "Synthetic Foreground", "app.exe",
                new RectPx(0, 0, 800, 600), WindowState.Normal, true, false, true, 0);
        }

        return DesktopBridge.RunOnDefaultDesktop(() =>
        {
            var fg = NativeMethods.GetForegroundWindow();
            return fg == IntPtr.Zero ? null : GetWindow(fg.ToInt64());
        });
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        if (_monitorCache != null)
            return _monitorCache.GetMonitors(NativeGetMonitors);
        return NativeGetMonitors();
    }

    private static IReadOnlyList<MonitorInfo> NativeGetMonitors() => DesktopBridge.RunOnDefaultDesktop(() =>
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

    public RectPx GetVirtualDesktopBounds()
    {
        if (_monitorCache != null)
            return _monitorCache.GetVirtualDesktopBounds(NativeGetVirtualDesktopBounds);
        return NativeGetVirtualDesktopBounds();
    }

    private static RectPx NativeGetVirtualDesktopBounds() => new(
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
    public bool CloseWindow(long hwnd)
    {
        if (OnCloseWindow != null) return OnCloseWindow(hwnd);
        return DesktopBridge.RunOnDefaultDesktop(() =>
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
    }

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

    private IVirtualDesktopManager? TryCreateVdm()
    {
        if (_vdmHolder != null)
            return _vdmHolder.GetOrCreate(NativeTryCreateVdm);
        return NativeTryCreateVdm();
    }

    private static IVirtualDesktopManager? NativeTryCreateVdm()
    {
        try { return (IVirtualDesktopManager)new VirtualDesktopManagerCom(); }
        catch { return null; }
    }

    internal static DateTimeOffset? GetProcessStartTime(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch
        {
            return null;
        }
    }

    public static bool TryActivateWindowByExactTitle(string title)
    {
        return DesktopBridge.RunOnDefaultDesktop(() =>
        {
            var hwnd = NativeMethods.FindWindowW(null, title);
            if (hwnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                NativeMethods.SetForegroundWindow(hwnd);
                return true;
            }
            return false;
        });
    }
}
