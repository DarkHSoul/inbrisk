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
    /// Injects a mouse move event to the window under the specified desktop coordinates on the ghost desktop.
    /// Does NOT move the user's physical mouse cursor on "Default".
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    public void SendMouseMove(int desktopX, int desktopY)
    {
        RunOnGhostThread(() =>
        {
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            if (_isDraggingWindow && _dragHwnd != IntPtr.Zero && IsWindowOnGhostDesktop(_dragHwnd))
            {
                int deltaX = desktopX - _dragStartMouseX;
                int deltaY = desktopY - _dragStartMouseY;

                int newX = _dragStartWindowRect.Left;
                int newY = _dragStartWindowRect.Top;
                int newW = _dragStartWindowRect.Right - _dragStartWindowRect.Left;
                int newH = _dragStartWindowRect.Bottom - _dragStartWindowRect.Top;

                if (_dragHitTest == HTCAPTION)
                {
                    newX += deltaX;
                    newY += deltaY;
                }
                else
                {
                    switch (_dragHitTest)
                    {
                        case HTRIGHT:
                            newW = Math.Max(160, newW + deltaX);
                            break;
                        case HTBOTTOM:
                            newH = Math.Max(80, newH + deltaY);
                            break;
                        case HTBOTTOMRIGHT:
                            newW = Math.Max(160, newW + deltaX);
                            newH = Math.Max(80, newH + deltaY);
                            break;
                        case HTLEFT:
                            int cdx = Math.Min(deltaX, newW - 160);
                            newX += cdx;
                            newW -= cdx;
                            break;
                        case HTTOP:
                            int cdy = Math.Min(deltaY, newH - 80);
                            newY += cdy;
                            newH -= cdy;
                            break;
                        case HTTOPLEFT:
                            int cdx1 = Math.Min(deltaX, newW - 160);
                            int cdy1 = Math.Min(deltaY, newH - 80);
                            newX += cdx1;
                            newW -= cdx1;
                            newY += cdy1;
                            newH -= cdy1;
                            break;
                        case HTTOPRIGHT:
                            int cdy2 = Math.Min(deltaY, newH - 80);
                            newY += cdy2;
                            newH -= cdy2;
                            newW = Math.Max(160, newW + deltaX);
                            break;
                        case HTBOTTOMLEFT:
                            int cdx2 = Math.Min(deltaX, newW - 160);
                            newX += cdx2;
                            newW -= cdx2;
                            newH = Math.Max(80, newH + deltaY);
                            break;
                    }
                }

                Win32.SetWindowPos(_dragHwnd, IntPtr.Zero, newX, newY, newW, newH, SWP_NOZORDER | SWP_NOACTIVATE);
                return;
            }

            IntPtr hwnd;
            POINT clientPt;

            if (_heldMouseKeys != 0 && _capturedMouseHwnd != IntPtr.Zero && IsWindowOnGhostDesktop(_capturedMouseHwnd))
            {
                hwnd = _capturedMouseHwnd;
                clientPt = new POINT { X = desktopX, Y = desktopY };
                Win32.ScreenToClient(hwnd, ref clientPt);
            }
            else
            {
                hwnd = GetWindowAtPoint(desktopX, desktopY, out clientPt);
                if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
                {
                    _lastTargetHwnd = hwnd;
                }
            }

            if (hwnd == IntPtr.Zero) return;

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, WM_MOUSEMOVE, (IntPtr)_heldMouseKeys, lParam);

            if (_heldMouseKeys != 0)
            {
                IntPtr pointerWParam = (IntPtr)(1 /* POINTER_ID 1 */ | (0x0004 << 16 /* INCONTACT */) | (0x0010 << 16 /* FIRSTBUTTON */) | (0x2000 << 16 /* PRIMARY */));
                Win32.PostMessageW(hwnd, WM_POINTERUPDATE, pointerWParam, lParam);
            }
        });
    }

    /// <summary>
    /// Injects a mouse button press (Down) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to press.</param>
    public void SendMouseDown(int desktopX, int desktopY, MouseButton button)
    {
        RunOnGhostThread(() =>
        {
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt, out var hitTopHwnd);

            if (button == MouseButton.Left && hitTopHwnd != IntPtr.Zero && hitTopHwnd != Win32.GetDesktopWindow())
            {
                int hitTest = GetHitTest(hitTopHwnd, desktopX, desktopY);

                if (hitTest == HTCAPTION || hitTest == HTRIGHT || hitTest == HTBOTTOM ||
                    hitTest == HTBOTTOMRIGHT || hitTest == HTLEFT || hitTest == HTTOP ||
                    hitTest == HTTOPLEFT || hitTest == HTTOPRIGHT || hitTest == HTBOTTOMLEFT)
                {
                    _lastTargetHwnd = hitTopHwnd;
                    _isDraggingWindow = true;
                    _dragHwnd = hitTopHwnd;
                    _dragHitTest = hitTest;
                    _dragStartMouseX = desktopX;
                    _dragStartMouseY = desktopY;
                    Win32.GetWindowRect(hitTopHwnd, out _dragStartWindowRect);

                    Win32.SetForegroundWindow(hitTopHwnd);
                    Win32.SetActiveWindow(hitTopHwnd);
                    Win32.SetFocus(hitTopHwnd);
                    Win32.PostMessageW(hitTopHwnd, WM_ACTIVATE, (IntPtr)1 /* WA_ACTIVE */, IntPtr.Zero);
                    return;
                }

                if (hitTest == HTCLOSE)
                {
                    Win32.PostMessageW(hitTopHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    return;
                }

                if (hitTest == HTMINBUTTON)
                {
                    Win32.ShowWindow(hitTopHwnd, 6 /* SW_MINIMIZE */);
                    return;
                }

                if (hitTest == HTMAXBUTTON)
                {
                    ToggleMaximize(hitTopHwnd);
                    return;
                }
            }

            if (hwnd == IntPtr.Zero) return;

            uint msg;
            switch (button)
            {
                case MouseButton.Right:
                    _heldMouseKeys |= MK_RBUTTON;
                    msg = WM_RBUTTONDOWN;
                    break;
                case MouseButton.Middle:
                    _heldMouseKeys |= MK_MBUTTON;
                    msg = WM_MBUTTONDOWN;
                    break;
                case MouseButton.Left:
                default:
                    _heldMouseKeys |= MK_LBUTTON;
                    msg = WM_LBUTTONDOWN;
                    break;
            }

            if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
            {
                _lastTargetHwnd = hwnd;
                _capturedMouseHwnd = hwnd;
                IntPtr root = Win32.GetAncestor(hwnd, 2 /* GA_ROOT */);
                if (root == IntPtr.Zero) root = hwnd;

                Win32.SendMessageTimeoutW(root, WM_MOUSEACTIVATE, root, MakeLParam(1 /* HTCLIENT */, (int)msg), 2 /* SMTO_ABORTIFHUNG */, 50, out _);
                Win32.SetForegroundWindow(root);
                Win32.SetActiveWindow(root);
                Win32.SetFocus(hwnd);
                Win32.PostMessageW(root, WM_ACTIVATE, (IntPtr)1 /* WA_ACTIVE */, IntPtr.Zero);
                Win32.PostMessageW(hwnd, WM_SETFOCUS, IntPtr.Zero, IntPtr.Zero);
            }
            else
            {
                _capturedMouseHwnd = IntPtr.Zero;
            }

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, WM_MOUSEMOVE, (IntPtr)_heldMouseKeys, lParam);
            Win32.PostMessageW(hwnd, msg, (IntPtr)_heldMouseKeys, lParam);

            IntPtr pointerWParam = (IntPtr)(1 /* POINTER_ID 1 */ | (0x0001 << 16 /* NEW */) | (0x0002 << 16 /* INRANGE */) | (0x0004 << 16 /* INCONTACT */) | (0x0010 << 16 /* FIRSTBUTTON */) | (0x2000 << 16 /* PRIMARY */));
            Win32.PostMessageW(hwnd, WM_POINTERDOWN, pointerWParam, lParam);
        });
    }

    /// <summary>
    /// Injects a mouse button release (Up) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to release.</param>
    public void SendMouseUp(int desktopX, int desktopY, MouseButton button)
    {
        RunOnGhostThread(() =>
        {
            _lastMouseX = desktopX;
            _lastMouseY = desktopY;

            if (_isDraggingWindow)
            {
                _isDraggingWindow = false;
                _dragHwnd = IntPtr.Zero;
            }

            IntPtr hwnd;
            POINT clientPt;

            if (_capturedMouseHwnd != IntPtr.Zero && IsWindowOnGhostDesktop(_capturedMouseHwnd))
            {
                hwnd = _capturedMouseHwnd;
                POINT pt = new POINT { X = desktopX, Y = desktopY };
                Win32.ScreenToClient(hwnd, ref pt);
                clientPt = pt;
            }
            else
            {
                hwnd = GetWindowAtPoint(desktopX, desktopY, out clientPt);
                if (hwnd != IntPtr.Zero && hwnd != Win32.GetDesktopWindow())
                {
                    _lastTargetHwnd = hwnd;
                }
                else if (_lastTargetHwnd != IntPtr.Zero && IsWindowOnGhostDesktop(_lastTargetHwnd))
                {
                    hwnd = _lastTargetHwnd;
                    POINT pt = new POINT { X = desktopX, Y = desktopY };
                    Win32.ScreenToClient(hwnd, ref pt);
                    clientPt = pt;
                }
            }

            uint msg;
            switch (button)
            {
                case MouseButton.Right:
                    _heldMouseKeys &= ~MK_RBUTTON;
                    msg = WM_RBUTTONUP;
                    break;
                case MouseButton.Middle:
                    _heldMouseKeys &= ~MK_MBUTTON;
                    msg = WM_MBUTTONUP;
                    break;
                case MouseButton.Left:
                default:
                    _heldMouseKeys &= ~MK_LBUTTON;
                    msg = WM_LBUTTONUP;
                    break;
            }

            if (_heldMouseKeys == 0)
            {
                _capturedMouseHwnd = IntPtr.Zero;
            }

            if (hwnd == IntPtr.Zero) return;

            IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
            Win32.PostMessageW(hwnd, msg, (IntPtr)_heldMouseKeys, lParam);

            IntPtr pointerWParam = (IntPtr)(1 /* POINTER_ID 1 */ | (0x2000 << 16 /* PRIMARY */));
            Win32.PostMessageW(hwnd, WM_POINTERUP, pointerWParam, lParam);
        });
    }

    /// <summary>
    /// Injects a complete mouse click (or double-click) at the specified desktop coordinates on the ghost desktop.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="button">Mouse button to click (default: Left).</param>
    /// <param name="doubleClick">Whether to perform a double-click gesture.</param>
    public void SendMouseClick(int desktopX, int desktopY, MouseButton button = MouseButton.Left, bool doubleClick = false)
    {
        if (doubleClick && button == MouseButton.Left)
        {
            IntPtr hitTopHwnd = IntPtr.Zero;
            RunOnGhostThread(() => GetWindowAtPoint(desktopX, desktopY, out _, out hitTopHwnd));
            if (hitTopHwnd != IntPtr.Zero && hitTopHwnd != Win32.GetDesktopWindow())
            {
                int hitTest = GetHitTest(hitTopHwnd, desktopX, desktopY);
                if (hitTest == HTCAPTION)
                {
                    ToggleMaximize(hitTopHwnd);
                    return;
                }
            }
        }

        SendMouseMove(desktopX, desktopY);
        SendMouseDown(desktopX, desktopY, button);
        try
        {
            Thread.Sleep(15);
        }
        finally
        {
            SendMouseUp(desktopX, desktopY, button);
        }

        if (doubleClick)
        {
            Thread.Sleep(30);
            RunOnGhostThread(() =>
            {
                IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out var clientPt);
                if (hwnd == IntPtr.Zero) return;

                if (hwnd != Win32.GetDesktopWindow())
                {
                    _lastTargetHwnd = hwnd;
                }

                uint dblMsg = button switch
                {
                    MouseButton.Right => WM_RBUTTONDBLCLK,
                    MouseButton.Middle => WM_MBUTTONDBLCLK,
                    _ => WM_LBUTTONDBLCLK
                };

                uint upMsg = button switch
                {
                    MouseButton.Right => WM_RBUTTONUP,
                    MouseButton.Middle => WM_MBUTTONUP,
                    _ => WM_LBUTTONUP
                };

                uint btnFlag = button switch
                {
                    MouseButton.Right => MK_RBUTTON,
                    MouseButton.Middle => MK_MBUTTON,
                    _ => MK_LBUTTON
                };

                _heldMouseKeys |= btnFlag;
                IntPtr lParam = MakeLParam(clientPt.X, clientPt.Y);
                Win32.PostMessageW(hwnd, dblMsg, (IntPtr)_heldMouseKeys, lParam);

                try
                {
                    Thread.Sleep(15);
                }
                finally
                {
                    _heldMouseKeys &= ~btnFlag;
                    Win32.PostMessageW(hwnd, upMsg, (IntPtr)_heldMouseKeys, lParam);
                }
            });
        }
    }

    /// <summary>
    /// Injects a vertical mouse scroll (wheel) event at the specified desktop coordinates.
    /// </summary>
    /// <param name="desktopX">Physical X coordinate on the ghost desktop.</param>
    /// <param name="desktopY">Physical Y coordinate on the ghost desktop.</param>
    /// <param name="delta">Scroll delta (typically multiples of 120; positive is scroll up, negative is scroll down).</param>
    public void SendMouseScroll(int desktopX, int desktopY, int delta)
    {
        RunOnGhostThread(() =>
        {
            IntPtr hwnd = GetWindowAtPoint(desktopX, desktopY, out _);
            if (hwnd == IntPtr.Zero || hwnd == Win32.GetDesktopWindow())
            {
                hwnd = FindKeyboardTarget();
            }

            if (hwnd == IntPtr.Zero) return;

            // WM_MOUSEWHEEL: wParam high word is delta, low word is keys
            IntPtr wParam = unchecked((IntPtr)((((uint)delta & 0xFFFF) << 16) | ((uint)_heldMouseKeys & 0xFFFF)));
            // WM_MOUSEWHEEL: lParam holds absolute screen coordinates
            IntPtr lParam = unchecked((IntPtr)((((uint)desktopY & 0xFFFF) << 16) | ((uint)desktopX & 0xFFFF)));

            Win32.PostMessageW(hwnd, WM_MOUSEWHEEL, wParam, lParam);
        });
    }

    /// <summary>
    /// Performs a mouse drag-and-drop gesture on the ghost desktop from start to end coordinates.
    /// </summary>
    public void SendMouseDrag(
        int startX, int startY, int endX, int endY,
        MouseButton button = MouseButton.Left,
        int steps = 10,
        int stepDelayMs = 10)
    {
        SendMouseMove(startX, startY);
        Thread.Sleep(10);
        SendMouseDown(startX, startY, button);

        try
        {
            Thread.Sleep(15);

            steps = Math.Max(1, steps);
            for (int i = 1; i <= steps; i++)
            {
                double t = (double)i / steps;
                int currX = (int)Math.Round(startX + (endX - startX) * t);
                int currY = (int)Math.Round(startY + (endY - startY) * t);
                SendMouseMove(currX, currY);
                if (stepDelayMs > 0 && i < steps)
                {
                    Thread.Sleep(stepDelayMs);
                }
            }

            Thread.Sleep(15);
        }
        finally
        {
            SendMouseUp(endX, endY, button);
        }
    }



    /// <summary>
    /// Resolves the specific target HWND and computes client coordinates for a given ghost desktop point.
    /// Enumerates visible windows specifically on the isolated ghost desktop (_hDesktop) in Z-order.
    /// </summary>
    private IntPtr GetWindowAtPoint(int desktopX, int desktopY, out POINT clientPt) =>
        GetWindowAtPoint(desktopX, desktopY, out clientPt, out _);

    private IntPtr GetWindowAtPoint(int desktopX, int desktopY, out POINT clientPt, out IntPtr hitTopHwnd)
    {
        EnsureDesktopAttached();

        hitTopHwnd = IntPtr.Zero;

        if (_hDesktop != IntPtr.Zero)
        {
            IntPtr found = IntPtr.Zero;
            Win32.EnumDesktopWindows(_hDesktop, (hWnd, lParam) =>
            {
                if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                {
                    return true;
                }

                if (!Win32.GetWindowRect(hWnd, out RECT rect))
                {
                    return true;
                }

                int w = rect.Right - rect.Left;
                int h = rect.Bottom - rect.Top;
                if (w <= 16 || h <= 16)
                {
                    return true;
                }

                var sbClass = new StringBuilder(256);
                Win32.GetClassNameW(hWnd, sbClass, 256);
                string cls = sbClass.ToString();

                if (IsIgnoredSystemWindowClass(cls))
                {
                    return true;
                }

                if (desktopX >= rect.Left && desktopX < rect.Right &&
                    desktopY >= rect.Top && desktopY < rect.Bottom)
                {
                    found = hWnd;
                    return false; // Found topmost hit!
                }

                return true;
            }, IntPtr.Zero);
            hitTopHwnd = found;
        }

        if (hitTopHwnd == IntPtr.Zero)
        {
            clientPt = new POINT { X = desktopX, Y = desktopY };
            return IntPtr.Zero;
        }

        // Drill down into visible child controls
        IntPtr targetHwnd = hitTopHwnd;
        POINT curPt = new POINT { X = desktopX, Y = desktopY };
        Win32.ScreenToClient(targetHwnd, ref curPt);

        while (true)
        {
            IntPtr child = Win32.ChildWindowFromPointEx(
                targetHwnd,
                curPt,
                CWP_SKIPINVISIBLE | CWP_SKIPDISABLED | CWP_SKIPTRANSPARENT);

            if (child == IntPtr.Zero || child == targetHwnd || !Win32.IsWindow(child))
            {
                break;
            }

            POINT screenPt = new POINT { X = desktopX, Y = desktopY };
            Win32.ScreenToClient(child, ref screenPt);
            curPt = screenPt;
            targetHwnd = child;
        }

        clientPt = curPt;
        return targetHwnd;
    }

    private static int GetHitTest(IntPtr hwnd, int screenX, int screenY)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return HTNOWHERE;

        IntPtr lParam = unchecked((IntPtr)((((uint)screenY & 0xFFFF) << 16) | ((uint)screenX & 0xFFFF)));
        if (Win32.SendMessageTimeoutW(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam, 2 /* SMTO_ABORTIFHUNG */, 80, out IntPtr res) != IntPtr.Zero)
        {
            int h = res.ToInt32();
            if (h != 0 && h != -1)
            {
                // For custom-framed windows (Chrome, VS Code, modern apps) that return HTCLIENT for the entire window:
                // If the hit is in the top caption band, treat it as non-client caption / window controls.
                if (h == HTCLIENT && Win32.GetWindowRect(hwnd, out RECT rcc))
                {
                    if (screenY >= rcc.Top && screenY < rcc.Top + 34)
                    {
                        if (screenX >= rcc.Right - 46) return HTCLOSE;
                        if (screenX >= rcc.Right - 92) return HTMAXBUTTON;
                        if (screenX >= rcc.Right - 138) return HTMINBUTTON;
                        return HTCAPTION;
                    }
                }
                return h;
            }
        }

        if (Win32.GetWindowRect(hwnd, out RECT rc))
        {
            if (screenX >= rc.Left && screenX < rc.Right &&
                screenY >= rc.Top && screenY < rc.Top + 34)
            {
                if (screenX >= rc.Right - 46) return HTCLOSE;
                if (screenX >= rc.Right - 92) return HTMAXBUTTON;
                if (screenX >= rc.Right - 138) return HTMINBUTTON;
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        return HTCLIENT;
    }

    private static void ToggleMaximize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return;
        if (Win32.IsZoomed(hwnd))
        {
            Win32.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        }
        else
        {
            Win32.ShowWindow(hwnd, 3 /* SW_MAXIMIZE */);
        }
    }

    /// <summary>
    /// Dynamically locates the active keyboard input target window on the ghost desktop.
    /// Strictly restricts targets to windows belonging to the isolated desktop (_hDesktop).
    private static bool IsIgnoredSystemWindowClass(string cls)
    {
        return string.Equals(cls, "Progman", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "Default IME", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "UAC_InputIndicatorOverlayWnd", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "UAC Input Indicator", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(cls, "EdgeUiInputTopWndClass", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the given window actually resides on the isolated ghost desktop by
    /// resolving its owning thread's desktop name. Every input path must only ever post
    /// messages to windows that pass this check, preventing leaks to the user's real desktop.
    /// </summary>
    private bool IsWindowOnGhostDesktop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd))
        {
            return false;
        }

        uint threadId = Win32.GetWindowThreadProcessId(hwnd, out _);
        if (threadId == 0)
        {
            return false;
        }

        IntPtr hThreadDesktop = Win32.GetThreadDesktop(threadId);
        if (hThreadDesktop == IntPtr.Zero)
        {
            return false;
        }

        var nameBuf = new byte[512];
        if (!NativeMethods.GetUserObjectInformationW(hThreadDesktop, NativeMethods.UOI_NAME, nameBuf, nameBuf.Length, out _))
        {
            return false;
        }

        string deskName = Encoding.Unicode.GetString(nameBuf).TrimEnd('\0');
        return string.Equals(deskName, _desktopName, StringComparison.OrdinalIgnoreCase) ||
               deskName.EndsWith("\\" + _desktopName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dynamically locates the active keyboard input target window on the ghost desktop.
    /// Strictly restricts targets to windows belonging to the isolated desktop (_hDesktop).
    /// Automatically routes input to modal dialogs / active popups (GetLastActivePopup).
    /// </summary>
    private IntPtr FindKeyboardTarget()
    {
        IntPtr target = IntPtr.Zero;

        // 1. Explicit target override (must reside on the ghost desktop)
        if (TargetHwnd.HasValue && IsWindowOnGhostDesktop(TargetHwnd.Value))
        {
            target = TargetHwnd.Value;
        }
        // 2. Last target window touched by mouse actions on the ghost desktop (if still valid and visible)
        else if (_lastTargetHwnd != IntPtr.Zero && IsWindowOnGhostDesktop(_lastTargetHwnd) && Win32.IsWindowVisible(_lastTargetHwnd) && !Win32.IsIconic(_lastTargetHwnd))
        {
            target = _lastTargetHwnd;
        }

        // 3. Fallback: Topmost visible window on _hDesktop
        if (target == IntPtr.Zero)
        {
            EnsureDesktopAttached();
            if (_hDesktop != IntPtr.Zero)
            {
                Win32.EnumDesktopWindows(_hDesktop, (hWnd, lParam) =>
                {
                    if (hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd) || !Win32.IsWindowVisible(hWnd) || Win32.IsIconic(hWnd))
                    {
                        return true;
                    }

                    var sbClass = new StringBuilder(256);
                    Win32.GetClassNameW(hWnd, sbClass, 256);
                    string cls = sbClass.ToString();

                    if (IsIgnoredSystemWindowClass(cls))
                    {
                        return true;
                    }

                    target = hWnd;
                    return false; // Found topmost valid window
                }, IntPtr.Zero);
            }
        }

        if (target != IntPtr.Zero)
        {
            // If the target window has an active modal dialog or popup, route to the popup!
            IntPtr popup = Win32.GetLastActivePopup(target);
            if (popup != IntPtr.Zero && popup != target && Win32.IsWindow(popup) && Win32.IsWindowVisible(popup))
            {
                target = popup;
            }

            _lastTargetHwnd = target;

            uint threadId = Win32.GetWindowThreadProcessId(target, out _);
            if (threadId != 0)
            {
                var gui = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
                if (Win32.GetGUIThreadInfo(threadId, ref gui))
                {
                    if (gui.hwndFocus != IntPtr.Zero && Win32.IsWindow(gui.hwndFocus))
                        return gui.hwndFocus;
                    if (gui.hwndActive != IntPtr.Zero && Win32.IsWindow(gui.hwndActive))
                        return gui.hwndActive;
                }
            }
            return target;
        }

        // 4. Fallback to the ghost desktop window only when this thread is bound to it
        return _isDesktopBound ? Win32.GetDesktopWindow() : IntPtr.Zero;
    }

    private static IntPtr MakeLParam(int x, int y)
    {
        return unchecked((IntPtr)((((uint)y & 0xFFFF) << 16) | ((uint)x & 0xFFFF)));
    }

    private static bool IsExtendedKey(uint vk)
    {
        return vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 // PageUp, PageDown, End, Home, Left, Up, Right, Down
            or 0x2D or 0x2E // Insert, Delete
            or 0x6F // NumPad Divide
            or 0xA3 // Right Control
            or 0xA5 // Right Menu (Alt)
            or 0x5B or 0x5C or 0x5D // Win keys, Apps
            or 0x90 // NumLock
            or 0x2C // PrintScreen
            => true,
            _ => false
        };
    }
}
