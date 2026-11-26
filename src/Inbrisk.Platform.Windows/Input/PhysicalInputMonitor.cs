using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Input;

/// <summary>
/// Low-level Win32 physical input monitor using WH_KEYBOARD_LL and WH_MOUSE_LL on a dedicated
/// message-loop thread.
///
/// Design invariants:
/// - Injected events (LLKHF_INJECTED, LLKHF_LOWER_IL_INJECTED, LLMHF_INJECTED, LLMHF_LOWER_IL_INJECTED)
///   are strictly ignored so AI-injected SendInput calls never trigger self-pause.
/// - Hook callbacks never block (&lt; 1ms): qualifying events are enqueued to a lock-free queue and
///   dispatched on a background thread pool worker; CallNextHookEx is always called immediately.
/// - Mouse movement applies a configurable deadzone (MouseMoveThresholdPx, default 12px) using an
///   anchored position so 1-2px sensor jitter is ignored while intentional movement accumulates.
///   Mouse clicks and wheel events have a zero threshold and always count.
/// </summary>
public sealed class PhysicalInputMonitor : IPhysicalInputMonitor
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;

    public const uint LLKHF_INJECTED = 0x10;
    public const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    public const uint LLMHF_INJECTED = 0x01;
    public const uint LLMHF_LOWER_IL_INJECTED = 0x02;

    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_SYSKEYDOWN = 0x0104;

    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_XBUTTONDOWN = 0x020B;
    public const uint WM_MOUSEHWHEEL = 0x020E;

    private const uint VK_PAUSE = 0x13;
    private const uint VK_CANCEL = 0x03;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr DwExtraInfo;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    private readonly object _lifecycleGate = new();
    private readonly ConcurrentQueue<PhysicalInputEvent> _dispatchQueue = new();
    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;

    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private uint _threadId;
    private IntPtr _kbHook;
    private IntPtr _mouseHook;
    private int _running;
    private int _disposed;
    private int _draining;
    private int _mouseMoveThresholdPx;

    // Mouse deadzone anchor state (accessed on hook thread / test evaluation)
    private bool _hasAnchor;
    private int _anchorX;
    private int _anchorY;

    public event Action<PhysicalInputEvent>? InputDetected;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public int MouseMoveThresholdPx
    {
        get => Volatile.Read(ref _mouseMoveThresholdPx);
        set => Volatile.Write(ref _mouseMoveThresholdPx, Math.Max(0, value));
    }

    public PhysicalInputMonitor(int mouseMoveThresholdPx = 12)
    {
        _mouseMoveThresholdPx = Math.Max(0, mouseMoveThresholdPx);
        // Root delegates for the lifetime of this monitor so GC cannot collect them while hooks are live.
        _kbProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
    }

    /// <summary>Returns true if the Win32 KBDLLHOOKSTRUCT flags mark an injected key event.</summary>
    public static bool IsKeyboardEventInjected(uint flags) =>
        (flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;

    /// <summary>Returns true if the Win32 MSLLHOOKSTRUCT flags mark an injected mouse event.</summary>
    public static bool IsMouseEventInjected(uint flags) =>
        (flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0;

    /// <summary>Resets the mouse deadzone anchor (useful on Start or in unit tests).</summary>
    public void ResetMouseAnchor(int x, int y)
    {
        _anchorX = x;
        _anchorY = y;
        _hasAnchor = true;
    }

    /// <summary>Clears the mouse deadzone anchor so the next WM_MOUSEMOVE seeds it.</summary>
    public void ClearMouseAnchor()
    {
        _hasAnchor = false;
        _anchorX = 0;
        _anchorY = 0;
    }

    /// <summary>
    /// Pure evaluation of a raw WH_KEYBOARD_LL event. Returns a <see cref="PhysicalInputEvent"/>
    /// if the event represents real physical keyboard input, or null if ignored.
    /// </summary>
    public PhysicalInputEvent? EvaluateKeyboardEvent(uint msg, uint vkCode, uint flags)
    {
        if (msg is not (WM_KEYDOWN or WM_SYSKEYDOWN))
            return null;
        if (IsKeyboardEventInjected(flags))
            return null;
        // Let GlobalHotkeyService handle Pause/Break panic/resume chords without racing intervention pause.
        if (vkCode is VK_PAUSE or VK_CANCEL)
            return null;
        return new PhysicalInputEvent(
            PhysicalInputKind.Keyboard,
            DateTimeOffset.UtcNow,
            VirtualKey: vkCode,
            Flags: flags);
    }

    /// <summary>
    /// Pure evaluation of a raw WH_MOUSE_LL event. Returns a <see cref="PhysicalInputEvent"/>
    /// if the event represents real physical mouse input exceeding the deadzone (or any click/wheel),
    /// or null if ignored.
    /// </summary>
    public PhysicalInputEvent? EvaluateMouseEvent(uint msg, int x, int y, uint flags)
    {
        if (IsMouseEventInjected(flags))
            return null;

        switch (msg)
        {
            case WM_LBUTTONDOWN:
            case WM_RBUTTONDOWN:
            case WM_MBUTTONDOWN:
            case WM_XBUTTONDOWN:
                _anchorX = x;
                _anchorY = y;
                _hasAnchor = true;
                return new PhysicalInputEvent(
                    PhysicalInputKind.MouseButton,
                    DateTimeOffset.UtcNow,
                    X: x,
                    Y: y,
                    DeltaPx: 0,
                    Flags: flags);

            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
                _anchorX = x;
                _anchorY = y;
                _hasAnchor = true;
                return new PhysicalInputEvent(
                    PhysicalInputKind.MouseWheel,
                    DateTimeOffset.UtcNow,
                    X: x,
                    Y: y,
                    DeltaPx: 0,
                    Flags: flags);

            case WM_MOUSEMOVE:
                if (!_hasAnchor)
                {
                    _anchorX = x;
                    _anchorY = y;
                    _hasAnchor = true;
                    return null;
                }
                var dx = x - _anchorX;
                var dy = y - _anchorY;
                if (dx == 0 && dy == 0)
                    return null;
                var threshold = MouseMoveThresholdPx;
                var distSq = (long)dx * dx + (long)dy * dy;
                var thresholdSq = (long)threshold * threshold;
                if (threshold > 0 && distSq < thresholdSq)
                    return null;
                var dist = (int)Math.Round(Math.Sqrt(distSq));
                _anchorX = x;
                _anchorY = y;
                return new PhysicalInputEvent(
                    PhysicalInputKind.MouseMove,
                    DateTimeOffset.UtcNow,
                    X: x,
                    Y: y,
                    DeltaPx: dist,
                    Flags: flags);

            default:
                return null;
        }
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _running) != 0)
                return;

            if (NativeMethods.GetCursorPos(out var pt))
                ResetMouseAnchor(pt.X, pt.Y);
            else
                ClearMouseAnchor();

            _ready = new ManualResetEventSlim(false);
            _thread = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "InbriskPhysicalInputMonitor"
            };
            _thread.Start();
            _ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint tid;
        ManualResetEventSlim? ready;
        lock (_lifecycleGate)
        {
            thread = _thread;
            tid = _threadId;
            ready = _ready;
            _thread = null;
            _threadId = 0;
            _ready = null;
            Volatile.Write(ref _running, 0);
        }

        if (thread != null && thread.IsAlive && tid != 0)
        {
            try { NativeMethods.PostThreadMessageW(tid, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { thread.Join(1000); } catch { }
        }

        try { ready?.Dispose(); } catch { }
        while (_dispatchQueue.TryDequeue(out _)) { }
    }

    private void PumpLoop()
    {
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            var hMod = NativeMethods.GetModuleHandleW(null);
            _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, hMod, 0);
            _mouseHook = SetWindowsHookExW(WH_MOUSE_LL, _mouseProc, hMod, 0);

            if (_kbHook != IntPtr.Zero || _mouseHook != IntPtr.Zero)
                Volatile.Write(ref _running, 1);

            _ready?.Set();

            while (NativeMethods.GetMessageW(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Low-level hooks are dispatched by GetMessageW on this thread.
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"inbrisk physical input monitor exited: {ex}");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            if (_kbHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_kbHook); } catch { }
                _kbHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_mouseHook); } catch { }
                _mouseHook = IntPtr.Zero;
            }
            try { _ready?.Set(); } catch { }
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero && Volatile.Read(ref _running) != 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var ev = EvaluateKeyboardEvent((uint)wParam.ToInt64(), info.VkCode, info.Flags);
                if (ev.HasValue)
                    EnqueueOffThread(ev.Value);
            }
            catch
            {
                // Never throw out of a Win32 hook callback.
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero && Volatile.Read(ref _running) != 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var ev = EvaluateMouseEvent((uint)wParam.ToInt64(), info.Pt.X, info.Pt.Y, info.Flags);
                if (ev.HasValue)
                    EnqueueOffThread(ev.Value);
            }
            catch
            {
                // Never throw out of a Win32 hook callback.
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void EnqueueOffThread(PhysicalInputEvent ev)
    {
        // Cap queue length to stay lock-free and bounded under bursty input.
        if (_dispatchQueue.Count >= 64)
            return;
        _dispatchQueue.Enqueue(ev);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static state => ((PhysicalInputMonitor)state!).DrainDispatchQueue(), this);
    }

    private void DrainDispatchQueue()
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && _dispatchQueue.TryDequeue(out var ev))
            {
                try { InputDetected?.Invoke(ev); }
                catch { /* subscribers must not kill the dispatcher */ }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _draining, 0);
            if (Volatile.Read(ref _disposed) == 0 && !_dispatchQueue.IsEmpty &&
                Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((PhysicalInputMonitor)state!).DrainDispatchQueue(), this);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Stop();
    }
}
