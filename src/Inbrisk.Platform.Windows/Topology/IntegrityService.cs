using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows.Native;

namespace Inbrisk.Platform.Windows.Topology;

/// <summary>
/// Integrity-level and secure-desktop detection. Decides *before* acting
/// whether input can legally reach a target — UIPI drops injected input
/// silently, so silent failure must never be our answer.
/// </summary>
public sealed class IntegrityService : IIntegrityService
{
    public IntegrityRelation CheckTarget(long hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(new IntPtr(hwnd), out var pid);
        if (pid == 0) return IntegrityRelation.Unknown;
        var target = ProcessIntegrityLevel(pid);
        var ours = ProcessIntegrityLevel(Environment.ProcessId);
        if (target < 0 || ours < 0) return IntegrityRelation.Unknown;
        return target > ours ? IntegrityRelation.TargetElevated : IntegrityRelation.Reachable;
    }

    /// <summary>
    /// UAC consent, lock screen, Ctrl+Alt+Del run on a separate input desktop
    /// ("Winlogon"). Neither capture nor input can reach it.
    /// </summary>
    public bool IsSecureDesktopActive()
    {
        var h = NativeMethods.OpenInputDesktop(0, false, NativeMethods.DESKTOP_READOBJECTS);
        if (h == IntPtr.Zero) return true; // can't even open → secure/locked
        try
        {
            var buf = new byte[256];
            if (!NativeMethods.GetUserObjectInformationW(h, NativeMethods.UOI_NAME, buf, buf.Length, out _))
                return false;
            var name = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
            return !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { NativeMethods.CloseDesktop(h); }
    }

    /// <summary>Returns RID of the token's integrity level (0x2000 medium,
    /// 0x3000 high) or -1 if unreadable.</summary>
    public static int ProcessIntegrityLevel(int pid)
    {
        var hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            return err == 5 ? 0x3000 : -1; // ERROR_ACCESS_DENIED implies higher integrity than current caller
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(hProc, NativeMethods.TOKEN_QUERY, out var token))
            {
                var err = Marshal.GetLastWin32Error();
                return err == 5 ? 0x3000 : -1;
            }
            try
            {
                NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                    Array.Empty<byte>(), 0, out var len);
                var buf = new byte[len];
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenIntegrityLevel,
                        buf, len, out _)) return -1;
                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(
                    Marshal.UnsafeAddrOfPinnedArrayElement(buf, 0));
                var count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(label.Sid));
                var last = Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(label.Sid, count - 1));
                return last;
            }
            finally { NativeMethods.CloseHandle(token); }
        }
        finally { NativeMethods.CloseHandle(hProc); }
    }

    public static bool IsProcessElevated(int pid)
    {
        var lvl = ProcessIntegrityLevel(pid);
        return lvl >= 0x3000;
    }
}
