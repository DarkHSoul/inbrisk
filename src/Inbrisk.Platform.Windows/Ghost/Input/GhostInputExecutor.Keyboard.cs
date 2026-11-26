using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

public sealed partial class GhostInputExecutor : IDisposable
{


    /// <summary>
    /// Types arbitrary Unicode text into the currently focused window using KEYEVENTF_UNICODE.
    /// Handles special characters, multi-byte runes, carriage returns, and newlines properly.
    /// </summary>
    public void TypeText(string text, int delayBetweenKeysMs = 10, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (string.IsNullOrEmpty(text))
            return;

        delayBetweenKeysMs = Math.Clamp(delayBetweenKeysMs, 0, 200);

        for (int i = 0; i < text.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            char c = text[i];

            if (c == '\r')
                continue;

            if (c == '\n')
            {
                KeyPress(KeyCode.Enter, 20, ct);
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
                continue;
            }

            if (c == '\t')
            {
                KeyPress(KeyCode.Tab, 20, ct);
                if (delayBetweenKeysMs > 0)
                    Thread.Sleep(delayBetweenKeysMs);
                continue;
            }

            var down = new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    Ki = new KEYBDINPUT
                    {
                        WScan = c,
                        DwFlags = NativeMethods.KEYEVENTF_UNICODE,
                        DwExtraInfo = Marker
                    }
                }
            };

            var up = new INPUT
            {
                Type = NativeMethods.INPUT_KEYBOARD,
                U = new INPUTUNION
                {
                    Ki = new KEYBDINPUT
                    {
                        WScan = c,
                        DwFlags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP,
                        DwExtraInfo = Marker
                    }
                }
            };

            NativeMethods.SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());

            if (delayBetweenKeysMs > 0)
                Thread.Sleep(delayBetweenKeysMs);
        }
    }

    /// <summary>
    /// Executes a hotkey sequence parsed from a string representation (e.g. "ctrl+s", "alt+f4", "ctrl+shift+esc").
    /// </summary>
    public void ExecuteHotkey(string hotkeyCombo, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(hotkeyCombo);

        var (modifiers, targetKey) = ParseHotkeyString(hotkeyCombo);
        ExecuteHotkey(modifiers, targetKey, ct);
    }

    /// <summary>
    /// Executes a hotkey sequence with specified modifiers and primary key.
    /// Modifiers are pressed down in sequence and released in reverse order in a finally block.
    /// </summary>
    public void ExecuteHotkey(IReadOnlyList<KeyCode> modifiers, KeyCode targetKey, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        var pressedModifiers = new List<ushort>();
        try
        {
            foreach (var mod in modifiers)
            {
                ct.ThrowIfCancellationRequested();
                ushort vk = KeyCodeToVk(mod);
                SendKey(vk, down: true);
                pressedModifiers.Add(vk);
                lock (_stateGate) _heldVirtualKeys.Add(vk);
            }

            Thread.Sleep(20);
            ct.ThrowIfCancellationRequested();

            ushort targetVk = KeyCodeToVk(targetKey);
            SendKey(targetVk, down: true);
            lock (_stateGate) _heldVirtualKeys.Add(targetVk);
            try
            {
                Thread.Sleep(30);
            }
            finally
            {
                SendKey(targetVk, down: false);
                lock (_stateGate) _heldVirtualKeys.Remove(targetVk);
            }
        }
        finally
        {
            for (int i = pressedModifiers.Count - 1; i >= 0; i--)
            {
                ushort modVk = pressedModifiers[i];
                try
                {
                    SendKey(modVk, down: false);
                    lock (_stateGate) _heldVirtualKeys.Remove(modVk);
                }
                catch { /* Ensure all modifier releases complete */ }
            }
        }
    }

    /// <summary>
    /// Simulates a single key press and release with hold duration.
    /// </summary>
    public void KeyPress(KeyCode key, int holdDurationMs = 25, CancellationToken ct = default)
    {
        KeyPress(KeyCodeToVk(key), holdDurationMs, ct);
    }

    /// <summary>
    /// Simulates a single key press and release by Virtual Key code.
    /// </summary>
    public void KeyPress(ushort vk, int holdDurationMs = 25, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        holdDurationMs = Math.Clamp(holdDurationMs, 0, 2000);

        SendKey(vk, down: true);
        lock (_stateGate) _heldVirtualKeys.Add(vk);

        try
        {
            if (holdDurationMs > 0)
                Thread.Sleep(holdDurationMs);
        }
        finally
        {
            SendKey(vk, down: false);
            lock (_stateGate) _heldVirtualKeys.Remove(vk);
        }
    }

    /// <summary>
    /// Presses and holds a key down.
    /// </summary>
    public void KeyDown(KeyCode key) => KeyDown(KeyCodeToVk(key));

    /// <summary>
    /// Presses and holds a key down by Virtual Key code.
    /// </summary>
    public void KeyDown(ushort vk)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SendKey(vk, down: true);
        lock (_stateGate) _heldVirtualKeys.Add(vk);
    }

    /// <summary>
    /// Releases a held key.
    /// </summary>
    public void KeyUp(KeyCode key) => KeyUp(KeyCodeToVk(key));

    /// <summary>
    /// Releases a held key by Virtual Key code.
    /// </summary>
    public void KeyUp(ushort vk)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        SendKey(vk, down: false);
        lock (_stateGate) _heldVirtualKeys.Remove(vk);
    }

    private static void SendKey(ushort vk, bool down)
    {
        uint flags = down ? 0 : NativeMethods.KEYEVENTF_KEYUP;
        ushort scan = (ushort)NativeMethods.MapVirtualKeyW(vk, NativeMethods.MAPVK_VK_TO_VSC);

        if (IsExtendedKey(vk))
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        var input = new INPUT
        {
            Type = NativeMethods.INPUT_KEYBOARD,
            U = new INPUTUNION
            {
                Ki = new KEYBDINPUT
                {
                    WVk = vk,
                    WScan = scan,
                    DwFlags = flags,
                    DwExtraInfo = Marker
                }
            }
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static bool IsExtendedKey(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 // PageUp, PageDown, End, Home
        or 0x25 or 0x26 or 0x27 or 0x28 // Left, Up, Right, Down
        or 0x2D or 0x2E // Insert, Delete
        or 0x5B or 0x5C or 0x5D // LWin, RWin, Apps
        or 0xA3 or 0xA5 // RControl, RMenu
        or 0x6F; // Divide

    private static (IReadOnlyList<KeyCode> Modifiers, KeyCode Key) ParseHotkeyString(string combo)
    {
        var parts = combo.Split(new[] { '+', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = new List<KeyCode>();
        KeyCode? primaryKey = null;

        foreach (var part in parts)
        {
            string lower = part.ToLowerInvariant();
            if (lower is "ctrl" or "control")
                modifiers.Add(KeyCode.Ctrl);
            else if (lower is "shift")
                modifiers.Add(KeyCode.Shift);
            else if (lower is "alt" or "menu")
                modifiers.Add(KeyCode.Alt);
            else if (lower is "win" or "windows" or "super" or "cmd")
                modifiers.Add(KeyCode.Win);
            else
            {
                primaryKey = ParseKeyName(lower);
            }
        }

        if (!primaryKey.HasValue)
            throw new ArgumentException($"Invalid hotkey string: \"{combo}\" (no primary key found)");

        return (modifiers, primaryKey.Value);
    }

    private static KeyCode ParseKeyName(string name) => name switch
    {
        "enter" or "return" => KeyCode.Enter,
        "esc" or "escape" => KeyCode.Escape,
        "tab" => KeyCode.Tab,
        "space" => KeyCode.Space,
        "backspace" or "back" => KeyCode.Backspace,
        "del" or "delete" => KeyCode.Delete,
        "up" => KeyCode.Up,
        "down" => KeyCode.Down,
        "left" => KeyCode.Left,
        "right" => KeyCode.Right,
        "home" => KeyCode.Home,
        "end" => KeyCode.End,
        "pageup" or "pgup" => KeyCode.PageUp,
        "pagedown" or "pgdn" => KeyCode.PageDown,
        "pause" => KeyCode.Pause,
        "f1" => KeyCode.F1,
        "f2" => KeyCode.F2,
        "f3" => KeyCode.F3,
        "f4" => KeyCode.F4,
        "f5" => KeyCode.F5,
        "f6" => KeyCode.F6,
        "f7" => KeyCode.F7,
        "f8" => KeyCode.F8,
        "f9" => KeyCode.F9,
        "f10" => KeyCode.F10,
        "f11" => KeyCode.F11,
        "f12" => KeyCode.F12,
        _ when name.Length == 1 && name[0] >= 'a' && name[0] <= 'z' => (KeyCode)((int)KeyCode.A + (name[0] - 'a')),
        _ when name.Length == 1 && name[0] >= '0' && name[0] <= '9' => (KeyCode)((int)KeyCode.D0 + (name[0] - '0')),
        _ => throw new ArgumentException($"Unrecognized key name: \"{name}\"")
    };

    public static ushort KeyCodeToVk(KeyCode key) => key switch
    {
        KeyCode.Enter => 0x0D,
        KeyCode.Escape => 0x1B,
        KeyCode.Tab => 0x09,
        KeyCode.Space => 0x20,
        KeyCode.Backspace => 0x08,
        KeyCode.Delete => 0x2E,
        KeyCode.Left => 0x25,
        KeyCode.Up => 0x26,
        KeyCode.Right => 0x27,
        KeyCode.Down => 0x28,
        KeyCode.Home => 0x24,
        KeyCode.End => 0x23,
        KeyCode.PageUp => 0x21,
        KeyCode.PageDown => 0x22,
        KeyCode.Ctrl => 0xA2, // VK_LCONTROL
        KeyCode.Shift => 0xA0, // VK_LSHIFT
        KeyCode.Alt => 0xA4, // VK_LMENU
        KeyCode.Win => 0x5B, // VK_LWIN
        KeyCode.Pause => 0x13,
        KeyCode.F1 => 0x70,
        KeyCode.F2 => 0x71,
        KeyCode.F3 => 0x72,
        KeyCode.F4 => 0x73,
        KeyCode.F5 => 0x74,
        KeyCode.F6 => 0x75,
        KeyCode.F7 => 0x76,
        KeyCode.F8 => 0x77,
        KeyCode.F9 => 0x78,
        KeyCode.F10 => 0x79,
        KeyCode.F11 => 0x7A,
        KeyCode.F12 => 0x7B,
        >= KeyCode.A and <= KeyCode.Z => (ushort)(0x41 + (int)key - (int)KeyCode.A),
        >= KeyCode.D0 and <= KeyCode.D9 => (ushort)(0x30 + (int)key - (int)KeyCode.D0),
        _ => throw new NotSupportedException($"No VirtualKey mapping for {key}")
    };
}
