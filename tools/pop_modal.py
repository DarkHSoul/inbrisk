# Harness-side modal helper: pops a real Win32 MessageBox (#32770, Button-class
# children, BM_CLICK-able) owned by the run's target hwnd. Runs from python.exe
# — NOT a protected process — so the interceptor's IsWindowProtected veto
# passes (powershell.exe/conhost/cmd are protected and would be vetoed).
import ctypes, sys, time
from ctypes import wintypes

owner = int(sys.argv[1])
text = sys.argv[2]
title = sys.argv[3]
buttons = int(sys.argv[4])          # 0=OK 1=OKCancel 3=YesNoCancel 4=YesNo
delay_ms = int(sys.argv[5]) if len(sys.argv) > 5 else 1200

time.sleep(delay_ms / 1000)
u32 = ctypes.windll.user32
u32.MessageBoxW.argtypes = [wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR, wintypes.UINT]
u32.MessageBoxW(wintypes.HWND(owner), text, title, buttons | 0x40)  # + MB_ICONINFORMATION
