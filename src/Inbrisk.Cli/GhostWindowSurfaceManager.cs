using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Runtime;

namespace Inbrisk.Cli;

/// <summary>
/// Manages real top-level application windows as Ghost surfaces.
///
/// This backend intentionally does NOT synthesize or paint a replacement UI.
/// The real window remains composited by DWM on the caller's interactive
/// desktop, is made click-through and fully transparent, and its live surface
/// is projected into the PiP host with DwmRegisterThumbnail. Input is posted
/// back to the real HWND/child HWND, preserving native chrome, WinUI and
/// DirectComposition content that PrintWindow cannot capture.
/// </summary>
internal sealed class GhostWindowSurfaceManager : IDisposable
{
    private const int SurfaceLogicalWidth = 1920;
    private const int SurfaceLogicalHeight = 1080;
    private const int PipHeaderHeight = 26;

    private readonly object _gate = new();
    private readonly GhostPipManager _pipManager;
    private readonly Dictionary<IntPtr, GhostWindowSurface> _surfaces = new();
    private readonly Timer _thumbnailTimer;
    private GhostWindowSurface? _active;
    private DwmThumbnailPresenter? _presenter;
    private bool _disposed;

    public GhostWindowSurfaceManager(GhostPipManager pipManager)
    {
        _pipManager = pipManager ?? throw new ArgumentNullException(nameof(pipManager));
        _thumbnailTimer = new Timer(_ => RefreshThumbnail(), null, Timeout.Infinite, Timeout.Infinite);
        AttachPresenter();
    }

    /// <summary>Gets the active managed window handle, if any.</summary>
    public IntPtr ActiveHwnd
    {
        get { lock (_gate) return _active?.Hwnd ?? IntPtr.Zero; }
    }

    /// <summary>Returns managed surfaces for status/window listing.</summary>
    public IReadOnlyList<object> GetSurfaceStatus()
    {
        lock (_gate)
        {
            return _surfaces.Values
                .Where(s => s.IsAlive)
                .Select(s => s.GetStatus(s == _active))
                .ToList();
        }
    }

    public async Task<GhostWindowSurface> LaunchAsync(
        string program,
        string? arguments,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(program))
            throw new ArgumentException("program is required", nameof(program));

        var startInfo = new ProcessStartInfo
        {
            FileName = program,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };

        var beforeWindows = SnapshotTopLevelWindows();
        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
        }
        catch
        {
            // Some shell-only entries still need UseShellExecute. Keep PID tracking
            // optional in that path and discover the new window by HWND delta below.
            startInfo.UseShellExecute = true;
            process = Process.Start(startInfo);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        IntPtr hwnd = IntPtr.Zero;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            hwnd = FindBestWindowForProcess(process);
            if (hwnd == IntPtr.Zero)
            {
                hwnd = FindNewTopLevelWindow(beforeWindows);
            }
            if (hwnd != IntPtr.Zero) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (hwnd == IntPtr.Zero)
        {
            try { process?.Refresh(); } catch { }
            throw new TimeoutException($"launched '{program}' (PID {SafePid(process)}) but no top-level window appeared");
        }

        return Attach(hwnd, process, activate: true);
    }

    public GhostWindowSurface Attach(IntPtr hwnd, Process? process = null, bool activate = true)
    {
        ThrowIfDisposed();
        var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
        if (root == IntPtr.Zero || !Native.IsWindow(root))
            throw new InvalidOperationException($"invalid window handle 0x{hwnd.ToInt64():X}");

        GhostWindowSurface surface;
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(root, out surface!))
            {
                surface = new GhostWindowSurface(root, process, SurfaceLogicalWidth, SurfaceLogicalHeight);
                _surfaces[root] = surface;
            }

            surface.HideForGhostComposition();
            if (activate)
            {
                SetActiveLocked(surface);
            }
        }

        return surface;
    }

    public bool Select(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(hwnd, out var surface) || !surface.IsAlive)
                return false;
            SetActiveLocked(surface);
            return true;
        }
    }

    public bool Close(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(hwnd, out var surface))
                return false;

            if (_active == surface)
            {
                _active = null;
                _pipManager.SetDirectInputTarget(null);
                _presenter?.Clear();
            }

            _surfaces.Remove(hwnd);
            surface.Close();
            surface.Dispose();
            return true;
        }
    }

    public bool ForwardMouse(PipInputEventArgs args, int x, int y)
    {
        GhostWindowSurface? surface;
        lock (_gate) surface = _active;
        return surface != null && surface.ForwardMouse(args, x, y);
    }

    public bool ForwardKey(PipInputEventArgs args)
    {
        GhostWindowSurface? surface;
        lock (_gate) surface = _active;
        return surface != null && surface.ForwardKey(args);
    }

    private void SetActiveLocked(GhostWindowSurface surface)
    {
        _active = surface;
        _pipManager.SetDirectInputTarget(surface);
        _presenter?.Attach(surface.Hwnd);
        _thumbnailTimer.Change(100, 100);
    }

    private void AttachPresenter()
    {
        var host = _pipManager.WindowHost;
        if (host == null || host.Hwnd == IntPtr.Zero)
            return;

        _presenter = new DwmThumbnailPresenter(host.Hwnd, PipHeaderHeight);
        host.BoundsChanged += (_, _, _, _) => RefreshThumbnail();
    }

    private void RefreshThumbnail()
    {
        lock (_gate)
        {
            if (_active == null || !_active.IsAlive)
            {
                if (_active != null)
                {
                    _active = null;
                    _pipManager.SetDirectInputTarget(null);
                    _presenter?.Clear();
                }
                return;
            }

            _presenter?.Update(_active.Hwnd);
        }
    }

    private static int SafePid(Process? process)
    {
        try { return process?.Id ?? 0; } catch { return 0; }
    }

    private IntPtr FindBestWindowForProcess(Process? process)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = -1;
        int pid = SafePid(process);
        if (pid <= 0) return IntPtr.Zero;

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return true;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(hwnd, out var windowPid);
            if (windowPid != pid) return true;

            var title = Native.GetWindowText(hwnd);
            var className = Native.GetClassName(hwnd);
            if (string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(className, "InbriskGhostPipWindowClass", StringComparison.OrdinalIgnoreCase))
                return true;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(title)) score += 10;
            if (string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (Native.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static HashSet<IntPtr> SnapshotTopLevelWindows()
    {
        var windows = new HashSet<IntPtr>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (IsCandidateTopLevelWindow(hwnd)) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindNewTopLevelWindow(HashSet<IntPtr> before)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = -1;
        Native.EnumWindows((hwnd, _) =>
        {
            if (before.Contains(hwnd) || !IsCandidateTopLevelWindow(hwnd)) return true;

            var title = Native.GetWindowText(hwnd);
            var className = Native.GetClassName(hwnd);
            var score = 0;
            if (!string.IsNullOrWhiteSpace(title)) score += 10;
            if (string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase)) score += 5;
            if (Native.GetWindowRect(hwnd, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top) score += 1;
            if (score > bestScore)
            {
                bestScore = score;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool IsCandidateTopLevelWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd)) return false;
        if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
        var className = Native.GetClassName(hwnd);
        return !string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(className, "InbriskGhostPipWindowClass", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GhostWindowSurfaceManager));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _thumbnailTimer.Dispose(); } catch { }

        lock (_gate)
        {
            _pipManager.SetDirectInputTarget(null);
            try { _presenter?.Dispose(); } catch { }
            _presenter = null;

            foreach (var surface in _surfaces.Values)
            {
                try { surface.Close(); } catch { }
                try { surface.Dispose(); } catch { }
            }
            _surfaces.Clear();
            _active = null;
        }
    }

    private sealed class DwmThumbnailPresenter : IDisposable
    {
        private readonly IntPtr _destination;
        private readonly int _topInset;
        private IntPtr _thumbnail;
        private IntPtr _source;

        public DwmThumbnailPresenter(IntPtr destination, int topInset)
        {
            _destination = destination;
            _topInset = topInset;
        }

        public void Attach(IntPtr source)
        {
            if (_source == source && _thumbnail != IntPtr.Zero)
            {
                Update(source);
                return;
            }

            Clear();
            int hr = Native.DwmRegisterThumbnail(_destination, source, out _thumbnail);
            if (hr != 0)
            {
                throw new InvalidOperationException($"DwmRegisterThumbnail failed: 0x{hr:X8}");
            }
            _source = source;
            Update(source);
        }

        public void Update(IntPtr source)
        {
            if (_thumbnail == IntPtr.Zero || source == IntPtr.Zero || !Native.IsWindow(source))
                return;

            if (!Native.GetClientRect(_destination, out var destination) ||
                !Native.GetWindowRect(source, out var sourceRect))
                return;

            destination.Top = Math.Min(destination.Bottom, _topInset);
            var normalizedSource = new Native.RECT
            {
                Left = 0,
                Top = 0,
                Right = Math.Max(1, sourceRect.Right - sourceRect.Left),
                Bottom = Math.Max(1, sourceRect.Bottom - sourceRect.Top)
            };

            var props = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION |
                          Native.DWM_TNP_RECTSOURCE |
                          Native.DWM_TNP_OPACITY |
                          Native.DWM_TNP_VISIBLE,
                rcDestination = destination,
                rcSource = normalizedSource,
                opacity = 255,
                fVisible = 1,
                fSourceClientAreaOnly = 0
            };
            _ = Native.DwmUpdateThumbnailProperties(_thumbnail, ref props);
        }

        public void Clear()
        {
            if (_thumbnail != IntPtr.Zero)
            {
                try { Native.DwmUnregisterThumbnail(_thumbnail); } catch { }
                _thumbnail = IntPtr.Zero;
            }
            _source = IntPtr.Zero;
        }

        public void Dispose() => Clear();
    }

    internal sealed class GhostWindowSurface : IGhostWindowSurfaceInputTarget, IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Process? _process;
        private readonly long _originalExStyle;
        private readonly long _originalStyle;
        private readonly Native.RECT _originalRect;
        private IntPtr _lastInputHwnd;
        private int _heldMouseKeys;
        private bool _hidden;
        private bool _closed;

        public GhostWindowSurface(IntPtr hwnd, Process? process, int surfaceWidth, int surfaceHeight)
        {
            _hwnd = hwnd;
            _process = process;
            _originalExStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            _originalStyle = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            Native.GetWindowRect(hwnd, out _originalRect);
            SurfaceWidth = surfaceWidth;
            SurfaceHeight = surfaceHeight;
        }

        public IntPtr Hwnd => _hwnd;
        public int SurfaceWidth { get; }
        public int SurfaceHeight { get; }
        public bool IsAlive => !_closed && Native.IsWindow(_hwnd) && ProcessAlive;

        private bool ProcessAlive
        {
            get
            {
                if (_process == null) return true;
                try { return !_process.HasExited; } catch { return true; }
            }
        }

        public object GetStatus(bool active)
        {
            Native.GetWindowThreadProcessId(_hwnd, out var pid);
            Native.GetWindowRect(_hwnd, out var rect);
            return new
            {
                hwnd = $"0x{_hwnd.ToInt64():X}",
                pid,
                title = Native.GetWindowText(_hwnd),
                className = Native.GetClassName(_hwnd),
                active,
                hidden = _hidden,
                rect = new { left = rect.Left, top = rect.Top, width = rect.Right - rect.Left, height = rect.Bottom - rect.Top }
            };
        }

        public void HideForGhostComposition()
        {
            if (!Native.IsWindow(_hwnd)) return;

            // Keep the window on-screen and visible to DWM, but make it fully
            // transparent and click-through. DwmRegisterThumbnail still renders
            // alpha=0 layered windows, while the user cannot see or hit-test it.
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, SurfaceWidth, SurfaceHeight,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);

            long exStyle = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            long newStyle = exStyle |
                            Native.WS_EX_LAYERED |
                            Native.WS_EX_TRANSPARENT |
                            Native.WS_EX_NOACTIVATE |
                            Native.WS_EX_TOOLWINDOW;
            newStyle &= ~Native.WS_EX_APPWINDOW;
            if (newStyle != exStyle)
            {
                Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(newStyle));
            }

            Native.SetLayeredWindowAttributes(_hwnd, 0, 0, Native.LWA_ALPHA);
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            _hidden = true;
        }

        public bool ForwardMouse(PipInputEventArgs args, int targetX, int targetY)
        {
            if (!IsAlive) return false;

            if (!Native.GetWindowRect(_hwnd, out var windowRect) ||
                windowRect.Right <= windowRect.Left || windowRect.Bottom <= windowRect.Top)
                return false;

            int x = (int)Math.Clamp((long)targetX * (windowRect.Right - windowRect.Left) / Math.Max(1, SurfaceWidth),
                0, windowRect.Right - windowRect.Left - 1);
            int y = (int)Math.Clamp((long)targetY * (windowRect.Bottom - windowRect.Top) / Math.Max(1, SurfaceHeight),
                0, windowRect.Bottom - windowRect.Top - 1);
            int screenX = windowRect.Left + x;
            int screenY = windowRect.Top + y;

            var clientOrigin = new Native.POINT { X = 0, Y = 0 };
            Native.ClientToScreen(_hwnd, ref clientOrigin);
            int clientX = screenX - clientOrigin.X;
            int clientY = screenY - clientOrigin.Y;
            Native.GetClientRect(_hwnd, out var clientRect);

            bool clientArea = clientX >= clientRect.Left && clientX < clientRect.Right &&
                              clientY >= clientRect.Top && clientY < clientRect.Bottom;
            if (!clientArea)
            {
                return ForwardNonClient(args, screenX, screenY);
            }

            var target = FindDeepestInputWindow(screenX, screenY);
            if (target == IntPtr.Zero) target = _hwnd;
            _lastInputHwnd = target;

            var pt = new Native.POINT { X = screenX, Y = screenY };
            Native.ScreenToClient(target, ref pt);
            IntPtr lParam = Native.MakeLParam(pt.X, pt.Y);

            return args.EventType switch
            {
                PipInputEventType.MouseMove => Native.PostMessageW(target, Native.WM_MOUSEMOVE, new IntPtr(_heldMouseKeys), lParam),
                PipInputEventType.MouseDown => PostButton(target, args.Button, down: true, lParam),
                PipInputEventType.MouseUp => PostButton(target, args.Button, down: false, lParam),
                PipInputEventType.DoubleClick => PostDoubleClick(target, args.Button, lParam),
                PipInputEventType.MouseWheel => PostWheel(target, screenX, screenY, args.Delta),
                _ => false
            };
        }

        public bool ForwardKey(PipInputEventArgs args)
        {
            if (!IsAlive) return false;
            var target = _lastInputHwnd != IntPtr.Zero && Native.IsWindow(_lastInputHwnd)
                ? _lastInputHwnd
                : _hwnd;

            return args.EventType switch
            {
                PipInputEventType.KeyDown => Native.PostMessageW(target, Native.WM_KEYDOWN, new IntPtr(args.KeyCode), IntPtr.Zero),
                PipInputEventType.KeyUp => Native.PostMessageW(target, Native.WM_KEYUP, new IntPtr(args.KeyCode), IntPtr.Zero),
                PipInputEventType.Char when args.Character == '\n' =>
                    Native.PostMessageW(target, Native.WM_KEYDOWN, new IntPtr(0x0D), IntPtr.Zero) &&
                    Native.PostMessageW(target, Native.WM_KEYUP, new IntPtr(0x0D), IntPtr.Zero),
                PipInputEventType.Char when args.Character != '\0' =>
                    Native.PostMessageW(target, Native.WM_CHAR, new IntPtr(args.Character), IntPtr.Zero),
                _ => false
            };
        }

        private bool ForwardNonClient(PipInputEventArgs args, int screenX, int screenY)
        {
            IntPtr lParam = Native.MakeLParam(screenX, screenY);
            IntPtr hit = Native.SendMessageTimeoutW(_hwnd, Native.WM_NCHITTEST, IntPtr.Zero, lParam,
                Native.SMTO_ABORTIFHUNG, 50, out _);
            int hitCode = hit.ToInt32();

            if (args.EventType == PipInputEventType.MouseMove)
            {
                return Native.PostMessageW(_hwnd, Native.WM_NCMOUSEMOVE, new IntPtr(hitCode), lParam);
            }

            if (args.EventType == PipInputEventType.DoubleClick && args.Button == MouseButton.Left)
            {
                return Native.PostMessageW(_hwnd, Native.WM_NCLBUTTONDBLCLK, new IntPtr(hitCode), lParam);
            }

            bool down = args.EventType == PipInputEventType.MouseDown;
            bool up = args.EventType == PipInputEventType.MouseUp;
            if (!down && !up) return false;

            if (down && hitCode == Native.HTCAPTION && args.Button == MouseButton.Left)
            {
                Native.ReleaseCapture();
                return Native.PostMessageW(_hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MOVE | 0x0002), lParam);
            }

            if (down && hitCode == Native.HTCLOSE)
            {
                return Native.PostMessageW(_hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_CLOSE), lParam);
            }

            if (down && hitCode == Native.HTMINBUTTON)
            {
                return Native.PostMessageW(_hwnd, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MINIMIZE), lParam);
            }

            if (down && hitCode == Native.HTMAXBUTTON)
            {
                return Native.PostMessageW(_hwnd, Native.WM_SYSCOMMAND,
                    new IntPtr(Native.IsZoomed(_hwnd) ? Native.SC_RESTORE : Native.SC_MAXIMIZE), lParam);
            }

            uint msg = args.Button switch
            {
                MouseButton.Right => down ? Native.WM_NCRBUTTONDOWN : Native.WM_NCRBUTTONUP,
                MouseButton.Middle => down ? Native.WM_NCMBUTTONDOWN : Native.WM_NCMBUTTONUP,
                _ => down ? Native.WM_NCLBUTTONDOWN : Native.WM_NCLBUTTONUP
            };
            return Native.PostMessageW(_hwnd, msg, new IntPtr(hitCode), lParam);
        }

        private bool PostButton(IntPtr target, MouseButton button, bool down, IntPtr lParam)
        {
            int keyBit;
            uint msg;
            switch (button)
            {
                case MouseButton.Right:
                    keyBit = Native.MK_RBUTTON;
                    msg = down ? Native.WM_RBUTTONDOWN : Native.WM_RBUTTONUP;
                    break;
                case MouseButton.Middle:
                    keyBit = Native.MK_MBUTTON;
                    msg = down ? Native.WM_MBUTTONDOWN : Native.WM_MBUTTONUP;
                    break;
                default:
                    keyBit = Native.MK_LBUTTON;
                    msg = down ? Native.WM_LBUTTONDOWN : Native.WM_LBUTTONUP;
                    break;
            }

            if (down) _heldMouseKeys |= keyBit;
            else _heldMouseKeys &= ~keyBit;

            bool ok = Native.PostMessageW(target, Native.WM_MOUSEMOVE, new IntPtr(_heldMouseKeys), lParam);
            ok |= Native.PostMessageW(target, msg, new IntPtr(_heldMouseKeys), lParam);
            return ok;
        }

        private bool PostDoubleClick(IntPtr target, MouseButton button, IntPtr lParam)
        {
            uint msg = button == MouseButton.Right ? Native.WM_RBUTTONDBLCLK : Native.WM_LBUTTONDBLCLK;
            return Native.PostMessageW(target, msg, new IntPtr(_heldMouseKeys), lParam);
        }

        private bool PostWheel(IntPtr target, int screenX, int screenY, int delta)
        {
            IntPtr wParam = new((delta & 0xFFFF) << 16);
            return Native.PostMessageW(target, Native.WM_MOUSEWHEEL, wParam, Native.MakeLParam(screenX, screenY));
        }

        private IntPtr FindDeepestInputWindow(int screenX, int screenY)
        {
            IntPtr best = _hwnd;
            long bestArea = long.MaxValue;
            Native.EnumChildWindows(_hwnd, (child, _) =>
            {
                if (!Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child)) return true;
                if (!Native.GetWindowRect(child, out var rect)) return true;
                if (screenX < rect.Left || screenX >= rect.Right || screenY < rect.Top || screenY >= rect.Bottom)
                    return true;

                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = child;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            try { Native.PostMessageW(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); } catch { }
        }

        public void Restore()
        {
            if (!Native.IsWindow(_hwnd)) return;
            try
            {
                Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(_originalStyle));
                Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(_originalExStyle));
                Native.SetLayeredWindowAttributes(_hwnd, 0, 255, Native.LWA_ALPHA);
                Native.SetWindowPos(_hwnd, IntPtr.Zero,
                    _originalRect.Left, _originalRect.Top,
                    Math.Max(1, _originalRect.Right - _originalRect.Left),
                    Math.Max(1, _originalRect.Bottom - _originalRect.Top),
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
            }
            catch { }
        }

        public void Dispose()
        {
            try { _process?.Dispose(); } catch { }
        }
    }

    private static class Native
    {
        public const int GW_OWNER = 4;
        public const int GA_ROOT = 2;
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint LWA_ALPHA = 0x00000002;
        public const int SW_SHOWNOACTIVATE = 4;

        public const uint WM_CLOSE = 0x0010;
        public const uint WM_ACTIVATE = 0x0006;
        public const uint WM_SETFOCUS = 0x0007;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;
        public const uint WM_SYSCOMMAND = 0x0112;
        public const uint WM_NCHITTEST = 0x0084;
        public const uint WM_NCMOUSEMOVE = 0x00A0;
        public const uint WM_NCLBUTTONDOWN = 0x00A1;
        public const uint WM_NCLBUTTONUP = 0x00A2;
        public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
        public const uint WM_NCRBUTTONDOWN = 0x00A4;
        public const uint WM_NCRBUTTONUP = 0x00A5;
        public const uint WM_NCMBUTTONDOWN = 0x00A7;
        public const uint WM_NCMBUTTONUP = 0x00A8;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_LBUTTONDBLCLK = 0x0203;
        public const uint WM_RBUTTONDOWN = 0x0204;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_RBUTTONDBLCLK = 0x0206;
        public const uint WM_MBUTTONDOWN = 0x0207;
        public const uint WM_MBUTTONUP = 0x0208;
        public const uint WM_MOUSEWHEEL = 0x020A;

        public const int HTCLIENT = 1;
        public const int HTCAPTION = 2;
        public const int HTMINBUTTON = 8;
        public const int HTMAXBUTTON = 9;
        public const int HTCLOSE = 20;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_RESTORE = 0xF120;
        public const int SC_CLOSE = 0xF060;
        public const int SC_MOVE = 0xF010;
        public const int MK_LBUTTON = 0x0001;
        public const int MK_RBUTTON = 0x0002;
        public const int MK_MBUTTON = 0x0010;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        public const int DWM_TNP_RECTDESTINATION = 0x00000001;
        public const int DWM_TNP_RECTSOURCE = 0x00000002;
        public const int DWM_TNP_OPACITY = 0x00000004;
        public const int DWM_TNP_VISIBLE = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            public int fVisible;
            public int fSourceClientAreaOnly;
        }

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);

        public static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

        public static string GetWindowText(IntPtr hwnd)
        {
            int len = GetWindowTextLengthW(hwnd);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            _ = GetWindowTextW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            _ = GetClassNameW(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
