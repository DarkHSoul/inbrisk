using System.Runtime.InteropServices;
using System.Text;

namespace Inbrisk.Platform.Windows.Native;

/// <summary>
/// Ensures worker threads and UIA dispatchers bind to the user's interactive
/// "Default" desktop even when Inbrisk is launched from an isolated desktop sandbox
/// (such as agent environments running on exebox-* desktops on WinSta0).
/// </summary>
public static class DesktopBridge
{
    private const uint DesktopAllAccess = 0x01FF;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    public static string GetCurrentDesktopName()
    {
        try
        {
            var hCur = GetThreadDesktop(GetCurrentThreadId());
            if (hCur == IntPtr.Zero) return "";
            var sb = new StringBuilder(256);
            return GetUserObjectInformation(hCur, 2, sb, 256, out _) ? sb.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    public static bool IsOnDefaultDesktop()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("INBRISK_DESKTOP_BRIDGE"), "off", StringComparison.OrdinalIgnoreCase))
            return true;
        var name = GetCurrentDesktopName();
        return string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TrySwitchCurrentThread()
    {
        if (IsOnDefaultDesktop()) return true;
        try
        {
            var hDef = OpenDesktop("Default", 0, false, DesktopAllAccess);
            if (hDef == IntPtr.Zero) return false;
            var ok = SetThreadDesktop(hDef);
            // Note: do not close hDef if SetThreadDesktop succeeded; thread now uses it
            if (!ok) CloseDesktop(hDef);
            return ok;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Executes the specified function on the Default desktop. If the calling thread
    /// is already on Default, runs inline. If the calling thread cannot switch (has windows/hooks),
    /// executes on a fresh MTA worker thread where SetThreadDesktop is invoked before any window calls.
    /// </summary>
    public static T RunOnDefaultDesktop<T>(Func<T> action)
    {
        if (IsOnDefaultDesktop() || TrySwitchCurrentThread())
        {
            return action();
        }

        // Spawn clean worker thread to cross desktop boundary
        T result = default!;
        Exception? error = null;

        var t = new Thread(() =>
        {
            try
            {
                var hDef = OpenDesktop("Default", 0, false, DesktopAllAccess);
                if (hDef != IntPtr.Zero)
                {
                    SetThreadDesktop(hDef);
                }
                result = action();
                if (hDef != IntPtr.Zero) CloseDesktop(hDef);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        })
        {
            IsBackground = true,
            Name = "inbrisk-desktop-bridge"
        };

        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();

        if (error != null) throw new InvalidOperationException("Failed on Default desktop", error);
        return result;
    }
}
