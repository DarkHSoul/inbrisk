using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

public sealed partial class GhostKeyboardForwarder : IDisposable
{


    private void ListenerPump()
    {
        try
        {
            _listenerThreadId = Win32.GetCurrentThreadId();
            EnsureListenerClassRegistered();

            _listenerHwnd = Win32.CreateWindowExW(
                0,
                ListenerWindowClass,
                "InbriskHotkeyListener",
                0,
                0, 0, 0, 0,
                HwndMessage,
                IntPtr.Zero,
                Win32.GetModuleHandleW(null),
                IntPtr.Zero);

            RegisterDefaultHotkeys();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostKeyboardForwarder] ListenerPump init fault: {ex.Message}");
        }
        finally
        {
            try { _listenerReady.Set(); } catch { }
        }

        while (Win32.GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            try
            {
                if (msg.message == WM_HOTKEY)
                {
                    HandleHotKey(msg.wParam.ToInt32());
                }

                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessageW(ref msg);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GhostKeyboardForwarder] Pump message loop exception: {ex.Message}");
            }
        }

        if (_listenerHwnd != IntPtr.Zero)
        {
            UnregisterAllHotkeys();
            Win32.DestroyWindow(_listenerHwnd);
            _listenerHwnd = IntPtr.Zero;
        }
    }

    private static void EnsureListenerClassRegistered()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;

            _staticWndProc = (hwnd, msg, wParam, lParam) =>
            {
                return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
            };

            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticWndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = Win32.GetModuleHandleW(null),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero,
                lpszMenuName = null,
                lpszClassName = ListenerWindowClass,
                hIconSm = IntPtr.Zero
            };

            Win32.RegisterClassExW(ref wc);
            _classRegistered = true;
        }
    }

    private void RegisterDefaultHotkeys()
    {
        if (_listenerHwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

        // 1. Ctrl + Alt + I -> Toggle Interactive mode
        bool okInteractive = Win32.RegisterHotKey(
            _listenerHwnd,
            HotKeyToggleInteractiveId,
            MOD_CONTROL | MOD_ALT | MOD_NOREPEAT,
            VK_I);

        if (!okInteractive)
        {
            Debug.WriteLine("[GhostKeyboardForwarder] Warning: Failed to register hotkey Ctrl+Alt+I.");
        }

        // 2. Ctrl + Shift + P -> Toggle PiP visibility
        bool okVisibility = Win32.RegisterHotKey(
            _listenerHwnd,
            HotKeyToggleVisibilityId,
            MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT,
            VK_P);

        if (!okVisibility)
        {
            Debug.WriteLine("[GhostKeyboardForwarder] Warning: Failed to register hotkey Ctrl+Shift+P.");
        }

        // 3. Esc -> Exit fullscreen (conditional on fullscreen or user setting)
        UpdateEscHotkeyRegistration();
    }

    private void UpdateEscHotkeyRegistration()
    {
        if (_listenerHwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

        bool shouldRegisterEsc = !_registerEscOnlyWhenFullscreen || _isFullscreen;
        if (shouldRegisterEsc)
        {
            Win32.RegisterHotKey(
                _listenerHwnd,
                HotKeyEscapeFullscreenId,
                MOD_NOREPEAT,
                VK_ESCAPE);
        }
        else
        {
            Win32.UnregisterHotKey(_listenerHwnd, HotKeyEscapeFullscreenId);
        }
    }

    private void UnregisterAllHotkeys()
    {
        if (_listenerHwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

        Win32.UnregisterHotKey(_listenerHwnd, HotKeyToggleInteractiveId);
        Win32.UnregisterHotKey(_listenerHwnd, HotKeyToggleVisibilityId);
        Win32.UnregisterHotKey(_listenerHwnd, HotKeyEscapeFullscreenId);
    }

    private void HandleHotKey(int id)
    {
        HotKeyPressed?.Invoke(id);

        switch (id)
        {
            case HotKeyToggleInteractiveId:
                ToggleInteractive();
                break;

            case HotKeyToggleVisibilityId:
                ToggleVisibility();
                break;

            case HotKeyEscapeFullscreenId:
                HandleEscape();
                break;
        }
    }



    /// <summary>
    /// Toggles interactive mode (WS_EX_TRANSPARENT click-through).
    /// </summary>
    public bool ToggleInteractive()
    {
        bool next = !IsInteractive;
        SetInteractive(next);
        return next;
    }

    /// <summary>
    /// Explicitly sets interactive mode.
    /// </summary>
    public void SetInteractive(bool interactive)
    {
        _isInteractive = interactive;

        if (_pipManager != null)
        {
            _pipManager.SetInteractive(interactive);
        }
        else
        {
            var hwnd = TargetHwnd;
            if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
            {
                SetClickThrough(hwnd, !interactive);
            }
        }

        InteractiveModeToggled?.Invoke(interactive);
    }

    /// <summary>
    /// Toggles PiP window visibility (hide / show).
    /// </summary>
    public bool ToggleVisibility()
    {
        var hwnd = TargetHwnd;
        bool newVisible;

        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            bool cur = Win32.IsWindowVisible(hwnd);
            newVisible = !cur;
            Win32.ShowWindow(hwnd, newVisible ? SW_SHOWNA : SW_HIDE);
        }
        else
        {
            newVisible = true;
        }

        VisibilityToggled?.Invoke(newVisible);
        return newVisible;
    }

    /// <summary>
    /// Expands the PiP window to fill the entire primary screen.
    /// </summary>
    public void EnterFullscreen()
    {
        if (_isFullscreen) return;

        var hwnd = TargetHwnd;
        if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            if (Win32.GetWindowRect(hwnd, out var rect))
            {
                _savedBounds = (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            }

            int cx = Win32.GetSystemMetrics(SM_CXSCREEN);
            int cy = Win32.GetSystemMetrics(SM_CYSCREEN);
            if (cx <= 0) cx = 1920;
            if (cy <= 0) cy = 1080;

            Win32.SetWindowPos(hwnd, HwndTopmost, 0, 0, cx, cy, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        _isFullscreen = true;
        UpdateEscHotkeyRegistration();
        FullscreenChanged?.Invoke(true);
    }

    /// <summary>
    /// Restores the PiP window from fullscreen back to its previous bounds or default mini box.
    /// </summary>
    public void ExitFullscreen()
    {
        if (!_isFullscreen) return;

        var hwnd = TargetHwnd;

        if (_pipManager != null)
        {
            _pipManager.SetScale(1.0f);
            _pipManager.SetPosition(PipPresetPosition.TopRight);
        }
        else if (hwnd != IntPtr.Zero && OperatingSystem.IsWindows())
        {
            int x = _savedBounds?.X ?? 100;
            int y = _savedBounds?.Y ?? 100;
            int w = _savedBounds?.Width ?? GhostPipManager.BaseWidth;
            int h = _savedBounds?.Height ?? GhostPipManager.BaseHeight;
            Win32.SetWindowPos(hwnd, HwndTopmost, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        _isFullscreen = false;
        UpdateEscHotkeyRegistration();
        FullscreenChanged?.Invoke(false);
        FullscreenExitRequested?.Invoke();
    }

    /// <summary>
    /// Notifies the forwarder that fullscreen state changed externally
    /// (tray menu, IPC command, or expander) so the Escape hotkey stays registered correctly.
    /// </summary>
    public void NotifyFullscreen(bool isFullscreen)
    {
        if (_isFullscreen == isFullscreen)
        {
            return;
        }
        _isFullscreen = isFullscreen;
        UpdateEscHotkeyRegistration();
    }

    /// <summary>
    /// Handles the Escape key action.
    /// </summary>
    private void HandleEscape()
    {
        if (_isFullscreen)
        {
            ExitFullscreen();
        }
        else
        {
            EscapePressed?.Invoke();
        }
    }

    private static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        try
        {
            long cur = Win32.GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
            long updated = clickThrough ? (cur | WS_EX_TRANSPARENT) : (cur & ~WS_EX_TRANSPARENT);
            if (cur != updated)
            {
                Win32.SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(updated));
                Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }
        catch { }
    }
}
