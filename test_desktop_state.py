import time
import win32gui
import win32clipboard
import win32api
import subprocess

def get_state():
    fg = win32gui.GetForegroundWindow()
    fg_title = win32gui.GetWindowText(fg)
    mouse_pos = win32api.GetCursorPos()
    try:
        win32clipboard.OpenClipboard()
        clip = win32clipboard.GetClipboardData(win32clipboard.CF_UNICODETEXT)
        win32clipboard.CloseClipboard()
    except Exception:
        clip = None
    return fg, fg_title, mouse_pos, clip

print("Initial Desktop State:")
fg, fg_title, mouse, clip = get_state()
print(f"  Foreground Window: HWND={hex(fg)} Title={fg_title!r}")
print(f"  Mouse Position: {mouse}")
print(f"  Clipboard: {clip!r}")
