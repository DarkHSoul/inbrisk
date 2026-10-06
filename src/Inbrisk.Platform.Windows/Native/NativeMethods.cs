using System.Runtime.InteropServices;
using System.Text;

namespace Inbrisk.Platform.Windows.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
internal struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public POINT Pt;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MOUSEINPUT
{
    public int Dx, Dy;
    public uint MouseData;
    public uint DwFlags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KEYBDINPUT
{
    public ushort WVk;
    public ushort WScan;
    public uint DwFlags;
    public uint Time;
    public IntPtr DwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct HARDWAREINPUT
{
    public uint UMsg;
    public ushort WParamL;
    public ushort WParamH;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT Mi;
    [FieldOffset(0)] public KEYBDINPUT Ki;
    [FieldOffset(0)] public HARDWAREINPUT Hi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct INPUT
{
    public uint Type;
    public INPUTUNION U;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEXW
{
    public int CbSize;
    public RECT RcMonitor;
    public RECT RcWork;
    public uint DwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string SzDevice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TOKEN_MANDATORY_LABEL { public IntPtr Sid; }

internal static class NativeMethods
{
    // ---- system metrics / coordinates ----
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_CXDRAG = 68;
    public const int SM_CYDRAG = 69;

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int nIndex);

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] internal static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);

    // ---- windows ----
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern void SwitchToThisWindow(IntPtr hWnd, bool fUnknown);
    [DllImport("user32.dll")] internal static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    public const byte VK_MENU = 0x12;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
    public const uint MAPVK_VK_TO_VSC = 0;
    [DllImport("user32.dll")] internal static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int nVirtKey);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] internal static extern IntPtr GetLastActivePopup(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    public const uint GW_HWNDFIRST = 0;
    public const uint GW_HWNDLAST = 1;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;
    public const uint GW_CHILD = 5;
    public const uint GW_ENABLEDPOPUP = 6;

    public const uint TH32CS_SNAPPROCESS = 0x00000002;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_DLGMODALFRAME = 0x00000001;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_APPWINDOW = 0x00040000;
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_VISIBLE = 0x10000000;
    public const uint WS_MINIMIZEBOX = 0x00020000;
    public const uint WS_MAXIMIZEBOX = 0x00010000;
    public const uint WS_THICKFRAME = 0x00040000;
    public const uint WS_SIZEBOX = 0x00040000;
    public const uint WS_SYSMENU = 0x00080000;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_SHOWWINDOW = 0x0040;

    // ---- window class / painting (screen indicator overlays) ----
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEXW
    {
        public uint CbSize;
        public uint Style;
        public IntPtr LpfnWndProc;
        public int CbClsExtra, CbWndExtra;
        public IntPtr HInstance, HIcon, HCursor, HbrBackground;
        public string? LpszMenuName;
        public string LpszClassName;
        public IntPtr HIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public bool FErase;
        public RECT RcPaint;
        public bool FRestore, FIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] RgbReserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);
    [DllImport("user32.dll")] internal static extern IntPtr BeginPaint(IntPtr hWnd, out PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] internal static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] internal static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateSolidBrush(uint crColor);
    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr ho);

    // ---- layered-window per-pixel alpha rendering ----
    internal struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    internal const byte AC_SRC_OVER = 0x00;
    internal const byte AC_SRC_ALPHA = 0x01;
    internal const uint ULW_ALPHA = 0x00000002;
    internal const uint DIB_RGB_COLORS = 0;
    internal const int BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public int BiSize, BiWidth, BiHeight;
        public short BiPlanes, BiBitCount;
        public int BiCompression, BiSizeImage, BiXPelsPerMeter, BiYPelsPerMeter,
            BiClrUsed, BiClrImportant;
    }

    [DllImport("gdi32.dll")] internal static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint iUsage, out IntPtr ppvBits,
        IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr hdc);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] internal static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
    [DllImport("user32.dll")] internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint dwAffinity);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int nExitCode);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint WM_NULL = 0x0000;
    public const int SW_SHOWNOACTIVATE = 4;

    public const uint LWA_COLORKEY = 0x00000001;
    public const uint LWA_ALPHA = 0x00000002;
    public const uint WDA_NONE = 0x00000000;
    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_KEYDOWN = 0x0100;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_CHAR = 0x0102;
    public const uint WM_SYSKEYDOWN = 0x0104;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_APP = 0x8000;

    public const int SW_RESTORE = 9;
    public const int SW_SHOW = 5;
    public const uint GA_ROOT = 2;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowAttributeRect(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    // ---- secure desktop ----
    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(IntPtr hDesktop);

    // ---- SendInput ----
    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // ---- monitors ----
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW lpmi);
    public const int MONITORINFOF_PRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ---- WinEvents ----
    internal delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint EVENT_OBJECT_CREATE = 0x8000;
    public const uint EVENT_OBJECT_DESTROY = 0x8001;
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    public const uint EVENT_OBJECT_FOCUS = 0x8005;
    public const uint EVENT_OBJECT_STATECHANGE = 0x800A;
    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    public const uint EVENT_OBJECT_VALUECHANGE = 0x800E;

    // ---- message pump (for the WinEvent hook thread) ----
    [DllImport("user32.dll")] internal static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessageW(ref MSG lpMsg);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessageW(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    public const uint WM_QUIT = 0x0012;

    // ---- process / tokens ----
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenIntegrityLevel = 25;
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr hObject);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder exeName, ref int lpdwSize);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, byte[] TokenInformation, int TokenInformationLength, out int ReturnLength);
    [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);
    [DllImport("advapi32.dll")] internal static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

    // ---- clipboard ----
    public const uint CF_UNICODETEXT = 13;
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    public const uint GMEM_MOVEABLE = 0x0002;
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] internal static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr hMem);

    // ---- tray icon & menus ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATAW
    {
        public uint CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
        public uint DwState;
        public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;
        public uint UTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;
        public uint DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;
    public const uint NIM_SETVERSION = 0x00000004;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;
    public const uint NIF_STATE = 0x00000008;
    public const uint NIF_INFO = 0x00000010;

    public const uint WM_NCHITTEST = 0x0084;
    public const uint WM_NCCALCSIZE = 0x0083;
    public const uint WM_NCLBUTTONDOWN = 0x00A1;
    public const uint WM_NCLBUTTONDBLCLK = 0x00A3;
    public const uint WM_GETMINMAXINFO = 0x0024;
    public const uint WM_SYSCOMMAND = 0x0112;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
    public const int HTCAPTION = 2;
    public const int HTLEFT = 10;
    public const int HTRIGHT = 11;
    public const int HTTOP = 12;
    public const int HTTOPLEFT = 13;
    public const int HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15;
    public const int HTBOTTOMLEFT = 16;
    public const int HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_CAPTURECHANGED = 0x0215;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_RBUTTONDBLCLK = 0x0206;
    public const uint WM_MBUTTONDOWN = 0x0207;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_MBUTTONDBLCLK = 0x0209;
    public const uint WM_MOUSEWHEEL = 0x020A;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint NIN_SELECT = 0x0400;
    public const uint NIN_KEYSELECT = 0x0401;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")] internal static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] internal static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    internal static extern uint TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lpTPMParams);

    public const uint MF_STRING = 0x00000000;
    public const uint MF_SEPARATOR = 0x00000800;
    public const uint MF_CHECKED = 0x00000008;
    public const uint MF_UNCHECKED = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_DISABLED = 0x00000002;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] internal static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);
    [DllImport("user32.dll")] internal static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] internal static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
    [DllImport("user32.dll")] internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")] internal static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr SetActiveWindow(IntPtr hWnd);

    public const uint WM_SETCURSOR = 0x0020;
    public static readonly IntPtr IDI_APPLICATION = new(32512);
    public static readonly IntPtr IDC_ARROW = new(32512);
    public static readonly IntPtr IDC_HAND = new(32649);
    public static readonly IntPtr IDC_SIZEWE = new(32646);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")] internal static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] internal static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

    // ---- message-based ("silent") input: timeout-guarded sends ----
    // Every cross-window SendMessage goes through SendMessageTimeoutW so a
    // hung target can never block the dispatcher. Three lParam shapes:
    // plain IntPtr, LPWStr text (WM_SETTEXT / EM_REPLACESEL), and a
    // caller-sized StringBuilder (WM_GETTEXT read-back).
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg,
        IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint msg,
        IntPtr wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessageTimeoutBuffer(IntPtr hWnd, uint msg,
        IntPtr wParam, StringBuilder lParam,
        uint fuFlags, uint uTimeout, out UIntPtr lpdwResult);

    public const uint WM_GETTEXT = 0x000D;
    public const uint WM_GETTEXTLENGTH = 0x000E;
    public const uint WM_SETTEXT = 0x000C;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_COMMAND = 0x0111;

    public const uint EM_SETSEL = 0x00B1;
    public const uint EM_REPLACESEL = 0x00C2;

    public const uint BM_GETCHECK = 0x00F0;
    public const uint BM_SETCHECK = 0x00F1;
    public const uint BM_CLICK = 0x00F5;
    public const int BST_UNCHECKED = 0;
    public const int BST_CHECKED = 1;
    public const int BST_INDETERMINATE = 2;
    public const int BN_CLICKED = 0;

    public const int GWL_STYLE = -16;
    public const long BS_TYPEMASK = 0x0000000FL;
    public const long BS_CHECKBOX = 0x00000002L;
    public const long BS_AUTOCHECKBOX = 0x00000003L;
    public const long BS_RADIOBUTTON = 0x00000004L;
    public const long BS_3STATE = 0x00000005L;
    public const long BS_AUTO3STATE = 0x00000006L;
    public const long BS_AUTORADIOBUTTON = 0x00000009L;

    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern int GetDlgCtrlID(IntPtr hWnd);

    // ---- menu traversal (WM_COMMAND commandId discovery) ----
    [DllImport("user32.dll")] internal static extern IntPtr GetMenu(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);
    [DllImport("user32.dll")] internal static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll")] internal static extern uint GetMenuItemID(IntPtr hMenu, int nPos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);
    public const uint MF_BYPOSITION = 0x00000400;
    public const uint MENU_ITEM_SUBMENU = 0xFFFFFFFF;
}
