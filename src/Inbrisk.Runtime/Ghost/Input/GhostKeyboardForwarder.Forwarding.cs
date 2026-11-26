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


    /// <summary>
    /// Attaches to a PiP native window handle to capture WM_KEYDOWN, WM_KEYUP, and WM_CHAR messages.
    /// </summary>
    public void AttachToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;
        lock (_gate)
        {
            _targetHwnd = hwnd;
            AttachWindowSubclass(hwnd);
        }
    }

    private void AttachWindowSubclass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows()) return;

        try
        {
            DetachWindowSubclass();

            _subclassDelegate = SubclassWindowProc;
            bool ok = Win32.SetWindowSubclass(hwnd, _subclassDelegate, SubclassId, IntPtr.Zero);
            if (ok)
            {
                _subclassedHwnd = hwnd;
            }
            else
            {
                Debug.WriteLine($"[GhostKeyboardForwarder] SetWindowSubclass failed for HWND 0x{hwnd:X}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostKeyboardForwarder] Failed to attach subclass: {ex.Message}");
        }
    }

    private void DetachWindowSubclass()
    {
        if (_subclassedHwnd != IntPtr.Zero && _subclassDelegate != null && OperatingSystem.IsWindows())
        {
            try
            {
                Win32.RemoveWindowSubclass(_subclassedHwnd, _subclassDelegate, SubclassId);
            }
            catch { }
            _subclassedHwnd = IntPtr.Zero;
        }
    }

    private IntPtr SubclassWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (ProcessWindowMessage(hWnd, uMsg, wParam, lParam, out bool handled))
        {
            if (handled)
            {
                return IntPtr.Zero;
            }
        }

        return Win32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }



    /// <summary>
    /// Manually feeds a Win32 window message into the keyboard forwarder.
    /// Useful if integrating directly into an existing WndProc loop.
    /// </summary>
    public bool ProcessWindowMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, out bool handled)
    {
        handled = false;

        switch (msg)
        {
            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            {
                int vk = (int)wParam;
                long lp = lParam.ToInt64();
                int scan = (int)((lp >> 16) & 0xFF);
                bool extended = (lp & (1 << 24)) != 0;
                bool alt = (lp & (1 << 29)) != 0;
                bool ctrl = (Win32.GetKeyState(0x11) & 0x8000) != 0;
                bool shift = (Win32.GetKeyState(0x10) & 0x8000) != 0;
                bool win = (Win32.GetKeyState(0x5B) & 0x8000) != 0 || (Win32.GetKeyState(0x5C) & 0x8000) != 0;

                if (IsEmergencyChord(vk, ctrl, alt, shift, win))
                {
                    handled = true;
                    return true;
                }

                // Escape key in fullscreen mode triggers exit to mini box
                if (vk == VK_ESCAPE && _isFullscreen)
                {
                    HandleEscape();
                    handled = true;
                    return true;
                }

                if (IsInteractive && _forwardingEnabled && !EmergencyGate.IsStopped)
                {
                    _ = ForwardKeyAsync(GhostKeyEventKind.KeyDown, vk, scan, extended, alt, ctrl, shift);
                    handled = true;
                }
                return true;
            }

            case WM_KEYUP:
            case WM_SYSKEYUP:
            {
                int vk = (int)wParam;
                long lp = lParam.ToInt64();
                int scan = (int)((lp >> 16) & 0xFF);
                bool extended = (lp & (1 << 24)) != 0;
                bool alt = (lp & (1 << 29)) != 0;
                bool ctrl = (Win32.GetKeyState(0x11) & 0x8000) != 0;
                bool shift = (Win32.GetKeyState(0x10) & 0x8000) != 0;

                if (IsInteractive && _forwardingEnabled && !EmergencyGate.IsStopped)
                {
                    _ = ForwardKeyAsync(GhostKeyEventKind.KeyUp, vk, scan, extended, alt, ctrl, shift);
                    handled = true;
                }
                return true;
            }

            case WM_CHAR:
            case WM_SYSCHAR:
            {
                char c = (char)wParam;

                if (IsInteractive && _forwardingEnabled && !EmergencyGate.IsStopped)
                {
                    _ = ForwardCharAsync(c);
                    handled = true;
                }
                return true;
            }

            case WM_HOTKEY:
            {
                HandleHotKey(wParam.ToInt32());
                handled = true;
                return true;
            }
        }

        return false;
    }



    /// <summary>
    /// Forwards a key press or release event across the Ghost IPC bus to Session 2.
    /// </summary>
    public async Task<bool> ForwardKeyAsync(
        GhostKeyEventKind kind,
        int virtualKey,
        int scanCode = 0,
        bool isExtended = false,
        bool alt = false,
        bool ctrl = false,
        bool shift = false,
        CancellationToken ct = default)
    {
        var payload = new GhostKeyboardPayload
        {
            Event = kind switch
            {
                GhostKeyEventKind.KeyDown => "keydown",
                GhostKeyEventKind.KeyUp => "keyup",
                GhostKeyEventKind.Char => "char",
                _ => "keydown"
            },
            VirtualKey = virtualKey,
            ScanCode = scanCode,
            Extended = isExtended,
            Alt = alt,
            Ctrl = ctrl,
            Shift = shift,
            TimestampTicks = DateTime.UtcNow.Ticks
        };

        return await SendPayloadOverIpcAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards a Unicode character generated by keyboard input to Session 2.
    /// </summary>
    public async Task<bool> ForwardCharAsync(char character, CancellationToken ct = default)
    {
        var payload = new GhostKeyboardPayload
        {
            Event = "char",
            Character = character.ToString(),
            VirtualKey = character,
            TimestampTicks = DateTime.UtcNow.Ticks
        };

        return await SendPayloadOverIpcAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Forwards text directly to the active application in Session 2.
    /// </summary>
    public async Task<bool> ForwardTextAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return true;

        var payload = new GhostKeyboardPayload
        {
            Event = "type",
            Character = text,
            TimestampTicks = DateTime.UtcNow.Ticks
        };

        return await SendPayloadOverIpcAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task<bool> SendPayloadOverIpcAsync(GhostKeyboardPayload payload, CancellationToken ct)
    {
        var bus = _ipcBus;

        if (EmergencyGate.IsStopped)
        {
            if (bus != null) SendPanicRelease(bus);
            return false;
        }
        _panicReleaseSent = false;

        if (bus == null || !bus.IsConnected)
        {
            return false;
        }

        string json = JsonSerializer.Serialize(payload);

        try
        {
            // 1. Primary: dedicated forward_keyboard action
            await bus.SendRequestAsync("forward_keyboard", json, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            KeyForwarded?.Invoke(payload);
            return true;
        }
        catch (GhostIpcException)
        {
            // 2. Fallback: format as execute_batch step for Session 2 daemons
            return await FallbackBatchForwardAsync(bus, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostKeyboardForwarder] Forwarding error: {ex.Message}");
            ForwardingError?.Invoke(ex);
            return false;
        }
    }

    private async Task<bool> FallbackBatchForwardAsync(IGhostIpcBus bus, GhostKeyboardPayload payload, CancellationToken ct)
    {
        try
        {
            string? stepJson = null;

            if (payload.Event == "char" || payload.Event == "type" || !string.IsNullOrEmpty(payload.Character))
            {
                string text = payload.Character ?? string.Empty;
                stepJson = JsonSerializer.Serialize(new[]
                {
                    new { @do = "type", v = text }
                });
            }
            else if (payload.Event == "keydown")
            {
                string? keyName = VirtualKeyToKeyName(payload.VirtualKey);
                if (!string.IsNullOrEmpty(keyName))
                {
                    stepJson = JsonSerializer.Serialize(new[]
                    {
                        new { @do = "key", v = keyName }
                    });
                }
            }

            if (!string.IsNullOrEmpty(stepJson))
            {
                await bus.SendRequestAsync("execute_batch", stepJson, TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                KeyForwarded?.Invoke(payload);
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[GhostKeyboardForwarder] Fallback batch error: {ex.Message}");
            ForwardingError?.Invoke(ex);
        }

        return false;
    }

    /// <summary>
    /// Translates a virtual key code into a friendly key identifier name.
    /// </summary>
    public static string? VirtualKeyToKeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x13 => "Pause",
        0x14 => "CapsLock",
        0x1B => "Escape",
        0x20 => "Space",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x70 + 1}",
        _ => null
    };

    private void SendPanicRelease(IGhostIpcBus bus)
    {
        if (_panicReleaseSent)
        {
            return;
        }
        _panicReleaseSent = true;
        try
        {
            var release = new GhostInputPacket { Type = "ReleaseAll", Action = "release_all" };
            _ = bus.SendRequestAsync("forward_input", release.ToJson(), TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch { }
    }

    private static bool IsEmergencyChord(int vk, bool ctrl, bool alt, bool shift, bool win)
    {
        foreach (var c in EmergencyChords())
        {
            if (c.Vk == vk && c.Ctrl == ctrl && c.Alt == alt && c.Shift == shift && c.Win == win)
            {
                return true;
            }
        }
        return false;
    }

    private static (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win)[] EmergencyChords()
    {
        if (_emergencyChords != null)
        {
            return _emergencyChords;
        }

        try
        {
            var settings = UserSettings.Load();
            var panic = Environment.GetEnvironmentVariable("INBRISK_PANIC_HOTKEY") ?? settings.PanicHotkey ?? "Ctrl+Alt+Pause";
            var resume = Environment.GetEnvironmentVariable("INBRISK_RESUME_HOTKEY") ?? settings.ResumeHotkey ?? "Ctrl+Alt+Shift+Pause";

            var list = new List<(int Vk, bool Ctrl, bool Alt, bool Shift, bool Win)>();
            if (TryParseChord(panic, out var p)) list.Add(p);
            if (TryParseChord(resume, out var r)) list.Add(r);
            _emergencyChords = list.Count > 0
                ? list.ToArray()
                : new[] { (0x13, true, true, false, false), (0x13, true, true, true, false) };
        }
        catch
        {
            _emergencyChords = new[] { (0x13, true, true, false, false), (0x13, true, true, true, false) };
        }

        return _emergencyChords;
    }

    private static bool TryParseChord(string? chord, out (int Vk, bool Ctrl, bool Alt, bool Shift, bool Win) result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        bool ctrl = false, alt = false, shift = false, win = false;
        int vk = 0;
        foreach (var part in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = part.ToLowerInvariant();
            if (p is "ctrl" or "control") ctrl = true;
            else if (p is "alt" or "menu") alt = true;
            else if (p is "shift") shift = true;
            else if (p is "win" or "windows" or "super") win = true;
            else vk = ParseChordKey(p);
        }

        if (vk == 0)
        {
            return false;
        }

        result = (vk, ctrl, alt, shift, win);
        return true;
    }

    private static int ParseChordKey(string name) => name switch
    {
        "pause" or "break" => 0x13,
        "esc" or "escape" => 0x1B,
        "enter" or "return" => 0x0D,
        "tab" => 0x09,
        "space" => 0x20,
        "backspace" => 0x08,
        "delete" or "del" => 0x2E,
        "insert" or "ins" => 0x2D,
        "home" => 0x24,
        "end" => 0x23,
        "pageup" or "pgup" => 0x21,
        "pagedown" or "pgdn" => 0x22,
        "up" => 0x26,
        "down" => 0x28,
        "left" => 0x25,
        "right" => 0x27,
        "capslock" => 0x14,
        "printscreen" or "prtsc" => 0x2C,
        _ when name.Length == 1 && char.IsLetterOrDigit(name[0]) => char.ToUpperInvariant(name[0]),
        _ when name.Length > 1 && name[0] == 'f' && int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 24 => 0x70 + fn - 1,
        _ => 0
    };
}
