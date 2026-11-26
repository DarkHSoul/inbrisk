using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows;

public sealed partial class GhostDesktopInput : IDisposable
{


    /// <summary>
    /// Injects a key press (KeyDown + KeyUp) using a <see cref="VirtualKey"/>.
    /// Posts directly to the focused or target window on the ghost desktop.
    /// </summary>
    /// <param name="key">Virtual key code to press.</param>
    public void SendKeyPress(VirtualKey key)
    {
        SendKeyDown(key);
        try
        {
            Thread.Sleep(20);
        }
        finally
        {
            SendKeyUp(key);
        }
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key presses.
    /// </summary>
    public void SendKeyPress(KeyCode key) => SendKeyPress(ToVirtualKey(key));

    /// <summary>
    /// Overload for raw virtual key numeric codes.
    /// </summary>
    public void SendKeyPress(ushort key) => SendKeyPress((VirtualKey)key);

    /// <summary>
    /// Overload for raw integer virtual key codes.
    /// </summary>
    public void SendKeyPress(int key) => SendKeyPress((VirtualKey)key);

    /// <summary>
    /// Injects a key down event to the ghost desktop target window.
    /// </summary>
    public void SendKeyDown(VirtualKey key)
    {
        RunOnGhostThread(() =>
        {
            IntPtr targetHwnd = FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero) return;

            uint vk = (uint)key;
            // Mask the scan to its low byte: an 0xE0-prefixed extended scan
            // shifted into bits 16-23 would otherwise spill into bits 29-31,
            // faking Alt-context / previous-state / transition (release) flags
            // on a key-DOWN. Extended-ness belongs to bit 24 only.
            uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            uint lParam = 1 | ((scanCode & 0xFF) << 16);
            if (IsExtendedKey(vk))
            {
                lParam |= (1u << 24);
            }

            Win32.PostMessageW(targetHwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)lParam);
            _heldKeysHwnd[vk] = targetHwnd;
        });
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key down.
    /// </summary>
    public void SendKeyDown(KeyCode key) => SendKeyDown(ToVirtualKey(key));

    /// <summary>
    /// Injects a key up event to the ghost desktop target window.
    /// </summary>
    public void SendKeyUp(VirtualKey key)
    {
        RunOnGhostThread(() =>
        {
            uint vk = (uint)key;

            IntPtr targetHwnd = _heldKeysHwnd.TryGetValue(vk, out IntPtr tracked) && IsWindowOnGhostDesktop(tracked)
                ? tracked
                : FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero) return;

            uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC);
            uint lParam = 1 | ((scanCode & 0xFF) << 16) | (1u << 30) | (1u << 31);
            if (IsExtendedKey(vk))
            {
                lParam |= (1u << 24);
            }

            Win32.PostMessageW(targetHwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)lParam);
            _heldKeysHwnd.Remove(vk);
        });
    }

    /// <summary>
    /// Overload for <see cref="KeyCode"/> key up.
    /// </summary>
    public void SendKeyUp(KeyCode key) => SendKeyUp(ToVirtualKey(key));

    /// <summary>
    /// Types Unicode text into the target window on the ghost desktop via <c>WM_CHAR</c>
    /// or <c>WM_KEYDOWN</c>/<c>WM_KEYUP</c> pairs for console windows (<c>ConsoleWindowClass</c>).
    /// Properly converts newlines and tabs to their respective virtual key events.
    /// </summary>
    /// <param name="text">Text string to type.</param>
    public void SendText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        RunOnGhostThread(() =>
        {
            IntPtr targetHwnd = FindKeyboardTarget();
            if (targetHwnd == IntPtr.Zero || targetHwnd == Win32.GetDesktopWindow()) return;

            bool isConsole = IsConsoleWindowClass(targetHwnd);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') continue;

                if (c == '\n')
                {
                    SendKeyPressCore(targetHwnd, VirtualKey.Enter);
                    continue;
                }

                if (c == '\t')
                {
                    SendKeyPressCore(targetHwnd, VirtualKey.Tab);
                    continue;
                }

                if (isConsole)
                {
                    SendConsoleChar(targetHwnd, c);
                }
                else
                {
                    // WM_CHAR lParam carries the scan code of the PHYSICAL key
                    // the character maps to. Resolve char→VK via VkKeyScanW
                    // first — passing the char itself to MapVirtualKeyW reads
                    // it as a virtual-key code ('e' = 0x65 = VK_NUMPAD5,
                    // 's' = VK_F4) and stamps a garbage scan code.
                    uint lParam = 1;
                    short ks = Win32.VkKeyScanW(c);
                    if (ks != -1)
                    {
                        uint sc = Win32.MapVirtualKeyW((uint)(byte)(ks & 0xFF), MAPVK_VK_TO_VSC);
                        lParam = 1 | ((sc & 0xFF) << 16);
                    }
                    Win32.PostMessageW(targetHwnd, WM_CHAR, (IntPtr)c, (IntPtr)lParam);
                }
            }
        });
    }

    /// <summary>
    /// Checks whether the specified window or its root belongs to the Windows Console Host (<c>ConsoleWindowClass</c>).
    /// </summary>
    public static bool IsConsoleWindowClass(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var sb = new StringBuilder(64);
        Win32.GetClassNameW(hwnd, sb, 64);
        if (string.Equals(sb.ToString(), "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
            return true;

        IntPtr root = Win32.GetAncestor(hwnd, 2 /* GA_ROOT */);
        if (root != IntPtr.Zero && root != hwnd)
        {
            sb.Clear();
            Win32.GetClassNameW(root, sb, 64);
            return string.Equals(sb.ToString(), "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static void SendConsoleChar(IntPtr hwnd, char c)
    {
        short res = Win32.VkKeyScanW(c);
        if (res == -1)
        {
            uint scanCode = Win32.MapVirtualKeyW((uint)c, MAPVK_VK_TO_VSC);
            uint lParam = 1 | (scanCode << 16);
            Win32.PostMessageW(hwnd, WM_CHAR, (IntPtr)c, (IntPtr)lParam);
            return;
        }

        byte vk = (byte)(res & 0xFF);
        byte shiftState = (byte)((res >> 8) & 0xFF);
        bool shift = (shiftState & 1) != 0;
        bool ctrl = (shiftState & 2) != 0;
        bool alt = (shiftState & 4) != 0;

        uint shiftSc = Win32.MapVirtualKeyW((uint)VirtualKey.Shift, MAPVK_VK_TO_VSC);
        uint ctrlSc = Win32.MapVirtualKeyW((uint)VirtualKey.Control, MAPVK_VK_TO_VSC);
        uint altSc = Win32.MapVirtualKeyW((uint)VirtualKey.Alt, MAPVK_VK_TO_VSC);

        if (shift) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Shift, (IntPtr)(1 | (shiftSc << 16)));
        if (ctrl) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Control, (IntPtr)(1 | (ctrlSc << 16)));
        if (alt) Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)VirtualKey.Alt, (IntPtr)(1 | (altSc << 16)));

        uint sc = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) & 0xFF;
        try
        {
            uint downLParam = 1 | (sc << 16);
            if (IsExtendedKey(vk)) downLParam |= (1u << 24);
            Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)downLParam);

            Thread.Sleep(5);

            uint upLParam = 1 | (sc << 16) | (1u << 30) | (1u << 31);
            if (IsExtendedKey(vk)) upLParam |= (1u << 24);
            Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)upLParam);
        }
        finally
        {
            if (alt) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Alt, (IntPtr)(1 | ((altSc & 0xFF) << 16) | (1u << 30) | (1u << 31)));
            if (ctrl) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Control, (IntPtr)(1 | ((ctrlSc & 0xFF) << 16) | (1u << 30) | (1u << 31)));
            if (shift) Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)VirtualKey.Shift, (IntPtr)(1 | ((shiftSc & 0xFF) << 16) | (1u << 30) | (1u << 31)));
        }
    }

    private static void SendKeyPressCore(IntPtr hwnd, VirtualKey key)
    {
        uint vk = (uint)key;
        uint scanCode = Win32.MapVirtualKeyW(vk, MAPVK_VK_TO_VSC) & 0xFF;
        uint downLParam = 1 | (scanCode << 16);
        if (IsExtendedKey(vk)) downLParam |= (1u << 24);
        Win32.PostMessageW(hwnd, WM_KEYDOWN, (IntPtr)vk, (IntPtr)downLParam);

        try
        {
            Thread.Sleep(15);
        }
        finally
        {
            uint upLParam = 1 | (scanCode << 16) | (1u << 30) | (1u << 31);
            if (IsExtendedKey(vk)) upLParam |= (1u << 24);
            Win32.PostMessageW(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)upLParam);
        }
    }



    /// <summary>
    /// Maps relative coordinates from the PiP window overlay (e.g. 320x180) to absolute
    /// physical desktop coordinates on the ghost desktop (e.g. 1920x1080).
    /// </summary>
    /// <param name="pipX">X pixel coordinate inside the PiP overlay.</param>
    /// <param name="pipY">Y pixel coordinate inside the PiP overlay.</param>
    /// <returns>Physical desktop coordinates (DesktopX, DesktopY).</returns>
    public (int DesktopX, int DesktopY) MapPipToDesktop(int pipX, int pipY)
    {
        return MapPipToDesktopCoordinates(
            pipX, pipY,
            PipWidth, PipHeight,
            DesktopWidth, DesktopHeight,
            PipHeaderHeight);
    }

    /// <summary>
    /// Pure function to map coordinates from any PiP overlay dimensions to ghost desktop coordinates.
    /// Handles optional top header bar offsets and clamps coordinates within valid bounds.
    /// </summary>
    public static (int DesktopX, int DesktopY) MapPipToDesktopCoordinates(
        int pipX, int pipY,
        int pipWidth, int pipHeight,
        int desktopWidth = 1920, int desktopHeight = 1080,
        int pipHeaderHeight = 0)
    {
        int effectiveWidth = Math.Max(1, pipWidth);
        int effectiveHeight = Math.Max(1, pipHeight - pipHeaderHeight);
        int effectiveY = Math.Max(0, pipY - pipHeaderHeight);

        int mappedX = (int)Math.Round((double)pipX / effectiveWidth * desktopWidth);
        int mappedY = (int)Math.Round((double)effectiveY / effectiveHeight * desktopHeight);

        int clampedX = Math.Clamp(mappedX, 0, Math.Max(0, desktopWidth - 1));
        int clampedY = Math.Clamp(mappedY, 0, Math.Max(0, desktopHeight - 1));

        return (clampedX, clampedY);
    }

    /// <summary>
    /// Injects a mouse move event originating from an interactive PiP click/hover.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseMove(int pipX, int pipY)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseMove(dx, dy);
    }

    /// <summary>
    /// Injects a mouse click event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseClick(int pipX, int pipY, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseClick(dx, dy, button, doubleClick);
    }

    /// <summary>
    /// Injects a mouse down event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseDown(int pipX, int pipY, MouseButton button)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseDown(dx, dy, button);
    }

    /// <summary>
    /// Injects a mouse up event originating from an interactive PiP click.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseUp(int pipX, int pipY, MouseButton button)
    {
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseUp(dx, dy, button);
    }

    /// <summary>
    /// Injects a mouse scroll event originating from an interactive PiP wheel action.
    /// Returns immediately without injecting if <see cref="IsPipInteractive"/> is false.
    /// </summary>
    public void SendPipMouseScroll(int pipX, int pipY, int delta)
    {
        if (!IsPipInteractive) return;
        var (dx, dy) = MapPipToDesktop(pipX, pipY);
        SendMouseScroll(dx, dy, delta);
    }

    /// <summary>
    /// Forwards an interactive PiP input event to the ghost desktop.
    /// Automatically maps PiP coordinates to ghost desktop resolution if not already mapped.
    /// </summary>
    public void ForwardPipInput(PipInputEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsPipInteractive && e.EventType is not (PipInputEventType.MouseUp or PipInputEventType.KeyUp)) return;

        if (e.EventType == PipInputEventType.KeyDown)
        {
            SendKeyDown((VirtualKey)e.KeyCode);
            return;
        }

        if (e.EventType == PipInputEventType.KeyUp)
        {
            SendKeyUp((VirtualKey)e.KeyCode);
            return;
        }

        if (e.EventType == PipInputEventType.Char)
        {
            if (e.Character != '\0')
            {
                SendText(e.Character.ToString());
            }
            return;
        }

        var (dx, dy) = e.IsMapped ? (e.X, e.Y) : MapPipToDesktop(e.X, e.Y);

        switch (e.EventType)
        {
            case PipInputEventType.MouseMove:
                SendMouseMove(dx, dy);
                break;
            case PipInputEventType.MouseDown:
                SendMouseDown(dx, dy, e.Button);
                break;
            case PipInputEventType.MouseUp:
                SendMouseUp(dx, dy, e.Button);
                break;
            case PipInputEventType.DoubleClick:
                SendMouseClick(dx, dy, e.Button, doubleClick: true);
                break;
            case PipInputEventType.MouseWheel:
                SendMouseScroll(dx, dy, e.Delta);
                break;
        }
    }

    /// <summary>
    /// Forwards a keyboard key action (KeyDown, KeyUp, Char) directly to the ghost desktop.
    /// </summary>
    public void ForwardKeyInput(PipInputEventType eventType, int keyCode, char character)
    {
        if (!IsPipInteractive && eventType != PipInputEventType.KeyUp) return;

        if (eventType == PipInputEventType.KeyDown)
        {
            SendKeyDown((VirtualKey)keyCode);
        }
        else if (eventType == PipInputEventType.KeyUp)
        {
            SendKeyUp((VirtualKey)keyCode);
        }
        else if (eventType == PipInputEventType.Char)
        {
            if (character != '\0')
            {
                SendText(character.ToString());
            }
        }
    }

    /// <summary>
    /// Attaches this input injector to a <see cref="GhostPipWindowHost"/> instance to automatically
    /// forward interactive mouse and wheel events to the ghost desktop.
    /// </summary>
    public void AttachToPipHost(GhostPipWindowHost pipHost)
    {
        ArgumentNullException.ThrowIfNull(pipHost);
        pipHost.OnInteractiveInput += ForwardPipInput;
    }

    /// <summary>
    /// Detaches this input injector from a <see cref="GhostPipWindowHost"/> instance.
    /// </summary>
    public void DetachFromPipHost(GhostPipWindowHost pipHost)
    {
        ArgumentNullException.ThrowIfNull(pipHost);
        pipHost.OnInteractiveInput -= ForwardPipInput;
    }
}
